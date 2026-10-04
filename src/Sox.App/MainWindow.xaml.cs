using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sox.App.Interop;
using Sox.App.Materials;
using Sox.App.Services;
using Sox.App.Services.QueryProviders;
using Sox.App.ViewModels;
using IQueryProvider = Sox.App.Services.QueryProviders.IQueryProvider;
using Sox.Core;
using Sox.Core.Indexer.Usn;
using Windows.System;
using Windows.UI;
using WinUIEx;

namespace Sox.App;

public sealed partial class MainWindow : WindowEx
{
    private readonly ThemeService _themeService;
    private readonly SearchHost _searchHost = new();
    private readonly TrayIconService _tray = new();
    private readonly EverythingIpcHost _everythingIpc;
    private readonly Services.QueryProviders.QueryProviderRegistry _queryProviders = Services.QueryProviders.QueryProviderRegistry.CreateDefault();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounceTimer;
    private readonly List<ResultItem> _results = [];
    private readonly SingleInstanceActivationServer _activationServer;

    private IntPtr _hwnd;
    private NativeMethods.WndProc? _wndProc;
    private IntPtr _originalWndProc;
    private bool _themeInitialized;
    private readonly HotkeyService _hotkeys;
    private readonly ImeController _ime;
    private readonly AppUpdateService _updates = new();
    private readonly Sox.Core.Hook.Ipc.HookIpcClient _hookIpc = new();

    public MainWindow(ThemeService themeService)
    {
        _themeService = themeService;
        _everythingIpc = new EverythingIpcHost(_searchHost);
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hotkeys = new HotkeyService(_hwnd);
        _ime = new ImeController(_hwnd);

        SystemBackdrop = new WinUIEx.TransparentTintBackdrop { TintColor = Microsoft.UI.Colors.Transparent };
        ConfigureWindow();

        _debounceTimer = DispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(30);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (_, _) => _ = RunSearchAsync();

        _previewHoverTimer = DispatcherQueue.CreateTimer();
        _previewHoverTimer.Interval = TimeSpan.FromMilliseconds(70);
        _previewHoverTimer.IsRepeating = false;
        _previewHoverTimer.Tick += OnPreviewHoverTick;

        RootCard.Loaded += (_, _) =>
        {
            RootCard.SetCardStretch(true);
            ApplyTheme();
            HookResultScroll();
        };
        RootCard.ActualThemeChanged += (_, _) => ApplyTheme();

        _themeService.ThemeChanged += (_, _) => DispatcherQueue.TryEnqueue(ApplyTheme);
        App.Current.SettingsChanged += OnAppSettingsChanged;
        _queryProviders.SuggestionsUpdated += OnSuggestionsUpdated;

        // A repeat launch (desktop icon, Start menu, pinned taskbar) is blocked by Program's mutex and
        // forwarded over a pipe; answer it by summoning this window instead of doing nothing.
        _activationServer = new SingleInstanceActivationServer(
            () => DispatcherQueue.TryEnqueue(ShowWindow));

        SetupTray();

        HideWindow();

        // Autostart launches with --minimized: stay hidden in the tray instead of flashing the spotlight.
        if (!Environment.GetCommandLineArgs().Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            ShowWindow();
        }

        _ = InitializeAsync();
        _ = AutoCheckUpdatesAsync();

        // Start the file-dialog integration: connects to the hook process (launched on demand via the
        // service) that owns the global keyboard/mouse hooks and Explorer/dialog tracking. Quick Switch
        // (Ctrl+G inside a file dialog) runs entirely inside that hook process, so starting it is all
        // this side needs for the feature to work.
        StartHookIntegration();
    }

    private void StartHookIntegration()
    {
        try
        {
            _hookIpc.OnError += message => Log.Warning($"Hook IPC: {message}");
            _hookIpc.OnExplorerActivated += OnExplorerActivated;
            _hookIpc.OnPathCaptured += OnPathCaptured;
            _hookIpc.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start hook integration", ex);
        }
    }

    private InlineSearchWindow? _inlineWindow;

    // A dialog (or Explorer window) became the active window. Only a common file dialog (#32770) gets the
    // docked panel; a plain Explorer window is served by the user's own navigation, not by us.
    private void OnExplorerActivated(IntPtr hwnd, string title, string className, bool isDesktop)
    {
        if (string.Equals(className, "#32770", StringComparison.OrdinalIgnoreCase))
        {
            _pendingDialogHwnd = hwnd;
        }
        else
        {
            _pendingDialogHwnd = IntPtr.Zero;
        }
    }

    private IntPtr _pendingDialogHwnd;

    // The dialog's current folder, captured right after activation. This is what the panel searches in
    // and shows as its header.
    private void OnPathCaptured(string path, bool isDesktop, bool isDialog)
    {
        if (!isDialog || _pendingDialogHwnd == IntPtr.Zero)
        {
            return;
        }

        var hwnd = _pendingDialogHwnd;
        var folder = path;
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                _inlineWindow ??= new InlineSearchWindow(_searchHost, _hookIpc, _themeService);
                _inlineWindow.ShowForDialog(hwnd, folder);
            }
            catch (Exception ex)
            {
                Log.Error("Inline: ShowForDialog failed", ex);
            }
        });
    }

    /// <summary>Silent background update check on startup, honouring AutoCheckUpdates. Only caches the
    /// result (AppUpdateService.LastResult); the About page surfaces it.</summary>
    private async Task AutoCheckUpdatesAsync()
    {
        try
        {
            if (!UserSettings.Load().AutoCheckUpdates)
                return;

            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(true);
            var info = await _updates.CheckAsync().ConfigureAwait(true);
            if (info.Error is not null)
                Log.Warning($"Auto update check failed: {info.Error}");
            else
                Log.Info($"Update check: current {info.CurrentVersion}, latest {info.LatestVersion}, hasUpdate={info.HasUpdate}");
        }
        catch (Exception ex)
        {
            Log.Error("Auto update check failed", ex);
        }
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<ResultItem> _visibleResults = [];
    private int _windowStart;

    private void HookResultScroll()
    {
        ResultList.ItemsSource = _visibleResults;
        ResultList.SelectionChanged += ResultList_SelectionChanged;
        ResultList.PointerMoved += ResultList_PointerMoved;
        ResultList.PointerExited += ResultList_PointerExited;

        ResultList.AddHandler(
            Microsoft.UI.Xaml.UIElement.PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler(ResultList_PointerWheelChanged),
            handledEventsToo: true);
    }

    // Lets a result row be dragged straight out to Explorer or another drop target as a real file/folder
    // (shared with the file-dialog panel -- see ResultDragDrop).
    private void ResultList_DragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        ResultDragDrop.OnDragItemsStarting(e);

    // ---- Preview pane (R3) ----

    private CancellationTokenSource? _previewCts;
    private ResultItem? _hoveredItem;
    private bool _resetting;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _previewHoverTimer;

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_menuOpen)
        {
            return;
        }

        // While the pointer is over the list, hover decides what is previewed; leaving the list
        // falls back to whatever the keyboard has selected.
        if (_hoveredItem is not null)
        {
            return;
        }

        _ = UpdatePreviewAsync(ResultList.SelectedItem as ResultItem);
    }

    private void ResultList_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (_menuOpen)
        {
            return;
        }

        var item = FindResultItem(e.OriginalSource as DependencyObject);
        if (ReferenceEquals(item, _hoveredItem))
        {
            return;
        }

        _hoveredItem = item;
        if (item is null)
        {
            _previewHoverTimer.Stop();
            _ = UpdatePreviewAsync(ResultList.SelectedItem as ResultItem);
            return;
        }

        // Debounce hover: while the pointer sweeps across many rows, only the row it settles on
        // is previewed, so rapid movement never queues a backlog of preview loads.
        _previewHoverTimer.Stop();
        _previewHoverTimer.Start();
    }

    private void ResultList_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        _previewHoverTimer.Stop();
        if (_hoveredItem is null)
        {
            return;
        }

        _hoveredItem = null;
        _ = UpdatePreviewAsync(ResultList.SelectedItem as ResultItem);
    }

    private void OnPreviewHoverTick(object? sender, object e)
    {
        _previewHoverTimer.Stop();
        if (_hoveredItem is { } item && !_menuOpen)
        {
            _ = UpdatePreviewAsync(item);
        }
    }

    private static ResultItem? FindResultItem(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ListViewItem listViewItem && listViewItem.Content is ResultItem item)
            {
                return item;
            }

            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private async Task UpdatePreviewAsync(ResultItem? item)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;

        // The preview column is a sibling of the results area, not a child, so collapsing the results
        // does not hide it. With an empty query the window shows recent files, and auto-selecting the
        // first row must not pop the preview open.
        if (item is null || string.IsNullOrEmpty(SearchBox.Text.Trim()))
        {
            HidePreview();
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        var content = await PreviewService.LoadAsync(item.Path, item.IsDir);
        if (cts.IsCancellationRequested)
        {
            return;
        }

        switch (content.Kind)
        {
            case PreviewKind.Image:
                ShowPreviewImage(content.Bytes!);
                break;

            case PreviewKind.Svg:
                ShowPreviewSvg(content.Bytes!);
                break;

            case PreviewKind.Text:
                ShowPreviewText(content);
                break;

            default:
                HidePreview();
                break;
        }
    }

    private void ShowPreviewImage(byte[] bytes)
    {
        PreviewTextScroll.Visibility = Visibility.Collapsed;
        PreviewImage.Source = CreateBitmap(bytes);
        PreviewImage.Visibility = Visibility.Visible;
        SetPreviewVisible(true);
    }

    private void ShowPreviewSvg(byte[] bytes)
    {
        PreviewTextScroll.Visibility = Visibility.Collapsed;
        PreviewImage.Source = CreateSvg(bytes);
        PreviewImage.Visibility = Visibility.Visible;
        SetPreviewVisible(true);
    }

    private void ShowPreviewText(PreviewContent content)
    {
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewText.Inlines.Clear();

        if (content.Tokens is { } tokens)
        {
            foreach (var token in tokens)
            {
                var run = new Microsoft.UI.Xaml.Documents.Run { Text = token.Text };
                if (TokenBrush(token.Kind) is { } brush)
                {
                    run.Foreground = brush;
                }

                PreviewText.Inlines.Add(run);
            }
        }

        PreviewTextScroll.Visibility = Visibility.Visible;
        SetPreviewVisible(true);
    }

    private void HidePreview()
    {
        SetPreviewVisible(false);
        PreviewImage.Source = null;
        PreviewText.Inlines.Clear();
    }

    private void SetPreviewVisible(bool visible)
    {
        if (visible)
        {
            PreviewHost.Visibility = Visibility.Visible;
            PreviewColumn.Width = new GridLength(PreviewWidthDip);
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            PreviewHost.Visibility = Visibility.Collapsed;
            PreviewColumn.Width = new GridLength(0);
        }

        DispatcherQueue.TryEnqueue(ResizeToContent);
    }

    private static Microsoft.UI.Xaml.Media.Brush? TokenBrush(TokenKind kind) =>
        Application.Current.Resources[$"Sox.Syntax.{kind}"] as Microsoft.UI.Xaml.Media.Brush;

    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CreateBitmap(byte[] bytes)
    {
        try
        {
            var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var writer = new Windows.Storage.Streams.DataWriter(stream);
            try
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
                writer.DetachStream();
            }
            finally
            {
                writer.Dispose();
            }

            stream.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            image.SetSource(stream);
            return image;
        }
        catch (Exception ex)
        {
            Log.Error("CreateBitmap failed", ex);
            return null;
        }
    }

    private static Microsoft.UI.Xaml.Media.Imaging.SvgImageSource? CreateSvg(byte[] bytes)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"sox-preview-{Guid.NewGuid():N}.svg");
            File.WriteAllBytes(temp, bytes);
            var image = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource { UriSource = new Uri(temp) };
            return image;
        }
        catch (Exception ex)
        {
            Log.Error("CreateSvg failed", ex);
            return null;
        }
    }

    // The list is windowed to MaxVisibleRows items, so scrolling is managed here instead of
    // by the inner ScrollViewer. That keeps every wheel step exactly one row.
    private void ResultList_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(ResultList).Properties.MouseWheelDelta;
        if (delta == 0)
        {
            return;
        }

        ScrollWindow(delta > 0 ? -1 : 1);
        e.Handled = true;
    }

    private void ScrollWindow(int rowDelta)
    {
        var maxStart = Math.Max(0, _results.Count - MaxVisibleRows);
        ScrollWindowTo(Math.Clamp(_windowStart + rowDelta, 0, maxStart));
    }

    private void RebuildVisibleWindow()
    {
        _windowStart = 0;
        SyncVisibleWindow();
    }

    // Replace rows in place instead of clearing/rebuilding: the ListViewItem containers are
    // reused, so neither streaming updates nor scrolling cause a structural rebuild (flicker).
    private void SyncVisibleWindow()
    {
        // Fast path for an emptied result set: RemoveAt in the loop below advances i while the list
        // shrinks, so it can leave trailing rows behind. Clearing outright is what makes the list go
        // blank the moment the box is cleared.
        if (_results.Count == 0)
        {
            if (_visibleResults.Count > 0)
            {
                _visibleResults.Clear();
            }

            return;
        }

        for (var i = 0; i < MaxVisibleRows; i++)
        {
            var index = _windowStart + i;
            var item = index < _results.Count ? _results[index] : null;

            if (i < _visibleResults.Count)
            {
                if (ReferenceEquals(_visibleResults[i], item))
                {
                    continue;
                }

                if (item is null)
                {
                    _visibleResults.RemoveAt(i);
                }
                else
                {
                    _visibleResults[i] = item;
                }
            }
            else if (item is not null)
            {
                _visibleResults.Add(item);
            }
        }

        ApplyShortcutLabels();
        RequestVisibleIcons();
    }

    // Icons are only resolved for the rows actually on screen: a search can hold hundreds of results,
    // and decoding every one would swamp the icon worker while only nine are ever visible.
    private void RequestVisibleIcons()
    {
        var pixelSize = IconPixelSize;
        foreach (var item in _visibleResults)
        {
            item.RequestIcon(DispatcherQueue, pixelSize);
        }
    }

    private void ScrollWindowTo(int start)
    {
        start = Math.Clamp(start, 0, Math.Max(0, _results.Count - MaxVisibleRows));
        if (start == _windowStart)
        {
            return;
        }

        _windowStart = start;
        SyncVisibleWindow();
    }

    private void ApplyShortcutLabels()
    {
        for (var i = 0; i < _visibleResults.Count; i++)
        {
            _visibleResults[i].ShortcutText = $"Ctrl+{i + 1}";
        }
    }

    private void SetupTray()
    {
        _tray.OpenRequested += () => DispatcherQueue.TryEnqueue(ShowWindow);
        _tray.SettingsRequested += () => DispatcherQueue.TryEnqueue(() => OpenSettings());
        _tray.AboutRequested += () => DispatcherQueue.TryEnqueue(ShowAbout);
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(ExitApp);
        _tray.ExitAndStopServiceRequested += () => DispatcherQueue.TryEnqueue(ExitAppAndStopService);

        var hide = Sox.Core.UserSettings.Load().HideTrayIcon;
        _tray.Show(!hide);
    }

    private SettingsWindow? _settingsWindow;

    /// <summary>Exits so a staged update script can replace the files and relaunch. The script waits on
    /// Sox.App.exe, so the process has to be gone before it proceeds.</summary>
    public void ExitForUpdate()
    {
        try
        {
            _tray.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("Tray dispose during update failed", ex);
        }

        Application.Current.Exit();
    }

    private void OpenSettings() => OpenSettings(null);

    private void OpenSettings(string? initialTag)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            if (initialTag is not null)
            {
                _settingsWindow.NavigateTo(initialTag);
            }

            return;
        }

        _settingsWindow = new SettingsWindow(_themeService, _searchHost, _updates, ExitForUpdate, initialTag);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            try
            {
                _everythingIpc.Apply(UserSettings.Load().EnableEverythingIpc);
            }
            catch (Exception ex)
            {
                Log.Error("Re-apply Everything IPC failed", ex);
            }
        };
        _settingsWindow.Activate();
    }

    private void ShowAbout() => OpenSettings("about");

    private void ExitApp()
    {
        _tray.Dispose();
        Application.Current.Exit();
    }

    /// <summary>Exit like <see cref="ExitApp"/> but also stop the Windows service first, so no Sox
    /// process is left behind. Stopping on the UI thread would block on the SCM poll, so it runs on a
    /// background thread and the app exits once the service reports STOPPED.</summary>
    private async void ExitAppAndStopService()
    {
        _tray.Dispose();
        await Task.Run(() => ServiceBootstrapper.TryStop());
        Application.Current.Exit();
    }

    private void ConfigureWindow()
    {
        try
        {
            AppWindow.Title = "Sox";
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "sox-tray.ico");
            if (File.Exists(icon))
            {
                AppWindow.SetIcon(icon);
            }

            ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("ConfigureWindow failed", ex);
        }

        StripWindowChrome();
        ApplyBorderAttributes();
        InstallHotkeyHook();
    }

    private void StripWindowChrome()
    {
        var style = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_STYLE);
        style &= ~(NativeMethods.WS_CAPTION
            | NativeMethods.WS_THICKFRAME
            | NativeMethods.WS_BORDER
            | NativeMethods.WS_DLGFRAME
            | NativeMethods.WS_SYSMENU
            | NativeMethods.WS_MINIMIZEBOX
            | NativeMethods.WS_MAXIMIZEBOX);
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_STYLE, style);

        // Tool window: keeps the spotlight out of the taskbar, Alt+Tab and the recent-jump list, which
        // is what a summon-and-hide utility should be. Applied to the extended style (not the regular
        // one) so it does not affect the non-client look we already stripped above.
        var exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
    }

    private void ApplyBorderAttributes()
    {
        var corner = NativeMethods.DWMWCP_DONOTROUND;
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        var border = unchecked((int)NativeMethods.DWMWA_COLOR_NONE);
        NativeMethods.DwmSetWindowAttribute(_hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        NativeMethods.RedrawWindow(
            _hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_FRAME);
    }

    private void InstallHotkeyHook()
    {
        _wndProc = WndProcImpl;
        var pointer = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_wndProc);
        _originalWndProc = NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_WNDPROC, pointer);

        _hotkeys.Pressed += () => DispatcherQueue.TryEnqueue(OnSummonHotkeyPressed);
        ApplySummonHotkey(UserSettings.Load().SummonHotkey);
    }

    // Ignore the summon while a fullscreen app owns the foreground, when the user opted into that, so
    // the spotlight never pops over a game. Toggling an already-visible window is always allowed (the
    // user clearly wants it gone); only the "show" direction is suppressed.
    private void OnSummonHotkeyPressed()
    {
        if (!IsVisibleToUser()
            && UserSettings.Load().DisableHotkeyInFullscreen
            && FullscreenDetector.IsForegroundFullscreen())
        {
            return;
        }

        ToggleWindow();
    }

    /// <summary>Binds (or re-binds) the summon hotkey from settings. Called at startup and whenever the
    /// hotkey page saves, so a change takes effect without a restart.</summary>
    private void ApplySummonHotkey(string hotkey)
    {
        if (_hotkeys.Register(hotkey))
            return;

        Log.Warning($"Summon hotkey '{hotkey}' could not be registered");

        // If a previous binding was restored, the requested one is unusable right now; write the actual
        // bound value back so the settings page reflects reality instead of showing a hotkey that does
        // nothing. At startup there is no previous binding, so leave the preference untouched.
        var actual = _hotkeys.Current;
        if (!string.IsNullOrEmpty(actual) && !string.Equals(actual, hotkey, StringComparison.OrdinalIgnoreCase))
        {
            var settings = UserSettings.Load();
            if (!string.Equals(settings.SummonHotkey, actual, StringComparison.Ordinal))
            {
                settings.SummonHotkey = actual;
                settings.Save();
                App.Current.RaiseSettingsChanged();
            }
        }
    }

    private IntPtr WndProcImpl(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (_hotkeys.HandleMessage(msg, wParam))
        {
            return IntPtr.Zero;
        }

        // The mouse's side buttons act as Back: while the action menu is open either side button closes
        // it and returns to the result list (matching Esc / Left arrow), and while a keyword scope is
        // active it leaves the scope. Both XBUTTON1 (the usual "back" side button) and XBUTTON2
        // ("forward") do the same, since inside the spotlight there is no forward history to go to.
        if (msg == NativeMethods.WM_XBUTTONUP)
        {
            var button = (int)((long)wParam >> 16) & 0xFFFF;
            if (button is NativeMethods.XBUTTON1 or NativeMethods.XBUTTON2)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_menuOpen)
                    {
                        CloseMenu();
                    }
                    else if (_scopeProvider is not null)
                    {
                        ExitScope();
                    }
                });
                return IntPtr.Zero;
            }
        }

        if (msg == NativeMethods.WM_DPICHANGED)
        {
            var result = NativeMethods.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            DispatcherQueue.TryEnqueue(() =>
            {
                ResizeToContent();
                // Re-decode the visible rows' icons at the new scale so they stay crisp across monitors.
                RequestVisibleIcons();
            });
            return result;
        }

        return NativeMethods.CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
    }

    private CancellationTokenSource? _statusCts;
    private string? _lastStatusSignature;

    private async Task InitializeAsync()
    {
        var ready = await EnsureServiceReadyAsync();
        if (!ready)
        {
            Log.Warning("SoxService is not reachable");
            return;
        }

        var cts = new CancellationTokenSource();
        _statusCts = cts;
        _ = Task.Run(() => _searchHost.SubscribeStatusAsync(OnIndexStatus, cts.Token));

        PushSearchContext(UserSettings.Load());

        try
        {
            _everythingIpc.Apply(UserSettings.Load().EnableEverythingIpc);
        }
        catch (Exception ex)
        {
            Log.Error("Apply Everything IPC failed", ex);
        }
    }

    // One shared readiness task: both InitializeAsync and the startup recent-files load need the
    // service up, and without sharing they would race to bootstrap it (and the recent load would query
    // the pipe before the service answered, timing out on the first show).
    private Task<bool>? _serviceReady;

    private Task<bool> EnsureServiceReadyAsync() => _serviceReady ??= _searchHost.EnsureServiceAsync();

    private void OnIndexStatus(UsnIndexer.IndexerStatus status)
    {
        var show = IsIndexingState(status.State);

        // Only log when the state or progress actually changes; the service pushes status
        // repeatedly even while idle, which would otherwise drown the log.
        var signature = $"{status.State}|{status.Progress}";
        if (signature != _lastStatusSignature)
        {
            _lastStatusSignature = signature;
            Log.Info($"Index status: {status.State} {status.Progress}%");
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            var isVisible = IndexStatusStrip.Visibility == Visibility.Visible;
            if (!show)
            {
                if (isVisible)
                {
                    IndexStatusStrip.Visibility = Visibility.Collapsed;
                    IndexProgressRing.IsActive = false;
                    DispatcherQueue.TryEnqueue(ResizeToContent);
                }

                return;
            }

            IndexStatusStrip.Visibility = Visibility.Visible;
            IndexProgressRing.IsActive = true;
            IndexStatusText.Text = status.State switch
            {
                "indexing" or "pending" => $"正在索引… {status.Progress}%",
                "loading-cache" => "正在加载索引缓存…",
                "maintenance" => "正在维护索引…",
                _ => $"索引中：{string.Join(", ", status.ActiveDrives)}",
            };
            DispatcherQueue.TryEnqueue(ResizeToContent);
        });
    }

    private static bool IsIndexingState(string state) =>
        state is "pending" or "indexing" or "loading-cache" or "maintenance";

    private void ApplyTheme()
    {
        var snapshot = _themeService.Current;
        RootCard.RequestedTheme = snapshot.Theme;
        _tray.ApplyTheme(snapshot.Theme);
        var config = BackdropStyles.Get(snapshot.BackdropParameters.Style);
        RootCard.ApplyBackdrop(snapshot.BackdropParameters, config.ControllerKind, isImageMode: false, hasColorization: false);
    }

    // Settings pages raise App.SettingsChanged after every save. Re-read the file and re-apply the
    // pieces the spotlight reads live: search matching flags, tray visibility, and geometry (so a
    // width/preview-size change is reflected on the spot without needing a search or a restart).
    private void OnAppSettingsChanged(object? sender, EventArgs e)
    {
        try
        {
            var settings = UserSettings.Load();
            PushSearchContext(settings);
            _tray.Show(!settings.HideTrayIcon);
            ApplySummonHotkey(settings.SummonHotkey);
            // Re-assert the Run command only when autostart is already on (so a changed "start
            // minimized" preference is picked up) -- never re-enable an entry the user removed.
            StartupService.RefreshIfEnabled();
            ResizeToContent();
        }
        catch (Exception ex)
        {
            Log.Error("Apply settings live failed", ex);
        }
    }

    private static void PushSearchContext(UserSettings settings)
    {
        SearchContext.DefaultFuzzyMatchEnabled = settings.EnableFuzzyMatch;
        SearchContext.DefaultAndFirstPrecedence = !settings.OrFirstPrecedence;
    }

    public void ToggleWindow()
    {
        if (IsVisibleToUser())
        {
            HideWindow();
        }
        else
        {
            ShowWindow();
        }
    }

    private bool IsVisibleToUser()
    {
        if (!Visible || WindowCloak.IsCloaked(_hwnd))
        {
            return false;
        }

        return true;
    }

    private void ShowWindow()
    {
        try
        {
            if (NativeMethods.IsIconic(_hwnd))
            {
                WindowCloak.Cloak(_hwnd);
                NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_RESTORE);
            }

            PositionCentered();
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOW);
            ApplyBorderAttributes();
            WindowCloak.Uncloak(_hwnd);
            ForegroundHelper.ForceForeground(_hwnd);
            NativeMethods.SetWindowPos(
                _hwnd,
                NativeMethods.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_FRAMECHANGED);
            NativeMethods.RedrawWindow(
                _hwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.RDW_INVALIDATE | NativeMethods.RDW_UPDATENOW | NativeMethods.RDW_ALLCHILDREN | NativeMethods.RDW_FRAME);

            if (!_themeInitialized)
            {
                _themeInitialized = true;
                ApplyTheme();
            }

            RootCard.SetIsInputActive(true);
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
            ScheduleRecentFilesOnShow();
        }
        catch (Exception ex)
        {
            Log.Error("ShowWindow failed", ex);
        }
    }

    // HideWindow blanks the box and the list. Restore the usual "empty query shows recent files"
    // behaviour for the new session, but only once the window is actually up, so the fill never races
    // the show and flashes during the fade-in.
    private void ScheduleRecentFilesOnShow()
    {
        if (!string.IsNullOrEmpty(SearchBox.Text))
        {
            return;
        }

        _ = LoadRecentAsync();
    }

    private void HideWindow()
    {
        var cloaked = WindowCloak.Cloak(_hwnd);
        NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);

        if (cloaked)
        {
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_SHOWNA);
        }

        RootCard.SetIsInputActive(false);
        ResetSearchState();
    }

    // Put the spotlight back to a clean slate on hide, so the next summon never flashes the previous
    // query, results, preview or action menu before the first keystroke. Text is cleared without going
    // through the debounce, and the in-flight search is cancelled so it cannot repopulate the list.
    private void ResetSearchState()
    {
        _debounceTimer.Stop();
        _previewHoverTimer.Stop();
        _searchHost.CancelSearch();

        _currentQuery = string.Empty;
        _resultCache.Clear();
        _hoveredItem = null;

        _resetting = true;
        try
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
            }
        }
        finally
        {
            _resetting = false;
        }

        if (_menuOpen)
        {
            CloseMenu();
        }

        ExitScope();
        HidePreview();
        ApplyResults([]);
    }

    private void PositionCentered()
    {
        ResizeToContent();
    }

    private double ScaleFactor()
    {
        var dpi = NativeMethods.GetDpiForWindow(_hwnd);
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    private const double ResultIconDip = 28;

    // Source icon pixels for the row icon: the on-screen physical size, rounded up to a small set of
    // buckets so the decoded bitmaps stay crisp on high-DPI/HDR panels without a separate decode per
    // fractional scale.
    private int IconPixelSize
    {
        get
        {
            var needed = (int)Math.Ceiling(ResultIconDip * ScaleFactor());
            foreach (var bucket in new[] { 48, 64, 96, 128, 192, 256 })
            {
                if (needed <= bucket)
                {
                    return bucket;
                }
            }

            return 256;
        }
    }

    private double ScaleForDisplay(Microsoft.UI.Windowing.DisplayArea display)
    {
        var center = new NativeMethods.POINT
        {
            X = display.WorkArea.X + display.WorkArea.Width / 2,
            Y = display.WorkArea.Y + display.WorkArea.Height / 2,
        };
        var monitor = NativeMethods.MonitorFromPoint(center, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero &&
            NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }

        return ScaleFactor();
    }

    private Microsoft.UI.Windowing.DisplayArea GetTargetDisplay()
    {
        // Spotlight behaviour: summon on whichever monitor the cursor is on.
        if (NativeMethods.GetCursorPos(out var cursor))
        {
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromPoint(
                new Windows.Graphics.PointInt32(cursor.X, cursor.Y),
                Microsoft.UI.Windowing.DisplayAreaFallback.Nearest);
            if (area is not null)
            {
                return area;
            }
        }

        return Microsoft.UI.Windowing.DisplayArea.Primary;
    }

    private const double SearchRowHeight = 56;
    private const double ResultItemHeight = 48;
    private const double ActionItemHeight = 40;
    private const double ResultsHostPadding = 12;
    private const double EmptyStateHeight = 140;
    private const int DefaultWindowWidthDip = 720;
    private const int MaxVisibleRows = 9;

    private double WindowWidthDip
    {
        get
        {
            try
            {
                var w = UserSettings.Load().SearchWindow.SearchBarWidth;
                return w > 300 ? w : DefaultWindowWidthDip;
            }
            catch
            {
                return DefaultWindowWidthDip;
            }
        }
    }

    private double PreviewWidthDip
    {
        get
        {
            try
            {
                var w = UserSettings.Load().PreviewWindow.Width;
                return w > 0 ? w : 400;
            }
            catch
            {
                return 400;
            }
        }
    }

    private void ResizeToContent()
    {
        try
        {
            var display = GetTargetDisplay();
            var work = display.WorkArea;

            var pad = RootCard.ShadowPadding.Top + RootCard.ShadowPadding.Bottom;

            // The spotlight column keeps its own natural height. The preview is a full-height right
            // column of the same card, so sizing the card to the spotlight alone leaves no blank
            // strip under the results; the preview simply fills whatever height that is.
            var cardHeight = SearchRowHeight;
            if (IndexStatusStrip.Visibility == Visibility.Visible)
            {
                cardHeight += 28;
            }

            if (ResultsHost.Visibility == Visibility.Visible)
            {
                cardHeight += 1;
                if (_menuOpen)
                {
                    // Every action is listed at once -- no scrolling -- so the card grows to fit them all.
                    cardHeight += _actions.Count * ActionItemHeight + ResultsHostPadding;
                }
                else
                {
                    cardHeight += _results.Count > 0
                        ? Math.Min(_results.Count, MaxVisibleRows) * ResultItemHeight + ResultsHostPadding
                        : EmptyStateHeight;
                }
            }

            var previewVisible = PreviewHost.Visibility == Visibility.Visible;

            var scale = ScaleForDisplay(display);
            var widthDip = previewVisible ? WindowWidthDip + PreviewWidthDip : WindowWidthDip;
            var width = (int)Math.Round(widthDip * scale);
            var height = (int)Math.Round((cardHeight + pad) * scale);

            // Pin the left edge where the spotlight alone would sit, so opening the preview expands
            // to the right without sliding the search box sideways.
            var spotlightWidth = (int)Math.Round(WindowWidthDip * scale);
            var x = work.X + (work.Width - spotlightWidth) / 2;
            var y = work.Y + (int)Math.Round(work.Height * 0.20);

            // The action menu lists every entry at once, so on a short screen the card can reach past
            // the bottom edge. Slide it up just enough to keep the whole card on screen; never above
            // the top (a card taller than the work area simply starts at the top and clips at the end).
            var workBottom = work.Y + work.Height;
            if (y + height > workBottom)
            {
                y = Math.Max(work.Y, workBottom - height);
            }

            NativeMethods.SetWindowPos(
                _hwnd,
                IntPtr.Zero,
                x,
                y,
                width,
                height,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Error("ResizeToContent failed", ex);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // ResetSearchState / EnterScope set the text programmatically; without this guard the paths
        // below would kick off a load or re-detect the scope mid-edit.
        if (_resetting)
        {
            return;
        }

        // Keep the box to ASCII while not in a network-search scope, so file search never needs an IME
        // (the index matches pinyin, so Latin letters still find Chinese names). Applied here rather than
        // by disabling the IME, so the user's input method is left alone.
        if (_scopeProvider is null && UserSettings.Load().AsciiOnlySearchBox && HasNonAscii(SearchBox.Text))
        {
            StripNonAscii();
        }

        var text = SearchBox.Text;

        // Enter scope the instant the user types "keyword " (a known keyword followed by a space) and no
        // scope is active yet. The keyword moves into the badge and the box keeps only what follows.
        if (_scopeProvider is not null)
        {
            // Scope stays active even with an empty box -- the badge is showing and the user is about to
            // type the term. Leaving the scope is explicit (hide/reset), not a side effect of an empty
            // box, which the entry itself briefly produces while stripping the keyword.
        }
        else
        {
            var space = text.IndexOf(' ');
            if (space > 0 &&
                _queryProviders.TryMatchKeyword(text[..space], out var scope, out var provider))
            {
                EnterScope(scope, provider);
                return;
            }
        }

        var hasText = text.Length > 0;
        ClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

        if (!hasText)
        {
            _debounceTimer.Stop();
            _searchHost.CancelSearch();
            _currentQuery = string.Empty;
            _resultCache.Clear();
            _hoveredItem = null;
            HidePreview();

            // In scope mode an empty box just means "no term yet": show an empty list, not recent files.
            if (_scopeProvider is not null)
            {
                ApplyResults([]);
                return;
            }

            // Clear the rows synchronously so the list empties the instant the box does; the recent
            // files are then filled in asynchronously (that call crosses the pipe).
            ApplyResults([]);
            _ = LoadRecentAsync();
            return;
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus(FocusState.Programmatic);
    }

    // Try to keep the box in English input mode while focused, so an IME candidate window does not pop
    // up during file search. Network-search scopes are exempt (a search term is free text). Best-effort:
    // legacy IMM32 IMEs obey this, TSF-based third-party IMEs may not (see ImeController).
    private void SearchBox_GotFocus(object sender, RoutedEventArgs e) => ApplyImeMode();

    private void SearchBox_LostFocus(object sender, RoutedEventArgs e) => _ime.Restore();

    private void ApplyImeMode()
    {
        if (_scopeProvider is not null || !UserSettings.Load().AsciiOnlySearchBox)
        {
            _ime.Restore();
            return;
        }

        _ime.ForceEnglish();
    }

    private static bool HasNonAscii(string text)
    {
        foreach (var c in text)
        {
            if (c > 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    // Removes every non-ASCII character in place, preserving the caret by counting how many characters
    // before it survive. Guarded by _resetting so the reassignment does not re-enter this handler.
    private void StripNonAscii()
    {
        var original = SearchBox.Text;
        var caret = SearchBox.SelectionStart;

        var builder = new System.Text.StringBuilder(original.Length);
        var keptBeforeCaret = 0;
        for (var i = 0; i < original.Length; i++)
        {
            if (original[i] > 0x7F)
            {
                continue;
            }

            if (i < caret)
            {
                keptBeforeCaret++;
            }

            builder.Append(original[i]);
        }

        _resetting = true;
        try
        {
            SearchBox.Text = builder.ToString();
            SearchBox.SelectionStart = Math.Clamp(keptBeforeCaret, 0, SearchBox.Text.Length);
        }
        finally
        {
            _resetting = false;
        }
    }

    // PreviewKeyDown runs before the TextBox applies the edit, so SearchBox.Text still reflects the
    // PRE-delete state. That is what makes "delete the last character" not exit the scope: the first
    // backspace that empties the box sees a non-empty Text and passes through; only the NEXT backspace,
    // pressed with the box already empty, sees Text.Length == 0 and leaves the mode.
    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Back && _scopeProvider is not null && SearchBox.Text.Length == 0)
        {
            ExitScope();
            e.Handled = true;
        }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {        if (IsControlDown())
        {
            var index = DigitIndexForKey(e.Key);
            if (index >= 0)
            {
                if (_menuOpen)
                {
                    ExecuteActionByIndex(index);
                }
                else
                {
                    OpenByIndex(index);
                }

                e.Handled = true;
                return;
            }
        }

        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (_scopeProvider is not null)
                {
                    ExitScope();
                }
                else if (_menuOpen)
                {
                    CloseMenu();
                }
                else
                {
                    HideWindow();
                }

                e.Handled = true;
                break;

            case VirtualKey.Left:
                if (_menuOpen)
                {
                    CloseMenu();
                }
                else
                {
                    OpenSelected();
                }

                e.Handled = true;
                break;

            case VirtualKey.Right:
                if (!_menuOpen)
                {
                    OpenMenuForSelected();
                }

                e.Handled = true;
                break;

            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (_menuOpen)
                {
                    ExecuteSelectedAction();
                }
                else
                {
                    OpenSelected();
                }

                e.Handled = true;
                break;
        }
    }

    private static bool IsControlDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private static int DigitIndexForKey(VirtualKey key) => key switch
    {
        >= VirtualKey.Number1 and <= VirtualKey.Number9 => key - VirtualKey.Number1,
        >= VirtualKey.NumberPad1 and <= VirtualKey.NumberPad9 => key - VirtualKey.NumberPad1,
        _ => -1,
    };

    private void OpenByIndex(int visibleRow)
    {
        if (visibleRow >= 0 && visibleRow < _visibleResults.Count)
        {
            ResultList.SelectedIndex = visibleRow;
            Open(_visibleResults[visibleRow]);
        }
    }

    private void MoveSelection(int delta)
    {
        if (_menuOpen)
        {
            MoveActionSelection(delta);
            return;
        }

        if (_results.Count == 0)
        {
            return;
        }

        var current = _windowStart + Math.Max(0, ResultList.SelectedIndex);
        var target = Math.Clamp(current + delta, 0, _results.Count - 1);
        SelectGlobalIndex(target);
    }

    private void SelectGlobalIndex(int globalIndex)
    {
        if (globalIndex < 0 || globalIndex >= _results.Count)
        {
            return;
        }

        // Scroll the window only when the target is off screen.
        if (globalIndex < _windowStart)
        {
            ScrollWindowTo(globalIndex);
        }
        else if (globalIndex >= _windowStart + MaxVisibleRows)
        {
            ScrollWindowTo(globalIndex - MaxVisibleRows + 1);
        }

        var visibleRow = globalIndex - _windowStart;
        if (visibleRow >= 0 && visibleRow < _visibleResults.Count)
        {
            ResultList.SelectedIndex = visibleRow;
        }
    }

    private void OpenSelected()
    {
        if (ResultList.SelectedItem is ResultItem item)
        {
            Open(item);
        }
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItem item)
        {
            Open(item);
        }
    }

    // Right-click on a result row opens the same action menu the keyboard Right arrow does, targeting the
    // row under the pointer (not whatever was previously selected). Without this binding a right-click did
    // nothing -- only the keyboard path could open the menu.
    private void ResultList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindResultItem(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        // Move the selection to the clicked row so the menu (and any action that acts on "the selected
        // item") targets what the user actually right-clicked.
        var row = _visibleResults.IndexOf(item);
        if (row >= 0)
        {
            ResultList.SelectedIndex = row;
        }

        OpenMenu(item);
        e.Handled = true;
    }

    private void Open(ResultItem item)
    {
        try
        {
            if (item.Instant is { } instant)
            {
                ExecuteInstant(instant);
                return;
            }

            // Capture the keyword before HideWindow: it resets the search box, so reading it afterwards
            // recorded an empty keyword and SearchHistoryStore.Record dropped the entry -- file/folder
            // opens never entered history and so were never ranked back to the top.
            var kind = item.IsDir ? Sox.PluginSdk.Services.HistoryEntryKind.Folder : Sox.PluginSdk.Services.HistoryEntryKind.File;
            var query = SearchBox.Text;

            Process.Start(new ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true,
            });

            HideWindow();
            Task.Run(() => Sox.Core.SearchHistoryStore.Record(query, item.Path, kind));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open '{item.Path}'", ex);
        }
    }

    private void ExecuteInstant(Services.QueryProviders.InstantResult instant)
    {
        switch (instant.Action)
        {
            case Services.QueryProviders.InstantAction.Copy:
                CopyToClipboard(instant.LaunchTarget);
                break;

            case Services.QueryProviders.InstantAction.ActivateWindow:
                if (long.TryParse(instant.LaunchTarget, out var handle))
                {
                    var hwnd = new IntPtr(handle);
                    if (NativeMethods.IsIconic(hwnd))
                    {
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    }

                    ForegroundHelper.ForceForeground(hwnd);
                }

                break;

            case Services.QueryProviders.InstantAction.RunCommand:
                Process.Start(new ProcessStartInfo
                {
                    FileName = instant.LaunchTarget,
                    Arguments = instant.Arguments,
                    UseShellExecute = true,
                    WorkingDirectory = instant.WorkingDirectory,
                    WindowStyle = instant.RunSilently ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    Verb = instant.RunAsAdmin ? "runas" : string.Empty,
                });
                break;

            default:
                Process.Start(new ProcessStartInfo
                {
                    FileName = instant.LaunchTarget,
                    UseShellExecute = true,
                });

                if (instant.Id.StartsWith("app:", StringComparison.Ordinal))
                {
                    var appQuery = SearchBox.Text;
                    var appTarget = instant.LaunchTarget;
                    Task.Run(() => Sox.Core.SearchHistoryStore.Record(appQuery, appTarget, Sox.PluginSdk.Services.HistoryEntryKind.Application));
                }

                break;
        }

        HideWindow();
    }

    private readonly Dictionary<string, ResultItem> _resultCache = new(StringComparer.OrdinalIgnoreCase);
    private string _currentQuery = string.Empty;

    // Active scope mode: non-null while the box is isolated to one provider ("g 你好"). The keyword has
    // already been consumed into the badge and removed from the box, so the box holds only the term.
    private IQueryProvider? _scopeProvider;
    private string _scopeKeyword = string.Empty;

    // ---- Action menu (R2) ----

    private readonly System.Collections.ObjectModel.ObservableCollection<ActionItem> _actions = [];
    private bool _menuOpen;
    private ResultItem? _menuTarget;

    private void OpenMenuForSelected()
    {
        if (_visibleResults.Count == 0)
        {
            return;
        }

        var row = Math.Max(0, ResultList.SelectedIndex);
        if (row >= _visibleResults.Count)
        {
            return;
        }

        OpenMenu(_visibleResults[row]);
    }

    private void OpenMenu(ResultItem item)
    {
        _menuTarget = item;
        _menuOpen = true;

        _actions.Clear();
        foreach (var action in BuildActions(item))
        {
            _actions.Add(action);
        }

        ActionList.ItemsSource = _actions;
        ActionList.SelectedIndex = 0;
        ResultList.Visibility = Visibility.Collapsed;
        ActionList.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        HidePreview();

        DispatcherQueue.TryEnqueue(ResizeToContent);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void CloseMenu()
    {
        _menuOpen = false;
        _menuTarget = null;
        ActionList.Visibility = Visibility.Collapsed;
        ResultList.Visibility = Visibility.Visible;
        ActionList.ItemsSource = null;
        DispatcherQueue.TryEnqueue(ResizeToContent);
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void MoveActionSelection(int delta)
    {
        if (_actions.Count == 0)
        {
            return;
        }

        // The menu lists every action at once (no scrolling), so moving the selection never needs to
        // scroll anything into view.
        ActionList.SelectedIndex = Math.Clamp(ActionList.SelectedIndex + delta, 0, _actions.Count - 1);
    }

    private void ExecuteSelectedAction()
    {
        if (ActionList.SelectedItem is ActionItem action)
        {
            ExecuteAction(action);
        }
    }

    private void ExecuteActionByIndex(int index)
    {
        if (index >= 0 && index < _actions.Count)
        {
            ActionList.SelectedIndex = index;
            ExecuteAction(_actions[index]);
        }
    }

    private void ActionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ActionItem action)
        {
            ExecuteAction(action);
        }
    }

    // Right-click anywhere in the action menu is a Back gesture: it closes the menu and returns to the
    // result list (the same as Esc / Left arrow / the mouse back button), rather than opening a menu of
    // its own -- there is nothing to act on inside a menu.
    private void ActionList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        CloseMenu();
        e.Handled = true;
    }

    private List<ActionItem> BuildActions(ResultItem item)
    {
        // An instant result (application, URL, calculator, window, command) is a launch target, not a
        // file: "reveal in folder" / "run as admin" / "pin" are file actions it has no path for.
        if (item.IsInstant)
        {
            return new List<ActionItem>
            {
                new(ResultAction.Open, "打开", "\uE8E5"),
                new(ResultAction.CopyName, "复制名称", "\uE8C8"),
            };
        }

        var actions = new List<ActionItem>
        {
            new(ResultAction.Open, "打开", "\uE8E5"),
            new(ResultAction.OpenContainingFolder, "打开所在文件夹", "\uE838"),
            new(ResultAction.CopyFile, "复制", "\uE8C8"),
            new(ResultAction.CutFile, "剪切", "\uE8C6"),
            new(ResultAction.Rename, "重命名", "\uE8AC"),
            new(ResultAction.CopyPath, "复制完整路径", "\uE8C8"),
            new(ResultAction.CopyFolderPath, "复制所在文件夹路径", "\uE8C8"),
            new(ResultAction.CopyName, "复制文件名", "\uE8C8"),
            new(ResultAction.PinToFavorites, "固定到收藏", "\uE734"),
            new(ResultAction.DeleteToRecycleBin, "删除到回收站", "\uE74D"),
        };

        if (!item.IsDir)
        {
            actions.Insert(2, new ActionItem(ResultAction.RunAsAdmin, "以管理员身份运行", "\uE7EF"));
            actions.Insert(3, new ActionItem(ResultAction.OpenWith, "打开方式…", "\uE7AC"));
        }

        // Rarely-wanted, destructive, or informational entries go last.
        actions.Add(new ActionItem(ResultAction.DeletePermanently, "永久删除", "\uE74D"));
        actions.Add(new ActionItem(ResultAction.ShowProperties, "属性", "\uE946"));

        return actions;
    }

    private void ExecuteAction(ActionItem action)
    {
        var item = _menuTarget;
        if (item is null)
        {
            return;
        }

        try
        {
            switch (action.Action)
            {
                case ResultAction.Open:
                    Open(item);
                    return;

                case ResultAction.OpenContainingFolder:
                    Sox.PluginSdk.Helpers.ShellOpenHelper.TryRevealInFolder(item.Path);
                    HideWindow();
                    break;

                case ResultAction.RunAsAdmin:
                    RunAsAdmin(item);
                    break;

                case ResultAction.OpenWith:
                    OpenWith(item);
                    break;

                case ResultAction.CopyPath:
                    CopyToClipboard(item.Path);
                    break;

                case ResultAction.CopyFolderPath:
                    CopyToClipboard(Path.GetDirectoryName(item.Path) ?? item.Path);
                    break;

                case ResultAction.CopyName:
                    CopyToClipboard(item.Name);
                    break;

                case ResultAction.PinToFavorites:
                    PinToFavorites(item);
                    break;

                case ResultAction.CopyFile:
                    Sox.PluginSdk.Shell.FileOperations.ShellClipboardHelper.SetCopy(new[] { item.Path });
                    HideWindow();
                    break;

                case ResultAction.CutFile:
                    Sox.PluginSdk.Shell.FileOperations.ShellClipboardHelper.SetCut(new[] { item.Path });
                    HideWindow();
                    break;

                case ResultAction.Rename:
                    _ = RenameAsync(item);
                    return;

                case ResultAction.DeleteToRecycleBin:
                    Sox.PluginSdk.Shell.FileOperations.ShellDeleteHelper.DeleteAsync(new[] { item.Path }, permanent: false);
                    HideWindow();
                    break;

                case ResultAction.DeletePermanently:
                    Sox.PluginSdk.Shell.FileOperations.ShellDeleteHelper.DeleteAsync(new[] { item.Path }, permanent: true);
                    HideWindow();
                    break;

                case ResultAction.ShowProperties:
                    ShowProperties(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Action '{action.Action}' failed", ex);
        }

        CloseMenu();
    }

    private void RunAsAdmin(ResultItem item)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = item.Path,
            UseShellExecute = true,
            Verb = "runas",
        });
        HideWindow();
    }

    // The shell's own "Open with" chooser, via the documented OpenWith.exe launcher (the same dialog
    // Explorer's context menu shows). Goes through the shell rather than enumerating verbs ourselves so
    // the list and its "always use this app" checkbox stay exactly what the OS provides.
    private void OpenWith(ResultItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "OpenWith.exe"),
                Arguments = $"\"{item.Path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("OpenWith failed", ex);
        }

        HideWindow();
    }

    // The shell's Properties dialog (the same one a right-click -> 属性 shows), via the "properties"
    // verb. ShowWindow is hidden first so the dialog is not owned by a window that is about to cloak.
    private void ShowProperties(ResultItem item)
    {
        HideWindow();
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = item.Path,
                UseShellExecute = true,
                Verb = "properties",
            });
        }
        catch (Exception ex)
        {
            Log.Error("ShowProperties failed", ex);
        }
    }

    // Rename through the shell's IFileOperation so the change is a normal, undoable Explorer rename. The
    // new name is collected with a ContentDialog first; an empty or unchanged name is a no-op.
    private async Task RenameAsync(ResultItem item)
    {
        var current = Path.GetFileName(item.Path.TrimEnd(Path.DirectorySeparatorChar));
        var box = new TextBox
        {
            Text = current,
            SelectionStart = 0,
            SelectionLength = current.Length,
        };

        var dialog = new ContentDialog
        {
            Title = "重命名",
            Content = box,
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootCard.XamlRoot,
        };

        // Close the action menu first: leaving it open keeps the list hidden and its own focus handling
        // would fight the dialog for the keyboard.
        CloseMenu();

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var newName = box.Text.Trim();
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, current, StringComparison.Ordinal))
        {
            return;
        }

        Sox.PluginSdk.Shell.FileOperations.ShellRenameHelper.RenameAsync(item.Path, newName);
        HideWindow();
    }

    private static void CopyToClipboard(string text)
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }

    private void PinToFavorites(ResultItem item)
    {
        var settings = Sox.Core.UserSettings.Load();
        settings.Favorites.Add(new Sox.Core.FavoriteItemSetting { Name = item.Name, Path = item.Path });
        settings.Save();
        HideWindow();
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();

        // Scope mode: the box holds only the term; the keyword was consumed into the badge on entry.
        // Re-attach the keyword before handing it to the provider, whose Query parses "keyword term".
        if (_scopeProvider is not null)
        {
            _searchHost.CancelSearch();
            _currentQuery = query;
            _resultCache.Clear();
            ApplyResults(BuildScopedResults($"{_scopeKeyword} {query}".Trim(), query, _scopeProvider));
            return;
        }

        if (string.IsNullOrEmpty(query))
        {
            _searchHost.CancelSearch();
            _currentQuery = string.Empty;
            _resultCache.Clear();
            await LoadRecentAsync();
            return;
        }

        _currentQuery = query;
        _resultCache.Clear();
        _searchHost.CancelSearch();

        await Task.Run(async () =>
        {
            await _searchHost.SearchStreamingAsync(
                query,
                list => DispatcherQueue.TryEnqueue(() => OnSearchUpdate(query, list)));
        });
    }

    // Scope results are provider-only and synchronous, so they are built directly (no file stream).
    // providerQuery re-attaches the scope keyword ("g sad"); displayQuery is the term alone ("sad"), used
    // for highlighting so "Google：sad" highlights "sad" rather than the consumed keyword.
    private List<ResultItem> BuildScopedResults(string providerQuery, string displayQuery, IQueryProvider provider)
    {
        var items = new List<ResultItem>();
        foreach (var instant in _queryProviders.QueryScoped(provider, providerQuery))
        {
            if (!_resultCache.TryGetValue(instant.Id, out var instantItem))
            {
                instantItem = new ResultItem(instant, displayQuery);
                _resultCache[instant.Id] = instantItem;
            }

            items.Add(instantItem);
        }

        return items;
    }

    // Suggestions arrive asynchronously after the scope was first shown; re-run the scoped query so the
    // list picks them up. Only when the same scope is still active.
    private void OnSuggestionsUpdated()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_scopeProvider is null)
            {
                return;
            }

            var query = SearchBox.Text.Trim();
            _resultCache.Clear();
            ApplyResults(BuildScopedResults($"{_scopeKeyword} {query}".Trim(), query, _scopeProvider));
        });
    }

    /// <summary>
    /// Enters scope mode for <paramref name="keyword"/>: shows the badge, clears the keyword from the box,
    /// and leaves the box ready for the term. Called the moment the user types the keyword plus a space.
    /// </summary>
    private void EnterScope(QueryScope scope, IQueryProvider provider)
    {
        _scopeProvider = provider;
        _scopeKeyword = scope.Keyword;
        ShowScope(scope);

        // Strip the consumed "keyword " from the box; the remainder (usually empty) becomes the term.
        // The assignment raises TextChanged, which may be dispatched asynchronously -- after _resetting
        // has been reset -- so re-assert the scope on the next tick, otherwise the empty-text branch
        // would see scope active with an empty box and immediately exit it.
        _resetting = true;
        try
        {
            SearchBox.Text = SearchBox.Text.Trim()[scope.Keyword.Length..].TrimStart();
        }
        finally
        {
            _resetting = false;
        }

        ClearButton.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        SearchBox.Focus(FocusState.Programmatic);

        // A network-search scope allows free text, so let the IME back to the user's own state.
        _ime.Restore();
    }

    private void ExitScope()
    {
        if (_scopeProvider is null)
        {
            return;
        }

        _scopeProvider = null;
        _scopeKeyword = string.Empty;
        ScopeBadge.Visibility = Visibility.Collapsed;
        ScopeIcon.Source = null;

        // Back to file-search mode: re-assert English for the box.
        ApplyImeMode();
    }

    private void ShowScope(QueryScope scope)
    {
        ScopeBadge.Visibility = Visibility.Visible;

        // Just the engine's icon (no label). Prefer a real icon; fall back to its glyph while the image
        // loads, or when the engine has no custom icon. IconLoader resolves on the UI thread, so a
        // cached icon is returned inline.
        ScopeIcon.Source = null;
        ScopeGlyph.Glyph = scope.Glyph;
        if (!string.IsNullOrWhiteSpace(scope.IconPath))
        {
            // Rasterise at the physical pixel size of the 28-DIP badge so it renders 1:1, not minified.
            var pixelSize = (int)Math.Ceiling(28 * ScaleFactor());
            var icon = IconLoader.Get(scope.IconPath, DispatcherQueue, image =>
            {
                // Ignore a late arrival if the user already left the scope or switched engines.
                if (_scopeProvider is not null && string.Equals(_scopeKeyword, scope.Keyword, StringComparison.OrdinalIgnoreCase))
                {
                    ScopeIcon.Source = image;
                    ScopeGlyph.Glyph = string.Empty;
                }
            }, pixelSize);

            if (icon is not null)
            {
                ScopeIcon.Source = icon;
                ScopeGlyph.Glyph = string.Empty;
            }
        }
    }

    private async Task LoadRecentAsync()
    {
        _currentQuery = string.Empty;
        _resultCache.Clear();

        // On a normal (non --minimized) launch the window is shown immediately and this fires before the
        // service is up; wait for readiness so the first show is not an empty list plus a pipe timeout.
        if (!await EnsureServiceReadyAsync())
        {
            return;
        }

        var files = await Task.Run(() => _searchHost.GetRecentAsync(20));

        // This crosses the pipe, so a keystroke can land while it is in flight. Applying the recent
        // files then would overwrite the search results that arrived in the meantime -- the flicker
        // where results appear and are a moment later replaced by an unrelated set. Only fill when the
        // box is still empty and no search has taken over since.
        if (!string.IsNullOrEmpty(SearchBox.Text.Trim()) || !string.IsNullOrEmpty(_currentQuery))
        {
            return;
        }

        // Pinned favorites lead the empty-query list, so they have a visible home and are one keystroke
        // away on summon. A favorite is a plain path; build it as a folder result.
        var items = new List<ResultItem>();
        foreach (var favorite in UserSettings.Load().Favorites)
        {
            if (string.IsNullOrWhiteSpace(favorite.Path))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(favorite.Name)
                ? Path.GetFileName(favorite.Path.TrimEnd('\\'))
                : favorite.Name;
            var result = new SearchResult
            {
                Name = name,
                Path = favorite.Path,
                IsDir = Directory.Exists(favorite.Path),
            };
            items.Add(new ResultItem(result, string.Empty));
        }

        items.AddRange(files.Select(r => new ResultItem(r, string.Empty)));
        ApplyResults(items);
    }

    // Called on the UI thread for every (throttled) streaming snapshot.
    private void OnSearchUpdate(string query, IReadOnlyList<SearchResult> list)
    {
        if (!string.Equals(query, _currentQuery, StringComparison.Ordinal) ||
            !string.Equals(query, SearchBox.Text.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        // Merge instant results and file results, then order by "was it opened before" ACROSS both
        // sources. Pinning every application above every file made a matching history entry lose to
        // never-used start-menu apps (typing "apera" after opening ApeRadar.exe showed Samples/Media
        // Player first). History is the strongest signal regardless of which source produced the row;
        // see ADR-0018. Order: curated (by score) -> plain instant -> plain files.
        var history = SearchHistoryStore.Snapshot();

        var curated = new List<(ResultItem Item, double Score)>();
        var plainInstant = new List<ResultItem>();

        foreach (var instant in _queryProviders.Query(query))
        {
            if (!_resultCache.TryGetValue(instant.Id, out var instantItem))
            {
                instantItem = new ResultItem(instant, query);
                _resultCache[instant.Id] = instantItem;
            }

            if (history.TryGetValue(NormalizeHistoryPath(instant.LaunchTarget), out var score))
            {
                curated.Add((instantItem, score));
            }
            else
            {
                plainInstant.Add(instantItem);
            }
        }

        var plainFiles = new List<ResultItem>();
        foreach (var result in list)
        {
            if (!_resultCache.TryGetValue(result.Path, out var item))
            {
                item = new ResultItem(result, query);
                _resultCache[result.Path] = item;
            }

            if (history.TryGetValue(NormalizeHistoryPath(result.Path), out var score))
            {
                curated.Add((item, score));
            }
            else
            {
                plainFiles.Add(item);
            }
        }

        var items = new List<ResultItem>(curated.Count + plainInstant.Count + plainFiles.Count);
        items.AddRange(curated.OrderByDescending(c => c.Score).Select(c => c.Item));
        items.AddRange(plainInstant);
        items.AddRange(plainFiles);

        ApplyResults(items);
    }

    // Mirrors SearchHistoryStore's path normalization (trim quotes/whitespace, unify separators, drop a
    // trailing separator) so a lookup here matches the store's keys.
    private static string NormalizeHistoryPath(string path)
    {
        var normalized = path.Trim().Trim('"').Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.TrimEnd(Path.DirectorySeparatorChar);
    }

    private void ApplyResults(List<ResultItem> items)
    {
        _results.Clear();
        _results.AddRange(items);
        _hoveredItem = null;

        var hasQuery = !string.IsNullOrEmpty(SearchBox.Text.Trim());
        ResultsHost.Visibility = hasQuery ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = items.Count == 0 && hasQuery ? Visibility.Visible : Visibility.Collapsed;

        _windowStart = 0;
        RebuildVisibleWindow();

        // Keep a row selected across streaming refreshes. Each snapshot clears and rebuilds the visible
        // window, which drops the ListView's selection; without re-selecting, the list ends up with
        // nothing highlighted even though results are showing and Enter/left-arrow would do nothing.
        if (_visibleResults.Count > 0 && ResultList.SelectedIndex < 0)
        {
            ResultList.SelectedIndex = 0;
        }

        DispatcherQueue.TryEnqueue(ResizeToContent);
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_themeInitialized && args.WindowActivationState != WindowActivationState.Deactivated)
        {
            _themeInitialized = true;
            ApplyTheme();
        }

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // While a debugger is attached, keep the window up on focus loss so it stays inspectable.
            if (Debugger.IsAttached)
            {
                return;
            }

            HideWindow();
        }
        else
        {
            RootCard.SetIsInputActive(true);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        try
        {
            NativeMethods.UnregisterHotKey(_hwnd, 1);
            if (_originalWndProc != IntPtr.Zero)
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_WNDPROC, _originalWndProc);
            }

            RootCard.ClearBackdrop();
            _themeService.Dispose();
            App.Current.SettingsChanged -= OnAppSettingsChanged;
            _queryProviders.SuggestionsUpdated -= OnSuggestionsUpdated;
            _queryProviders.Dispose();
            _activationServer.Dispose();
            _hookIpc.Dispose();
            _hotkeys.Dispose();
            _everythingIpc.Dispose();
            _searchHost.Dispose();
            _tray.Dispose();
            _statusCts?.Cancel();
            _statusCts?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow_Closed cleanup failed", ex);
        }
    }
}
