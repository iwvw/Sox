using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Sox.App.Services;

internal sealed class TrayIconService : IDisposable
{
    private TaskbarIcon? _icon;
    private ElementTheme _theme = ElementTheme.Default;
    private bool _disposed;

    public TrayIconService()
    {
        // A hard exit (taskkill from the updater, Task Manager) never runs Dispose, and the shell then
        // leaves a phantom icon behind until the user hovers it. Removing it here covers those paths,
        // because ProcessExit still runs for them.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
    }

    public event Action? OpenRequested;
    public event Action? SettingsRequested;
    public event Action? AboutRequested;
    public event Action? ExitRequested;

    public void Show(bool visible)
    {
        if (_icon is null)
        {
            if (!visible)
            {
                return;
            }

            _icon = CreateIcon();
        }

        _icon.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        if (_icon is not null)
        {
            _icon.RequestedTheme = theme;
        }
    }

    private TaskbarIcon CreateIcon()
    {
        var icon = (TaskbarIcon)Application.Current.Resources["SoxTrayIcon"];

        icon.RequestedTheme = _theme;
        icon.ContextMenuMode = ContextMenuMode.SecondWindow;
        icon.LeftClickCommand = new RelayCommand(() => OpenRequested?.Invoke());
        icon.NoLeftClickDelay = true;

        if (icon.ContextFlyout is MenuFlyout menu)
        {
            Wire(menu, 0, () => OpenRequested?.Invoke());
            Wire(menu, 2, () => SettingsRequested?.Invoke());
            Wire(menu, 3, () => AboutRequested?.Invoke());
            Wire(menu, 5, () => ExitRequested?.Invoke());
        }

        icon.ForceCreate(enablesEfficiencyMode: false);
        return icon;
    }

    private static void Wire(MenuFlyout menu, int index, Action handler)
    {
        if (index < menu.Items.Count && menu.Items[index] is MenuFlyoutItem item)
        {
            item.Click += (_, _) => handler();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_icon is null)
            return;

        try
        {
            _icon.Visibility = Visibility.Collapsed;
            _icon.Dispose();
        }
        catch
        {
            // ProcessExit can run after the XAML/dispatcher is gone; the shell drops the icon on exit anyway.
        }
        _icon = null;
    }

    private sealed class RelayCommand(Action execute) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();
    }
}
