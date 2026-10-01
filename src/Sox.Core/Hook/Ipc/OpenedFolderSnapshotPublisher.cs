using Sox.PluginSdk.Registries;
using Sox.Core.Wire;

namespace Sox.Core.Hook.Ipc;

internal sealed class OpenedFolderSnapshotPublisher
{
    private readonly HookIpcServer _ipcServer;

    public OpenedFolderSnapshotPublisher(HookIpcServer ipcServer) => _ipcServer = ipcServer;

    public void Publish()
    {
        var paths = OpenedFolderCollectorRegistry.GetOpenedFolders()
            .Select(folder => folder.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        _ipcServer.SendMessage(new IpcMessage { Id = IpcMessageId.OpenedFoldersCaptured, StringList = paths });
    }
}
