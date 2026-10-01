using System.Diagnostics;

namespace Sox.App.Services;

internal static class ServiceBootstrapper
{
    private const string ServiceName = "SoxService";

    public static void TryStart()
    {
        try
        {
            if (QueryRunning())
            {
                return;
            }

            var start = RunSc($"start {ServiceName}");
            if (start.Ok)
            {
                return;
            }

            TryInstallElevated();
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start SoxService", ex);
        }
    }

    private static bool QueryRunning()
    {
        var result = RunSc($"query {ServiceName}");
        return result.Ok && result.Output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static (bool Ok, string Output) RunSc(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return (false, string.Empty);
            }

            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(10000);
            return (process.ExitCode == 0, output);
        }
        catch (Exception ex)
        {
            Log.Error($"sc.exe {arguments} failed", ex);
            return (false, string.Empty);
        }
    }

    private static void TryInstallElevated()
    {
        var exe = ResolveServiceExe();
        if (exe is null)
        {
            Log.Warning("Sox.Service.exe not found; cannot install the service.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "--install",
                UseShellExecute = true,
                Verb = "runas",
            };
            Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            Log.Error("Elevated service install failed", ex);
        }
    }

    private static string? ResolveServiceExe()
    {
        var baseDir = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(baseDir, "Sox.Service.exe"),
            Path.Combine(baseDir, "Service", "Sox.Service.exe"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "Sox.Service", "Sox.Service.exe")),
        })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
