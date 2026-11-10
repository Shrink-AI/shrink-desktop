using System.Text.Json;
using System.Security.Principal;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new Fixture());
    }
}

internal sealed class Fixture : Form
{
    private readonly bool elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    private readonly TextBox input = new() { Dock = DockStyle.Top, AccessibleName = "Fixture input" };
    private readonly TextBox output = new() { Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, AccessibleName = "Fixture output" };
    private readonly Button invoke = new() { Dock = DockStyle.Bottom, Text = "Invoke fixture", AccessibleName = "Invoke fixture" };
    private int invoked, entered, interrupted, escaped;
    private readonly string path = Path.Combine(Path.GetTempPath(), "shrink-desktop-window-fixture.json");

    public Fixture()
    {
        Text = elevated ? "Shrink elevated control fixture" : "Shrink desktop control fixture"; Width = 650; Height = 350;
        Controls.Add(output); Controls.Add(input); Controls.Add(invoke);
        invoke.Click += (_, _) => { invoked++; Save(); };
        input.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { entered++; e.SuppressKeyPress = true; }
            if (e.Control && e.KeyCode == Keys.C) { interrupted++; e.SuppressKeyPress = true; }
            if (e.KeyCode == Keys.Escape) { escaped++; Text = "Shrink fixture identity changed"; e.SuppressKeyPress = true; }
            Save();
        };
        input.TextChanged += (_, _) => Save();
        Shown += (_, _) => { input.Focus(); Save(); };
    }

    private void Save()
    {
        var text = JsonSerializer.Serialize(new { pid = Environment.ProcessId, hwnd = Handle.ToInt64().ToString(),
            title = Text, elevated, input = input.Text, invoked, entered, interrupted, escaped });
        File.WriteAllText(path + ".tmp", text);
        File.Move(path + ".tmp", path, true);
        output.Text = text;
    }
}
