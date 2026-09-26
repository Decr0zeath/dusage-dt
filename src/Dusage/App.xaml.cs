using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Dusage;

public partial class App : Application
{
    public const string Version = "1.0.0";

    Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.StartsWith("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Probe.Run(raw: e.Args.Contains("--probe-raw", StringComparer.OrdinalIgnoreCase)));
            return;
        }

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\dusage-widget", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        // A few dozen pixels don't need a GPU device; skipping it keeps the widget's memory small.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        // A widget that lives all day should shrug off surprises rather than vanish.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception);
            args.Handled = true;
        };

        MainWindow = new MainWindow(Settings.Load());
        MainWindow.Show();
    }
}
