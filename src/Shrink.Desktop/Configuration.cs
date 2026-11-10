using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shrink.Desktop;

public sealed class Configuration
{
    public string Server { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public string Name { get; set; } = Environment.MachineName;
    public Dictionary<string, AgentSettings> Agents { get; set; } = new();
    public string Dashboard { get; set; } = "";
    public string CraftTableCli { get; set; } = "";
    public string GiteaCli { get; set; } = "";
    public string GiteaHost { get; set; } = "";
    public string GiteaUser { get; set; } = "";
    public List<string> GiteaRepositories { get; set; } = [];
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShrinkDesktop");
    public static string FileName => Path.Combine(Root, "config.json");
    [System.Text.Json.Serialization.JsonIgnore]
    public string Token => Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser));
    public void SetToken(string token) => ProtectedToken = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
    public void Save()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(FileName + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(FileName + ".tmp", FileName, true);
    }
    public static Configuration Load() => File.Exists(FileName)
        ? JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FileName))! : new();
}

public sealed class AgentSettings
{
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public Dictionary<string, string> Environment { get; set; } = new();
}
