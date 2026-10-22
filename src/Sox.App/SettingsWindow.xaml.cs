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
    private BackdropStyle? _appliedBackdropStyle;
    private AppUpdateInfo? _titleUpdate;
    private bool _titleUpdateBusy;

    public SettingsWindow(ThemeService themeService, SearchHost searchHost, AppUpdateService updates, Action exitForUpdate)
        : this(themeService, searchHost, updates, exitForUpdate, initialTag: null)
    {
    }

    public SettingsWindow(
        ThemeService themeService,
        SearchHost searchHost,
        AppUpdateService updates,
        Action exitForUpdate,
        string? initialTag)
    {
        _themeService = themeService;
        _searchHost = searchHost;
        _updates = updates;
        _exitForUpdate = exitForUpdate;
        InitializeComponent();

        ApplyWindowTheme();
        _themeService.ThemeChanged += OnThemeChanged;

        AppVersionText.Text = $"v{_updates.CurrentVersion}";
        _ = CheckTitleUpdateAsync();

        // RequestedTheme lands asynchronously, so the first ApplyWindowTheme still reads the previous
        // ActualTheme; without this re-apply the material keeps the old light/dark tint until the next
        // switch. MainWindow and the docked panel both rely on the same hook.
        RootElement.ActualThemeChanged += (_, _) => ApplyWindowTheme();

        // AppWindow.SetIcon only takes effect once the window has a taskbar button, which does not
        // exist during the constructor; the first activation is the earliest reliable point.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                ApplyWindowIcon();
            }
        };

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
            // Hand only the empty strip to the drag logic, so the version chip and the update button
            // stay clickable instead of acting as title-bar surface.
            SetTitleBar(TitleBarDragRegion);
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            ApplyWindowIcon();
        }
        catch (Exception ex)
        {
            Log.Error("SettingsWindow init failed", ex);
        }

        NavigateTo(initialTag ?? "general");

        // Open centred on screen (WinUIEx handles the monitor/DPI maths); without this it lands wherever
        // the OS default places a new window.
        WinUIEx.WindowExtensions.CenterOnScreen(this);
    }

    /// <summary>Selects the navigation item with the given tag, opening that page. No-op for an unknown
    /// tag (falls back to the first item).</summary>
    public void NavigateTo(string tag)
    {
        var item = NavView.MenuItems
            .OfType<Microsoft.UI.Xaml.Controls.NavigationViewItem>()
            .FirstOrDefault(i => string.Equals(i.Tag as string, tag, StringComparison.OrdinalIgnoreCase));

        NavView.SelectedItem = item ?? NavView.MenuItems[0];
    }

    private void OnThemeChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(ApplyWindowTheme);

    // Runs in the background on every open. The About page owns the verbose "check / update" flow; the
    // title bar only needs the quiet summary, so a failure here leaves the version chip and no button.
    private async Task CheckTitleUpdateAsync()
    {
        try
        {
            var info = AppUpdateService.LastResult ?? await _updates.CheckAsync();
            if (info.HasUpdate && info.Error is null)
            {
                _titleUpdate = info;
                TitleUpdateText.Text = $"更新到 v{info.LatestVersion}";
                TitleUpdateButton.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Title bar update check failed", ex);
        }
    }

    private async void OnTitleUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_titleUpdate is not { HasUpdate: true } info || _titleUpdateBusy)
        {
            return;
        }

        _titleUpdateBusy = true;
        TitleUpdateButton.IsEnabled = false;
        TitleUpdateText.Text = "下载中 0%";

        try
        {
            var progress = new Progress<double>(p => TitleUpdateText.Text = $"下载中 {p * 100:F0}%");
            var result = await _updates.PrepareUpdateAsync(progress);

            if (result.Error is not null)
            {
                TitleUpdateText.Text = $"更新失败：{result.Error}";
                return;
            }

            TitleUpdateText.Text = "即将重启…";

            if (_updates.ConsumePendingScript() is { } script)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"\"{script}\"\"",
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                });
            }

            _exitForUpdate();
        }
        catch (Exception ex)
        {
            Log.Error("Title bar update failed", ex);
            TitleUpdateText.Text = $"更新失败：{ex.Message}";
        }
        finally
        {
            _titleUpdateBusy = false;
            TitleUpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Applies the same material engine (Acrylic / Mica / Clear) and light/dark theme the
    /// spotlight window uses, so the settings surface matches the main window's look.
    /// </summary>
    private void ApplyWindowTheme()
    {
        var snapshot = _themeService.Current;
        RootElement.RequestedTheme = snapshot.Theme;

        // Recreating the backdrop on every theme change crashes inside the compositor: the previous
        // backdrop is still attached to the window and detaching it mid-swap trips an access violation
        // in Microsoft.UI.Xaml.dll. Only rebuild when the material itself changed; a pure light/dark
        // switch is handled by AlwaysActiveAcrylicBackdrop's own configuration callback.
        var style = snapshot.BackdropParameters.Style;
        if (_appliedBackdropStyle != style)
        {
            _appliedBackdropStyle = style;

            var previous = _windowBackdrop;
            _windowBackdrop = CreateWindowBackdrop(style);

            // WindowEx shadows SystemBackdrop with its own WinUIEx type; assign the base Window
            // property so MicaBackdrop / AlwaysActiveAcrylicBackdrop work. Assigning detaches the
            // previous backdrop, so it is only safe to dispose that one afterwards.
            ((Microsoft.UI.Xaml.Window)this).SystemBackdrop = _windowBackdrop;
            (previous as IDisposable)?.Dispose();
        }
        else if (_windowBackdrop is AlwaysActiveAcrylicBackdrop acrylic)
        {
            // Same material, new theme: hand the resolved theme to the live controllers instead of
            // relying on the framework's configuration callback, which fires before RequestedTheme
            // has propagated and would therefore apply the previous theme.
            acrylic.ApplyTheme(RootElement.ActualTheme);
        }
    }

    private void ApplyWindowIcon()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sox-tray.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }
        }
        catch (Exception ex)
        {
            Log.Error("SettingsWindow icon failed", ex);
        }
    }

    private Microsoft.UI.Xaml.Media.SystemBackdrop? CreateWindowBackdrop(BackdropStyle style) =>
        style switch
        {
            BackdropStyle.Mica => new Microsoft.UI.Xaml.Media.MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
            _ => new AlwaysActiveAcrylicBackdrop(),
        };

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
                "index" => new IndexPage(_searchHost, this),
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