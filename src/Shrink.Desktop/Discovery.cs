using System.Text.Json.Nodes;
using System.Diagnostics;

namespace Shrink.Desktop;

public static class Discovery
{
    public static JsonArray List(string agent, string? query = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = agent switch { "claude" => Path.Combine(home, ".claude", "projects"),
            "codex" => Path.Combine(home, ".codex", "sessions"), "dsh" => Path.Combine(home, ".dsh", "sessions"),
            _ => throw new ArgumentException("Unknown agent") };
        var result = new JsonArray();
        if (!Directory.Exists(root)) return result;
        foreach (var file in Directory.EnumerateFiles(root, agent == "dsh" ? "*.jsonl*" : "*.jsonl", SearchOption.AllDirectories)
                     .Where(x => !x.Contains(Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar))
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(1000))
        {
            try
            {
                using var reader = Open(file);
                string? cwd = null, sid = null;
                var match = string.IsNullOrWhiteSpace(query) || file.Contains(query, StringComparison.OrdinalIgnoreCase);
                var lines = 0;
                while (reader.ReadLine() is { } line && lines++ < 100000)
                {
                    if (line.Length > 2000000) continue;
                    if (!match && line.Contains(query!, StringComparison.OrdinalIgnoreCase)) match = true;
                    JsonNode? item;
                    try { item = JsonNode.Parse(line); } catch { continue; }
                    if (agent == "codex" && item?["type"]?.GetValue<string>() == "session_meta")
                    { cwd = item["payload"]?["cwd"]?.GetValue<string>(); sid = item["payload"]?["id"]?.GetValue<string>(); }
                    else
                    {
                        cwd ??= item?["cwd"]?.GetValue<string>();
                        sid ??= (item?["sessionId"] ?? item?["session_id"])?.GetValue<string>();
                        if (agent == "dsh" && item?["cwd"] != null) sid ??= item["id"]?.GetValue<string>();
                    }
                    if (match && cwd != null && sid != null) break;
                }
                if (!match || cwd == null) continue;
                sid ??= Path.GetFileNameWithoutExtension(file);
                result.Add(new JsonObject { ["id"] = $"external:{agent}:{sid}", ["native_id"] = sid, ["agent"] = agent,
                    ["cwd"] = cwd, ["path"] = file, ["mode"] = "observe", ["state"] = "unverified",
                    ["modified"] = File.GetLastWriteTimeUtc(file).ToString("O"), ["capabilities"] = new JsonArray("read", "bind_window") });
                if (result.Count >= 50) break;
            }
            catch (IOException) { }
        }
        return result;
    }
    private static TextReader Open(string path)
    {
        if (!path.EndsWith(".zstd", StringComparison.OrdinalIgnoreCase))
            return new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
        var start = new ProcessStartInfo("node") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8 };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add("process.stdout.write(require('node:zlib').zstdDecompressSync(require('node:fs').readFileSync(process.argv[1]),{maxOutputLength:67108864}))");
        start.ArgumentList.Add(path);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000)) { process.Kill(); throw new IOException("DSH transcript decompression timed out"); }
        if (process.ExitCode != 0) throw new IOException("DSH transcript cannot be decoded; Node 24 with zstd is required");
        return new StringReader(output.GetAwaiter().GetResult());
    }
    public static JsonArray Read(JsonObject session)
    {
        var file = session["path"]!.GetValue<string>();
        var lines = new Queue<string>();
        using var reader = Open(file);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 64000) line = "[large record omitted]";
            lines.Enqueue(line); if (lines.Count > 20) lines.Dequeue();
        }
        return new JsonArray(lines.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
    }
}
