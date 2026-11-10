using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;
using System.Collections.Concurrent;

namespace Shrink.Desktop;

public sealed class DesktopWindows
{
    private readonly ConcurrentDictionary<string, (nint Hwnd, uint Pid, long Started, string Title)> bindings = new();
    private readonly SemaphoreSlim serial = new(1);
    private readonly ConcurrentDictionary<string, (string Binding, AutomationElement Element)> controls = new();
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint arg);
    private delegate bool EnumProc(nint hwnd, nint arg);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput info);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetUserObjectInformation(nint handle, int index, StringBuilder name, uint length, out uint needed);
    [DllImport("user32.dll")] private static extern nint GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW")]
    private static extern bool WTSQuerySessionInformation(nint server, int session, int information, out nint buffer, out uint bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint buffer);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int information, out int elevation, int length, out int required);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inherit, uint pid);
    // WTSINFOEXW's union is aligned to eight bytes because LEVEL1 contains LARGE_INTEGER fields.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct SessionInformation
    {
        [FieldOffset(0)] public uint Level;
        [FieldOffset(8)] public uint SessionId;
        [FieldOffset(12)] public int ConnectionState;
        [FieldOffset(16)] public int SessionFlags;
    }
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size, Tick; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Keyboard { public ushort Key, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public Keyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    private static string Title(nint hwnd) { var s = new StringBuilder(1024); GetWindowText(hwnd, s, s.Capacity); return s.ToString(); }
    public JsonArray List()
    {
        var list = new JsonArray();
        EnumWindows((h, _) =>
        {
            var title = Title(h);
            if (IsWindowVisible(h) && title.Length > 0)
            {
                GetWindowThreadProcessId(h, out var pid);
                try
                {
                    var p = Process.GetProcessById((int)pid);
                    list.Add(new JsonObject { ["hwnd"] = h.ToInt64().ToString(), ["pid"] = pid, ["process"] = p.ProcessName, ["title"] = title });
                }
                catch (ArgumentException) { }
            }
            return true;
        }, 0);
        return list;
    }
    public string Bind(string hwnd, string expectedTitle, uint expectedPid)
    {
        var h = (nint)long.Parse(hwnd);
        GetWindowThreadProcessId(h, out var pid);
        if (!IsWindowVisible(h) || pid != expectedPid || Title(h) != expectedTitle)
            throw new InvalidOperationException("Window changed; inspect again");
        RequireCompatibleElevation(pid);
        var token = Guid.NewGuid().ToString("N");
        bindings[token] = (h, pid, Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks, expectedTitle);
        return token;
    }
    private nint Validate(string token, bool input)
    {
        RequireUnlockedSession();
        if (!bindings.TryGetValue(token, out var b)) throw new InvalidOperationException("Window binding expired; inspect and bind again");
        GetWindowThreadProcessId(b.Hwnd, out var pid);
        if (!IsWindowVisible(b.Hwnd) || pid != b.Pid || Title(b.Hwnd) != b.Title ||
            Process.GetProcessById((int)pid).StartTime.ToUniversalTime().Ticks != b.Started)
            throw new InvalidOperationException("Window identity changed");
        RequireCompatibleElevation(pid);
        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0) throw new InvalidOperationException("Interactive desktop unavailable or locked");
        try
        {
            var inputName = new StringBuilder(256);
            var currentName = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, inputName, 512, out _) ||
                !GetUserObjectInformation(GetThreadDesktop(GetCurrentThreadId()), 2, currentName, 512, out _) ||
                !string.Equals(inputName.ToString(), currentName.ToString(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(inputName.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Interactive desktop unavailable or locked");
        }
        finally { CloseDesktop(desktop); }
        if (input)
        {
            var last = new LastInput { Size = (uint)Marshal.SizeOf<LastInput>() };
            if (!GetLastInputInfo(ref last) || unchecked((uint)Environment.TickCount - last.Tick) < 5000)
                throw new InvalidOperationException("User is active; desktop action paused");
        }
        return b.Hwnd;
    }
    private static bool IsElevated(uint pid)
    {
        var process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (process == 0)
            throw new InvalidOperationException("Cannot verify target elevation; desktop control refused");
        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out var token))
                throw new InvalidOperationException("Cannot verify target elevation; desktop control refused");
            try
            {
                if (!GetTokenInformation(token, 20 /* TokenElevation */, out var elevated, sizeof(int), out _))
                    throw new InvalidOperationException("Cannot verify target elevation; desktop control refused");
                return elevated != 0;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }
    private static void RequireCompatibleElevation(uint pid)
    {
        if (IsElevated(pid) && !IsElevated((uint)Environment.ProcessId))
            throw new InvalidOperationException("Target window is elevated; desktop control refused");
    }
    private static void RequireUnlockedSession()
    {
        var session = Process.GetCurrentProcess().SessionId;
        if (!WTSQuerySessionInformation(0, session, 25 /* WTSSessionInfoEx */, out var buffer, out var bytes))
            throw new InvalidOperationException("Interactive desktop unavailable or locked: session state unavailable");
        try
        {
            if (bytes < Marshal.SizeOf<SessionInformation>())
                throw new InvalidOperationException("Interactive desktop unavailable or locked: incomplete session state");
            var info = Marshal.PtrToStructure<SessionInformation>(buffer);
            if (info.Level != 1 || info.SessionId != session || info.ConnectionState != 0 || info.SessionFlags != 1)
                throw new InvalidOperationException("Interactive desktop unavailable or locked");
        }
        finally { WTSFreeMemory(buffer); }
    }
    public JsonObject Capture(string token)
    {
        var h = Validate(token, false);
        if (GetForegroundWindow() != h) throw new InvalidOperationException("Target must be visible in foreground for a reliable capture");
        GetWindowRect(h, out var r);
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        using var buffer = new MemoryStream(); bmp.Save(buffer, System.Drawing.Imaging.ImageFormat.Png);
        return new() { ["mime"] = "image/png", ["base64"] = Convert.ToBase64String(buffer.ToArray()) };
    }
    public JsonArray Inspect(string token)
    {
        foreach (var key in controls.Where(x => x.Value.Binding == token).Select(x => x.Key).ToArray()) controls.TryRemove(key, out _);
        var root = AutomationElement.FromHandle(Validate(token, false));
        var found = root.FindAll(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true));
        var result = new JsonArray();
        foreach (AutomationElement element in found)
        {
            if (result.Count >= 150) break;
            var current = element.Current;
            var id = Guid.NewGuid().ToString("N");
            controls[id] = (token, element);
            result.Add(new JsonObject { ["control_id"] = id, ["name"] = current.Name,
                ["type"] = current.ControlType.ProgrammaticName, ["enabled"] = current.IsEnabled,
                ["focused"] = current.HasKeyboardFocus, ["offscreen"] = current.IsOffscreen });
        }
        return result;
    }
    public async Task ControlAction(string token, string controlId, string action)
    {
        await serial.WaitAsync();
        try
        {
            var h = Validate(token, true);
            if (!controls.TryGetValue(controlId, out var target) || target.Binding != token)
                throw new InvalidOperationException("Inspect the target control first");
            var control = target.Element;
            if (!control.Current.IsEnabled || control.Current.IsOffscreen) throw new InvalidOperationException("Control unavailable");
            control.SetFocus();
            if (GetForegroundWindow() != h) SetForegroundWindow(h);
            await Task.Delay(150);
            if (action == "invoke")
            {
                Validate(token, true);
                if (GetForegroundWindow() != h) throw new InvalidOperationException("Foreground changed");
                if (!control.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                    throw new InvalidOperationException("This control does not support invocation");
                ((InvokePattern)pattern).Invoke();
                return;
            }
            if (action is not ("escape" or "interrupt" or "enter")) throw new ArgumentException("Unsupported window action");
            if (control.Current.ControlType != ControlType.Edit && control.Current.ControlType != ControlType.Document)
                throw new InvalidOperationException("Select the terminal or input control");
            control.SetFocus();
            await Task.Delay(150);
            Validate(token, true);
            if (GetForegroundWindow() != h || !Automation.Compare(AutomationElement.FocusedElement, control))
                throw new InvalidOperationException("Target focus changed");
            var events = new List<Input>();
            void Key(ushort key, bool up = false) => events.Add(new Input { Type = 1, Value = new() { Keyboard = new() { Key = key, Flags = up ? 2u : 0u } } });
            if (action == "interrupt") { Key(17); Key(67); Key(67, true); Key(17, true); }
            else { var key = action == "escape" ? (ushort)27 : (ushort)13; Key(key); Key(key, true); }
            if (SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<Input>()) != events.Count)
                throw new InvalidOperationException("Key delivery uncertain; inspect before retrying");
        }
        finally { serial.Release(); }
    }
    public async Task InputText(string token, string controlId, string text, bool enter)
    {
        if (text.Length > 10000) throw new ArgumentException("Desktop input too long");
        await serial.WaitAsync();
        try
        {
            var h = Validate(token, true);
            if (!controls.TryGetValue(controlId, out var target) || target.Binding != token)
                throw new InvalidOperationException("Inspect and select the exact input control first");
            var control = target.Element;
            if (!control.Current.IsEnabled || control.Current.IsOffscreen)
                throw new InvalidOperationException("Input control unavailable");
            if (control.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly)
                throw new InvalidOperationException("Selected control is read only");
            if (control.Current.ControlType != ControlType.Edit && control.Current.ControlType != ControlType.Document)
                throw new InvalidOperationException("Selected control is not a text input");
            control.SetFocus();
            if (GetForegroundWindow() != h) SetForegroundWindow(h);
            await Task.Delay(150);
            Validate(token, true);
            if (GetForegroundWindow() != h) throw new InvalidOperationException("Focus moved; no input sent");
            if (!Automation.Compare(AutomationElement.FocusedElement, control))
                throw new InvalidOperationException("Input control changed; no input sent");
            var events = new List<Input>();
            foreach (var ch in text)
            {
                events.Add(new Input { Type = 1, Value = new() { Keyboard = new() { Scan = ch, Flags = 4 } } });
                events.Add(new Input { Type = 1, Value = new() { Keyboard = new() { Scan = ch, Flags = 6 } } });
            }
            if (enter)
            {
                events.Add(new Input { Type = 1, Value = new() { Keyboard = new() { Key = 13 } } });
                events.Add(new Input { Type = 1, Value = new() { Keyboard = new() { Key = 13, Flags = 2 } } });
            }
            if (SendInput((uint)events.Count, events.ToArray(), Marshal.SizeOf<Input>()) != events.Count)
                throw new InvalidOperationException("Input delivery uncertain; inspect window before any retry");
        }
        finally { serial.Release(); }
    }
}
