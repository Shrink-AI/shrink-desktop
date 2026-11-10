using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public sealed class AgentSession : IAsyncDisposable
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string NativeId { get; private set; } = "";
    public string Kind { get; }
    public string Cwd { get; }
    public string State { get; private set; } = "starting";
    public string Turn { get; private set; } = "";
    private string output = "";
    private bool interruptRequested;
    private readonly string? resumeId;
    private readonly Journal journal;
    private readonly JsonProcess rpc;
    private readonly SemaphoreSlim mutation = new(1);
    private readonly ConcurrentDictionary<string, (JsonObject Message, DateTimeOffset Expires)> approvals = new();
    public JsonObject Snapshot() => new() { ["id"] = Id, ["native_id"] = NativeId, ["agent"] = Kind, ["cwd"] = Cwd,
        ["state"] = State, ["mode"] = "managed", ["pid"] = rpc.Pid, ["recovery"] = resumeId != null ? "history_resume" : null,
        ["capabilities"] = new JsonArray("read", "send", "interrupt", "approve"),
        ["output"] = output };
    public AgentSession(string kind, string cwd, AgentSettings settings, Journal journal, string? resumeId = null)
    {
        Kind = kind; Cwd = Path.GetFullPath(cwd); this.journal = journal; this.resumeId = resumeId;
        var args = kind switch
        {
            "codex" => new[] { "app-server", "--stdio" },
            "claude" => new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--permission-prompt-tool", "stdio" },
            "dsh" => Array.Empty<string>(),
            _ => throw new ArgumentException("Unknown agent")
        };
        rpc = new JsonProcess(settings, Cwd, kind == "claude" && resumeId != null ? args.Concat(["--resume", resumeId]) : args);
        rpc.OnMessage = Message;
    }
    private void Publish(string type, JsonNode? detail = null)
    {
        journal.Session(Snapshot());
        journal.Emit(new JsonObject { ["kind"] = type, ["session_id"] = Id, ["native_id"] = NativeId,
            ["state"] = State, ["detail"] = detail?.DeepClone() });
    }
    public async Task Start()
    {
        rpc.Start();
        if (Kind == "codex")
        {
            await rpc.Call("initialize", new() { ["clientInfo"] = new JsonObject { ["name"] = "shrink_desktop", ["version"] = "0.1.0" } });
            await rpc.Notify("initialized", new());
            var parameters = new JsonObject { ["cwd"] = Cwd, ["approvalPolicy"] = "on-request", ["sandbox"] = "workspace-write" };
            if (resumeId != null) parameters["threadId"] = resumeId;
            var response = await rpc.Call(resumeId == null ? "thread/start" : "thread/resume", parameters);
            NativeId = response!["thread"]!["id"]!.GetValue<string>();
        }
        else if (Kind == "dsh")
        {
            var init = await rpc.Call("initialize", new() { ["protocolVersion"] = 1,
                ["clientCapabilities"] = new JsonObject { ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false }, ["terminal"] = false } });
            var recoveryMethod = init?["agentCapabilities"]?["sessionCapabilities"]?["resume"] != null ? "session/resume" :
                init?["agentCapabilities"]?["loadSession"]?.GetValue<bool>() == true ? "session/load" : null;
            if (resumeId != null && recoveryMethod == null)
                throw new InvalidOperationException("Installed DSH does not advertise session recovery");
            var parameters = new JsonObject { ["cwd"] = Cwd, ["mcpServers"] = new JsonArray() };
            if (resumeId != null) parameters["sessionId"] = resumeId;
            var response = await rpc.Call(resumeId == null ? "session/new" : recoveryMethod!, parameters);
            NativeId = resumeId ?? response!["sessionId"]!.GetValue<string>();
        }
        else NativeId = resumeId ?? "";
        State = "idle"; Publish("session");
    }
    public async Task Send(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("Empty prompt");
        await mutation.WaitAsync();
        try
        {
            if (State == "waiting_confirmation") throw new InvalidOperationException("Resolve the pending confirmation first");
            if (State == "running")
            {
                if (Kind != "codex") throw new InvalidOperationException("This adapter accepts additional instructions after interruption or completion");
                await rpc.Call("turn/steer", new() { ["threadId"] = NativeId, ["expectedTurnId"] = Turn, ["input"] = Text(prompt) });
                return;
            }
            if (rpc.Exited) throw new InvalidOperationException("Process exited; start or explicitly recover a session");
            State = "running"; output = ""; interruptRequested = false; Publish("state");
            if (Kind == "codex")
            {
                var r = await rpc.Call("turn/start", new() { ["threadId"] = NativeId, ["input"] = Text(prompt) });
                Turn = r!["turn"]!["id"]!.GetValue<string>();
            }
            else if (Kind == "claude")
                await rpc.Send(new() { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = prompt } });
            else _ = DshTurn(prompt);
        }
        finally { mutation.Release(); }
    }
    private static JsonArray Text(string text) => new(new JsonObject { ["type"] = "text", ["text"] = text });
    private async Task DshTurn(string prompt)
    {
        try
        {
            var r = await rpc.Call("session/prompt", new() { ["sessionId"] = NativeId, ["prompt"] = Text(prompt) }, 86400);
            State = r?["stopReason"]?.GetValue<string>() == "cancelled" ? "interrupted" : "completed";
            Publish("result", new JsonObject { ["text"] = output, ["stop"] = r?.DeepClone() });
        }
        catch (Exception ex) { State = "unknown"; Publish("error", JsonValue.Create(ex.Message)); }
    }
    private async Task Message(JsonObject msg)
    {
        var method = msg["method"]?.GetValue<string>() ?? "";
        var type = msg["type"]?.GetValue<string>() ?? "";
        if (type == "process_exit")
        {
            if (State is "running" or "starting" or "waiting_confirmation") { State = "unknown"; Publish("disconnected"); }
            return;
        }
        if (type == "control_request" || (msg["id"] != null && method != ""))
        {
            var supported = Kind switch
            {
                "claude" => msg["request"]?["subtype"]?.GetValue<string>() == "can_use_tool",
                "dsh" => method == "session/request_permission",
                _ => method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval"
            };
            if (!supported)
            {
                if (msg["id"] != null) await rpc.Send(new() { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Unsupported host request" } });
                return;
            }
            var key = Guid.NewGuid().ToString("N");
            approvals[key] = (msg.DeepClone().AsObject(), DateTimeOffset.UtcNow.AddMinutes(15));
            State = "waiting_confirmation";
            Publish("confirmation", new JsonObject { ["approval_id"] = key, ["expires"] = approvals[key].Expires.ToString("O"),
                ["request"] = (msg["params"] ?? msg["request"])?.DeepClone() });
            return;
        }
        if (Kind == "codex")
        {
            if (method == "turn/started") Turn = msg["params"]?["turn"]?["id"]?.GetValue<string>() ?? Turn;
            if (method == "item/agentMessage/delta") Append(msg["params"]?["delta"]?.GetValue<string>());
            if (method == "turn/completed")
            {
                State = msg["params"]?["turn"]?["status"]?.GetValue<string>() ?? "unknown";
                Publish("result", new JsonObject { ["text"] = output, ["turn"] = msg["params"]?.DeepClone() });
            }
        }
        else if (Kind == "claude")
        {
            NativeId = msg["session_id"]?.GetValue<string>() ?? NativeId;
            if (type == "assistant" && msg["message"]?["content"] is JsonArray content)
                foreach (var item in content) if (item?["type"]?.GetValue<string>() == "text") Append(item["text"]?.GetValue<string>());
            if (type == "result")
            {
                State = interruptRequested ? "interrupted" : msg["is_error"]?.GetValue<bool>() == true ? "failed" : "completed";
                if (msg["result"] is JsonValue value) output = value.GetValue<string>();
                Publish("result", new JsonObject { ["text"] = output, ["subtype"] = msg["subtype"]?.DeepClone() });
            }
        }
        else if (method == "session/update")
        {
            var update = msg["params"]?["update"];
            if (update?["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk") Append(update["content"]?["text"]?.GetValue<string>());
        }
        if (method.Contains("tool") || method == "item/started") Publish("progress", msg);
    }
    private void Append(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        output += text;
        if (output.Length > 200000) output = output[^200000..];
        Publish("output", JsonValue.Create(text));
    }
    public async Task Interrupt()
    {
        if (State is not ("running" or "waiting_confirmation")) return;
        interruptRequested = true;
        if (Kind == "codex") await rpc.Call("turn/interrupt", new() { ["threadId"] = NativeId, ["turnId"] = Turn });
        else if (Kind == "dsh") await rpc.Notify("session/cancel", new() { ["sessionId"] = NativeId });
        else await rpc.Send(new() { ["type"] = "control_request", ["request_id"] = Guid.NewGuid().ToString("N"), ["request"] = new JsonObject { ["subtype"] = "interrupt" } });
        Publish("interrupt_requested");
    }
    public async Task Approve(string key, bool allow)
    {
        if (!approvals.TryRemove(key, out var pending)) throw new InvalidOperationException("Confirmation not pending in this session");
        if (DateTimeOffset.UtcNow > pending.Expires) allow = false;
        var msg = pending.Message;
        if (Kind == "claude")
        {
            var response = new JsonObject { ["behavior"] = allow ? "allow" : "deny" };
            if (allow) response["updatedInput"] = msg["request"]?["input"]?.DeepClone() ?? new JsonObject();
            else response["message"] = "Denied by device controller";
            await rpc.Send(new() { ["type"] = "control_response", ["response"] = new JsonObject { ["subtype"] = "success",
                ["request_id"] = msg["request_id"]!.DeepClone(), ["response"] = response } });
        }
        else
        {
            JsonObject result;
            if (Kind == "dsh")
            {
                var option = (msg["params"]?["options"] as JsonArray)?.FirstOrDefault(x => x?["kind"]?.GetValue<string>() == (allow ? "allow_once" : "reject_once"));
                result = new() { ["outcome"] = option == null ? new JsonObject { ["outcome"] = "cancelled" }
                    : new JsonObject { ["outcome"] = "selected", ["optionId"] = option["optionId"]!.DeepClone() } };
            }
            else result = new() { ["decision"] = allow ? "accept" : "decline" };
            await rpc.Send(new() { ["jsonrpc"] = "2.0", ["id"] = msg["id"]!.DeepClone(), ["result"] = result });
        }
        State = "running"; Publish("confirmation_resolved", JsonValue.Create(key));
    }
    public async Task ExpireApprovals()
    {
        foreach (var entry in approvals.Where(x => x.Value.Expires <= DateTimeOffset.UtcNow).ToArray())
            await Approve(entry.Key, false);
    }
    public ValueTask DisposeAsync() => rpc.DisposeAsync();
}
