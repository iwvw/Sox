using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sox.App.Controls;
using Sox.App.Materials;
using Sox.App.Services;
using Sox.App.Settings;
using WinUIEx;

namespace Sox.App;

public sealed partial class SettingsWindow : WindowEx
{
    private readonly ThemeService _themeService;
    private readonly SearchHost _searchHost;
    private readonly AppUpdateService _updates;
    private readonly Action _exitForUpdate;
    private readonly Dictionary<string, object> _pages = new();
    private Microsoft.UI.Xaml.Media.SystemBackdrop? _windowBackdrop;

    public SettingsWindow(ThemeService themeService, SearchHost searchHost, AppUpdateService updates, Action exitForUpdate)
    {
        _themeService = themeService;
        _searchHost = searchHost;
        _updates = updates;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();

        ApplyWindowTheme();
        _themeService.ThemeChanged += OnThemeChanged;

        Closed += (_, _) =>
        {
            _themeService.ThemeChanged -= OnThemeChanged;
            (_windowBackdrop as IDisposable)?.Dispose();
            if (_pages.TryGetValue("index", out var p) && p is IndexPage indexPage)
            {
                indexPage.Cleanup();
            }
        };

        try
        {
            AppWindow.Title = "Sox 设置";
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sox-tray.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SettingsWindow init failed", ex);
        }

        NavView.SelectedItem = NavView.MenuItems[0];

        // Open centred on screen (WinUIEx handles the monitor/DPI maths); without this it lands wherever
        // the OS default places a new window.
        WinUIEx.WindowExtensions.CenterOnScreen(this);
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ApplyWindowTheme);

    /// <summary>
    /// Applies the same material engine (Acrylic / Mica / Clear) and light/dark theme the
    /// spotlight window uses, so the settings surface matches the main window's look.
    /// </summary>
    private void ApplyWindowTheme()
    {
        var snapshot = _themeService.Current;
        RootElement.RequestedTheme = snapshot.Theme;

        // WindowEx shadows SystemBackdrop with its own WinUIEx type; assign the base Window
        // property so MicaBackdrop / AlwaysActiveAcrylicBackdrop work.
        ((Microsoft.UI.Xaml.Window)this).SystemBackdrop = ResolveWindowBackdrop(snapshot.BackdropParameters.Style);
    }

    private Microsoft.UI.Xaml.Media.SystemBackdrop? ResolveWindowBackdrop(BackdropStyle style)
    {
        (_windowBackdrop as IDisposable)?.Dispose();

        _windowBackdrop = style switch
        {
            BackdropStyle.Mica => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
            _ => new AlwaysActiveAcrylicBackdrop(),
        };

        return _windowBackdrop;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
        {
            return;
        }

        // Switch the pane content in place; pages are cached so their state survives navigation.
        if (!_pages.TryGetValue(tag, out var page))
        {
            page = tag switch
            {
                "general" => new GeneralPage(),
                "appearance" => CreateAppearancePage(),
                "hotkeys" => new HotkeyPage(),
                "search" => new SearchPage(),
                "index" => new IndexPage(_searchHost),
                "websearch" => new WebSearchPage(),
                "about" => new AboutPage(_updates, _exitForUpdate),
                _ => null,
            };

            if (page is null)
            {
                return;
            }

            _pages[tag] = page;
        }

        NavView.Content = page;
        PlayContentEntrance(page);
    }

    // NavigationView.ContentTransitions does not fire for a direct Content assignment, so the section
    // switch is animated here instead: a short fade + slide-in on the incoming page.
    private void PlayContentEntrance(object page)
    {
        if (page is not FrameworkElement element)
        {
            return;
        }

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(180));

        var fade = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fade, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(fade);

        var slide = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 24,
            To = 0,
            Duration = duration,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, element);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "(UIElement.RenderTransform).(TranslateTransform.Y)");
        storyboard.Children.Add(slide);

        element.RenderTransform = new Microsoft.UI.Xaml.Media.TranslateTransform();
        storyboard.Begin();
    }

    private AppearancePage CreateAppearancePage()
    {
        var page = new AppearancePage(_themeService);
        page.SettingsChanged += () => DispatcherQueue.TryEnqueue(ApplyWindowTheme);
        return page;
    }
}