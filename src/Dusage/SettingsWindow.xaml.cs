using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Navigation;

namespace Dusage;

/// <summary>What to show and how the widget behaves. Every change applies and saves immediately.</summary>
public partial class SettingsWindow : Window
{
    readonly MainWindow _widget;

    Settings Settings => _widget.Settings;

    public SettingsWindow(MainWindow widget)
    {
        InitializeComponent();
        _widget = widget;
        Logo.Source = Logos.App;
        Tagline.Text = $"AI plan limits, at a glance  ·  v{AppInfo.Version}";
        AuthorLink.NavigateUri = new Uri(AppInfo.AuthorUrl);
        LicenseLink.NavigateUri = new Uri(AppInfo.RepoUrl + "/blob/main/LICENSE");
        RepoLink.NavigateUri = new Uri(AppInfo.RepoUrl);
        BuildServices();
        BuildOptions();
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

        AddRow(Options, null, "Opacity", null, null,
            Segments("Opacity", [1, 0.85, 0.7, 0.55], v => $"{v * 100:0}%", Settings.Opacity, v => Settings.Opacity = v));

        var reset = new Button { Content = "Reset", Style = (Style)FindResource("Flat") };
        AutomationProperties.SetName(reset, "Reset position");
        reset.Click += (_, _) => _widget.ResetPosition();
        AddRow(Options, null, "Position", "Drag the widget anywhere, even onto the taskbar", Resource("DimBrush"), reset);
    }

    // ---- building blocks -----------------------------------------------------------------------------------

    static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xD9, 0xA5, 0x5B));

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

    void OnLink(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
