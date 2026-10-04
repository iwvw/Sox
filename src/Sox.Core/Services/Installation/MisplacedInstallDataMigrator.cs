namespace Sox.Core.Services.Installation;

/// <summary>
/// One-time upgrade shim for the install-detection bug fixed alongside it.
/// </summary>
/// <remarks>
/// <see cref="InstallationDetector"/> used to require the running executable to sit directly in the
/// registered install directory, but the install ships the service (and the hook, the same image) under
/// a <c>Service\</c> subdirectory. Every service/hook process therefore detected itself as Portable and
/// kept its machine state -- the per-drive index caches, above all -- under
/// <c>&lt;install&gt;\Service\Data\Machine</c>, while the App, whose exe is in the install root,
/// correctly used the installed locations.
///
/// Fixing the detector makes the service use the installed location, but the index it had already built
/// would be invisible there, forcing a full re-scan of every drive on the first launch after the
/// upgrade. This copies the stranded index across first so the existing work is reused. Best-effort:
/// a failure here only costs that one rebuild, so it never blocks startup.
/// </remarks>
public static class MisplacedInstallDataMigrator
{
    public static void RecoverSharedData(InstallationMode mode, string applicationDirectory, string sharedDataDir)
    {
        // A genuine portable copy legitimately keeps its data beside the executable, so only the
        // Installed form has anything misplaced to recover.
        if (mode != InstallationMode.Installed)
            return;

        var misplaced = Path.Combine(applicationDirectory, "Data", "Machine");
        if (!Directory.Exists(misplaced) || string.Equals(Path.GetFullPath(misplaced), Path.GetFullPath(sharedDataDir), StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Directory.CreateDirectory(sharedDataDir);
            foreach (var file in Directory.EnumerateFiles(misplaced, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(sharedDataDir, Path.GetRelativePath(misplaced, file));
                var targetDirectory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(targetDirectory))
                    Directory.CreateDirectory(targetDirectory);
                // Copy-if-absent: the correct location is the newer of the two by construction, since this
                // only ever runs after the detector change shipped.
                if (!File.Exists(target))
                    File.Copy(file, target, overwrite: false);
            }

            TryDelete(Path.Combine(applicationDirectory, "Data"));
            Logger.Log($"[Install] Recovered misplaced install data from '{misplaced}' into '{sharedDataDir}'.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Install] Could not recover misplaced install data from '{misplaced}': {ex.Message}", LogLevel.Warn);
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            // Leftovers under the install directory are removed by the uninstaller anyway.
            Logger.Log($"[Install] Could not remove '{directory}': {ex.Message}", LogLevel.Warn);
        }
    }
}
