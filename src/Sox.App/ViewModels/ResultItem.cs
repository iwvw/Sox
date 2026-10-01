using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Sox.App.Services;
using Sox.App.Services.QueryProviders;
using Sox.Core;

namespace Sox.App.ViewModels;

internal sealed class ResultItem : INotifyPropertyChanged
{
    private string _shortcutText = string.Empty;
    private BitmapImage? _icon;
    private int _iconRequestedSize;

    /// <summary>File-result form: wraps one core search result.</summary>
    public ResultItem(SearchResult result, string query)
    {
        Result = result;
        Name = result.Name;
        Path = result.Path;
        IsDir = result.IsDir;
        Query = query;
    }

    /// <summary>Instant-result form: a self-contained action (application, URL, calculator, ...).</summary>
    public ResultItem(InstantResult instant, string query)
    {
        Instant = instant;
        Name = instant.Title;
        Path = instant.LaunchTarget;
        Query = query;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SearchResult? Result { get; }

    public InstantResult? Instant { get; }

    /// <summary>True for any provider result (application, URL, calculator, window, command) as
    /// opposed to a core file result.</summary>
    public bool IsInstant => Instant is not null;

    /// <summary>Stable identity for keyed diffing (instant rows use their provider id).</summary>
    public string Key => Instant?.Id ?? Path;

    public string Name { get; }

    public string Path { get; }

    public bool IsDir { get; }

    public string Query { get; }

    /// <summary>Glyph to show instead of a loaded icon; empty when a shell icon is used.</summary>
    public string Glyph => Instant?.Glyph ?? string.Empty;

    public BitmapImage? Icon
    {
        get => _icon;
        private set
        {
            if (ReferenceEquals(_icon, value))
            {
                return;
            }

            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
    }

    /// <summary>Shortcut shown for this row (e.g. Ctrl+1); empty when the row has none.</summary>
    public string ShortcutText
    {
        get => _shortcutText;
        set
        {
            if (_shortcutText == value)
            {
                return;
            }

            _shortcutText = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShortcutText)));
        }
    }

    // An instant result shows its description (open hint / result kind) as the second line; a file shows
    // the containing folder.
    public string PathDisplay => Instant is not null
        ? Instant.Description
        : IsDir ? Path : System.IO.Path.GetDirectoryName(Path) ?? Path;

    /// <summary>
    /// Loads the shell icon on the STA worker thread and raises PropertyChanged on the UI thread.
    /// Constructing the item must stay cheap so the search pipeline never blocks on COM/GDI.
    /// </summary>
    public void RequestIcon(Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int pixelSize)
    {
        // Re-request when the needed pixel size grows (e.g. the window moved to a higher-DPI monitor).
        // A one-shot latch here left every row showing an icon decoded for the old scale, which the new
        // monitor then upscaled -- the blurred, soft-edged icons on mixed-DPI setups. Shrinking needs no
        // reload: the existing (larger) bitmap downscales cleanly.
        if (pixelSize <= _iconRequestedSize)
        {
            return;
        }

        _iconRequestedSize = pixelSize;

        // A row that shows a glyph (URL / calculator / window / command) must not also fetch a shell
        // icon: the instant result has no real file behind it, so Path is the URL/argument, and the
        // shell would hand back a generic document icon that sits under the glyph and muddies it.
        if (!string.IsNullOrEmpty(Glyph))
        {
            return;
        }

        var path = Instant?.IconPath ?? Path;
        var isDir = IsDir;
        _ = Task.Run(() =>
        {
            var png = ShellIconProvider.GetIconPng(path, isDir, pixelSize);
            if (png is null)
            {
                return;
            }

            // BitmapImage must be created on the UI thread.
            dispatcher.TryEnqueue(() => Icon = ShellIconProvider.CreateImage(png));
        });
    }
}
