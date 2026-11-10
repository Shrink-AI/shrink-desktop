using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Shrink.Desktop;

public sealed class ProjectTools(Configuration config)
{
    private static readonly HashSet<string> CraftTools = ["list_spaces", "get_space_overview", "list_space_members", "list_work_items",
        "get_work_item", "list_documents", "get_document", "list_document_folders", "search_knowledge", "get_ai_suggestions",
        "create_work_item", "update_work_item", "create_document", "update_document_metadata", "update_markdown_document",
        "analyze_ai_suggestions", "delete_work_item", "delete_document"];
    public async Task<JsonNode> Call(JsonObject args)
    {
        var service = args["service"]!.GetValue<string>();
        var body = args["body"]!.AsObject();
        var command = new List<string>();
        string executable;
        string? stdin = null;
        if (service == "crafttable")
        {
            if (!File.Exists(config.CraftTableCli)) throw new InvalidOperationException("CraftTable CLI not configured");
            executable = "node"; command.Add(config.CraftTableCli);
            var tool = body["tool"]!.GetValue<string>();
            var parameters = body["arguments"]?.AsObject() ?? new();
            if (tool == "tools") command.Add("tools");
            else
            {
                if (!CraftTools.Contains(tool)) throw new ArgumentException("Unsupported CraftTable tool");
                if (tool != "list_spaces" && string.IsNullOrWhiteSpace(parameters["spaceId"]?.GetValue<string>()))
                    throw new ArgumentException("Explicit spaceId required; discover spaces first");
                command.AddRange(["call", tool, parameters.ToJsonString()]);
            }
        }
        else if (service == "gitea")
        {
            executable = config.GiteaCli;
            if (!File.Exists(executable)) throw new InvalidOperationException("Gitea CLI not configured");
            var endpoint = body["endpoint"]!.GetValue<string>().TrimStart('/');
            var method = body["method"]?.GetValue<string>().ToUpperInvariant() ?? "GET";
            if (endpoint.Contains("..") || endpoint.IndexOfAny(['%', '\\', '#', ':']) >= 0) throw new ArgumentException("Invalid Gitea endpoint");
            var account = endpoint is "user" or "version" or "user/repos";
            var repo = config.GiteaRepositories.Any(r => Regex.IsMatch(endpoint, "^repos/" + Regex.Escape(r) + @"(?:/[A-Za-z0-9_.\-/]+)?(?:\?[A-Za-z0-9_=&.\-]+)?$"));
            if (!repo && !account || account && method != "GET") throw new ArgumentException("Endpoint not authorized in local configuration");
            if (!new[] { "GET", "POST", "PATCH", "PUT", "DELETE" }.Contains(method)) throw new ArgumentException("Invalid method");
            if (method == "DELETE" && body["confirm"]?.GetValue<bool>() != true) throw new ArgumentException("Deletion requires explicit confirmation");
            command.AddRange(["api", endpoint, "--host", config.GiteaHost, "--user", config.GiteaUser, "--no-input", "--method", method]);
            if (body["body"] != null) { stdin = body["body"]!.ToJsonString(); command.AddRange(["--input", "-"]); }
        }
        else throw new ArgumentException("Unsupported project service");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in command) start.ArgumentList.Add(arg);
        start.Environment.Remove("CRAFTTABLE_MCP_TOKEN");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (stdin != null) await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        catch (TimeoutException) { process.Kill(true); throw new TimeoutException("CLI result unknown; inspect before retrying writes"); }
        if (process.ExitCode != 0) throw new InvalidOperationException((await stderr)[..Math.Min((await stderr).Length, 2000)]);
        var text = await stdout;
        if (text.Length > 2_000_000) throw new InvalidOperationException("Result too large; use a narrower query");
        var result = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        return service == "gitea" ? new JsonObject { ["ok"] = true, ["result"] = result } : result!;
    }
}
