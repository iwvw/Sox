using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Sox.App;

internal static class Program
{
    private static Mutex? _instanceMutex;
    private static DispatcherQueueSynchronizationContext? _uiContext;

    [STAThread]
    private static int Main(string[] args)
    {
        // Session-scoped and non-obvious: a bare name lands in the machine-wide Global namespace, where any
        // process (including a low-integrity one) can pre-create it and make Sox exit silently as
        // "already running", and where a second logged-in user would block the first's instance.
        var mutexName = $"Sox.SingleInstance.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        _instanceMutex = new Mutex(initiallyOwned: true, mutexName, out var isFirstInstance);

        if (!isFirstInstance)
        {
            SingleInstanceForwarder.ForwardActivation(args);
            return 0;
        }

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(_ =>
            {
                _uiContext = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(_uiContext);
                new App();
            });
        }
        finally
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        return 0;
    }
}
