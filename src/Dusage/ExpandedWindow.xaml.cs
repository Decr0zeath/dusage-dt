using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Dusage;

/// <summary>
/// The widget, expanded: every limit with a full-width bar and its reset time, then Settings (what to show and how
/// the widget behaves) and Info (updates, credits, the fine print) a click away. It takes the widget's place while
/// open and hands it back when collapsed, and keeps one size whichever page or settings section is open.
/// Every setting applies and saves immediately.
/// </summary>
public partial class ExpandedWindow : Window
{
    readonly MainWindow _widget;
    readonly (ToggleButton Head, UIElement Body)[] _sections;
    IntPtr _hwnd;
    bool _opening, _restoreArmed;

    Settings Settings => _widget.Settings;
    Updater Updater => _widget.Updater;

    public ExpandedWindow(MainWindow widget)
    {
        InitializeComponent();
        _widget = widget;
        Topmost = Settings.Topmost;
        Logo.Source = Logos.App;
        VersionText.Text = "v" + AppInfo.Version;
        AuthorLink.NavigateUri = new Uri(AppInfo.AuthorUrl);
        RepoLink.NavigateUri = new Uri(AppInfo.RepoUrl);
        NotesLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/releases");
        LicenseLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/blob/main/LICENSE");
        // The stars stop at the rounded corners, just inside the 1px frame.
        Inside.SizeChanged += (_, _) => Inside.Clip = new RectangleGeometry(new Rect(Inside.RenderSize), 9, 9);
        _sections = [(ServicesHead, ServicesBody), (WidgetHead, WidgetBody), (NumbersHead, NumbersBody), (ColorsHead, ColorsBody)];
        Open(ServicesHead);
        ShowUsage();
        BuildServices();
        BuildOptions();
        ShowUpdate();
        // With nothing to show yet, the first stop is choosing services.
        (_widget.Providers.Any(_widget.Wanted) ? UsageTab : SettingsTab).IsChecked = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
    }

    /// <summary>
    /// Opens where the widget is, lined up with the widget's corner nearest the screen's corner:
    /// a widget above the clock grows up and to the left.
    /// </summary>
    internal void ShowOver(Window widget)
    {
        var (right, bottom) = Native.Corner(new WindowInteropHelper(widget).Handle);
        var content = (FrameworkElement)Content;
        content.Measure(new Size(Width, double.PositiveInfinity));
        Left = right ? widget.Left + widget.ActualWidth - Width : widget.Left;
        Top = bottom ? widget.Top + widget.ActualHeight - content.DesiredSize.Height : widget.Top;
        Show();
        Native.KeepOnScreen(_hwnd, clearOfTaskbar: true);
    }

    /// <summary>Pages differ in size; near the bottom or right of the screen, grow upward or leftward so that edge stays put.</summary>
    void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.PreviousSize.Height == 0) return; // the first layout; ShowOver placed it
        var (right, bottom) = Native.Corner(_hwnd);
        if (right) Left -= e.NewSize.Width - e.PreviousSize.Width;
        if (bottom) Top -= e.NewSize.Height - e.PreviousSize.Height;
        Native.KeepOnScreen(_hwnd, clearOfTaskbar: true);
    }

    void OnDrag(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return; // the button was already released
        }
        Native.KeepOnScreen(_hwnd, clearOfTaskbar: true);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    void OnCollapseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>A card per service: every limit with a wide bar, the percent used, and when it resets.
    /// Also called by the widget whenever it redraws, which keeps the countdowns current.</summary>
    internal void ShowUsage()
    {
        var now = DateTimeOffset.Now;
        var shown = _widget.Providers.Where(_widget.Wanted).ToList();
        Usage.Children.Clear();
        var left = Settings.Numbers == NumberStyle.Left;
        PaceNote.Text = (left, Settings.ShowPace) switch
        {
            (true, true) => "Numbers show what's left of each limit; the tick, how much of its time is left.",
            (true, false) => "Numbers show what's left of each limit.",
            _ => "The tick marks how much of each window's time has passed.",
        };
        PaceNote.Visibility = shown.Count > 0 && (left || Settings.ShowPace) ? Visibility.Visible : Visibility.Collapsed;

        if (shown.Count == 0)
        {
            var hint = _widget.Providers.Any(p => p.SignedIn)
                ? "Every service is switched off. Settings has the switches."
                : "No signed-in services yet. Settings says how to sign in.";
            Usage.Children.Add(Card(new TextBlock { Text = hint, Foreground = Resource("DimBrush"), TextWrapping = TextWrapping.Wrap }));
            return;
        }

        foreach (var p in shown)
        {
            var card = Card(UsageCard(p, now));
            if (Usage.Children.Count > 0) card.Margin = new Thickness(0, 10, 0, 0);
            Usage.Children.Add(card);
        }
    }

    StackPanel UsageCard(Provider p, DateTimeOffset now)
    {
        var state = p.State;
        var panel = new StackPanel();

        var heading = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var logo = Logos.View(Logos.For(p.Key), 18);
        logo.Margin = new Thickness(0, 0, 9, 0);
        logo.Opacity = _widget.IsFresh(state, now) ? 1 : 0.35; // faded when it can't update, as on the widget
        heading.Children.Add(logo);
        var name = new TextBlock { FontFamily = (FontFamily)FindResource("Serif"), FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
        name.Inlines.Add(new Run(p.Source.Name) { FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Logos.ColorFor(p.Key)) });
        if (state.Last?.Plan is { Length: > 0 } plan)
            name.Inlines.Add(new Run("  " + Fmt.Title(plan)) { FontStyle = FontStyles.Italic, Foreground = Resource("DimBrush") });
        heading.Children.Add(name);
        panel.Children.Add(heading);

        var limits = new List<(string Label, UsageWindow? Window)> { ("5-hour", state.Last?.Session), ("Weekly", state.Last?.Weekly) };
        foreach (var extra in p.Extras)
        {
            if (extra.Session is { } session) limits.Add(($"{extra.Label} 5-hour", session));
            if (extra.Weekly is { } weekly) limits.Add(($"{extra.Label} weekly", weekly));
        }
        foreach (var (label, window) in limits)
            panel.Children.Add(Limit(label, window, now, first: panel.Children.Count == 1));

        panel.Children.Add(new TextBlock
        {
            Text = MainWindow.Status(state, now),
            Foreground = state.Problem is null ? Resource("DimBrush") : WarnBrush,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 12, 0, 0),
        });
        return panel;
    }

    /// <summary>One limit: its name, a wide bar, the percent used, and when it resets underneath.
    /// The name and percent columns line up across every card.</summary>
    Grid Limit(string label, UsageWindow? window, DateTimeOffset now, bool first)
    {
        var grid = new Grid { Margin = new Thickness(0, first ? 0 : 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Label" });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Percent" });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var name = new TextBlock { Text = label, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        var bar = new Meter { Height = 14, BarHeight = 6, VerticalAlignment = VerticalAlignment.Center };
        var percent = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 46,
            Margin = new Thickness(12, 0, 0, 0),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular },
        };
        _widget.Fill(bar, percent, window, now, ifMissing: Visibility.Visible, percentSign: true);
        var reset = new TextBlock
        {
            Text = window is null ? "no data yet" : Fmt.Reset(window, now, Settings.ResetTimes),
            Foreground = Resource("DimBrush"),
            FontSize = 11.5,
            Margin = new Thickness(0, 1, 0, 0),
        };
        if (reset.Text.Length == 0) reset.Visibility = Visibility.Collapsed;

        Grid.SetColumn(bar, 1);
        Grid.SetColumn(percent, 2);
        Grid.SetRow(reset, 1);
        Grid.SetColumn(reset, 1);
        Grid.SetColumnSpan(reset, 2);
        grid.Children.Add(name);
        grid.Children.Add(bar);
        grid.Children.Add(percent);
        grid.Children.Add(reset);
        return grid;
    }

    Border Card(UIElement content) => new()
    {
        Style = (Style)FindResource("Card"),
        Padding = new Thickness(16, 12, 16, 12),
        Child = content,
    };

    /// <summary>Also called by the widget when a sign-in, plan or extra limit changes.</summary>
    internal void BuildServices()
    {
        Services.Children.Clear();
        foreach (var p in _widget.Providers)
        {
            var status = !p.SignedIn ? p.Source.SignInHint
                : p.State.Last?.Plan is { Length: > 0 } plan ? $"{Fmt.Title(plan)} plan · via {p.Source.Via}"
                : $"Signed in · via {p.Source.Via}";
            AddRow(Services, Logos.View(Logos.For(p.Key), 22), p.Source.Name, status,
                p.SignedIn ? Resource("DimBrush") : WarnBrush,
                Switch(p.Source.Name, Settings.IsShown(p.Key), on => Settings.SetShown(p.Key, on)));

            foreach (var extra in p.Extras)
            {
                var windows = (extra.Session, extra.Weekly) switch
                {
                    (not null, not null) => "5-hour and weekly limits",
                    (not null, null) => "5-hour limit",
                    _ => "weekly limit",
                };
                AddRow(Services, null, extra.Label, $"Its own {windows}", Resource("DimBrush"),
                    Switch(extra.Label, Settings.IsShown(p.ExtraKey(extra)), on => Settings.SetShown(p.ExtraKey(extra), on)),
                    indent: 48);
            }
        }
        FitSettings();
    }

    /// <summary>Every section but Services. Built again after Restore defaults, which can change every value.</summary>
    void BuildOptions()
    {
        Options.Children.Clear();
        Reading.Children.Clear();
        Warnings.Children.Clear();

        AddRow(Options, null, "Layout", "A small box, or one long line", Resource("DimBrush"),
            Segments("Layout", Enum.GetValues<WidgetLayout>(), v => v.ToString(), Settings.Layout, v => Settings.Layout = v));

        AddRow(Options, null, "Bar labels", "A tiny 5h and 7d before the bars", Resource("DimBrush"),
            Switch("Bar labels", Settings.BarLabels, on => Settings.BarLabels = on));

        AddRow(Options, null, "Always on top", null, null,
            Switch("Always on top", Settings.Topmost, on => Settings.Topmost = on));

        var startup = new CheckBox { IsChecked = Autostart.IsEnabled, Style = (Style)FindResource("Switch") };
        AutomationProperties.SetName(startup, "Start with Windows");
        startup.Click += (_, _) =>
        {
            try
            {
                Autostart.Set(startup.IsChecked == true);
            }
            catch (Exception ex)
            {
                Log.Error(ex);
            }
            startup.IsChecked = Autostart.IsEnabled;
        };
        AddRow(Options, null, "Start with Windows", null, null, startup);

        AddRow(Options, null, "Refresh every", null, null,
            Segments<double>("Refresh every", [1, 3, 5, 10, 15], v => $"{v:0}m", Settings.RefreshMinutes, v => Settings.RefreshMinutes = v));

        AddRow(Options, null, "Opacity", null, null,
            Level("Opacity", 40, 100, 5, Settings.Opacity * 100, v => Settings.Opacity = v / 100, out _));

        var reset = new Button { Content = "Reset", Style = (Style)FindResource("Flat") };
        AutomationProperties.SetName(reset, "Reset position");
        reset.Click += (_, _) => _widget.ResetPosition();
        AddRow(Options, null, "Position", "Drag the widget anywhere, even onto the taskbar", Resource("DimBrush"), reset);

        AddRow(Reading, null, "Show", "How much of each limit is used, or how much is left", Resource("DimBrush"),
            Segments("Show", Enum.GetValues<NumberStyle>(), v => v.ToString(), Settings.Numbers, v => Settings.Numbers = v));

        AddRow(Reading, null, "Percent sign", "44% rather than 44 on the widget", Resource("DimBrush"),
            Switch("Percent sign", Settings.PercentSign, on => Settings.PercentSign = on));

        AddRow(Reading, null, "Reset times", "In the tooltip and on the Usage page", Resource("DimBrush"),
            Segments("Reset times", Enum.GetValues<ResetStyle>(), v => v.ToString(), Settings.ResetTimes, v => Settings.ResetTimes = v));

        AddRow(Reading, null, "Pace tick", "Marks each window's time, counted like the numbers", Resource("DimBrush"),
            Switch("Pace tick", Settings.ShowPace, on => Settings.ShowPace = on));

        // Red stays above amber: moving one past the other pushes the other along.
        Slider? amber = null, red = null;
        var amberLevel = Level("Amber from", 50, 95, 5, Settings.AmberAt, v =>
        {
            Settings.AmberAt = v;
            if (red is not null && red.Value <= v) red.Value = v + 5;
        }, out amber);
        var redLevel = Level("Red from", 55, 100, 5, Settings.RedAt, v =>
        {
            Settings.RedAt = v;
            if (amber is not null && amber.Value >= v) amber.Value = v - 5;
        }, out red);
        void ShowLevels()
        {
            amberLevel.IsEnabled = redLevel.IsEnabled = Settings.WarningColors;
            amberLevel.Opacity = redLevel.Opacity = Settings.WarningColors ? 1 : 0.4;
        }
        ShowLevels();

        AddRow(Warnings, null, "Warning colors", "Bars and numbers turn amber, then red, as a limit gets used up", Resource("DimBrush"),
            Switch("Warning colors", Settings.WarningColors, on =>
            {
                Settings.WarningColors = on;
                ShowLevels();
            }));
        AddRow(Warnings, Dot(Palette.Amber), "Amber from", "When this much of a limit is used", Resource("DimBrush"), amberLevel);
        AddRow(Warnings, Dot(Palette.Flare), "Red from", "When this much of a limit is used", Resource("DimBrush"), redLevel);

        FitSettings();
    }

    /// <summary>Called by the widget whenever the updater's state changes.</summary>
    internal void ShowUpdate()
    {
        var label = MainWindow.UpdateLabel(Updater);
        FooterUpdate.Content = label;
        FooterUpdate.IsEnabled = !Updater.Installing;
        FooterUpdate.Visibility = Updater.Available is null ? Visibility.Collapsed : Visibility.Visible;

        var now = DateTimeOffset.Now;
        var status = Updater.Installing ? "Follow along in the PowerShell window."
            : Updater.Checking ? "Checking…"
            : Updater.Problem is { } problem ? problem
            : Updater.Available is { } release ? $"Version {release.Version} is out."
            : Updater.CheckedAt is { } at ? $"Up to date · checked {Fmt.Ago(now - at)}"
            : "Not checked yet";

        Button action;
        if (Updater.Available is null)
        {
            action = new Button { Content = "Check now", Style = (Style)FindResource("Flat"), IsEnabled = !Updater.Checking };
            action.Click += (_, _) => _ = Updater.CheckAsync(CancellationToken.None);
        }
        else
        {
            action = new Button { Content = label, Style = (Style)FindResource("Primary"), IsEnabled = !Updater.Installing };
            action.Click += OnUpdateClick;
        }
        AutomationProperties.SetName(action, action.Content.ToString());

        Updates.Children.Clear();
        AddRow(Updates, null, $"dUsage/dt {AppInfo.Version}", status, Updater.Problem is null ? Resource("DimBrush") : WarnBrush, action);
        AddRow(Updates, null, "Check automatically", "Once a day, on GitHub", Resource("DimBrush"),
            Switch("Check for updates automatically", Settings.CheckForUpdates, on => Settings.CheckForUpdates = on));
    }

    void OnPageChecked(object sender, RoutedEventArgs e)
    {
        // Hidden, not collapsed: every page keeps its room, so the window doesn't change size between them.
        UsagePage.Visibility = Shown(UsageTab);
        SettingsPage.Visibility = Shown(SettingsTab);
        InfoPage.Visibility = Shown(InfoTab);
        VersionPanel.Visibility = InfoTab.IsChecked == true ? Visibility.Hidden : Visibility.Visible; // Info says it all
        if (InfoTab.IsChecked == true) ShowUpdate(); // freshen "checked … ago"

        static Visibility Shown(RadioButton tab) => tab.IsChecked == true ? Visibility.Visible : Visibility.Hidden;
    }

    /// <summary>A section's title was clicked, or toggled from the keyboard or a screen reader.</summary>
    void OnSectionToggled(object sender, RoutedEventArgs e)
    {
        if (_opening) return; // Open closing the other sections
        var head = (ToggleButton)sender;
        Open(head.IsChecked == true ? head : null);
    }

    /// <summary>Opens one settings section and closes the rest; null closes them all.</summary>
    void Open(ToggleButton? head)
    {
        _opening = true;
        foreach (var (h, body) in _sections)
        {
            h.IsChecked = h == head;
            body.Visibility = h == head ? Visibility.Visible : Visibility.Collapsed;
        }
        _opening = false;
    }

    /// <summary>
    /// Holds the Settings page at the height it has with its tallest section open, so opening another section
    /// never resizes the window. Measured again whenever the sections are rebuilt.
    /// </summary>
    void FitSettings()
    {
        var open = _sections.FirstOrDefault(s => s.Head.IsChecked == true).Head;
        var width = Width - 46; // the frame and the page margins
        SettingsPage.MinHeight = 0;
        double tallest = 0;
        foreach (var (head, _) in _sections)
        {
            Open(head);
            // Measure skips anything it thinks is unchanged, and opening a section only marks its own panel;
            // mark the way up to the page too, or the page reports the size it had before.
            foreach (var (_, body) in _sections)
                for (DependencyObject? e = body; e is not null && e != SettingsPage; e = VisualTreeHelper.GetParent(e))
                    (e as UIElement)?.InvalidateMeasure();
            SettingsPage.InvalidateMeasure();
            SettingsPage.Measure(new Size(width, double.PositiveInfinity));
            tallest = Math.Max(tallest, SettingsPage.DesiredSize.Height);
        }
        Open(open);
        SettingsPage.MinHeight = tallest;
    }

    void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (!_restoreArmed)
        {
            _restoreArmed = true;
            RestoreButton.Content = "Click again to restore defaults";
            RestoreButton.Foreground = WarnBrush;
            return;
        }
        OnRestoreLeave(sender, e);
        _widget.RestoreDefaults();
        BuildServices();
        BuildOptions();
        ShowUpdate();
        ShowUsage();
    }

    void OnRestoreLeave(object sender, RoutedEventArgs e)
    {
        _restoreArmed = false;
        RestoreButton.Content = "Restore defaults";
        RestoreButton.ClearValue(ForegroundProperty);
    }

    void OnUpdateClick(object sender, RoutedEventArgs e) => _ = Updater.InstallAsync();

    // ---- building blocks -----------------------------------------------------------------------------------

    static readonly Brush WarnBrush = Palette.Brush(Palette.Amber);

    Brush Resource(string key) => (Brush)FindResource(key);

    void AddRow(Panel card, UIElement? icon, string title, string? detail, Brush? detailBrush, FrameworkElement control, double indent = 14)
    {
        if (card.Children.Count > 0)
            card.Children.Add(new Border { Height = 1, Background = Resource("LineBrush"), Margin = new Thickness(indent, 0, 0, 0) });

        var grid = new Grid { Margin = new Thickness(indent, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon is FrameworkElement image)
        {
            image.Margin = new Thickness(0, 0, 12, 0);
            grid.Children.Add(image);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        if (detail is not null)
            text.Children.Add(new TextBlock
            {
                Text = detail,
                Foreground = detailBrush,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 1, 0, 0),
            });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        control.Margin = new Thickness(12, 0, 0, 0);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 2);
        grid.Children.Add(control);

        card.Children.Add(grid);
    }

    CheckBox Switch(string name, bool on, Action<bool> set)
    {
        var box = new CheckBox { IsChecked = on, Style = (Style)FindResource("Switch") };
        AutomationProperties.SetName(box, name);
        box.Click += (_, _) =>
        {
            set(box.IsChecked == true);
            _widget.ApplySettings();
        };
        return box;
    }

    StackPanel Segments<T>(string name, T[] values, Func<T, string> label, T current, Action<T> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var value in values)
        {
            var option = new RadioButton
            {
                Content = label(value),
                GroupName = name,
                IsChecked = EqualityComparer<T>.Default.Equals(value, current),
                Style = (Style)FindResource("Segment"),
            };
            AutomationProperties.SetName(option, $"{name} {label(value)}");
            option.Checked += (_, _) =>
            {
                set(value);
                _widget.ApplySettings();
            };
            panel.Children.Add(option);
        }
        return panel;
    }

    /// <summary>A small colored dot, standing in for an icon.</summary>
    static Border Dot(Color color) => new()
    {
        Width = 8,
        Height = 8,
        CornerRadius = new CornerRadius(4),
        Background = Palette.Brush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A slider in steps, with its value (as a percentage) beside it.</summary>
    StackPanel Level(string name, double min, double max, double step, double current, Action<double> set, out Slider slider)
    {
        var value = new TextBlock
        {
            Width = 34,
            Margin = new Thickness(0, 0, 8, 0),
            TextAlignment = TextAlignment.Right,
            Foreground = Resource("DimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider = new Slider
        {
            Width = 140,
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 2,
            TickFrequency = step,
            IsSnapToTickEnabled = true,
            Value = Math.Clamp(Math.Round(current / step) * step, min, max),
            Style = (Style)FindResource("Level"),
        };
        AutomationProperties.SetName(slider, name);
        value.Text = $"{slider.Value:0}%";
        slider.ValueChanged += (_, e) =>
        {
            value.Text = $"{e.NewValue:0}%";
            set(e.NewValue);
            _widget.ApplySettings();
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(value);
        panel.Children.Add(slider);
        return panel;
    }

    void OnLink(object sender, RequestNavigateEventArgs e)
    {
        AppInfo.Open(e.Uri.AbsoluteUri);
        e.Handled = true;
    }
}
