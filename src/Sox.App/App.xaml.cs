using Microsoft.UI.Xaml;
using Sox.App.Services;

namespace Sox.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public MainWindow? MainWindow { get; private set; }

    public ThemeService ThemeService { get; }

    /// <summary>Raised whenever any settings page persists a change, so live surfaces refresh at once.</summary>
    public event EventHandler? SettingsChanged;

    public void RaiseSettingsChanged() => SettingsChanged?.Invoke(this, EventArgs.Empty);

    public App()
    {
        InitializeComponent();
        // Append across restarts (overwrite:false) instead of truncating: the whole point of this log in
        // a self-hosted diagnostic tool is to see what the PREVIOUS run did -- truncating on every launch
        // destroyed exactly that evidence the moment the user restarted to reproduce a problem. Logger
        // still rolls over at 1 MB, so the file cannot grow without bound.
        Core.Logger.Initialize("app.log", Core.Logger.UserDataDir, overwrite: false);
        InstallGlobalExceptionHandlers();
        CoreAliasBootstrap.Initialize();
        ThemeService = new ThemeService();
    }

    /// <summary>
    /// Captures crashes that would otherwise terminate the process silently. Without this the App has no
    /// record of a UI-thread exception at all: the window simply disappears and app.log ends mid-stream,
    /// leaving nothing to diagnose. The handlers only log -- they cannot keep the process alive -- but a
    /// logged crash is the difference between "it vanished" and a fixable report.
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        UnhandledException += (_, e) =>
        {
            try
            {
                Core.Logger.Log($"[App] UNHANDLED UI EXCEPTION: {e.Exception}", Core.LogLevel.Error);
            }
            catch
            {
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                Core.Logger.Log($"[App] UNHANDLED EXCEPTION (terminating={e.IsTerminating}): {e.ExceptionObject}", Core.LogLevel.Error);
            }
            catch
            {
            }
        };

        // Unobserved task exceptions do not crash the process on .NET, but they are otherwise dropped
        // with no trace; logging them is what turns a silent background failure into a visible one.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try
            {
                Core.Logger.Log($"[App] UNOBSERVED TASK EXCEPTION: {e.Exception}", Core.LogLevel.Error);
            }
            catch
            {
            }

            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(ThemeService);
        MainWindow.Activate();
    }
}
