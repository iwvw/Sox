using System.ServiceProcess;

using Sox.Core;

using Sox.Core.Services.Installation;

namespace Sox.Service;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var isHook = args.Length > 0 && args[0].Equals("--hook", StringComparison.OrdinalIgnoreCase);

        // Register the crash handler BEFORE anything else, but do not rely on Logger: it is not
        // initialized yet, and Logger.Log silently drops (empty path) if a crash lands in the window
        // between registration and Initialize. Write straight to the file this run will use, so an
        // exception during startup still leaves evidence. (AppDomain.UnhandledException cannot keep the
        // process alive; it only records.)
        var crashLogPath = Path.Combine(isHook ? Logger.UserDataDir : Logger.SharedDataDir, isHook ? "hook.log" : "service.log");
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            WriteCrash(crashLogPath, $"CRITICAL {(isHook ? "HOOK" : "SERVICE")} UNHANDLED EXCEPTION:\n{e.ExceptionObject}");

        // Wire up plugin logger to the core logger
        PluginSdk.Logger.LogAction = (msg, lvl) => Logger.Log(msg, (LogLevel)(int)lvl);

        if (isHook)
        {
            Logger.Initialize("hook.log", Logger.UserDataDir, overwrite: false);
            Logger.Log("=========================================");
            Logger.Log($"Hook starting with arguments: {string.Join(" ", args)}");
        }
        else
        {
            Logger.Initialize("service.log", Logger.SharedDataDir, overwrite: false);
            // Recover any index/settings a pre-fix service left under <install>\Service\Data because it
            // mis-detected itself as Portable. Must run before MachineSettings.Load and before the
            // engine loads its caches, so the recovered files are what those read.
            Sox.Core.Services.Installation.MisplacedInstallDataMigrator.RecoverSharedData(
                InstallationDetector.Detect(), AppContext.BaseDirectory, Logger.SharedDataDir);
            // Before the first line, so the level applies to everything this run writes. The service is
            // the one process that cannot read the per-user log-level setting -- it runs as LocalSystem
            // and that setting lives under the interactive user's %LocalAppData% -- so it had none at
            // all, and every LogLevel.Debug line in the indexer was unreachable whatever the settings
            // page said. See MachineSettings.ServiceLogLevel.
            Logger.MinimumLevel = MachineSettings.Load().ResolveServiceLogLevel();
            Logger.Log("=========================================");
            Logger.Log($"Service starting with arguments: {string.Join(" ", args)}");
        }

        if (args.Length > 0)
        {
            var cmd = args[0].ToLowerInvariant();
            if (cmd == "--service")
            {
                Logger.Log("Running as Windows Service.");
                ServiceBase.Run(new UsnService());
                return;
            }
            else if (cmd == "--install" || cmd == "-i")
            {
                Logger.Log("Executing service installation.");
                ServiceInstaller.Install();
                return;
            }
            else if (cmd == "--uninstall" || cmd == "-u")
            {
                Logger.Log("Executing service uninstallation.");
                ServiceInstaller.Uninstall();
                return;
            }
            else if (cmd == "--hook")
            {
                Logger.Log("Running in hook mode.");
                HookModeLauncher.Run();
                return;
            }
        }

        // Default fallback: Debug Console Mode
        Logger.Log("Running in debug console mode.");
        Console.WriteLine("Sox Background Service is running. Press Ctrl+C to exit.");

        using var service = new UsnServiceDebugWrapper();
        service.Start();

        var quitEvent = new ManualResetEvent(false);
        Console.CancelKeyPress += (sender, eventArgs) =>
        {
            eventArgs.Cancel = true;
            quitEvent.Set();
        };
        quitEvent.WaitOne();
        service.Stop();
    }

    // Best-effort direct write, independent of Logger state. Never throws: a crash handler that throws
    // would replace the real exception with its own.
    private static void WriteCrash(string logPath, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [Error] {message}\n";
            File.AppendAllText(logPath, line);
        }
        catch
        {
        }
    }
}

class UsnServiceDebugWrapper : IDisposable
{
    private readonly UsnService _service = new UsnService();
    public void Start() => _service.TestStart();
    public void Stop() => _service.TestStop();
    public void Dispose() => _service.Dispose();
}
