using System.Runtime.InteropServices;

namespace Dusage;

static class Native
{
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>Keeps the widget out of Alt+Tab and the taskbar.</summary>
    public static void MakeToolWindow(IntPtr hwnd) =>
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

    /// <summary>Re-asserts always-on-top without activating; Windows sometimes drops it (e.g. after the taskbar is clicked).</summary>
    public static void BringToTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Pulls the window fully onto the nearest monitor, e.g. after that monitor was unplugged.
    /// The taskbar area is allowed, so the widget can sit on it.</summary>
    public static void KeepOnScreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), ref info)) return;

        var screen = info.rcMonitor;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        var x = Math.Clamp(r.Left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var y = Math.Clamp(r.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        if (x != r.Left || y != r.Top)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime))
            : TimeSpan.Zero;
    }

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
}
