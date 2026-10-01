using Microsoft.Win32;

namespace Sox.App.Services;

/// <summary>
/// Manages the per-user "run at sign-in" registry value for the spotlight app. A HKCU Run entry is
/// enough here because Sox.App runs unelevated (only the background service needs admin), unlike
/// Momomi which needs a scheduled task for its elevated main process.
/// </summary>
internal static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sox";

    /// <summary>The command written to the Run value: the current exe, started hidden.</summary>
    private static string Command
    {
        get
        {
            var exe = Environment.ProcessPath
                ?? Path.Combine(AppContext.BaseDirectory, "Sox.App.exe");
            return $"\"{exe}\" --minimized";
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Error("Reading startup registry value failed", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null)
                return false;

            if (enabled)
                key.SetValue(ValueName, Command, RegistryValueKind.String);
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Setting startup to {enabled} failed", ex);
            return false;
        }
    }

    /// <summary>Re-asserts the Run value so it points at the current exe path after an update moves it.</summary>
    public static void RefreshIfEnabled()
    {
        if (IsEnabled())
            SetEnabled(true);
    }
}
