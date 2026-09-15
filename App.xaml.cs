using System.Windows;

namespace NetAnalyzer;

public partial class App : Application
{
    /// <summary>
    /// The executable has two roles. Run normally it is the window; run with
    /// <see cref="EtwCollector.ChildSwitch"/> it is the collector the window spawns, which owns
    /// the ETW session and reports per-process byte counts down a pipe.
    ///
    /// One executable rather than two because the app ships as a single file that is copied
    /// around on its own — a separate helper binary would be left behind by every existing copy
    /// and by the desktop shortcut.
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(a => string.Equals(a, EtwCollector.ChildSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            // No window, no tray, no settings: this copy exists only to feed the parent.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = EtwCollector.RunAsChild(e.Args);
            Shutdown(code);
            return;
        }

        new MainWindow().Show();
    }
}
