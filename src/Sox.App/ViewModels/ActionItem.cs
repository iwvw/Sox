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

internal sealed class ActionItem
{
    public ActionItem(ResultAction action, string title, string glyph)
    {
        Action = action;
        Title = title;
        Glyph = glyph;
    }

    public ResultAction Action { get; }

    public string Title { get; }

    public string Glyph { get; }
}
