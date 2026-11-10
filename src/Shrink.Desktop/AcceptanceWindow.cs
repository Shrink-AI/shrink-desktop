namespace Shrink.Desktop;

/// <summary>An isolated interactive Agent window for end-to-end desktop control acceptance.</summary>
public sealed class AcceptanceWindow : Form
{
    private readonly TextBox prompt = new() { Multiline = false, Dock = DockStyle.Bottom, AccessibleName = "Agent 指令" };
    private readonly TextBox output = new() { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, AccessibleName = "Agent 输出" };
    private AgentSession? session;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    public AcceptanceWindow()
    {
        Text = "Shrink isolated Claude acceptance";
        Width = 850; Height = 500; StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(output); Controls.Add(prompt);
        Shown += async (_, _) =>
        {
            try
            {
                var root = Path.Combine(Path.GetTempPath(), "shrink-external-agent-acceptance");
                Directory.CreateDirectory(root);
                session = new AgentSession("claude", root, Configuration.Load().Agents["claude"], new Journal(Path.Combine(root, Guid.NewGuid().ToString("N") + ".db")));
                await session.Start();
                output.Text = "Ready. This is an independent Claude session, not a managed device session.";
                timer.Start(); prompt.Focus();
            }
            catch (Exception ex) { output.Text = ex.ToString(); }
        };
        prompt.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || session == null) return;
            e.SuppressKeyPress = true;
            var text = prompt.Text; prompt.Clear();
            try { await session.Send(text); } catch (Exception ex) { output.Text = ex.Message; }
        };
        timer.Tick += (_, _) =>
        {
            if (session == null || session.NativeId == "") return;
            output.Text = session.Snapshot().ToJsonString(new() { WriteIndented = true });
        };
        FormClosed += async (_, _) => { timer.Dispose(); if (session != null) await session.DisposeAsync(); };
    }
}
