using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Dusage;

/// <summary>
/// dUsage/dt's icon in the notification area: a click opens the expanded widget, right-click (or the menu key)
/// asks for the menu. It talks to the shell directly rather than pulling in Windows Forms for its NotifyIcon.
/// </summary>
sealed class TrayIcon : IDisposable
{
    const int WM_CONTEXTMENU = 0x007B, WM_CALLBACK = 0x8000 + 1, NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    const int SM_CXSMICON = 49, WS_EX_TOOLWINDOW = 0x80;
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;

    /// <summary>Broadcast when Explorer (re)starts, having forgotten every icon.</summary>
    static readonly int TaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    readonly HwndSource _window;
    readonly IntPtr _icon;
    readonly string _tip;

    public event Action? Click;
    public event Action? MenuRequested;

    public TrayIcon(string tip)
    {
        // An invisible window of its own receives the icon's messages.
        _window = new HwndSource(new HwndSourceParameters("dusage tray") { Width = 0, Height = 0, WindowStyle = 0, ExtendedWindowStyle = WS_EX_TOOLWINDOW });
        _window.AddHook(OnMessage);
        _icon = LoadIcon(GetSystemMetrics(SM_CXSMICON));
        _tip = tip;
        Add();
    }

    public void Dispose()
    {
        var data = Data(0);
        Shell_NotifyIcon(NIM_DELETE, ref data);
        DestroyIcon(_icon);
        _window.Dispose();
    }

    void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIcon(NIM_ADD, ref data);
        // Version 4 reports a click as a selection and right-click as a context-menu request, keyboard included.
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIcon(NIM_SETVERSION, ref data);
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uFlags = flags,
        uCallbackMessage = WM_CALLBACK,
        hIcon = _icon,
        szTip = _tip,
    };

    IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CALLBACK)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NIN_SELECT or NIN_KEYSELECT:
                    Click?.Invoke();
                    break;
                case WM_CONTEXTMENU:
                    MenuRequested?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == TaskbarCreated)
        {
            Add();
        }
        return IntPtr.Zero;
    }

    /// <summary>The app icon at the given size, taken from dusage.ico, which holds every common size.</summary>
    static IntPtr LoadIcon(int size)
    {
        using var stream = new MemoryStream();
        Application.GetResourceStream(new Uri("pack://application:,,,/Assets/dusage.ico")).Stream.CopyTo(stream);
        var ico = stream.ToArray();

        // An .ico file is a count, then a 16-byte entry per image. Take the exact size, else the next one up.
        int count = BitConverter.ToUInt16(ico, 4), pick = 0, best = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var width = ico[6 + 16 * i] is 0 ? 256 : ico[6 + 16 * i];
            var score = width >= size ? width - size : 1000 - width;
            if (score < best) (best, pick) = (score, i);
        }
        var entry = 6 + 16 * pick;
        var image = ico.AsSpan(BitConverter.ToInt32(ico, entry + 12), BitConverter.ToInt32(ico, entry + 8)).ToArray();
        return CreateIconFromResourceEx(image, (uint)image.Length, true, 0x00030000, size, size, 0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] static extern IntPtr CreateIconFromResourceEx(byte[] bits, uint size, bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
