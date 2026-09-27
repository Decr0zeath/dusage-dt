using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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

    static readonly Brush DividerBrush = Palette.Brush(Palette.Starlight, 0x2E);

    Settings _settings;
    readonly Provider[] _providers;
    readonly Updater _updater = new();
    readonly List<Row> _rows = [];
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    readonly CancellationTokenSource _closing = new();
    string? _layout, _services;
    ExpandedWindow? _expanded;
    TrayIcon? _tray;
    IntPtr _hwnd;
    bool _placed, _expandWhenPlaced;
    /// <summary>The corner that stays still as the widget grows or shrinks; see <see cref="OnSizeChanged"/>.</summary>
    (bool Right, bool Bottom) _corner = (true, true);
    bool _gitHubSignedIn = GitHubSignIn.Token is not null;

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

        /// <summary>The "5h" and "7d" (or "1d", "mo") before this line's bars, when bar labels are on.</summary>
        public TextBlock? SessionLabel { get; set; }
        public TextBlock? WeeklyLabel { get; set; }
    }

    internal IReadOnlyList<Provider> Providers => _providers;
    internal (bool Right, bool Bottom) Corner => _corner;
    internal Settings Settings => _settings;
    internal Updater Updater => _updater;

    public MainWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        _providers = IUsageSource.All().Select(s => new Provider(s)).ToArray();

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
        GitHubSignIn.Changed += OnGitHubSignInChanged;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SizeChanged += OnSizeChanged;
        UpdateRows();
        Render();
    }

    void ApplyLook()
    {
        Topmost = _settings.Topmost;
        Opacity = Math.Clamp(_settings.Opacity, 0.4, 1);
        if (_expanded is not null) _expanded.Topmost = _settings.Topmost;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Native.MakeToolWindow(_hwnd);
        HwndSource.FromHwnd(_hwnd).AddHook(Native.AllowAnySize);

        _tray = new TrayIcon(AppInfo.Name);
        _tray.Click += Expand;
        _tray.MenuRequested += ShowTrayMenu;
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // The first layout still used Windows' minimum window size (the hook came after creation); redo it.
        InvalidateMeasure();
        UpdateLayout();
        if (_settings.Left is null || _settings.Top is null) PlaceInCorner();
        else PlaceSaved();
        Native.KeepOnScreen(_hwnd);
        if (_settings.Left is { } left && _settings.Top is { } top && (left != Left || top != Top)) SavePosition();
        _placed = true;
        Tick();
        _timer.Start();
        if (_expandWhenPlaced) Expand();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _closing.Cancel();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        GitHubSignIn.Changed -= OnGitHubSignInChanged;
        SaveState();
        _expanded?.Close();
        _tray?.Dispose();
        base.OnClosed(e);
    }

    // ---- called from the expanded view and from a second launch --------------------------------------------

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
        _settings.ForgetPosition();
        _settings.Save();
        PlaceInCorner();
    }

    /// <summary>Every setting back to how a new install has it, the position included.
    /// Start with Windows lives in the registry, not in settings, and stays as it is.</summary>
    internal void RestoreDefaults()
    {
        _settings = new Settings();
        ApplySettings();
        PlaceInCorner();
    }

    /// <summary>Swaps the widget for the expanded view, opened over it. Collapsing it brings the widget back.
    /// Also what launching dusage again does, which recovers a widget you've lost track of.</summary>
    public void Expand()
    {
        if (_expanded is { } open)
        {
            open.Activate();
            return;
        }
        if (!_placed)
        {
            _expandWhenPlaced = true; // it opens over the widget, so the widget has to be in place first
            return;
        }
        _expanded = new ExpandedWindow(this);
        _expanded.Closed += OnCollapsed;
        _expanded.ShowOver(this);
        _expanded.Activate();
        Hide();
    }

    void OnCollapsed(object? sender, EventArgs e)
    {
        _expanded = null;
        if (_closing.IsCancellationRequested) return; // exiting, not collapsing
        Show();
        Native.KeepOnScreen(_hwnd);
        if (Topmost) Native.BringToTop(_hwnd);
    }

    // ---- placement -----------------------------------------------------------------------------------------

    void PlaceInCorner()
    {
        // Bottom-right, just above the clock.
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 10;
        Top = area.Bottom - ActualHeight - 10;
        _corner = (true, true);
        Native.KeepOnScreen(_hwnd);
    }

    /// <summary>
    /// Back where it was saved, lining up the corner it keeps still: a widget that starts up wider than it was saved
    /// (a service signed in meanwhile) grows away from its corner, as it would have while running.
    /// </summary>
    void PlaceSaved()
    {
        _corner = _settings.Corner is { } corner
            ? (corner is WidgetCorner.TopRight or WidgetCorner.BottomRight, corner is WidgetCorner.BottomLeft or WidgetCorner.BottomRight)
            : Native.Corner(_hwnd);
        if (_corner.Right && _settings.Right is { } right) Left = right - ActualWidth;
        if (_corner.Bottom && _settings.Bottom is { } bottom) Top = bottom - ActualHeight;
    }

    /// <summary>
    /// When rows come or go, grow away from the corner the widget keeps still, so a corner widget stays in its corner.
    /// That corner is only chosen again when the widget is dragged: worked out afresh after each resize, a wide widget
    /// near the middle of the screen could flip sides and wander off a little further every time.
    /// </summary>
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Size changes while starting up aren't rows coming or going; PlaceSaved lines the widget up once it's sized.
        if (!_placed || e.PreviousSize.Width == 0) return;
        if (_corner.Right) Left -= e.NewSize.Width - e.PreviousSize.Width;
        if (_corner.Bottom) Top -= e.NewSize.Height - e.PreviousSize.Height;
        Native.KeepOnScreen(_hwnd);
        if (_settings.Left is not null) SavePosition();
    }

    void SavePosition()
    {
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Right = Left + ActualWidth;
        _settings.Bottom = Top + ActualHeight;
        _settings.Corner = _corner switch
        {
            (false, false) => WidgetCorner.TopLeft,
            (true, false) => WidgetCorner.TopRight,
            (false, true) => WidgetCorner.BottomLeft,
            (true, true) => WidgetCorner.BottomRight,
        };
        _settings.Save();
    }

    // ---- polling -------------------------------------------------------------------------------------------

    internal bool Wanted(Provider p) => p.SignedIn && _settings.IsShown(p.Key);

    void Tick()
    {
        if (Topmost && IsVisible && !Pill.ContextMenu.IsOpen && !Tip.IsOpen) Native.BringToTop(_hwnd);

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

    /// <summary>Signed in or out with GitHub in Settings: show (and fetch) or drop Copilot now, not at the next tick.
    /// Also called as a sign-in starts, shows its code or is cancelled, which change nothing about the account.</summary>
    void OnGitHubSignInChanged()
    {
        var signedIn = GitHubSignIn.Token is not null;
        if (signedIn != _gitHubSignedIn)
        {
            _gitHubSignedIn = signedIn;
            var copilot = _providers.First(p => p.Source is CopilotSource);
            copilot.NextFetch = default;
            copilot.State = new ProviderState(); // another account's numbers, possibly
        }
        Tick();
        _expanded?.BuildServices();
    }

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
            _expanded?.BuildServices();
        }

        var line = _settings.Layout == WidgetLayout.Line;
        var layout = $"{_settings.Layout}:{_settings.BarLabels}:{_settings.PercentSign}:"
                   + string.Join('|', wanted.Select(w => w.Extra is null ? w.Provider.Key : w.Provider.ExtraKey(w.Extra)));
        if (layout == _layout) return;
        _layout = layout;

        Body.Children.Clear();
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        _rows.Clear();

        // Each line takes five columns (seven with the bar labels, each just before its bar). A box stacks the lines
        // in rows; a line puts them side by side in one row, with a divider column between services (and a plain
        // gap before a service's extra limit).
        var span = _settings.BarLabels ? 7 : 5;
        var columns = line ? Math.Max(1, wanted.Count) * (span + 1) - 1 : span;
        for (var i = 0; i < columns; i++) Body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

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
                    Text = extra.Short ?? extra.Label,
                    Foreground = DimBrush,
                    FontSize = 10,
                    MaxWidth = 56,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            mark.HorizontalAlignment = HorizontalAlignment.Right;
            mark.Margin = new Thickness(0, 0, 6, 0);

            var row = new Row(p, extra?.Key, mark);
            // Fixed widths, wide enough for "100" or "100%", so the widget doesn't change size as the numbers do.
            row.SessionText.Width = row.WeeklyText.Width = _settings.PercentSign ? 27 : 19;
            int index = _rows.Count, first = line ? index * (span + 1) : 0;
            if (!line || index == 0) Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (line && index > 0)
            {
                var divider = new Border
                {
                    Width = 1,
                    Margin = new Thickness(extra is null ? 8 : 4, 3, extra is null ? 8 : 4, 3),
                    Background = extra is null ? DividerBrush : Brushes.Transparent,
                };
                Grid.SetColumn(divider, first - 1);
                Body.Children.Add(divider);
            }
            if (_settings.BarLabels)
            {
                row.SessionLabel = BarLabel("5h");
                row.WeeklyLabel = BarLabel("7d");
            }
            FrameworkElement?[] cells = [mark, row.SessionLabel, row.SessionBar, row.SessionText, row.WeeklyLabel, row.WeeklyBar, row.WeeklyText];
            var column = first;
            foreach (var cell in cells.OfType<FrameworkElement>())
            {
                Grid.SetRow(cell, line ? 0 : index);
                Grid.SetColumn(cell, column++);
                Body.Children.Add(cell);
            }
            _rows.Add(row);
        }
    }

    /// <summary>A tiny "5h" or "7d", just before its bar; Render renames it after the bar's actual window.</summary>
    static TextBlock BarLabel(string text) => new()
    {
        Text = text,
        Foreground = DimBrush,
        FontSize = 8,
        Margin = new Thickness(0, 0, 3, 0),
        // Against its bar, however wide a longer label ("chat") makes the column.
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };

    static Meter Bar() => new()
    {
        Width = 32,
        Height = 8,
        Margin = new Thickness(0, 0, 4, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    static TextBlock Percent(Thickness margin) => new()
    {
        Margin = margin,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
    };

    void Render()
    {
        var now = DateTimeOffset.Now;
        foreach (var row in _rows)
        {
            var (session, weekly) = Windows(row);
            // A limit with only one window (a model's weekly cap, Copilot's monthly allowance) leaves the other slot
            // empty rather than showing a dash; in a line, it closes up. Dashes are for a service with no numbers yet.
            var ifMissing = session is null && weekly is null ? Visibility.Visible
                : _settings.Layout == WidgetLayout.Line ? Visibility.Collapsed : Visibility.Hidden;
            Fill(row.SessionBar, row.SessionText, session, now, ifMissing, _settings.PercentSign);
            Fill(row.WeeklyBar, row.WeeklyText, weekly, now, ifMissing, _settings.PercentSign);
            Label(row.SessionLabel, row.SessionBar, session);
            Label(row.WeeklyLabel, row.WeeklyBar, weekly);

            // Numbers stay readable when they can't update; only the logo fades, and the tooltip says why.
            row.Mark.Opacity = IsFresh(row.Provider.State, now) ? 1 : 0.35;
        }
        _expanded?.ShowUsage();

        static void Label(TextBlock? label, Meter bar, UsageWindow? window)
        {
            if (label is null) return;
            label.Visibility = bar.Visibility;
            if (window is not null) label.Text = Fmt.Tag(window);
        }
    }

    /// <summary>A line's two windows: the left (short) bar's and the right (long) bar's.</summary>
    static (UsageWindow? Session, UsageWindow? Weekly) Windows(Row row)
    {
        var last = row.Provider.State.Last;
        if (row.ExtraKey is null) return (last?.Session, last?.Weekly);
        var extra = row.Provider.Extras.FirstOrDefault(e => e.Key == row.ExtraKey);
        return (extra?.Session, extra?.Weekly);
    }

    /// <summary>Updated lately and without trouble.</summary>
    internal bool IsFresh(ProviderState state, DateTimeOffset now)
    {
        var staleAfter = 3 * _settings.RefreshInterval;
        if (staleAfter < TimeSpan.FromMinutes(20)) staleAfter = TimeSpan.FromMinutes(20);
        return state.Problem is null && state.FetchedAt is { } at && now - at < staleAfter;
    }

    /// <summary>When a service last updated, or why it can't.</summary>
    internal static string Status(ProviderState state, DateTimeOffset now) =>
        state.Problem is { } problem
            ? problem + (state.FetchedAt is { } good ? $" Last good update: {Fmt.Ago(now - good)}." : "")
            : state.FetchedAt is { } at ? "Updated " + Fmt.Ago(now - at) : "Loading…";

    /// <summary>Shows a window's use (or what's left) as a bar and a number, amber and then red as it fills up.
    /// With no data, the bar and number show a dash, or <paramref name="ifMissing"/> hides them.</summary>
    internal void Fill(Meter bar, TextBlock text, UsageWindow? window, DateTimeOffset now, Visibility ifMissing, bool percentSign)
    {
        bar.Visibility = text.Visibility = window is null ? ifMissing : Visibility.Visible;
        if (window is null)
        {
            bar.Value = bar.Pace = double.NaN;
            text.Text = "–";
            text.Foreground = DimBrush;
            return;
        }
        var used = window.PercentAt(now);
        var brush = !_settings.WarningColors ? NormalBrush
            : used >= _settings.RedAt ? HighBrush
            : used >= _settings.AmberAt ? WarnBrush
            : NormalBrush;
        bar.Value = _settings.Count(used) / 100;
        bar.Pace = _settings.ShowPace && window.ElapsedAt(now) is { } elapsed ? _settings.Pace(elapsed) : double.NaN;
        bar.Fill = brush;
        text.Text = Fmt.Pct(_settings.Count(used)) + (percentSign ? "%" : "");
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
            panel.Children.Add(new TextBlock { Text = "Nothing to show yet. Double-click to pick services.", Foreground = DimBrush });

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
            foreach (var (name, window) in state.Last?.Limits() ?? [])
                AddTipLine(grid, name, window, now);
            panel.Children.Add(grid);

            panel.Children.Add(new TextBlock
            {
                Text = Status(state, now),
                Foreground = state.Problem is null ? DimBrush : WarnBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 320,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
        var left = _settings.Numbers == NumberStyle.Left;
        var tick = !_settings.ShowPace ? "" : left ? " · tick: time left in the window" : " · tick: time elapsed in the window";
        panel.Children.Add(new TextBlock
        {
            Text = $"Left bar: {Kinds(w => w.Session, "5-hour")} · right bar: {Kinds(w => w.Weekly, "weekly")}"
                 + (left ? " · numbers: % left" : "") + tick + "\n"
                 + "Drag to move · double-click to refresh · right-click to expand",
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

        // What the bars on one side are: "5-hour", or "5-hour or daily" with Gemini's day beside Claude's 5 hours.
        string Kinds(Func<(UsageWindow? Session, UsageWindow? Weekly), UsageWindow?> side, string fallback)
        {
            var kinds = _rows.Select(r => side(Windows(r))).OfType<UsageWindow>()
                .Select(w => (w.Name ?? Fmt.Window(w.Length)).ToLowerInvariant()).Distinct().ToList();
            return kinds.Count > 0 ? string.Join(" or ", kinds) : fallback;
        }
    }

    void AddTipLine(Grid grid, string label, UsageWindow? window, DateTimeOffset now)
    {
        var row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Cell(label, 0, DimBrush, TextAlignment.Left);
        Cell(window is null ? "–" : Fmt.Pct(_settings.Count(window.PercentAt(now))) + "%", 1, TextBrush, TextAlignment.Right);
        Cell(window is null ? "no data" : Fmt.Reset(window, now, _settings.ResetTimes), 2, DimBrush, TextAlignment.Left);

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
            if (_rows.Count == 0) Expand();
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
        _corner = Native.Corner(_hwnd); // dropped somewhere new: keep still the corner nearest the screen's
        SavePosition();
    }

    /// <summary>The widget's right-click menu, opened at the tray icon.</summary>
    void ShowTrayMenu()
    {
        var menu = Pill.ContextMenu;
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
        // Without the foreground, a menu opened from the tray stays open when you click elsewhere.
        if (PresentationSource.FromVisual(menu) is HwndSource source) Native.Foreground(source.Handle);
    }

    void OnMenuOpened(object sender, RoutedEventArgs e) => ExpandItem.Header = _expanded is null ? "Expand" : "Collapse";

    void OnRefreshClick(object sender, RoutedEventArgs e) => RefreshNow();

    void OnExpandClick(object sender, RoutedEventArgs e)
    {
        if (_expanded is { } open) open.Close();
        else Expand();
    }

    void OnExitClick(object sender, RoutedEventArgs e) => Close();

    void OnUpdateClick(object sender, RoutedEventArgs e) => _ = _updater.InstallAsync();

    void OnUpdaterChanged()
    {
        var shown = _updater.Available is not null;
        UpdateItem.Visibility = UpdateSeparator.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        UpdateItem.Header = UpdateLabel(_updater);
        UpdateItem.IsEnabled = !_updater.Installing;
        _expanded?.ShowUpdate();
    }

    /// <summary>What the update button says, in the menu and in the expanded view.</summary>
    internal static string UpdateLabel(Updater updater) =>
        updater.Installing ? "Updating…"
        : updater.Available is not { } release ? ""
        : Updater.CanInstall ? $"Update to {release.Version}"
        : $"Download {release.Version}…";
}
