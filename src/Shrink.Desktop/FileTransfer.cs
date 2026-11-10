using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Shrink.Desktop;

public static class FileTransfer
{
    public static async Task<JsonObject> Save(Configuration config, JsonObject args)
    {
        var name = args["name"]!.GetValue<string>();
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("Invalid attachment filename");
        var size = args["size"]!.GetValue<long>();
        if (size < 0 || size > 50 * 1024 * 1024) throw new ArgumentException("Attachment exceeds 50 MiB");
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var target = Path.Combine(desktop, name);
        if (File.Exists(target)) throw new IOException("Desktop file already exists; not overwritten");
        var temp = Path.Combine(Configuration.Root, "download-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
        client.DefaultRequestHeaders.Add("X-Device-Id", config.DeviceId);
        try
        {
            using var response = await client.GetAsync(config.Server + "/files/" + Uri.EscapeDataString(args["file_id"]!.GetValue<string>()), HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer)) > 0)
                {
                    total += count;
                    if (total > size) throw new IOException("Attachment length mismatch");
                    await output.WriteAsync(buffer.AsMemory(0, count));
                }
                if (total != size) throw new IOException("Incomplete attachment");
            }
            await using (var file = File.OpenRead(temp))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(file)).ToLowerInvariant();
                if (hash != args["sha256"]!.GetValue<string>()) throw new IOException("Attachment digest mismatch");
            }
            File.Move(temp, target, false);
            return new() { ["ok"] = true, ["path"] = target, ["size"] = size, ["state"] = "saved" };
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
