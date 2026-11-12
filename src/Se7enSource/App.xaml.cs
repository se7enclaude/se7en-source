using System.Windows;

namespace Se7enSource;

public partial class App : Application
{
    private const string InstanceName = "se7enSource.SingleInstance";
    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Appelé par Seventh OS avant la désinstallation : on retire le lancement avec Windows.
        if (e.Args.Contains("--uninstall-cleanup"))
        {
            try { Services.Autostart.Set(false); } catch (Exception) { }
            Shutdown(0);
            return;
        }

        // Une seule instance : si l'app tourne déjà, on la ramène au premier plan.
        _mutex = new Mutex(true, InstanceName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        // filet : une erreur imprévue est notée dans le journal au lieu de fermer l'appli (les raccourcis restent actifs)
        DispatcherUnhandledException += (_, ev) =>
        {
            Services.Log.Error("Interface", ev.Exception);
            ev.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Services.Log.Error(ev.IsTerminating ? "Erreur fatale" : "Erreur", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Services.Log.Error("Tâche", ev.Exception);
            ev.SetObserved();
        };

        base.OnStartup(e);
        var window = new MainWindow(startMinimized: e.Args.Contains("--minimized"), diagnostic: e.Args.Contains("--diagnostic"));
        MainWindow = window;

        var signal = _showSignal;
        new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "show-signal" }.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
