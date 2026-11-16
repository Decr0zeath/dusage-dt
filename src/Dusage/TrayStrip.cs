using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Dusage;

/// <summary>
/// One service's spot in the tray: its logo, with its short window's number (5-hour) at the top left and its long
/// window's (weekly) at the top right. An empty number leaves its corner empty. <see cref="Key"/> null is dUsage/dt's
/// own mark, shown while there are no services. <see cref="Name"/> is what it says to a screen reader.
/// </summary>
readonly record struct TrayCell(string? Key, string Left, string Right, Color LeftInk, Color RightInk, bool Faded, string Name);

/// <summary>
/// The tray: the services, built into the taskbar just left of the notification area. It's a window of dUsage/dt's made a child
/// of Explorer's taskbar, which shortens the task buttons' band to make room, as TrafficMonitor's taskbar window does.
/// Being part of the taskbar, it hides when the taskbar does, slides away with it when it auto-hides, and stays under
/// fullscreen games.
///
/// It runs on a thread of its own: a window inside another process's window shares its input with that process's
/// thread, so a busy moment on the app's main thread can't stall the taskbar, nor the taskbar the app. Its events are
/// raised on the thread that created it.
/// </summary>
sealed class TrayStrip : IDisposable
{
    const string ClassName = "dusage tray strip";
    const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_EX_LAYERED = 0x80000;
    const int WM_DESTROY = 0x0002, WM_MOUSEACTIVATE = 0x0021, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
        WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205, WM_MOUSELEAVE = 0x02A3, MA_NOACTIVATE = 3;
    const uint SWP_NOACTIVATE = 0x10, TME_LEAVE = 0x2, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_SKIPOWNPROCESS = 0x2;
    const int ULW_ALPHA = 0x2, IDC_ARROW = 32512;

    // In device-independent pixels, as the taskbar's own scaling makes them.
    const double NumberSize = 10, NumberLine = 10, LogoSize = 16, Gap = 1, NumberGap = 3, Padding = 8, End = 0;

    static readonly ConcurrentDictionary<IntPtr, TrayStrip> Windows = new();
    static readonly WndProc Proc = OnMessage;
    static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static bool _registered;

    readonly Dispatcher _main, _own;
    readonly Thread _thread;
    readonly WinEventProc _onMoved;

    // The rest belongs to the strip's thread.
    IntPtr _hwnd, _taskbar, _host, _tasks, _notify, _hook;
    /// <summary>The task band's length along the taskbar as Explorer laid it out, and as this window last made it.</summary>
    int _tasksFull = -1, _tasksSet = -1;
    bool _horizontal, _layoutQueued, _tipOpen, _tracking, _pressed;
    uint _dpi;
    TrayCell[] _cells = [];
    bool _light;
    int? _hover;
    /// <summary>Each cell's start and length along the strip, in pixels, and the strip's size.</summary>
    (int Start, int Length)[] _spans = [];
    /// <summary>Each cell's number slots' width, in device-independent pixels.</summary>
    double[] _slots = [];
    /// <summary>What the spans and slots were measured for, so the periodic check doesn't measure text again.</summary>
    (TrayCell[] Cells, uint Dpi, int Thickness, bool Horizontal, int Length)? _measured;
    int _width, _height;
    DispatcherTimer _check = null!, _tipDelay = null!;

    /// <summary>A cell was clicked, or right-clicked (or the mouse rested on one, or left), with its place on screen.</summary>
    public event Action<int, Native.PixelRect>? Click, MenuRequested, TipOpening;
    public event Action? TipClosing;

    public TrayStrip()
    {
        _main = Dispatcher.CurrentDispatcher;
        _onMoved = OnMoved;
        Dispatcher? own = null;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            own = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "dusage tray strip" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
        _own = own!;
        _own.InvokeAsync(Start);
    }

    /// <summary>Gives the task buttons their room back and takes the strip off the taskbar.</summary>
    public void Dispose()
    {
        try
        {
            _own.Invoke(() =>
            {
                _check.Stop();
                _tipDelay.Stop();
                Forget();
            }, TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { }
        _own.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>What to show: a cell per service, drawn for a light or dark taskbar.</summary>
    public void Show(TrayCell[] cells, bool light) => _own.InvokeAsync(() =>
    {
        _cells = cells;
        _light = light;
        Layout(repaint: true);
        if (_hwnd != IntPtr.Zero) SetWindowText(_hwnd, Spoken());
    });

    /// <summary>Where the strip is on screen; null while it isn't on the taskbar.</summary>
    public Native.PixelRect? Bounds =>
        Volatile.Read(ref _hwnd) is var hwnd && hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && GetWindowRect(hwnd, out var r) && r.Right > r.Left
            ? new Native.PixelRect(r.Left, r.Top, r.Right, r.Bottom)
            : null;

    // ---- on the strip's thread -----------------------------------------------------------------------------

    void Start()
    {
        if (!_registered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                hInstance = GetModuleHandle(null),
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            _registered = true;
        }
        // Explorer restarting, the taskbar moving to another edge or monitor, its scaling changing: caught up with
        // here. Its buttons being laid out again is caught as it happens, in OnMoved. The first Show moves in.
        _check = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Layout(), _own);
        _tipDelay = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Normal, (_, _) => OpenTip(), _own) { IsEnabled = false };
    }

    /// <summary>
    /// The strip's window, in the taskbar: the task buttons' band on Windows 10, which it shortens to make room;
    /// failing that (Windows 11 draws its taskbar itself), the taskbar, beside the notification area. Made again once
    /// Explorer restarts.
    /// </summary>
    bool Find()
    {
        if (_hwnd != IntPtr.Zero && IsWindow(_hwnd) && IsWindow(_host)) return true;
        Forget();

        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return false;
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var rebar = FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        var tasks = rebar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
        if (tasks == IntPtr.Zero || !IsWindowVisible(tasks)) tasks = IntPtr.Zero;
        if (tasks == IntPtr.Zero && (notify == IntPtr.Zero || !IsWindowVisible(notify))) return false;

        var host = tasks != IntPtr.Zero ? rebar : taskbar;
        var hwnd = CreateWindowEx(WS_EX_LAYERED, ClassName, AppInfo.Name, WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0, 0, 0, 0, host, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) return false;
        Windows[hwnd] = this;
        (_taskbar, _host, _tasks, _notify) = (taskbar, host, tasks, notify);
        Volatile.Write(ref _hwnd, hwnd);
        SetWindowText(hwnd, Spoken());

        // Explorer moves the task band whenever it lays out the buttons again; hear of it rather than poll for it.
        var thread = GetWindowThreadProcessId(taskbar, out var process);
        _hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _onMoved, process, thread, WINEVENT_SKIPOWNPROCESS);
        _dpi = 0; // measure and draw afresh
        return true;
    }

    /// <summary>Hands the task buttons back their room and drops the window (if Explorer hasn't already).</summary>
    void Forget()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
        if (_tasks != IntPtr.Zero && IsWindow(_tasks) && _tasksSet >= 0)
        {
            var band = RectIn(_host, _tasks);
            if (Along(band) == _tasksSet)
                if (_horizontal) MoveWindow(_tasks, band.Left, band.Top, _tasksFull, band.Bottom - band.Top, true);
                else MoveWindow(_tasks, band.Left, band.Top, band.Right - band.Left, _tasksFull, true);
        }
        var hwnd = Volatile.Read(ref _hwnd);
        Volatile.Write(ref _hwnd, IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
        {
            Windows.TryRemove(hwnd, out _);
            if (IsWindow(hwnd)) DestroyWindow(hwnd);
        }
        (_taskbar, _host, _tasks, _notify) = (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _tasksFull = _tasksSet = -1;
        _hover = null;
    }

    /// <summary>
    /// Fits the strip into the taskbar as it is now: makes room by shortening the task buttons' band (Explorer gives it
    /// back its full length whenever it lays them out again), and sits in the room made, as thick as the band.
    /// </summary>
    void Layout(bool repaint = false)
    {
        if (_cells.Length == 0 || !Find()) return;
        GetWindowRect(_taskbar, out var bar);
        var horizontal = bar.Right - bar.Left >= bar.Bottom - bar.Top;
        var dpi = GetDpiForWindow(_taskbar) is var d and > 0 ? d : 96;
        if (dpi != _dpi || horizontal != _horizontal) repaint = true;
        if (horizontal != _horizontal) _tasksFull = _tasksSet = -1;
        (_dpi, _horizontal) = (dpi, horizontal);

        int x, y, width, height;
        if (_tasks != IntPtr.Zero)
        {
            var band = RectIn(_host, _tasks);
            var length = Along(band);
            if (length != _tasksSet) _tasksFull = length; // laid out again, at its full length
            var thickness = horizontal ? band.Bottom - band.Top : band.Right - band.Left;
            var need = Measure(thickness);
            var shortened = Math.Max(0, _tasksFull - need);
            if (length != shortened)
            {
                if (horizontal) MoveWindow(_tasks, band.Left, band.Top, shortened, thickness, true);
                else MoveWindow(_tasks, band.Left, band.Top, thickness, shortened, true);
            }
            _tasksSet = shortened;
            (x, y) = horizontal ? (band.Left + shortened, band.Top) : (band.Left, band.Top + shortened);
            (width, height) = horizontal ? (need, thickness) : (thickness, need);
        }
        else
        {
            var notify = RectIn(_host, _notify);
            var thickness = horizontal ? notify.Bottom - notify.Top : notify.Right - notify.Left;
            var need = Measure(thickness);
            (x, y) = horizontal ? (notify.Left - need, notify.Top) : (notify.Left, notify.Top - need);
            (width, height) = horizontal ? (need, thickness) : (thickness, need);
        }

        var now = RectIn(_host, _hwnd);
        if (now.Left != x || now.Top != y || now.Right - now.Left != width || now.Bottom - now.Top != height)
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, width, height, SWP_NOACTIVATE);
        if (width != _width || height != _height) repaint = true;
        (_width, _height) = (width, height);
        if (repaint) Paint();
    }

    int Along(RECT r) => _horizontal ? r.Right - r.Left : r.Bottom - r.Top;

    /// <summary>
    /// Works out each cell's length along the strip for a taskbar <paramref name="thickness"/> pixels thick, and returns
    /// the strip's whole length. Each number is centered over its corner of the logo, in a slot as wide as two digits,
    /// so the strip keeps its size as numbers change, growing only for a "100".
    /// </summary>
    int Measure(int thickness)
    {
        if (_measured is { } m && m.Cells == _cells && m.Dpi == _dpi && m.Thickness == thickness && m.Horizontal == _horizontal) return m.Length;
        var scale = _dpi / 96.0;
        var spans = new (int, int)[_cells.Length];
        var slots = new double[_cells.Length];
        int at = 0;
        for (var i = 0; i < _cells.Length; i++)
        {
            slots[i] = Math.Max(Width("88", scale), Math.Max(Width(_cells[i].Left, scale), Width(_cells[i].Right, scale)));
            var length = _horizontal
                ? Math.Max(2 * slots[i] + NumberGap, LogoSize) + 2 * Padding
                : NumberLine + Gap + Logo(thickness / scale) + Padding;
            var pixels = (int)Math.Round(length * scale);
            spans[i] = (at, pixels);
            at += pixels;
        }
        (_spans, _slots) = (spans, slots);
        var total = at + (int)Math.Round(End * scale);
        _measured = (_cells, _dpi, thickness, _horizontal, total);
        return total;
    }

    /// <summary>The logo's size: as big as fits under the numbers on a thin taskbar, at most <see cref="LogoSize"/>.</summary>
    static double Logo(double thickness) => Math.Max(10, Math.Min(LogoSize, thickness - NumberLine - Gap - 6));

    static double Width(string text, double scale) => text.Length == 0 ? 0
        : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, NumberSize, Brushes.White, scale).WidthIncludingTrailingWhitespace;

    // ---- drawing -------------------------------------------------------------------------------------------

    /// <summary>
    /// Draws the cells and hands the pixels to Windows, which blends them over the taskbar (and its blur) itself. The
    /// strip is never wholly see-through, though nearly: a window lets clicks through where it's fully transparent.
    /// </summary>
    void Paint()
    {
        if (_hwnd == IntPtr.Zero || _width <= 0 || _height <= 0) return;
        var scale = _dpi / 96.0;
        var root = new Canvas { Width = _width / scale, Height = _height / scale, Background = Palette.Brush(Colors.Black, 1) };
        TextOptions.SetTextFormattingMode(root, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(root, TextRenderingMode.Grayscale);
        RenderOptions.SetBitmapScalingMode(root, BitmapScalingMode.HighQuality);
        root.UseLayoutRounding = true;
        root.SnapsToDevicePixels = true;

        var hover = Palette.Brush(_light ? Colors.Black : Colors.White, (byte)(_pressed ? (_light ? 0x26 : 0x30) : (_light ? 0x14 : 0x20)));
        for (var i = 0; i < _cells.Length; i++)
        {
            var (start, length) = _spans[i];
            var cell = Cell(_cells[i], _slots[i], _horizontal ? _height / scale : _width / scale);
            cell.Width = (_horizontal ? length : _width) / scale;
            cell.Height = (_horizontal ? _height : length) / scale;
            if (_hover == i) cell.Background = hover;
            Canvas.SetLeft(cell, _horizontal ? start / scale : 0);
            Canvas.SetTop(cell, _horizontal ? 0 : start / scale);
            root.Children.Add(cell);
        }

        VisualTreeHelper.SetRootDpi(root, new DpiScale(scale, scale));
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        var bitmap = new RenderTargetBitmap(_width, _height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var pixels = new byte[_width * _height * 4];
        bitmap.CopyPixels(pixels, _width * 4, 0);
        Push(pixels);
    }

    /// <summary>A service: the logo, with a number centered over each of its top corners, all centered in the cell.</summary>
    Grid Cell(TrayCell c, double slot, double thickness)
    {
        var numbers = new Grid { Height = NumberLine, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var width in new[] { slot, NumberGap, slot }) numbers.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(width) });
        numbers.Children.Add(Number(c.Left, c.LeftInk, 0));
        numbers.Children.Add(Number(c.Right, c.RightInk, 2));

        var mark = Logos.View(c.Key is null ? Logos.App : Logos.For(c.Key, _light), Logo(thickness));
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        mark.Margin = new Thickness(0, Gap, 0, 0);
        // Numbers stay readable when they can't update; only the logo fades, as on the widget.
        mark.Opacity = c.Faded ? 0.35 : 1;

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (c.Key is not null) stack.Children.Add(numbers);
        stack.Children.Add(mark);
        var cell = new Grid { Background = Brushes.Transparent };
        cell.Children.Add(stack);
        return cell;
    }

    static TextBlock Number(string text, Color ink, int column)
    {
        var number = new TextBlock
        {
            Text = text,
            Foreground = Palette.Brush(ink),
            FontFamily = Face.FontFamily,
            FontWeight = Face.Weight,
            FontSize = NumberSize,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            LineHeight = NumberLine,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(number, column);
        return number;
    }

    void Push(byte[] pixels)
    {
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BITMAPINFOHEADER { biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = _width, biHeight = -_height, biPlanes = 1, biBitCount = 32 };
        var dib = CreateDIBSection(screen, ref info, 0, out var bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero)
        {
            Marshal.Copy(pixels, 0, bits, pixels.Length);
            var old = SelectObject(memory, dib);
            var size = new SIZE { cx = _width, cy = _height };
            var origin = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, screen, IntPtr.Zero, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA);
            SelectObject(memory, old);
            DeleteObject(dib);
        }
        DeleteDC(memory);
        ReleaseDC(IntPtr.Zero, screen);
    }

    /// <summary>The whole strip in words, as its window's name: what a screen reader says for it.</summary>
    string Spoken() => AppInfo.Name + ": " + string.Join("; ", _cells.Select(c => c.Name));

    // ---- the mouse -----------------------------------------------------------------------------------------

    static IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hwnd, out var strip))
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return MA_NOACTIVATE; // a click leaves the active window be; the expanded view takes the focus itself
                case WM_MOUSEMOVE:
                    strip.Moved(lParam);
                    return IntPtr.Zero;
                case WM_MOUSELEAVE:
                    strip._tracking = false;
                    strip.Hover(null);
                    return IntPtr.Zero;
                case WM_LBUTTONDOWN:
                    strip.Press(true);
                    return IntPtr.Zero;
                case WM_LBUTTONUP:
                    strip.Release(lParam, menu: false);
                    return IntPtr.Zero;
                case WM_RBUTTONUP:
                    strip.Release(lParam, menu: true);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    Windows.TryRemove(hwnd, out _);
                    break;
            }
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    void Moved(IntPtr lParam)
    {
        if (!_tracking)
        {
            var track = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TME_LEAVE, hwndTrack = _hwnd };
            _tracking = TrackMouseEvent(ref track);
        }
        Hover(At(lParam));
    }

    void Hover(int? cell)
    {
        if (cell == _hover) return;
        _hover = cell;
        _pressed = false;
        Paint();
        if (cell is null)
        {
            CloseTip();
            return;
        }
        // Resting on the strip opens a cell's tooltip; moving along it then switches tooltips straight away.
        if (_tipOpen) OpenTip();
        else _tipDelay.Start();
    }

    void Press(bool down)
    {
        _pressed = down;
        CloseTip();
        Paint();
    }

    void Release(IntPtr lParam, bool menu)
    {
        Press(false);
        if (At(lParam) is not { } cell) return;
        var where = Place(cell);
        _main.InvokeAsync(() => (menu ? MenuRequested : Click)?.Invoke(cell, where));
    }

    void OpenTip()
    {
        _tipDelay.Stop();
        if (_hover is not { } cell) return;
        _tipOpen = true;
        var where = Place(cell);
        _main.InvokeAsync(() => TipOpening?.Invoke(cell, where));
    }

    void CloseTip()
    {
        _tipDelay.Stop();
        if (!_tipOpen) return;
        _tipOpen = false;
        _main.InvokeAsync(() => TipClosing?.Invoke());
    }

    /// <summary>The cell under the mouse, from a mouse message's position.</summary>
    int? At(IntPtr lParam)
    {
        var along = _horizontal ? (short)(lParam.ToInt64() & 0xFFFF) : (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        for (var i = 0; i < _spans.Length; i++)
            if (along >= _spans[i].Start && along < _spans[i].Start + _spans[i].Length) return i;
        return null;
    }

    /// <summary>A cell's place on screen.</summary>
    Native.PixelRect Place(int cell)
    {
        GetWindowRect(_hwnd, out var r);
        var (start, length) = _spans[cell];
        return _horizontal
            ? new Native.PixelRect(r.Left + start, r.Top, r.Left + start + length, r.Bottom)
            : new Native.PixelRect(r.Left, r.Top + start, r.Right, r.Top + start + length);
    }

    /// <summary>Something of the taskbar's moved: if it's one of the windows the strip sits by, fit in again.</summary>
    void OnMoved(IntPtr hook, uint e, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero || (hwnd != _tasks && hwnd != _host && hwnd != _taskbar && hwnd != _notify)) return;
        if (_layoutQueued) return;
        _layoutQueued = true;
        _own.InvokeAsync(() =>
        {
            _layoutQueued = false;
            Layout();
        }, DispatcherPriority.Background);
    }

    /// <summary>A child window's rectangle in its parent's coordinates.</summary>
    static RECT RectIn(IntPtr parent, IntPtr child)
    {
        GetWindowRect(child, out var r);
        MapWindowPoints(IntPtr.Zero, parent, ref r, 2);
        return r;
    }

    // ---- Win32 ---------------------------------------------------------------------------------------------

    delegate IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    delegate void WinEventProc(IntPtr hook, uint e, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string name, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SetWindowText(IntPtr hwnd, string text);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref RECT points, int count);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT track);
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr instance, int name);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, IntPtr at, ref SIZE size, IntPtr src, ref POINT from, int key, ref BLENDFUNCTION blend, int flags);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
}
