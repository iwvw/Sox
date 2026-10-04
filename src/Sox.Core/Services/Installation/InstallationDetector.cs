using Microsoft.Win32;
using System.Security;

namespace Sox.Core.Services.Installation;

/// <summary>Identifies the installed copy without relying on its directory's name.</summary>
public static class InstallationDetector
{
    // Inno Setup creates this uninstall key from Installer/installer.iss's fixed AppId.
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{D37D0B75-B5E3-40D9-92EE-429C7D4D7F2A}_is1";

    /// <summary>
    /// Returns <see cref="InstallationMode.Installed"/> only when this process is running from within
    /// the directory Inno Setup registered. A copied installation therefore behaves as portable instead
    /// of inheriting the original copy's machine-level state.
    /// </summary>
    public static InstallationMode Detect()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UninstallKeyPath);
            if (key == null)
                return InstallationMode.Portable;

            return key.GetValue("InstallLocation") is string location &&
                   !string.IsNullOrWhiteSpace(location) &&
                   !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? IsInstalledAt(location, Environment.ProcessPath)
                    ? InstallationMode.Installed
                    : InstallationMode.Portable
                : InstallationMode.Portable;
        }
        catch (Exception exception) when (exception is IOException or SecurityException or UnauthorizedAccessException)
        {
            return InstallationMode.Portable;
        }
    }

    /// <summary>
    /// Whether <paramref name="executablePath"/> lives anywhere under <paramref name="installLocation"/>.
    /// </summary>
    /// <remarks>
    /// Containment rather than "same directory": the install ships the service and hook under a
    /// <c>Service\</c> subdirectory (see ADR-0020), so requiring the executable to sit directly in the
    /// registered directory made every service/hook process detect itself as Portable -- it then kept
    /// its index and machine settings under the install directory (Data\Machine) while the App, whose exe
    /// is directly in the install root, correctly used the installed per-user/per-machine directories. A
    /// copied install still fails this check because its executable is under a different root, which is
    /// the property the direct-directory version was there to protect.
    /// </remarks>
    internal static bool IsInstalledAt(string installLocation, string executablePath)
    {
        try
        {
            var installRoot = Path.GetFullPath(installLocation);
            if (!installRoot.EndsWith(Path.DirectorySeparatorChar))
                installRoot += Path.DirectorySeparatorChar;

            var executable = Path.GetFullPath(executablePath);
            return executable.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
