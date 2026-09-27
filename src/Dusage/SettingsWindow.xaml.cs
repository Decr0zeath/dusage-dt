using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Dusage;

/// <summary>
/// What to show and how the widget behaves, kept to one page; updates, credits and the fine print live under Info.
/// Every change applies and saves immediately.
/// </summary>
public partial class SettingsWindow : Window
{
    readonly MainWindow _widget;

    Settings Settings => _widget.Settings;
    Updater Updater => _widget.Updater;

    public SettingsWindow(MainWindow widget)
    {
        InitializeComponent();
        _widget = widget;
        Logo.Source = Logos.App;
        VersionText.Text = "v" + AppInfo.Version;
        AuthorLink.NavigateUri = new Uri(AppInfo.AuthorUrl);
        RepoLink.NavigateUri = new Uri(AppInfo.RepoUrl);
        NotesLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/releases");
        LicenseLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/blob/main/LICENSE");
        BuildServices();
        BuildOptions();
        ShowUpdate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

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
    }

    void BuildOptions()
    {
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

        AddRow(Options, null, "Pace tick", "Marks how much of each window's time has passed", Resource("DimBrush"),
            Switch("Pace tick", Settings.ShowPace, on => Settings.ShowPace = on));

        AddRow(Options, null, "Refresh every", null, null,
            Segments("Refresh every", [1, 3, 5, 10, 15], v => $"{v:0}m", Settings.RefreshMinutes, v => Settings.RefreshMinutes = v));

        AddRow(Options, null, "Opacity", null, null, Level("Opacity", 40, 100, 5, Settings.Opacity * 100, v => Settings.Opacity = v / 100));

        var reset = new Button { Content = "Reset", Style = (Style)FindResource("Flat") };
        AutomationProperties.SetName(reset, "Reset position");
        reset.Click += (_, _) => _widget.ResetPosition();
        AddRow(Options, null, "Position", "Drag the widget anywhere, even onto the taskbar", Resource("DimBrush"), reset);
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

    void OnPageClick(object sender, RoutedEventArgs e)
    {
        var info = InfoPage.Visibility != Visibility.Visible;
        InfoPage.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
        MainPage.Visibility = info ? Visibility.Collapsed : Visibility.Visible;
        VersionPanel.Visibility = info ? Visibility.Collapsed : Visibility.Visible; // Info says it all
        PageButton.Content = info ? "‹ Back" : "Info";
        if (info) ShowUpdate(); // freshen "checked … ago"
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

    StackPanel Segments(string name, double[] values, Func<double, string> label, double current, Action<double> set)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var value in values)
        {
            var option = new RadioButton
            {
                Content = label(value),
                GroupName = name,
                IsChecked = Math.Abs(value - current) < 0.001,
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

    /// <summary>A slider in steps, with its value (as a percentage) beside it.</summary>
    StackPanel Level(string name, double min, double max, double step, double current, Action<double> set)
    {
        var value = new TextBlock
        {
            Width = 34,
            Margin = new Thickness(0, 0, 8, 0),
            TextAlignment = TextAlignment.Right,
            Foreground = Resource("DimBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var slider = new Slider
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
