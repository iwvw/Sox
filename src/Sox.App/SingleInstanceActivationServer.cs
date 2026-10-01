using System.IO.Pipes;
using System.Text;
using Sox.App.Services;

namespace Sox.App;

/// <summary>
/// The receiving half of single-instance activation. Program's mutex gate stops a second Sox process from
/// starting, and <see cref="SingleInstanceForwarder"/> on that second process connects here; this listener
/// turns the connection into "summon the already-running window". Without it a repeat launch (desktop
/// icon, Start menu, pinned taskbar) was dropped silently by the mutex and the window never appeared.
/// </summary>
internal sealed class SingleInstanceActivationServer : IDisposable
{
    private const string PipeName = "Sox.App.Activation";

    private readonly Action _onActivated;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <param name="onActivated">Invoked on a background thread for every forwarded launch; the handler
    /// is responsible for marshalling onto the UI thread.</param>
    public SingleInstanceActivationServer(Action onActivated)
    {
        _onActivated = onActivated;
        _loop = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        // One pipe instance at a time, recreated per accept. NamedPipeClientStream.Connect retries while
        // the pipe is busy, so a client arriving during the recreate gap simply waits instead of failing.
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                // Drain the forwarded payload before closing: a client whose write is never read can see
                // its write fail with a broken pipe, even though the activation itself succeeded.
                using var reader = new StreamReader(server, Encoding.UTF8);
                await reader.ReadToEndAsync().ConfigureAwait(false);

                _onActivated();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error("Single-instance activation listener failed", ex);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
            // Shutting down; nothing left to do.
        }
    }
}
