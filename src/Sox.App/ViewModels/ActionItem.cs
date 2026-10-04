using System.ComponentModel;

namespace Sox.App.ViewModels;

internal enum ResultAction
{
    Open,
    OpenContainingFolder,
    RunAsAdmin,
    CopyPath,
    CopyName,
    PinToFavorites,
    OpenWith,
    CopyFile,
    CutFile,
    DeleteToRecycleBin,
    DeletePermanently,
    Rename,
    ShowProperties,
    CopyFolderPath,
}

internal sealed class ActionItem : INotifyPropertyChanged
{
    private string _shortcutText = string.Empty;

    public ActionItem(ResultAction action, string title, string glyph)
    {
        Action = action;
        Title = title;
        Glyph = glyph;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ResultAction Action { get; }

    public string Title { get; }

    public string Glyph { get; }

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
}
