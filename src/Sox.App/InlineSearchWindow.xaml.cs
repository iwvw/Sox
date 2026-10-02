using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Sox.App.Interop;
using Sox.App.Services;
using Sox.App.ViewModels;
using Sox.Core;
using Sox.Core.Hook.Ipc;
using Sox.Core.Wire;
using Windows.System;
using WinUIEx;

namespace Sox.App;

/// <summary>
/// The Listary-style panel that docks under a file dialog. It appears when the hook reports a dialog
/// became active, searches globally, and on pick tells the hook to navigate the dialog to the chosen
/// folder (a picked file resolves to its containing folder). It deliberately does NOT take focus on
/// show, so the user's normal typing in the dialog is undisturbed; clicking the box or pressing Ctrl+K
/// (routed by the hook as <see cref="IpcMessageId.FocusInlineSearch"/>) moves focus here.
/// </summary>
public sealed partial class InlineSearchWindow : WindowEx
{
    private const int MaxVisibleRows = 7;
    private const int MaxResults = 60;
    private const int RowHeightDip = 48;

    private readonly SearchHost _searchHost;
    private readonly HookIpcClient _hookIpc;
    private readonly ThemeService _themeService;
    private readonly System.Collections.ObjectModel.ObservableCollection<ResultItem> _results = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _debounce;
    private readonly Dictionary<string, ResultItem> _resultCache = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _dialogHwnd;
    private string _folder = string.Empty;
    private CancellationTokenSource? _searchCts;
    private bool _suppressTextChanged;
    private bool _shown;
    private readonly List<string> _openedFolders = [];

    public InlineSearchWindow(SearchHost searchHost, HookIpcClient hookIpc, ThemeService themeService)
    {
        _searchHost = searchHost;
        _hookIpc = hookIpc;
        _themeService = themeService;
        InitializeComponent();

        // Mirror MainWindow's window setup exactly so the material, rounding and frame match the
        // spotlight. In particular, do NOT set WinUIEx's IsTitleBarVisible here: MainWindow leaves it at
        // its default and only collapses the title-bar height, and setting it false re-introduced a frame
        // strip along the top of this window. The panel is not topmost: it is made an OWNED window of the
        // file dialog (see ShowForDialog), which keeps it directly above that dialog without floating over
        // unrelated applications.
        ExtendsContentIntoTitleBar = true;
        AppWindow.Title = "Sox";

        try
        {
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("Inline: title bar setup failed", ex);
        }

        // Transparent window backdrop so the card's rounded corners sit on nothing: without this the
        // window paints its own (square) background and the corners outside the card show as hard angles.
        SystemBackdrop = new WinUIEx.TransparentTintBackdrop { TintColor = Microsoft.UI.Colors.Transparent };
        StripWindowChrome();

        // Same material engine as the spotlight card, so the panel matches it. Stretch and theme are
        // applied on Loaded, exactly like MainWindow: SetCardStretch before the control is loaded does not
        // take effect.
        RootCard.Loaded += (_, _) =>
        {
            RootCard.SetCardStretch(true);
            ApplyTheme();
        };

        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(30);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => _ = RunSearchAsync();

        ResultList.ItemsSource = _results;
        SearchBox.KeyDown += SearchBox_KeyDown;

        _hookIpc.OnActiveWindowMoved += () => DispatcherQueue.TryEnqueue(Reposition);
        _hookIpc.OnExplorerDeactivated += () => DispatcherQueue.TryEnqueue(HidePanel);
        _hookIpc.OnFocusInlineSearchRequested += () => DispatcherQueue.TryEnqueue(FocusBox);
        _hookIpc.OnOpenedFoldersCaptured += folders => DispatcherQueue.TryEnqueue(() => OnOpenedFolders(folders));

        // The opened-folder list is a snapshot the hook only computes on request, so it goes stale as the
        // user opens/closes Explorer windows or navigates. Ask for a fresh snapshot whenever Explorer
        // activity is reported while the panel is up, so the list tracks what is actually open.
        _hookIpc.OnExplorerActivated += (_, _, _, _) => DispatcherQueue.TryEnqueue(RequestOpenedFoldersIfVisible);
        _hookIpc.OnPathCaptured += (_, _, _) => DispatcherQueue.TryEnqueue(RequestOpenedFoldersIfVisible);
    }

    private void ApplyTheme()
    {
        var snapshot = _themeService.Current;
        RootCard.RequestedTheme = snapshot.Theme;
        var config = Materials.BackdropStyles.Get(snapshot.BackdropParameters.Style);
        RootCard.ApplyBackdrop(snapshot.BackdropParameters, config.ControllerKind, isImageMode: false, hasColorization: false);
    }

    // Remove the window's own frame and DWM rounding so the card's rounded corners are the only ones
    // drawn; otherwise the frame's arcs sit at a different radius and read as a second, offset corner.
    private void StripWindowChrome()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        var style = Interop.NativeMethods.GetWindowLong(hwnd, Interop.NativeMethods.GWL_STYLE);
        style &= ~(Interop.NativeMethods.WS_CAPTION
            | Interop.NativeMethods.WS_THICKFRAME
            | Interop.NativeMethods.WS_BORDER
            | Interop.NativeMethods.WS_DLGFRAME
            | Interop.NativeMethods.WS_SYSMENU
            | Interop.NativeMethods.WS_MINIMIZEBOX
            | Interop.NativeMethods.WS_MAXIMIZEBOX);
        Interop.NativeMethods.SetWindowLong(hwnd, Interop.NativeMethods.GWL_STYLE, style);

        var exStyle = Interop.NativeMethods.GetWindowLong(hwnd, Interop.NativeMethods.GWL_EXSTYLE);
        exStyle |= Interop.NativeMethods.WS_EX_TOOLWINDOW;
        exStyle &= ~Interop.NativeMethods.WS_EX_APPWINDOW;
        Interop.NativeMethods.SetWindowLong(hwnd, Interop.NativeMethods.GWL_EXSTYLE, exStyle);

        // SetWindowLong only records the new style; the frame is not recomputed until a SetWindowPos with
        // SWP_FRAMECHANGED, so without this the old square non-client frame stays painted (the leftover
        // border around the rounded card). MainWindow does the same in its own ShowWindow.
        Interop.NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE | Interop.NativeMethods.SWP_NOZORDER | Interop.NativeMethods.SWP_FRAMECHANGED);

        // The card draws its own antialiased rounded corners (CardCornerRadius=8) and the window's square
        // frame falls exactly on the card, so tell DWM not to round (or border) the frame: a second set of
        // arcs at a different radius would read as an offset double corner.
        var corner = Interop.NativeMethods.DWMWCP_DONOTROUND;
        Interop.NativeMethods.DwmSetWindowAttribute(hwnd, Interop.NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = unchecked((int)Interop.NativeMethods.DWMWA_COLOR_NONE);
        Interop.NativeMethods.DwmSetWindowAttribute(hwnd, Interop.NativeMethods.DWMWA_BORDER_COLOR, ref border, sizeof(int));

        Interop.NativeMethods.RedrawWindow(
            hwnd,
            IntPtr.Zero,
            IntPtr.Zero,
            Interop.NativeMethods.RDW_INVALIDATE | Interop.NativeMethods.RDW_UPDATENOW | Interop.NativeMethods.RDW_ALLCHILDREN | Interop.NativeMethods.RDW_FRAME);
    }

    /// <summary>Shows the panel docked under <paramref name="dialogHwnd"/> and points it at
    /// <paramref name="folder"/>.</summary>
    public void ShowForDialog(IntPtr dialogHwnd, string folder)
    {
        var sameDialog = _dialogHwnd == dialogHwnd;
        _dialogHwnd = dialogHwnd;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            _folder = folder;
        }

        // The hook re-reports the same dialog (path polling, focus changes). Only the first report should
        // reset the box and take focus; a repeat must leave the user's typing and caret alone, or the
        // panel fights the keyboard on every poll.
        if (sameDialog && _shown)
        {
            Reposition();
            return;
        }

        // Make the panel an OWNED window of the file dialog: an owned window always floats directly above
        // its owner and follows it, which is the Listary behaviour. This replaces WS_EX_TOPMOST, which
        // made the panel hover over every unrelated application.
        var selfHwndForOwner = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Interop.NativeMethods.SetWindowLongPtr(selfHwndForOwner, Interop.NativeMethods.GWLP_HWNDPARENT, dialogHwnd);

        // Tell the hook the window is up, so its key handling keeps the dialog's own behaviour while the
        // panel only receives keys when clicked into.
        _hookIpc.SendMessage(new IpcMessage { Id = IpcMessageId.SetInlineWindowOnScreen, BoolVal = true });
        // Ask for the list of currently-open Explorer folders, shown while the box is empty so the user
        // can jump to any of them (Listary's Quick Switch list).
        RequestOpenedFolders();

        if (!_shown)
        {
            _shown = true;
            try
            {
                WinUIEx.WindowExtensions.Show(this);
            }
            catch (Exception ex)
            {
                Log.Error("Inline: Show() failed", ex);
            }
        }

        // WinUI's own Show does not always map the window on screen when it was never activated; drive
        // it through Win32 directly, and use SW_SHOWNOACTIVATE so the dialog keeps focus and the user's
        // typing is not interrupted.
        var selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(selfHwnd, NativeMethods.SW_SHOWNOACTIVATE);

        // Start with an empty query: show the open-folder list until the user types.
        _suppressTextChanged = true;
        SearchBox.Text = string.Empty;
        _suppressTextChanged = false;
        ShowOpenedFolders();
        Reposition();

        // Take focus so typing goes straight into the box (Listary behaviour). The hook's focus filter
        // ignores this process's own windows, so focusing the panel does not count as the dialog losing
        // focus and will not hide it.
        FocusBox();
    }

    private void OnOpenedFolders(IReadOnlyList<string> folders)
    {
        _openedFolders.Clear();
        foreach (var f in folders)
        {
            if (!string.IsNullOrWhiteSpace(f)
                && !string.Equals(f.TrimEnd('\\'), _folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                _openedFolders.Add(f);
            }
        }

        // Only refresh the visible list if the user has not started typing.
        if (string.IsNullOrEmpty(SearchBox.Text))
        {
            ShowOpenedFolders();
            Reposition();
        }
    }

    // Ask the hook for a fresh opened-folder snapshot. The hook computes it on demand and never pushes it,
    // so this is how the list stays current.
    private void RequestOpenedFolders() =>
        _hookIpc.SendMessage(new IpcMessage { Id = IpcMessageId.RequestOpenedFolders });

    private void RequestOpenedFoldersIfVisible()
    {
        if (_shown)
        {
            RequestOpenedFolders();
        }
    }

    // The empty-query list: every other currently-open Explorer folder, as jump targets.
    private void ShowOpenedFolders()
    {
        _results.Clear();
        foreach (var folder in _openedFolders)
        {
            var name = Path.GetFileName(folder.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name))
            {
                name = folder;
            }

            var result = new SearchResult { Name = name, Path = folder, IsDir = true };
            var item = new ResultItem(result, string.Empty);
            item.RequestIcon(DispatcherQueue, 48);
            _results.Add(item);
        }

        ResultList.SelectedIndex = _results.Count > 0 ? 0 : -1;
        UpdateResultsVisibility();
    }

    // With no rows (no opened folder to jump to) the panel is just the search box: collapse the divider
    // and the list so no empty band is left behind.
    private void UpdateResultsVisibility()
    {
        var hasResults = _results.Count > 0;
        ResultsDivider.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
        ResultList.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
    }

    public void HidePanel()
    {
        if (!_shown)
        {
            return;
        }

        _searchCts?.Cancel();
        _hookIpc.SendMessage(new IpcMessage { Id = IpcMessageId.SetInlineWindowOnScreen, BoolVal = false });
        this.Hide();

        // Drop the owner relationship: the panel is reused across dialogs, and leaving it owned by a
        // dialog that has since closed would make Windows destroy or mis-stack it.
        var selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Interop.NativeMethods.SetWindowLongPtr(selfHwnd, Interop.NativeMethods.GWLP_HWNDPARENT, IntPtr.Zero);

        // The window object is kept for reuse, but the next dialog activation must be treated as a fresh
        // show (reset the box, re-focus) -- otherwise the sameDialog guard below would swallow it and the
        // panel would never come back after the user switched away and returned.
        _shown = false;
        _dialogHwnd = IntPtr.Zero;
    }

    public void FocusBox()
    {
        if (!_shown)
        {
            return;
        }

        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    // Dock under the dialog's bottom edge, matching its width and horizontal position, clamped to the
    // monitor's work area. The dialog rect is in physical pixels, so the DIP size is scaled to match.
    private void Reposition()
    {
        if (_dialogHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_dialogHwnd))
        {
            return;
        }

        if (!NativeMethods.GetWindowRect(_dialogHwnd, out var rect))
        {
            return;
        }

        // GetWindowRect includes the invisible resize/shadow border, so docking at its bottom leaves a
        // visible gap above the panel. DWM's extended frame bounds are the dialog's actual visible edge;
        // fall back to the window rect if DWM has nothing to say.
        if (NativeMethods.DwmGetWindowAttributeRect(
                _dialogHwnd,
                NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var frame,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0)
        {
            rect = frame;
        }

        var dpi = NativeMethods.GetDpiForWindow(_dialogHwnd);
        var scale = dpi <= 0 ? 1.0 : dpi / 96.0;

        // Window IS the card, with no shadow margin: the panel sits on top of the file dialog, and a
        // transparent margin around the card would still hit-test, swallowing clicks on the dialog's own
        // edge controls. The card is narrower than the dialog (Listary's panel is a compact strip, not a
        // full-width bar) and centred under it, flush with its bottom edge.
        var dialogWidthPx = rect.Right - rect.Left;
        var widthPx = (int)Math.Round(dialogWidthPx * 0.62);
        if (widthPx < 360)
        {
            widthPx = 360;
        }

        if (widthPx > dialogWidthPx)
        {
            widthPx = dialogWidthPx;
        }

        var heightPx = EstimateHeightPx(scale);
        var x = rect.Left + (dialogWidthPx - widthPx) / 2;
        var y = rect.Bottom;

        var monitor = NativeMethods.MonitorFromWindow(_dialogHwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var work = info.rcWork;
            if (y + heightPx > work.Bottom)
            {
                // No room below the dialog: hang the card from the dialog's bottom edge upward instead.
                y = rect.Bottom - heightPx;
            }

            if (y < work.Top)
            {
                y = work.Top;
            }

            if (x + widthPx > work.Right)
            {
                x = work.Right - widthPx;
            }

            if (x < work.Left)
            {
                x = work.Left;
            }
        }

        // Position with raw physical pixels via SetWindowPos rather than MoveAndResize: MoveAndResize
        // takes DIPs and multiplies by this window's CURRENT DPI, which is still the old monitor's until
        // the move completes. When the dialog and this panel sit on monitors with different scaling, that
        // stale factor made the size and offset wrong. Physical pixels computed from the DIALOG's DPI are
        // correct on the target monitor; WinUI re-scales the content once the window lands there.
        var selfHwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        NativeMethods.SetWindowPos(
            selfHwnd,
            IntPtr.Zero,
            x,
            y,
            widthPx,
            heightPx,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private int EstimateHeightPx(double scale)
    {
        // Card border top+bottom.
        const int cardBorder = 2;
        // Search row only; with no rows the panel is just the search box, so the divider and list padding
        // must not be counted or they leave a blank band under the box.
        var dip = 40 + cardBorder;
        if (_results.Count > 0)
        {
            // The list is capped at MaxVisibleRows tall and scrolls beyond that, so the panel never grows
            // taller than seven rows however many results came back.
            var rows = Math.Min(_results.Count, MaxVisibleRows);
            // Divider (1) + list padding (2,4 => 8).
            dip += 1 + 8 + rows * RowHeightDip;
        }

        return (int)Math.Round(dip * scale);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (ResultList.SelectedItem is ResultItem selected)
                {
                    Navigate(selected);
                }
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                HidePanel();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0)
        {
            return;
        }

        var index = ResultList.SelectedIndex;
        index = Math.Clamp(index < 0 ? 0 : index + delta, 0, _results.Count - 1);
        ResultList.SelectedIndex = index;
    }

    private async Task RunSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        // Same lifecycle as the spotlight: the cache dedupes the repeated throttled snapshots of ONE
        // search, then is cleared for the next query so each row's highlight uses the current term.
        _resultCache.Clear();

        if (string.IsNullOrEmpty(query))
        {
            // Empty query: back to the open-folder list.
            ShowOpenedFolders();
            Reposition();
            return;
        }

        try
        {
            // Run the search off the UI thread, exactly like the spotlight: SearchStreamingAsync reads
            // settings and the history store synchronously before its first await, and on the UI thread
            // those disk reads blocked every keystroke. The callback marshals back via the dispatcher.
            await Task.Run(() => _searchHost.SearchStreamingAsync(
                query,
                list => DispatcherQueue.TryEnqueue(() => ApplyResults(query, list)),
                maxResults: MaxResults,
                throttleMs: 25));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Error("Inline search failed", ex);
        }
    }

    private void ApplyResults(string query, IReadOnlyList<SearchResult> list)
    {
        if (!string.Equals(query, SearchBox.Text.Trim(), StringComparison.Ordinal))
        {
            return;
        }

        _results.Clear();
        foreach (var result in list.Take(MaxResults))
        {
            // Reuse the item across keystrokes (same cache pattern as the spotlight): a fresh ResultItem
            // per update re-requested every row's shell icon through COM on each keystroke, which is what
            // made the panel feel far slower than the spotlight.
            if (!_resultCache.TryGetValue(result.Path, out var item))
            {
                item = new ResultItem(result, query);
                _resultCache[result.Path] = item;
            }

            item.RequestIcon(DispatcherQueue, 48);
            _results.Add(item);
        }

        if (_results.Count > 0 && ResultList.SelectedIndex < 0)
        {
            ResultList.SelectedIndex = 0;
        }

        UpdateResultsVisibility();

        Reposition();
    }

    private void ResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItem item)
        {
            Navigate(item);
        }
    }

    private void Navigate(ResultItem item)
    {
        var target = item.IsDir ? item.Path : Path.GetDirectoryName(item.Path);
        if (string.IsNullOrEmpty(target) || _dialogHwnd == IntPtr.Zero)
        {
            return;
        }

        // The adapter writes the path into the dialog's file-name box and only commits it (Enter) while
        // the dialog is the foreground window. Our panel is foreground here, so hand foreground back to
        // the dialog first -- the App is the current foreground process, which is what grants it the right
        // to do this (the Hook is not foreground and cannot).
        Interop.ForegroundHelper.ForceForeground(_dialogHwnd);

        _hookIpc.SendMessage(new IpcMessage
        {
            Id = IpcMessageId.NavigateDialog,
            Hwnd = _dialogHwnd.ToInt64(),
            StringVal1 = target,
        });

        Task.Run(() => Sox.Core.SearchHistoryStore.Record(
            SearchBox.Text,
            item.Path,
            item.IsDir ? Sox.PluginSdk.Services.HistoryEntryKind.Folder : Sox.PluginSdk.Services.HistoryEntryKind.File));

        HidePanel();
    }
}
