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
    static readonly Brush NormalBrush = Palette.Brush(Palette.Starlight, 0xD8), WarnBrush = Palette.Brush(Palette.Amber), HighBrush = Palette.Brush(Palette.Flare);
    static readonly Brush TextBrush = Palette.Brush(Palette.Starlight), DimBrush = Palette.Brush(Palette.Stardust), AccentBrush = Palette.Brush(Palette.Nebula);
    static readonly FontFamily Serif = new("Georgia");

    readonly Settings _settings;
    readonly Provider[] _providers;
    readonly Updater _updater = new();
    readonly List<Row> _rows = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly CancellationTokenSource _closing = new();
    string? _layout, _services;
    SettingsWindow? _settingsWindow;
    IntPtr _hwnd;
    bool _placed;

    /// <summary>One line of the widget: a service's main limits, or one of its extra limits.</summary>
    sealed class Row(Provider provider, string? extraKey, FrameworkElement mark)
    {
        public Provider Provider { get; } = provider;
        public string? ExtraKey { get; } = extraKey;
        public FrameworkElement Mark { get; } = mark;
        public Meter SessionBar { get; } = Bar();
        public Meter WeeklyBar { get; } = Bar();
        public TextBlock SessionText { get; } = Percent(new Thickness(0, 0, 9, 0));
        public TextBlock WeeklyText { get; } = Percent(new Thickness(0));
    }

    internal IReadOnlyList<Provider> Providers => _providers;
    internal Settings Settings => _settings;
    internal Updater Updater => _updater;

    public MainWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _providers = [new Provider(new ClaudeSource()), new Provider(new CodexSource())];

        var saved = StateStore.Load();
        foreach (var p in _providers)
        {
            if (saved.TryGetValue(p.Key, out var state)) p.State = state;
            p.SignedIn = p.Source.HasSignIn();
        }

        var area = SystemParameters.WorkArea;
        Left = settings.Left ?? area.Right - 170; // refined in OnContentRendered once the real size is known
        Top = settings.Top ?? area.Bottom - 50;
        ApplyLook();

        _timer.Tick += (_, _) => Tick();
        _updater.Changed += OnUpdaterChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SizeChanged += OnSizeChanged;
        UpdateRows();
        Render();
    }

    void ApplyLook()
    {
        Topmost = _settings.Topmost;
        Opacity = Math.Clamp(_settings.Opacity, 0.4, 1);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd).AddHook(Native.AllowAnySize);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // The first layout still used Windows' minimum window size (the hook came after creation); redo it.
        InvalidateMeasure();
        UpdateLayout();
        if (_settings.Left is null || _settings.Top is null) PlaceInCorner();
        Native.KeepOnScreen(_hwnd);
        _placed = true;
        Tick();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _closing.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SaveState();
        _settingsWindow?.Close();
        base.OnClosed(e);
    }

    // ---- called from Settings and from a second launch -----------------------------------------------------

    internal void ApplySettings()
    {
        _settings.Save();
        ApplyLook();
        // A shorter refresh interval takes effect now rather than after the current wait.
        foreach (var p in _providers)
            if (p.NextFetch > p.LastAttempt + _settings.RefreshInterval && DateTimeOffset.Now >= p.HoldUntil)
                p.NextFetch = p.LastAttempt + _settings.RefreshInterval;
        Tick();
    }

    internal void ResetPosition()
    {
        _settings.Left = _settings.Top = null;
        _settings.Save();
        PlaceInCorner();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>Someone launched dusage again: bring the widget back into view and open Settings.</summary>
    public void Summon()
    {
        Show();
        Native.KeepOnScreen(_hwnd);
        if (Topmost)
        {
            Native.BringToTop(_hwnd);
        }
        else
        {
            Topmost = true; // raise it once without pinning it
            Topmost = false;
        }
        OpenSettings();
    }

    // ---- placement -----------------------------------------------------------------------------------------

    void PlaceInCorner()
    {
        // Bottom-right, just above the clock.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 10;
        Top = area.Bottom - ActualHeight - 10;
        Native.KeepOnScreen(_hwnd);
    }

    /// <summary>When rows come or go, grow away from the nearest screen edges so a corner widget stays in its corner.</summary>
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Size changes while starting up aren't rows coming or going; the saved position already fits.
        if (!_placed || e.PreviousSize.Width == 0) return;
        var (right, bottom) = Native.Corner(_hwnd);
        if (right) Left -= e.NewSize.Width - e.PreviousSize.Width;
        if (bottom) Top -= e.NewSize.Height - e.PreviousSize.Height;
        Native.KeepOnScreen(_hwnd);
        if (_settings.Left is not null)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Save();
        }
    }

    // ---- polling -------------------------------------------------------------------------------------------

    bool Wanted(Provider p) => p.SignedIn && _settings.IsShown(p.Key);

    void Tick()
    {
        if (Topmost && !Pill.ContextMenu.IsOpen && !Tip.IsOpen) Native.BringToTop(_hwnd);

        foreach (var p in _providers) p.SignedIn = p.Source.HasSignIn();
        UpdateRows();

        // Nobody is looking while the PC sits idle, so poll far less often then.
        var now = DateTimeOffset.Now;
        var idle = Native.IdleTime() > IdleAfter;
        foreach (var p in _providers)
            if (Wanted(p) && !p.Busy && now >= p.NextFetch && (!idle || now - p.LastAttempt >= IdleInterval))
                _ = FetchAsync(p);
        if (_settings.CheckForUpdates && now >= _updater.NextCheck)
            _ = _updater.CheckAsync(_closing.Token);
        Render();
    }

    void RefreshNow()
    {
        var now = DateTimeOffset.Now;
        foreach (var p in _providers)
            if (Wanted(p) && !p.Busy && now >= p.HoldUntil)
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
        UpdateRows(); // extra limits may have appeared or gone
        Render();
    }

    void SaveState() => StateStore.Save(_providers.ToDictionary(p => p.Key, p => p.State));

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

    /// <summary>Rebuilds the rows when the set of things to show changes.</summary>
    void UpdateRows()
    {
        var wanted = new List<(Provider Provider, ExtraLimit? Extra)>();
        foreach (var p in _providers.Where(Wanted))
        {
            wanted.Add((p, null));
            foreach (var extra in p.Extras)
                if (_settings.IsShown(p.ExtraKey(extra))) wanted.Add((p, extra));
        }

        // Settings lists sign-ins, plans and extra limits; refresh it when those change.
        var services = string.Join('|', _providers.Select(p => $"{p.Key}:{p.SignedIn}:{p.State.Last?.Plan}:{string.Join(',', p.Extras.Select(e => e.Key))}"));
        if (services != _services)
        {
            _services = services;
            _settingsWindow?.BuildServices();
        }

        var layout = string.Join('|', wanted.Select(w => w.Extra is null ? w.Provider.Key : w.Provider.ExtraKey(w.Extra)));
        if (layout == _layout) return;
        _layout = layout;

        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        _rows.Clear();

        if (wanted.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = _providers.Any(p => p.SignedIn) ? "All services hidden" : "No signed-in services",
                Foreground = DimBrush,
                Margin = new Thickness(6, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(hint, 1);
            Grid.SetColumnSpan(hint, 4);
            Body.Children.Add(Logos.View(Logos.App, 13));
            Body.Children.Add(hint);
            return;
        }

        foreach (var (p, extra) in wanted)
        {
            FrameworkElement mark = extra is null
                ? Logos.View(Logos.For(p.Key), 13)
                : new TextBlock
                {
                    Text = extra.Label,
                    Foreground = DimBrush,
                    FontSize = 10,
                    MaxWidth = 56,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            mark.Margin = new Thickness(0, 0, 6, 0);

            var row = new Row(p, extra?.Key, mark);
            var index = Body.RowDefinitions.Count;
            Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            FrameworkElement[] cells = [mark, row.SessionBar, row.SessionText, row.WeeklyBar, row.WeeklyText];
            for (var column = 0; column < cells.Length; column++)
            {
                Grid.SetRow(cells[column], index);
                Grid.SetColumn(cells[column], column);
                Body.Children.Add(cells[column]);
            }
            _rows.Add(row);
        }
    }

    static Meter Bar() => new()
    {
        Width = 32,
        Height = 8,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Percent(Thickness margin) => new()
    {
        Width = 19,
        Margin = margin,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
    };

    void Render()
    {
        var now = DateTimeOffset.Now;
        var staleAfter = 3 * _settings.RefreshInterval;
        if (staleAfter < TimeSpan.FromMinutes(20)) staleAfter = TimeSpan.FromMinutes(20);

        foreach (var row in _rows)
        {
            var state = row.Provider.State;
            UsageWindow? session, weekly;
            if (row.ExtraKey is null)
            {
                (session, weekly) = (state.Last?.Session, state.Last?.Weekly);
            }
            else
            {
                var extra = row.Provider.Extras.FirstOrDefault(e => e.Key == row.ExtraKey);
                (session, weekly) = (extra?.Session, extra?.Weekly);
            }
            // A model's weekly-only limit leaves its 5-hour slot empty rather than showing a dash.
            var isExtra = row.ExtraKey is not null;
            Fill(row.SessionBar, row.SessionText, session, now, hideIfMissing: isExtra);
            Fill(row.WeeklyBar, row.WeeklyText, weekly, now, hideIfMissing: isExtra);

            // Numbers stay readable when they can't update; only the logo fades, and the tooltip says why.
            var fresh = state.Problem is null && state.FetchedAt is { } at && now - at < staleAfter;
            row.Mark.Opacity = fresh ? 1 : 0.35;
        }
    }

    void Fill(Meter bar, TextBlock text, UsageWindow? window, DateTimeOffset now, bool hideIfMissing)
    {
        bar.Visibility = text.Visibility = window is null && hideIfMissing ? Visibility.Hidden : Visibility.Visible;
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
        bar.Pace = _settings.ShowPace ? window.ElapsedAt(now) ?? double.NaN : double.NaN;
        bar.Fill = brush;
        text.Text = Fmt.Pct(percent);
        text.Foreground = brush == NormalBrush ? TextBrush : brush;
    }

    void OnTipOpening(object sender, ToolTipEventArgs e) => Tip.Content = BuildTip();

    StackPanel BuildTip()
    {
        var now = DateTimeOffset.Now;
        var panel = new StackPanel();
        Grid.SetIsSharedSizeScope(panel, true); // line the columns up across services

        var shown = _providers.Where(Wanted).ToList();
        if (shown.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Nothing to show yet. Right-click › Settings to pick services.", Foreground = DimBrush });

        foreach (var p in shown)
        {
            var state = p.State;
            var heading = new TextBlock { FontFamily = Serif, FontSize = 13, Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 10, 0, 3) };
            heading.Inlines.Add(new Run(p.Source.Name) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Logos.ColorFor(p.Key)) });
            if (state.Last?.Plan is { Length: > 0 } plan) heading.Inlines.Add(new Run("  " + Fmt.Title(plan)) { FontStyle = FontStyles.Italic, Foreground = DimBrush });
            panel.Children.Add(heading);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Percent" });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            AddTipLine(grid, "5-hour", state.Last?.Session, now);
            AddTipLine(grid, "Weekly", state.Last?.Weekly, now);
            foreach (var extra in p.Extras)
            {
                if (extra.Session is { } s) AddTipLine(grid, $"{extra.Label} 5-hour", s, now);
                if (extra.Weekly is { } w) AddTipLine(grid, $"{extra.Label} weekly", w, now);
            }
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
                 + "Drag to move · double-click to refresh · right-click for settings",
            Foreground = DimBrush,
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 0),
        });
        if (_updater.Available is { } release)
            panel.Children.Add(new TextBlock
            {
                Text = $"dUsage/dt {release.Version} is out · right-click to update",
                Foreground = AccentBrush,
                FontSize = 11,
                Margin = new Thickness(0, 6, 0, 0),
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
            if (_rows.Count == 0) OpenSettings();
            else RefreshNow();
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

    void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshNow();

    void OnSettingsClick(object sender, RoutedEventArgs e) => OpenSettings();

    void OnExitClick(object sender, RoutedEventArgs e) => Close();

    void OnUpdateClick(object sender, RoutedEventArgs e) => _ = _updater.InstallAsync();

    void OnUpdaterChanged()
    {
        var shown = _updater.Available is not null;
        UpdateItem.Visibility = UpdateSeparator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateItem.Header = UpdateLabel(_updater);
        UpdateItem.IsEnabled = !_updater.Installing;
        _settingsWindow?.ShowUpdate();
    }

    /// <summary>What the update button says, in the menu and in Settings.</summary>
    internal static string UpdateLabel(Updater updater) =>
        updater.Installing ? "Updating…"
        : updater.Available is not { } release ? ""
        : Updater.CanInstall ? $"Update to {release.Version}"
        : $"Download {release.Version}…";
}
