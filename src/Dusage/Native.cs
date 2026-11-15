using System.Runtime.InteropServices;

namespace Dusage;

static class Native
{
    const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
    const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;
    const int WM_GETMINMAXINFO = 0x0024, MDT_EFFECTIVE_DPI = 0;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    /// <summary>Keeps the widget out of Alt+Tab and the taskbar.</summary>
    public static void MakeToolWindow(IntPtr hwnd) =>
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);

    /// <summary>
    /// Window hook: Windows won't let an ordinary top-level window be smaller than a title bar
    /// (47 px tall at 125% scaling), which would pad a one-row widget with empty space. This lifts that floor.
    /// </summary>
    public static IntPtr AllowAnySize(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_GETMINMAXINFO)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            info.ptMinTrackSize = new POINT { X = 1, Y = 1 };
            Marshal.StructureToPtr(info, lParam, false);
            // Not marked handled: WPF reads the same struct next and sizes the window within these limits.
        }
        return IntPtr.Zero;
    }

    /// <summary>Re-asserts always-on-top without activating; Windows sometimes drops it (e.g. after the taskbar is clicked).</summary>
    public static void BringToTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>Pulls the window fully onto the nearest monitor, e.g. after that monitor was unplugged.
    /// The taskbar area is allowed unless <paramref name="clearOfTaskbar"/>, so the widget can sit on it.</summary>
    public static void KeepOnScreen(IntPtr hwnd, bool clearOfTaskbar = false)
    {
        if (!GetWindowRect(hwnd, out var r)) return;
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), ref info)) return;

        var screen = clearOfTaskbar ? info.rcWork : info.rcMonitor;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        var x = Math.Clamp(r.Left, screen.Left, Math.Max(screen.Left, screen.Right - width));
        var y = Math.Clamp(r.Top, screen.Top, Math.Max(screen.Top, screen.Bottom - height));
        if (x != r.Left || y != r.Top)
            SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Which quarter of its monitor the window sits in, so it can grow away from the nearest screen edges.</summary>
    public static (bool Right, bool Bottom) Corner(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return (false, false);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST), ref info)) return (false, false);
        var m = info.rcMonitor;
        return ((r.Left + r.Right) / 2 > (m.Left + m.Right) / 2, (r.Top + r.Bottom) / 2 > (m.Top + m.Bottom) / 2);
    }

    /// <summary>Gives the window the keyboard focus; allowed right after the user clicked dusage's tray icon.</summary>
    public static void Foreground(IntPtr hwnd) => SetForegroundWindow(hwnd);

    // ---- in the taskbar ------------------------------------------------------------------------------------

    /// <summary>A rectangle on screen in pixels: a service's spot on the taskbar.</summary>
    public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);

    /// <summary>
    /// Moves the window next to a spot on the taskbar, on the screen side of it, as the tray's own panels open: above a
    /// taskbar at the bottom, below one at the top, beside one at either side, and inside the monitor's work area.
    /// Returns the corner it should keep still if it changes size, the one nearest the spot.
    /// </summary>
    public static (bool Right, bool Bottom) PlaceNear(IntPtr hwnd, PixelRect icon)
    {
        var anchor = new RECT { Left = icon.Left, Top = icon.Top, Right = icon.Right, Bottom = icon.Bottom };
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromRect(ref anchor, MONITOR_DEFAULTTONEAREST);
        if (!GetWindowRect(hwnd, out var r) || !GetMonitorInfo(monitor, ref info)) return (true, true);
        var work = info.rcWork;
        var gap = GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? (int)Math.Round(8 * dpi / 96.0) : 8;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        int middleX = (icon.Left + icon.Right) / 2 - width / 2, middleY = (icon.Top + icon.Bottom) / 2 - height / 2;

        // Which side of the work area the spot is on says where the taskbar is; on an auto-hiding taskbar (inside
        // the work area) the window goes above it.
        int x, y;
        bool right = true, bottom = true;
        if (icon.Right <= work.Left) (x, y, right) = (work.Left + gap, middleY, false);
        else if (icon.Left >= work.Right) (x, y) = (work.Right - gap - width, middleY);
        else if (icon.Bottom <= work.Top) (x, y, bottom) = (middleX, work.Top + gap, false);
        else (x, y) = (middleX, Math.Min(icon.Top, work.Bottom) - gap - height);

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - gap - width));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - gap - height));
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        return (right, bottom);
    }

    /// <summary>The bottom-right corner of the main monitor's work area, for a window to open from when the taskbar
    /// can't say where the services are.</summary>
    public static PixelRect WorkAreaCorner()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var origin = new RECT();
        GetMonitorInfo(MonitorFromRect(ref origin, MONITOR_DEFAULTTOPRIMARY), ref info);
        return new PixelRect(info.rcWork.Right, info.rcWork.Bottom, info.rcWork.Right, info.rcWork.Bottom);
    }

    /// <summary>Whether the taskbar is light (Windows' "default app mode" for the system set to light).</summary>
    public static bool LightTaskbar()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int light && light != 0;
    }

    /// <summary>Keeps a window out of the way: it never takes the focus, and the mouse goes through it.</summary>
    public static void MakeHoverOnly(IntPtr hwnd) =>
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);

    public static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime))
            : TimeSpan.Zero;
    }

    [DllImport("kernel32.dll")]
    public static extern bool AttachConsole(int processId);

    // ---- Windows Credential Manager: where dUsage/dt's own GitHub sign-in is kept -----------------------------

    const uint CRED_TYPE_GENERIC = 1, CRED_PERSIST_LOCAL_MACHINE = 2;

    /// <summary>A secret saved under <paramref name="target"/> in the current user's Credential Manager, or null.</summary>
    public static string? ReadSecret(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<CREDENTIAL>(pointer);
            if (credential.CredentialBlobSize == 0) return null;
            var blob = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, blob, 0, blob.Length);
            return System.Text.Encoding.UTF8.GetString(blob);
        }
        finally
        {
            CredFree(pointer);
        }
    }

    /// <summary>Saves a secret for this user on this PC; Windows encrypts it, and it doesn't roam.</summary>
    public static void WriteSecret(string target, string userName, string secret)
    {
        var blob = System.Text.Encoding.UTF8.GetBytes(secret);
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var credential = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                CredentialBlob = handle.AddrOfPinnedObject(),
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
            };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            handle.Free();
        }
    }

    public static void DeleteSecret(string target) => CredDelete(target, CRED_TYPE_GENERIC, 0);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO { public uint cbSize, dwTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MINMAXINFO { public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(IntPtr buffer);

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
}
