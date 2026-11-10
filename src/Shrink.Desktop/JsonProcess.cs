using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public sealed class JsonProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim write = new(1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> requests = new();
    private int nextId;
    private bool started;
    public Func<JsonObject, Task>? OnMessage { get; set; }
    public Action<string>? OnError { get; set; }
    public Task? Reader { get; private set; }
    public int Pid => process.Id;
    public bool Exited => process.HasExited;
    public JsonProcess(AgentSettings settings, string cwd, IEnumerable<string> args)
    {
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException(cwd);
        var exe = settings.Executable;
        var allArgs = settings.Arguments.Concat(args).ToList();
        // npm Windows launchers contain a node entrypoint; run that directly rather than shell-quoting user input.
        if (Path.GetExtension(exe).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var script = File.ReadAllText(exe);
            var matches = System.Text.RegularExpressions.Regex.Matches(script, "\"%dp0%\\\\([^\"]+\\.(?:js|mjs|cjs))\"");
            if (matches.Count == 0)
                matches = System.Text.RegularExpressions.Regex.Matches(script, "\"%~dp0\\\\([^\"]+\\.(?:js|mjs|cjs))\"");
            if (matches.Count == 0) throw new InvalidOperationException("Configure a native executable and its explicit entrypoint arguments for this launcher");
            allArgs.Insert(0, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, matches[0].Groups[1].Value)));
            exe = "node";
        }
        var start = new ProcessStartInfo(exe) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in allArgs) start.ArgumentList.Add(arg);
        foreach (var pair in settings.Environment) start.Environment[pair.Key] = pair.Value;
        process = new Process { StartInfo = start };
    }
    public void Start()
    {
        process.Start();
        started = true;
        Reader = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    JsonObject? msg;
                    try { msg = JsonNode.Parse(line) as JsonObject; } catch { continue; }
                    if (msg == null) continue;
                    var id = msg["id"]?.ToJsonString();
                    if (id != null && msg["method"] == null && requests.TryRemove(id, out var completion))
                    {
                        if (msg["error"] != null) completion.TrySetException(new InvalidOperationException(msg["error"]!.ToJsonString()));
                        else completion.TrySetResult(msg["result"]?.DeepClone());
                    }
                    else if (OnMessage != null) await OnMessage(msg);
                }
            }
            finally
            {
                foreach (var item in requests.Values) item.TrySetException(new IOException("Agent process disconnected"));
                requests.Clear();
                if (OnMessage != null) await OnMessage(new JsonObject { ["type"] = "process_exit" });
            }
        });
        _ = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) OnError?.Invoke(line);
        });
    }
    public async Task Send(JsonObject obj)
    {
        await write.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(obj.ToJsonString()); await process.StandardInput.FlushAsync(); }
        finally { write.Release(); }
    }
    public async Task<JsonNode?> Call(string method, JsonObject? parameters = null, int seconds = 45)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id.ToString()] = tcs;
        try
        {
            await Send(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters ?? new() });
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(seconds));
        }
        finally { requests.TryRemove(id.ToString(), out _); }
    }
    public Task Notify(string method, JsonObject args) => Send(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = args });
    public async ValueTask DisposeAsync()
    {
        if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
        process.Dispose();
    }
}
