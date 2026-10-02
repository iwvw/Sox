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
        Core.Logger.Initialize("app.log", Core.Logger.UserDataDir);
        CoreAliasBootstrap.Initialize();
        ThemeService = new ThemeService();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(ThemeService);
        MainWindow.Activate();
    }
}
