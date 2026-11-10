namespace Shrink.Desktop;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--pair-stdin"))
        {
            var request = System.Text.Json.Nodes.JsonNode.Parse(Console.ReadLine()!)!;
            Connection.Pair(Configuration.Load(), request["server"]!.GetValue<string>(), request["code"]!.GetValue<string>()).GetAwaiter().GetResult();
            Console.WriteLine("Device paired");
            return;
        }
        if (args.Contains("--self-test")) { Environment.Exit(SelfTest.Run().GetAwaiter().GetResult()); return; }
        if (args.Contains("--agent-test")) { Environment.Exit(SelfTest.Agent(args.Last()).GetAwaiter().GetResult()); return; }
        if (args.Contains("--terminal-test")) { Application.Run(new AcceptanceWindow()); return; }
        using var mutex = new Mutex(true, "Local\\ShrinkDesktopController", out var first);
        if (!first) return;
        var config = Configuration.Load();
        var journal = new Journal(Path.Combine(Configuration.Root, "state.db"));
        var controller = new Controller(config, journal);
        var connection = new Connection(config, controller, journal);
        using var cancel = new CancellationTokenSource();
        var worker = connection.Run(cancel.Token);
        Application.Run(new Tray(config, controller, connection, cancel));
        cancel.Cancel();
        try { worker.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

public sealed class Tray : ApplicationContext
{
    public Tray(Configuration config, Controller controller, Connection connection, CancellationTokenSource stop)
    {
        var icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "石英电脑控制", Visible = true };
        var menu = new ContextMenuStrip();
        var status = menu.Items.Add("未连接");
        var pause = new ToolStripMenuItem("暂停远程控制") { CheckOnClick = true };
        pause.CheckedChanged += (_, _) => controller.Paused = pause.Checked;
        menu.Items.Add(pause);
        menu.Items.Add("配对设备", null, (_, _) =>
        {
            using var form = new Form { Text = "石英设备配对", Width = 480, Height = 230, StartPosition = FormStartPosition.CenterScreen };
            var server = new TextBox { Left = 20, Top = 30, Width = 420, Text = config.Server, PlaceholderText = "https://服务器/desktop" };
            var code = new TextBox { Left = 20, Top = 80, Width = 420, PlaceholderText = "一次性配对码" };
            var button = new Button { Text = "配对", Left = 340, Top = 125 };
            form.Controls.AddRange([server, code, button]);
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await Connection.Pair(config, server.Text, code.Text); form.Close(); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "配对失败"); button.Enabled = true; }
            };
            form.ShowDialog();
        });
        menu.Items.Add("任务与日志", null, (_, _) =>
        {
            using var f = new Form { Text = "本机 Agent 会话", Width = 900, Height = 600 };
            f.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both,
                Text = controller.Sessions().ToJsonString(new() { WriteIndented = true }) });
            f.ShowDialog();
        });
        var startup = new ToolStripMenuItem("登录时启动") { CheckOnClick = true };
        using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            startup.Checked = key?.GetValue("ShrinkDesktop") != null;
        startup.CheckedChanged += (_, _) =>
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (startup.Checked) key.SetValue("ShrinkDesktop", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("ShrinkDesktop", false);
        };
        menu.Items.Add(startup);
        menu.Items.Add("退出并停止托管 Agent", null, (_, _) => { stop.Cancel(); icon.Visible = false; ExitThread(); });
        icon.ContextMenuStrip = menu;
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => status.Text = connection.Status;
        timer.Start();
        Application.ApplicationExit += (_, _) => { timer.Dispose(); icon.Dispose(); };
    }
}
