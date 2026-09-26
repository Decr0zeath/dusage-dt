using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Dusage;

public partial class MainWindow : Window
{
    static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(10), IdleInterval = TimeSpan.FromMinutes(15);
    static readonly Brush NormalBrush = Solid(0xD4, 0xD8, 0xDE), WarnBrush = Solid(0xF2, 0xB8, 0x4B), HighBrush = Solid(0xF2, 0x60, 0x5C);
    static readonly Brush TextBrush = Solid(0xE8, 0xE8, 0xE8), DimBrush = Solid(0x9A, 0x9F, 0xA6);

    readonly Settings _settings;
    readonly Provider[] _providers;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly CancellationTokenSource _closing = new();
    IntPtr _hwnd;

    sealed class Provider(IUsageSource source, string mark, Color color)
    {
        public IUsageSource Source { get; } = source;
        public string Mark { get; } = mark;
        public Brush Brush { get; } = Solid(color.R, color.G, color.B);
        public ProviderState State { get; set; } = new();

        public DateTimeOffset NextFetch, LastAttempt, HoldUntil;
        public bool Busy;

        public readonly Meter SessionBar = Bar(), WeeklyBar = Bar();
        public readonly TextBlock SessionText = Percent(), WeeklyText = Percent();
        public FrameworkElement[] Cells = [];
    }

    public MainWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _providers =
        [
            new Provider(new ClaudeSource(), "C", Color.FromRgb(0xD9, 0x77, 0x57)),
            new Provider(new CodexSource(), "G", Color.FromRgb(0x19, 0xC3, 0x7D)),
        ];

        var saved = StateStore.Load();
        for (var i = 0; i < _providers.Length; i++)
        {
            if (saved.TryGetValue(_providers[i].Source.Key, out var state)) _providers[i].State = state;
            AddRow(_providers[i], i);
        }

        Topmost = settings.Topmost;
        Opacity = Math.Clamp(settings.Opacity, 0.2, 1);
        var area = SystemParameters.WorkArea;
        Left = settings.Left ?? area.Right - 170; // refined in OnContentRendered once the real size is known
        Top = settings.Top ?? area.Bottom - 50;

        _timer.Tick += (_, _) => Tick();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Render();
    }

    void AddRow(Provider p, int row)
    {
        Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var mark = new TextBlock
        {
            Text = p.Mark,
            Foreground = p.Brush,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        p.SessionText.Margin = new Thickness(0, 0, 9, 0);
        p.Cells = [mark, p.SessionBar, p.SessionText, p.WeeklyBar, p.WeeklyText];
        for (var column = 0; column < p.Cells.Length; column++)
        {
            Grid.SetRow(p.Cells[column], row);
            Grid.SetColumn(p.Cells[column], column);
            Body.Children.Add(p.Cells[column]);
        }
    }

    static Meter Bar() => new()
    {
        Width = 32,
        Height = 8,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Percent() => new()
    {
        Width = 19,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
    };

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_settings.Left is null || _settings.Top is null)
        {
            // Default spot: bottom-right, just above the clock.
            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - 10;
            Top = area.Bottom - ActualHeight - 10;
        }
        Native.KeepOnScreen(_hwnd);
        Tick();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _closing.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SaveState();
        base.OnClosed(e);
    }

    // ---- polling -------------------------------------------------------------------------------------------

    void Tick()
    {
        if (Topmost && !Pill.ContextMenu.IsOpen && !Tip.IsOpen) Native.BringToTop(_hwnd);

        // Nobody is looking while the PC sits idle, so poll far less often then.
        var now = DateTimeOffset.Now;
        var idle = Native.IdleTime() > IdleAfter;
        foreach (var p in _providers)
            if (!p.Busy && now >= p.NextFetch && (!idle || now - p.LastAttempt >= IdleInterval))
                _ = FetchAsync(p);
        Render();
    }

    void RefreshNow()
    {
        var now = DateTimeOffset.Now;
        foreach (var p in _providers)
            if (!p.Busy && now >= p.HoldUntil)
                _ = FetchAsync(p);
    }

    async Task FetchAsync(Provider p)
    {
        p.Busy = true;
        var started = p.LastAttempt = DateTimeOffset.Now;
        try
        {
            var snapshot = await Task.Run(() => p.Source.FetchAsync(_closing.Token));
            p.State.Last = snapshot;
            p.State.FetchedAt = DateTimeOffset.Now;
            p.State.Problem = null;
            p.NextFetch = started + _settings.RefreshInterval;
        }
        catch (UsageException e)
        {
            p.State.Problem = e.Message;
            p.NextFetch = started + (e.RetryAfter ?? _settings.RefreshInterval);
            if (e.Status == 429) p.HoldUntil = p.NextFetch;
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            Log.Error(e);
            p.State.Problem = "Unexpected error: " + e.Message;
            p.NextFetch = started + _settings.RefreshInterval;
        }
        finally
        {
            p.Busy = false;
        }
        SaveState();
        Render();
    }

    void SaveState() => StateStore.Save(_providers.ToDictionary(p => p.Source.Key, p => p.State));

    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // After sleep, refresh once the network has had a few seconds to come back.
        Dispatcher.InvokeAsync(() =>
        {
            var soon = DateTimeOffset.Now.AddSeconds(10);
            foreach (var p in _providers)
                if (p.NextFetch > soon && soon >= p.HoldUntil) p.NextFetch = soon;
        });
    }

    // ---- drawing -------------------------------------------------------------------------------------------

    void Render()
    {
        var now = DateTimeOffset.Now;
        var staleAfter = 3 * _settings.RefreshInterval;
        if (staleAfter < TimeSpan.FromMinutes(20)) staleAfter = TimeSpan.FromMinutes(20);

        foreach (var p in _providers)
        {
            Show(p.SessionBar, p.SessionText, p.State.Last?.Session, now);
            Show(p.WeeklyBar, p.WeeklyText, p.State.Last?.Weekly, now);
            // Dim a row whose numbers can't be trusted to be current; the tooltip says why.
            var fresh = p.State.Problem is null && p.State.FetchedAt is { } at && now - at < staleAfter;
            foreach (var cell in p.Cells) cell.Opacity = fresh ? 1 : 0.45;
        }
    }

    static void Show(Meter bar, TextBlock text, UsageWindow? window, DateTimeOffset now)
    {
        if (window is null)
        {
            bar.Value = bar.Pace = double.NaN;
            text.Text = "–";
            text.Foreground = DimBrush;
            return;
        }
        var percent = window.PercentAt(now);
        var brush = percent >= 90 ? HighBrush : percent >= 75 ? WarnBrush : NormalBrush;
        bar.Value = percent / 100;
        bar.Pace = window.ElapsedAt(now) ?? double.NaN;
        bar.Fill = brush;
        text.Text = Fmt.Pct(percent);
        text.Foreground = brush == NormalBrush ? TextBrush : brush;
    }

    void OnTipOpening(object sender, ToolTipEventArgs e) => Tip.Content = BuildTip();

    StackPanel BuildTip()
    {
        var now = DateTimeOffset.Now;
        var panel = new StackPanel();
        Grid.SetIsSharedSizeScope(panel, true); // line the columns up across providers
        foreach (var p in _providers)
        {
            var state = p.State;
            var heading = new TextBlock { Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 10, 0, 3) };
            heading.Inlines.Add(new Run(p.Source.Name) { FontWeight = FontWeights.SemiBold, Foreground = p.Brush });
            if (state.Last?.Plan is { Length: > 0 } plan) heading.Inlines.Add(new Run("  " + Fmt.Title(plan)) { Foreground = DimBrush });
            panel.Children.Add(heading);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Percent" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddTipLine(grid, "5-hour", state.Last?.Session, now);
            AddTipLine(grid, "Weekly", state.Last?.Weekly, now);
            if (state.Last is { } last)
                foreach (var extra in last.Extra) AddTipLine(grid, extra.Label, extra.Window, now);
            panel.Children.Add(grid);

            var status = state.Problem is { } problem
                ? problem + (state.FetchedAt is { } good ? $" Last good update: {Fmt.Ago(now - good)}." : "")
                : state.FetchedAt is { } at ? "Updated " + Fmt.Ago(now - at) : "Loading…";
            panel.Children.Add(new TextBlock
            {
                Text = status,
                Foreground = state.Problem is null ? DimBrush : WarnBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        panel.Children.Add(new TextBlock
        {
            Text = "Left bar: 5-hour · right bar: weekly · tick: time elapsed in the window\n"
                 + "Drag to move · double-click to refresh · right-click for options",
            Foreground = DimBrush,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
        });
        return panel;
    }

    static void AddTipLine(Grid grid, string label, UsageWindow? window, DateTimeOffset now)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(label, 0, DimBrush, TextAlignment.Left);
        Cell(window is null ? "–" : Fmt.Pct(window.PercentAt(now)) + "%", 1, TextBrush, TextAlignment.Right);
        Cell(window is null ? "no data" : Fmt.Reset(window, now), 2, DimBrush, TextAlignment.Left);

        void Cell(string text, int column, Brush brush, TextAlignment align)
        {
            var block = new TextBlock
            {
                Text = text,
                Foreground = brush,
                TextAlignment = align,
                Margin = new Thickness(0, 0, column < 2 ? 14 : 0, 0),
            };
            Grid.SetRow(block, row);
            Grid.SetColumn(block, column);
            grid.Children.Add(block);
        }
    }

    // ---- interaction ---------------------------------------------------------------------------------------

    void OnPillMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            RefreshNow();
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // the button was already released
        }
        Native.KeepOnScreen(_hwnd);
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();
    }

    void OnMenuOpening(object sender, ContextMenuEventArgs e)
    {
        TopmostItem.IsChecked = Topmost;
        StartupItem.IsChecked = Autostart.IsEnabled;
    }

    void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshNow();

    void OnTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = _settings.Topmost = TopmostItem.IsChecked;
        _settings.Save();
    }

    void OnStartupClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Autostart.Set(StartupItem.IsChecked);
        }
        catch (Exception ex)
        {
            Log.Error(ex);
        }
        StartupItem.IsChecked = Autostart.IsEnabled;
    }

    void OnExitClick(object sender, RoutedEventArgs e) => Close();

    static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
