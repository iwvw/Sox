using System.Reflection;
using System.Runtime.InteropServices;

namespace Sox.App.Services.QueryProviders;

/// <summary>
/// Enumerates the Windows "Apps" virtual shell folder (shell:AppsFolder), the same list the Start
/// menu's "All apps" shows. Unlike scanning Start Menu .lnk files, this surfaces modern packaged
/// (UWP/MSIX) apps such as Notepad, Calculator and Terminal, which have no shortcut file on disk.
/// Each entry carries its localized display name and an AppUserModelID (AUMID) used to launch it via
/// shell:AppsFolder\{AUMID}.
/// </summary>
internal static class AppsFolderEnumerator
{
    internal sealed class AppEntry
    {
        public string Name = string.Empty;

        /// <summary>FolderItem.Path: the AUMID for packaged apps (or a target path for classic ones).</summary>
        public string Aumid = string.Empty;
    }

    // Both spellings resolve to the Apps known folder; the plain "shell:AppsFolder" works on modern
    // Windows, the CLSID form is the robust fallback.
    private static readonly string[] AppsFolderParsingNames =
    {
        "shell:AppsFolder",
        "shell:::{4234d49b-0245-4df3-b780-3893943456e1}"
    };

    /// <summary>
    /// Enumerates all apps in shell:AppsFolder. Runs the Shell COM work on a dedicated STA thread, so
    /// it is safe to call from a thread-pool / MTA context.
    /// </summary>
    public static List<AppEntry> Enumerate()
    {
        var result = new List<AppEntry>();
        var thread = new Thread(() =>
        {
            try
            {
                EnumerateCore(result);
            }
            catch (Exception ex)
            {
                Log.Warning($"AppsFolder enumeration failed: {ex.Message}");
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }

    private static void EnumerateCore(List<AppEntry> result)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null)
        {
            return;
        }

        object? shell = null;
        object? folder = null;
        object? items = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell == null)
            {
                return;
            }

            foreach (var parsingName in AppsFolderParsingNames)
            {
                folder = Invoke(shell, "NameSpace", parsingName);
                if (folder != null)
                {
                    break;
                }
            }

            if (folder == null)
            {
                Log.Warning("Could not bind shell:AppsFolder.");
                return;
            }

            items = Invoke(folder, "Items");
            if (items == null)
            {
                return;
            }

            var count = Convert.ToInt32(Get(items, "Count"));
            for (var i = 0; i < count; i++)
            {
                object? item = null;
                try
                {
                    item = Invoke(items, "Item", i);
                    if (item == null)
                    {
                        continue;
                    }

                    var name = Get(item, "Name") as string;
                    var path = Get(item, "Path") as string; // AUMID for packaged apps
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path))
                    {
                        continue;
                    }

                    result.Add(new AppEntry { Name = name!.Trim(), Aumid = path! });
                }
                catch (Exception ex)
                {
                    Log.Warning($"Skipped an AppsFolder entry: {ex.Message}");
                }
                finally
                {
                    Release(item);
                }
            }
        }
        finally
        {
            Release(items);
            Release(folder);
            Release(shell);
        }
    }

    private static object? Invoke(object target, string member, params object[] args)
        => target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, args);

    private static object? Get(object target, string member)
        => target.GetType().InvokeMember(member, BindingFlags.GetProperty, null, target, null);

    private static void Release(object? comObj)
    {
        if (comObj != null && Marshal.IsComObject(comObj))
        {
            Marshal.FinalReleaseComObject(comObj);
        }
    }
}
