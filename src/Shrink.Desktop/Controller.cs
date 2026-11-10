using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public sealed class Controller(Configuration config, Journal journal) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> sessions = new();
    private readonly ConcurrentDictionary<string, JsonObject> observed = new();
    private readonly DesktopWindows windows = new();
    public bool Paused { get; set; }
    public JsonArray Sessions()
    {
        var result = journal.Sessions();
        foreach (var node in result)
        {
            if (node is not JsonObject item) continue;
            var id = item["id"]!.GetValue<string>();
            if (sessions.TryGetValue(id, out var live)) { item["state"] = live.State; continue; }
            item["mode"] = "observe"; item["capabilities"] = new JsonArray("read");
            if (item["state"]?.GetValue<string>() is "running" or "waiting_confirmation" or "starting") item["state"] = "unknown";
        }
        return result;
    }
    public async Task<JsonNode> Execute(JsonObject command)
    {
        var id = command["id"]?.GetValue<string>() ?? throw new ArgumentException("Command ID required");
        var started = journal.Begin(id, command);
        if (!started.Fresh) return started.Result!;
        JsonNode result;
        try
        {
            var action = command["action"]!.GetValue<string>();
            var args = command["arguments"]?.AsObject() ?? new();
            if (Paused && action is not ("sessions" or "read")) throw new InvalidOperationException("Device control paused locally");
            result = await Dispatch(action, args);
        }
        catch (Exception ex) { result = new JsonObject { ["ok"] = false, ["error"] = ex.Message, ["error_type"] = ex.GetType().Name }; }
        journal.Complete(id, result);
        return result;
    }
    private async Task<JsonNode> Dispatch(string action, JsonObject args)
    {
        string Required(string name) => args[name]?.GetValue<string>() ?? throw new ArgumentException(name + " required");
        switch (action)
        {
            case "sessions": return Sessions();
            case "discover":
                var found = await Task.Run(() => Discovery.List(Required("agent"), args["query"]?.GetValue<string>()));
                foreach (var item in found.OfType<JsonObject>())
                { observed[item["id"]!.GetValue<string>()] = item.DeepClone().AsObject(); journal.Session(item); }
                return found;
            case "start":
                var agent = Required("agent");
                if (!config.Agents.TryGetValue(agent, out var settings)) throw new ArgumentException("Agent is not configured");
                var session = new AgentSession(agent, Required("cwd"), settings, journal);
                try
                {
                    await session.Start(); sessions[session.Id] = session;
                    if (args["prompt"] is JsonValue prompt) await session.Send(prompt.GetValue<string>());
                    return session.Snapshot();
                }
                catch { await session.DisposeAsync(); throw; }
            case "resume":
                var previousId = Required("session_id");
                if (sessions.ContainsKey(previousId)) throw new InvalidOperationException("Original session is still managed; use send");
                var previous = journal.Sessions().OfType<JsonObject>().SingleOrDefault(x => x["id"]?.GetValue<string>() == previousId)
                    ?? throw new ArgumentException("Session not found");
                if (previous["mode"]?.GetValue<string>() != "managed")
                    throw new InvalidOperationException("External session is observe only; bind its existing window");
                if (previous["pid"] is JsonValue previousPid)
                {
                    try { using var process = System.Diagnostics.Process.GetProcessById(previousPid.GetValue<int>());
                        if (!process.HasExited) throw new InvalidOperationException("Original process may still be active; inspect it before historical recovery"); }
                    catch (ArgumentException) { }
                }
                var native = previous["native_id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(native)) throw new InvalidOperationException("No native session ID was recorded");
                var kind = previous["agent"]!.GetValue<string>();
                var recovered = new AgentSession(kind, previous["cwd"]!.GetValue<string>(), config.Agents[kind], journal, native);
                try
                {
                    await recovered.Start(); sessions[recovered.Id] = recovered;
                    var snapshot = recovered.Snapshot(); snapshot["recovered_from"] = previousId;
                    journal.Session(snapshot);
                    return snapshot;
                }
                catch { await recovered.DisposeAsync(); throw; }
            case "read":
                var sid = Required("session_id");
                if (sessions.TryGetValue(sid, out var active)) return active.Snapshot();
                if (observed.TryGetValue(sid, out var external)) return new JsonObject { ["session"] = external.DeepClone(), ["records"] = Discovery.Read(external) };
                var saved = journal.Sessions().OfType<JsonObject>().FirstOrDefault(x => x["id"]?.GetValue<string>() == sid);
                if (saved != null && saved["path"] != null)
                    return new JsonObject { ["session"] = saved.DeepClone(), ["records"] = Discovery.Read(saved) };
                return Sessions().FirstOrDefault(x => x?["id"]?.GetValue<string>() == sid)?.DeepClone() ?? throw new ArgumentException("Session not found");
            case "send":
                var target = Managed(Required("session_id"));
                if (target.Kind != "codex" && target.State == "running")
                {
                    await target.Interrupt();
                    var until = DateTime.UtcNow.AddSeconds(15);
                    while (target.State == "running" && DateTime.UtcNow < until) await Task.Delay(100);
                    if (target.State == "running") throw new InvalidOperationException("Interrupt not confirmed; additional instruction was not sent");
                }
                await target.Send(Required("prompt")); return new JsonObject { ["ok"] = true, ["state"] = "accepted" };
            case "interrupt":
                await Managed(Required("session_id")).Interrupt(); return new JsonObject { ["ok"] = true, ["state"] = "requested" };
            case "approve":
                await Managed(Required("session_id")).Approve(Required("approval_id"), args["allow"]!.GetValue<bool>());
                return new JsonObject { ["ok"] = true };
            case "windows": return windows.List();
            case "project": return await new ProjectTools(config).Call(args);
            case "save_file": return await FileTransfer.Save(config, args);
            case "bind_window": return new JsonObject { ["binding"] = windows.Bind(Required("hwnd"), Required("title"), args["pid"]!.GetValue<uint>()) };
            case "capture": return windows.Capture(Required("binding"));
            case "inspect_window": return windows.Inspect(Required("binding"));
            case "window_action":
                await windows.ControlAction(Required("binding"), Required("control_id"), Required("operation"));
                return new JsonObject { ["ok"] = true, ["state"] = "input_submitted" };
            case "input":
                await windows.InputText(Required("binding"), Required("control_id"), Required("text"), args["enter"]?.GetValue<bool>() ?? false);
                return new JsonObject { ["ok"] = true, ["state"] = "input_submitted" };
            default: throw new ArgumentException("Unsupported action");
        }
    }
    private AgentSession Managed(string id) => sessions.TryGetValue(id, out var s) ? s : throw new InvalidOperationException("Not a live managed session; observe or explicitly bind its window");
    public async Task Tick()
    {
        foreach (var s in sessions.Values) await s.ExpireApprovals();
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var s in sessions.Values) await s.DisposeAsync();
    }
}
