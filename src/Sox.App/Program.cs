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
        const string mutexName = "Sox.SingleInstance";
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
