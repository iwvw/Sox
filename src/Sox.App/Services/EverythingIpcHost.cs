using Sox.Core.Services.Everything;

namespace Sox.App.Services;

/// <summary>Hosts the Everything IPC emulation window inside the App process.</summary>
internal sealed class EverythingIpcHost : IDisposable
{
    private readonly SearchHost _searchHost;
    private EverythingIpcServer? _server;

    public EverythingIpcHost(SearchHost searchHost)
    {
        _searchHost = searchHost;
    }

    public bool IsRunning => _server?.IsRunning ?? false;

    public void Apply(bool enabled)
    {
        if (enabled && _server is null)
        {
            try
            {
                _server = new EverythingIpcServer(new EverythingSearchDataProvider(_searchHost.Service));
                _server.Start();
                Log.Info($"Everything IPC started hwnd={_server.Hwnd}");
            }
            catch (Exception ex)
            {
                Log.Error("Everything IPC start failed", ex);
            }
        }
        else if (!enabled && _server is not null)
        {
            try
            {
                _server.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("Everything IPC stop failed", ex);
            }

            _server.Dispose();
            _server = null;
        }
    }

    public void Dispose()
    {
        if (_server is not null)
        {
            _server.Stop();
            _server.Dispose();
            _server = null;
        }
    }
}
