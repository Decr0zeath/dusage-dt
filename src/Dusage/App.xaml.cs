using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Dusage;

public partial class App : Application
{
    Mutex? _singleInstance;
    EventWaitHandle? _summon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => a.StartsWith("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            Shutdown(Probe.Run(raw: e.Args.Contains("--probe-raw", StringComparer.OrdinalIgnoreCase)));
            return;
        }

        // Launching dusage while it already runs (say, from the Start menu) brings the running one back
        // into view with its settings open, instead of silently doing nothing.
        _summon = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\dusage-widget-summon");
        _singleInstance = new Mutex(initiallyOwned: true, @"Local\dusage-widget", out var isFirst);
        if (!isFirst)
        {
            _summon.Set();
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

        var settings = Settings.Load();
        var widget = new MainWindow(settings);
        MainWindow = widget;
        widget.Show();
        ThreadPool.RegisterWaitForSingleObject(_summon, (_, _) => widget.Dispatcher.InvokeAsync(widget.Summon), null, Timeout.Infinite, executeOnlyOnce: false);

        if (settings.IsNew)
        {
            // First run: Settings doubles as the welcome screen.
            settings.Save();
            widget.OpenSettings();
        }
    }
}
