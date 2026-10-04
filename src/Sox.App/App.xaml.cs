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
        CoreAliasBootstrap.Initialize();
        ThemeService = new ThemeService();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(ThemeService);
        MainWindow.Activate();
    }
}
