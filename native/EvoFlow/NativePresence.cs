using System.Runtime.InteropServices;
using System.Text;
using Microsoft.UI.Windowing;

namespace EvoFlow;

internal sealed class NativePresence : IDisposable
{
    private const uint TrayMessage = 0x8001;
    private static readonly Guid TrayGuid = new("47a68be2-512f-4de8-a3da-bdb92fcc1786");
    private readonly MainWindow window;
    private readonly nint hwnd;
    private readonly SubclassProc callback;
    private readonly Action toggle, displays;
    private readonly Action<int> menu;
    private readonly uint taskbarCreated;
    private readonly nint ownedIcon;
    private bool hidden, disposed, trayVersion4;
    private uint shortcut = 0x45;
    private string status = "Starting";
    private NotifyData tray;
    public bool IsMenuOpen { get; private set; }

    public NativePresence(MainWindow window, Action toggle, Action<int> menu, Action displays)
    {
        this.window = window; this.toggle = toggle; this.menu = menu; this.displays = displays;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window); callback = WindowProc;
        SetWindowSubclass(hwnd, callback, 1, 0);
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // The XAML shell supplies its own stroke and rounded region. Suppress the DWM
        // frame, not the system focus visuals inside keyboard-accessible controls.
        long style = GetWindowLongPtr(hwnd, -16).ToInt64();
        SetWindowLongPtr(hwnd, -16, (nint)(style & ~0x00C40000L));
        long extendedStyle = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (nint)(extendedStyle & ~0x00020301L));
        var margins = new Margins();
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        SetWindowPos(hwnd, 0, 0, 0, 0, 0, 0x0037);
        UseDarkTitleBar(hwnd);
        uint noBorder = 0xFFFFFFFE, noCorners = 1;
        DwmSetWindowAttribute(hwnd, 34, ref noBorder, sizeof(uint));
        DwmSetWindowAttribute(hwnd, 33, ref noCorners, sizeof(uint));
        ownedIcon = LoadImage(0, Path.Combine(AppContext.BaseDirectory, "Assets", "TrayIcon.ico"), 1, 0, 0, 0x0050);
        tray = new NotifyData
        {
            size = (uint)Marshal.SizeOf<NotifyData>(), hwnd = hwnd, id = 1,
            flags = 0xA7, callbackMessage = TrayMessage,
            icon = ownedIcon != 0 ? ownedIcon : LoadIcon(0, (nint)32512),
            guid = TrayGuid, tip = "EvoFlow · Starting · Ctrl+Alt+E", info = "", infoTitle = ""
        };
        AddTrayIcon();
    }
    private void AddTrayIcon()
    {
        if (disposed) return;
        if (Shell_NotifyIcon(0, ref tray)) { tray.timeout = 4; trayVersion4 = Shell_NotifyIcon(4, ref tray); }
    }
    public bool SetHotkey(uint key)
    {
        UnregisterHotKey(hwnd, 1); shortcut = key;
        bool registered = RegisterHotKey(hwnd, 1, 0x4003, key);
        UpdateStatus(status);
        return registered;
    }
    public void UpdateStatus(string value)
    {
        status = value;
        tray.tip = $"EvoFlow · {status} · Ctrl+Alt+{(char)shortcut}";
        if (tray.tip.Length > 127) tray.tip = tray.tip[..127];
        if (!disposed) Shell_NotifyIcon(1, ref tray);
    }
    public static void UseDarkTitleBar(nint handle)
    {
        uint enabled = 1;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(uint));
    }
    public void RoundCorners(bool expanded)
    {
        if (!GetWindowRect(hwnd, out var rect)) return;
        double scale = GetDpiForWindow(hwnd) / 96.0;
        int diameter = (int)(44 * scale);
        // Place the upper arcs above the window: the island joins the screen edge
        // flush while its lower corners stay rounded. The native region also clips hit testing.
        var region = CreateRoundRectRgn(0, -diameter / 2, rect.right - rect.left, rect.bottom - rect.top, diameter, diameter);
        if (region != 0 && SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }
    public bool IsPointerWithinWindow(int toleranceDip)
    {
        if (!IsWindowVisible(hwnd) || !GetCursorPos(out var pointer) || !GetWindowRect(hwnd, out var rect)) return false;
        int tolerance = (int)(toleranceDip * GetDpiForWindow(hwnd) / 96.0);
        return pointer.X >= rect.left - tolerance && pointer.X < rect.right + tolerance && pointer.Y >= rect.top - tolerance && pointer.Y < rect.bottom + tolerance;
    }
    public void ShowWithoutActivation() { if (hidden) { window.AppWindow.Show(false); hidden = false; } }
    public static double DpiForArea(DisplayArea area)
    {
        var monitor = MonitorFromPoint(new Point { X = area.WorkArea.X + 1, Y = area.WorkArea.Y + 1 }, 2);
        return GetDpiForMonitor(monitor, 0, out var x, out _) == 0 ? x / 96.0 : 1;
    }
    public void AvoidFullscreen(bool expanded, bool topmost)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd || expanded) { ShowWithoutActivation(); return; }
        if (foreground == 0 || !GetWindowRect(foreground, out var rect)) return;
        var monitor = MonitorFromWindow(foreground, 2);
        var info = new MonitorInfo { size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        var className = new StringBuilder(256); GetClassName(foreground, className, className.Capacity);
        bool desktop = className.ToString() is "Progman" or "WorkerW";
        bool fullscreen = !desktop && topmost && monitor == MonitorFromWindow(hwnd, 2) && rect.left <= info.monitor.left && rect.top <= info.monitor.top && rect.right >= info.monitor.right && rect.bottom >= info.monitor.bottom;
        if (fullscreen && !hidden) { window.AppWindow.Hide(); hidden = true; }
        else if (!fullscreen) ShowWithoutActivation();
    }
    private void ShowTrayMenu()
    {
        var popup = CreatePopupMenu();
        if (popup == 0) return;
        IsMenuOpen = true;
        int command = 0;
        try
        {
            AppendMenu(popup, 0, 1, "Open EvoFlow");
            AppendMenu(popup, 0, 5, "Memory and learned skills");
            AppendMenu(popup, 0, 2, "Pause / resume");
            AppendMenu(popup, 0, 3, "Settings");
            AppendMenu(popup, 0x800, 0, "");
            AppendMenu(popup, 0, 4, "Quit EvoFlow");
            var identifier = new NotifyIdentifier { size = (uint)Marshal.SizeOf<NotifyIdentifier>(), hwnd = hwnd, id = 1, guid = TrayGuid };
            Point point;
            if (Shell_NotifyIconGetRect(ref identifier, out var iconRect) == 0) point = new Point { X = iconRect.left, Y = iconRect.top };
            else GetCursorPos(out point);
            ShowWithoutActivation();
            SetForegroundWindow(hwnd);
            command = TrackPopupMenu(popup, 0x180, point.X, point.Y, 0, hwnd, 0);
            PostMessage(hwnd, 0, 0, 0);
        }
        finally { DestroyMenu(popup); IsMenuOpen = false; }
        if (command > 0) menu(command);
        else Shell_NotifyIcon(3, ref tray);
    }
    private nint WindowProc(nint handle, uint message, nuint wparam, nint lparam, nuint id, nuint data)
    {
        // Preserve the proposed full window rectangle as the client rectangle. Styling
        // the DWM border alone leaves the WinUI host's nonclient inset visible.
        if (message == 0x0083) return 0; // WM_NCCALCSIZE
        if (message == 0x0312) { toggle(); return 0; }
        if (message == taskbarCreated) { AddTrayIcon(); return 0; }
        if (message == 0x007E || message == 0x02E0) window.DispatcherQueue.TryEnqueue(() => displays());
        if (message == TrayMessage)
        {
            uint notification = (uint)(lparam.ToInt64() & 0xFFFF);
            if (notification is 0x400 or 0x401 || (!trayVersion4 && notification == 0x0202)) { menu(1); return 0; }
            if (notification == 0x007B || (!trayVersion4 && notification == 0x0205)) { ShowTrayMenu(); return 0; }
        }
        return DefSubclassProc(handle, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        UnregisterHotKey(hwnd, 1); Shell_NotifyIcon(2, ref tray); RemoveWindowSubclass(hwnd, callback, 1);
        if (ownedIcon != 0) DestroyIcon(ownedIcon);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int left, top, right, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int left, right, top, bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int size; public Rect monitor, work; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] private struct NotifyIdentifier { public uint size; public nint hwnd; public uint id; public Guid guid; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData { public uint size; public nint hwnd; public uint id, flags, callbackMessage; public nint icon; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string tip; public uint state, stateMask; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string info; public uint timeout; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string infoTitle; public uint infoFlags; public Guid guid; public nint balloonIcon; }
    private delegate nint SubclassProc(nint hwnd, uint msg, nuint wp, nint lp, nuint id, nuint data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(nint h, SubclassProc p, nuint id, nuint d);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(nint h, SubclassProc p, nuint id);
    [DllImport("comctl32.dll")] private static extern nint DefSubclassProc(nint h, uint m, nuint w, nint l);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint h, int id, uint mod, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out Rect r);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint h);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint h, uint attribute, ref uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(nint h, ref Margins margins);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint h, nint region, bool redraw);
    [DllImport("gdi32.dll")] private static extern nint CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint h);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(Point p, uint flags);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint h, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint h, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint h, ref MonitorInfo i);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint h, StringBuilder name, int count);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint h, nint name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint LoadImage(nint h, string name, uint type, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint h, uint message, nuint w, nint l);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint command, ref NotifyData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref NotifyIdentifier identifier, out Rect rect);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint h, uint flags, nuint id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint h, uint flags, int x, int y, int reserved, nint hwnd, nint rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint h);
}
