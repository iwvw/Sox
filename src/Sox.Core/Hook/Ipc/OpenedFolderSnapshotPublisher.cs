using Sox.PluginSdk.Registries;
using Sox.Core.Wire;

namespace Sox.Core.Hook.Ipc;

internal sealed class OpenedFolderSnapshotPublisher
{
    private readonly HookIpcServer _ipcServer;
    private readonly Func<string?> _suggestedFolder;

    public OpenedFolderSnapshotPublisher(HookIpcServer ipcServer, Func<string?> suggestedFolder)
    {
        _ipcServer = ipcServer;
        _suggestedFolder = suggestedFolder;
    }

    public void Publish()
    {
        var paths = OpenedFolderCollectorRegistry.GetOpenedFolders()
            .Select(folder => folder.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();

        // The folder the user most recently browsed OUTSIDE this dialog -- the same target Quick Switch
        // (Ctrl+G) jumps to. Put it first so the panel can show it as the suggested destination; the
        // collector's enumeration order is arbitrary, so without this it would be buried.
        var suggested = _suggestedFolder();
        if (!string.IsNullOrWhiteSpace(suggested))
        {
            paths.RemoveAll(p => string.Equals(p.TrimEnd('\\'), suggested.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            paths.Insert(0, suggested);
        }

        _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.OpenedFoldersCaptured, StringList = paths });
    }
}
