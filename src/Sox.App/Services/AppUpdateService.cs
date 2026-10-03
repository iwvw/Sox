using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Sox.Core;

namespace Sox.App.Services;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string? LatestVersion,
    bool HasUpdate,
    string? ReleaseUrl,
    string? DownloadUrl,
    string? Error);

/// <summary>
/// Self-update: checks GitHub Releases for a newer tag, downloads the artifact matching this install's
/// form (installer vs portable) and architecture, and stages a batch script that swaps the files after
/// Sox exits and relaunches it. Modelled on Momomi's AppUpdateService. The download runs over the same
/// GitHub mirrors Momomi uses so it works where direct GitHub access is slow.
/// </summary>
public sealed class AppUpdateService
{
    private const string ReleasesApi = "https://api.github.com/repos/iwvw/Sox/releases/latest";

    // GitHub mirrors tried in order; the empty entry is a direct connection. Only applied to GitHub hosts.
    private static readonly string[] Mirrors =
    [
        "https://gh-proxy.org",
        "https://ghproxy.net",
        "https://ghfast.top",
        "https://gh.llkk.cc",
        "",
    ];

    private readonly HttpClient _http;
    private string? _pendingScript;

    public AppUpdateService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Sox/" + CurrentVersion);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static AppUpdateInfo? LastResult { get; private set; }

    public string CurrentVersion => GetCurrentVersion();

    /// <summary>True when this copy was laid down by the Inno installer (unins000.exe beside the exe).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public static string GetCurrentVersion()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any "+<sha>" build metadata the SDK appends.
                var plus = info.IndexOf('+');
                return plus >= 0 ? info[..plus] : info;
            }

            var v = asm.GetName().Version;
            return v is null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch
        {
            return "0.0.0";
        }
    }

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var json = await FetchLatestAsync(ct).ConfigureAwait(false);
            var (tag, htmlUrl, assets) = ParseLatest(json);

            var latest = tag?.TrimStart('v');
            var hasUpdate = false;
            if (!string.IsNullOrEmpty(latest)
                && Version.TryParse(latest, out var latestV)
                && Version.TryParse(CurrentVersion, out var currentV))
                hasUpdate = latestV > currentV;

            var downloadUrl = latest is null ? null : SelectAssetUrl(latest, assets);
            var result = new AppUpdateInfo(CurrentVersion, latest, hasUpdate, htmlUrl, downloadUrl, null);
            LastResult = result;
            return result;
        }
        catch (Exception ex)
        {
            var result = new AppUpdateInfo(CurrentVersion, null, false, null, null, DescribeError(ex));
            LastResult = result;
            return result;
        }
    }

    /// <summary>Maps a transport failure to a user-facing message. The raw HttpRequestException text
    /// ("Response status code does not indicate success: 404") is meaningless to a user.</summary>
    private static string DescribeError(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } =>
            "无法访问发布仓库（404）。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized } =>
            "发布仓库鉴权失败（401）。",
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
            "发布仓库拒绝访问（403）。",
        TaskCanceledException => "请求超时，请检查网络连接。",
        HttpRequestException => "网络请求失败，请检查网络连接。",
        _ => ex.Message,
    };

    public async Task<AppUpdateInfo> PrepareUpdateAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var info = LastResult ?? await CheckAsync(ct).ConfigureAwait(false);
        if (info.Error is not null)
            return info with { Error = "无法获取更新信息：" + info.Error };
        if (string.IsNullOrEmpty(info.DownloadUrl))
            return info with { Error = "发布中未找到匹配当前版本的安装包" };

        try
        {
            var workDir = Path.Combine(Path.GetTempPath(), "SoxUpdate");
            Directory.CreateDirectory(workDir);

            var fileName = Path.GetFileName(new Uri(info.DownloadUrl).AbsolutePath);
            var downloaded = Path.Combine(workDir, fileName);
            await DownloadFileAsync(info.DownloadUrl, downloaded, progress, ct).ConfigureAwait(false);

            string? setupExe = IsInstalled ? downloaded : null;
            string? extractDir = null;
            if (setupExe is null)
            {
                extractDir = Path.Combine(workDir, "extract");
                if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
                ZipFile.ExtractToDirectory(downloaded, extractDir);
                try { File.Delete(downloaded); } catch { }

                // The portable zip wraps everything in a single top-level "Sox" folder (see
                // build-release.ps1). Copy that folder's *contents* into the app dir, not the folder
                // itself, or the update would land in appDir\Sox\ and leave the running exe untouched.
                var payload = ResolvePortablePayload(extractDir);
                if (payload is null)
                    return info with { Error = "更新包结构无法识别（缺少 Sox 目录）" };
                extractDir = payload;
            }

            _pendingScript = CreateUpdaterScript(setupExe, extractDir);
            return info with { Error = null };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return info with { Error = "准备更新失败：" + ex.Message };
        }
    }

    /// <summary>Consumes the staged update script path (null when nothing is staged).</summary>
    public string? ConsumePendingScript()
    {
        var script = _pendingScript;
        _pendingScript = null;
        return script;
    }

    /// <summary>The directory whose contents should be mirrored over the app dir. The portable zip
    /// contains a single top-level folder (currently "Sox"); if there is exactly one directory and no
    /// loose files, descend into it, otherwise treat the extract root as the payload.</summary>
    private static string? ResolvePortablePayload(string extractDir)
    {
        var dirs = Directory.GetDirectories(extractDir);
        var files = Directory.GetFiles(extractDir);
        if (files.Length == 0 && dirs.Length == 1)
            return dirs[0];

        // A single folder named Sox even alongside a readme is still the payload.
        var sox = dirs.FirstOrDefault(d => string.Equals(Path.GetFileName(d), "Sox", StringComparison.OrdinalIgnoreCase));
        return sox ?? (files.Length > 0 ? extractDir : null);
    }

    private static string CreateUpdaterScript(string? setupExe, string? extractDir)
    {
        var appDir = AppContext.BaseDirectory.TrimEnd('\\');
        var script = Path.Combine(Path.GetTempPath(), "SoxUpdate", $"updater-{DateTime.Now:HHmmss}.cmd");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("@echo off");
        sb.AppendLine("setlocal");
        sb.AppendLine(":WAIT");
        sb.AppendLine("tasklist /FI \"IMAGENAME eq Sox.App.exe\" 2>nul | find /I \"Sox.App.exe\" >nul");
        sb.AppendLine("if not errorlevel 1 ( timeout /t 1 /nobreak >nul & goto WAIT )");

        if (setupExe is not null)
        {
            // Installer path: run the new setup silently, which also stops the service, replaces the
            // files and re-registers the service.
            sb.AppendLine($"\"{setupExe}\" /SILENT /SP- /NORESTART");
        }
        else if (extractDir is not null)
        {
            // Portable path: the service runs from Service\ under the app dir and holds those DLLs
            // locked, so it has to be stopped before robocopy can replace them. The relaunched app
            // starts it again. User data (Data\) is never touched.
            //
            // Wait for STOPPED instead of a fixed sleep: the service also terminates its hook child on
            // stop, and copying while Service\Sox.Service.exe is still mapped fails. `sc stop` returns
            // before the SCM reaches STOPPED, so poll `sc query` for the STOPPED state (or a missing
            // service) rather than guessing a delay.
            sb.AppendLine("sc stop SoxService >nul 2>&1");
            sb.AppendLine("set /a _t=0");
            sb.AppendLine(":WAITSTOP");
            sb.AppendLine("sc query SoxService 2>nul | find \"STOPPED\" >nul");
            sb.AppendLine("if not errorlevel 1 goto SVCDOWN");
            sb.AppendLine("sc query SoxService >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto SVCDOWN");
            sb.AppendLine("set /a _t+=1");
            sb.AppendLine("if %_t% geq 30 goto SVCDOWN");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("goto WAITSTOP");
            sb.AppendLine(":SVCDOWN");
            // A --hook child from a pre-fix build survives the service stop and keeps
            // Service\Sox.Service.exe locked; force it down before robocopy overwrites it.
            sb.AppendLine("taskkill /F /IM Sox.Service.exe /T >nul 2>&1");
            sb.AppendLine($"robocopy \"{extractDir}\" \"{appDir}\" /E /XD Data /NFL /NDL /NJH /NJS /R:1 /W:1 >nul");
            // robocopy exit codes 0-7 are success (1 = files copied); 8 and above are real failures.
            // The script runs in a hidden window, so never `pause`: log the failure and still relaunch.
            sb.AppendLine("if errorlevel 8 echo %date% %time% robocopy errorlevel %errorlevel% > \"%TEMP%\\SoxUpdate\\update-error.log\"");
            sb.AppendLine($"rmdir /s /q \"{extractDir}\"");
        }

        sb.AppendLine($"start \"\" \"{appDir}\\Sox.App.exe\"");
        sb.AppendLine("del \"%~f0\"");
        sb.AppendLine("endlocal");

        File.WriteAllText(script, sb.ToString());
        return script;
    }

    /// <summary>Picks the artifact for this install: setup.exe when installed, else the portable zip,
    /// scoped by architecture and the running variant (merged = self-contained, split = framework
    /// dependent) so a split install is not silently swapped to the much larger merged package.</summary>
    private static string? SelectAssetUrl(string version, IReadOnlyList<ReleaseAsset> assets)
    {
        var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64";

        var variant = IsSelfContained ? "merged" : "split";
        var want = IsInstalled
            ? $"-{arch}-{variant}-setup.exe"
            : $"-{arch}-{variant}-portable.zip";

        // Exact naming first, then the other variant, then a looser fallback so a renamed or missing
        // variant still resolves.
        var exact = assets.FirstOrDefault(a => a.Name.EndsWith(want, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact.Url;

        var other = IsInstalled
            ? $"-{arch}-merged-setup.exe"
            : $"-{arch}-merged-portable.zip";
        var fallback = assets.FirstOrDefault(a => a.Name.EndsWith(other, StringComparison.OrdinalIgnoreCase));
        if (fallback is not null)
            return fallback.Url;

        var installed = IsInstalled;
        return assets.FirstOrDefault(a =>
                a.Name.Contains(arch, StringComparison.OrdinalIgnoreCase)
                && (installed ? a.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)
                              : a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            ?.Url;
    }

    /// <summary>True when this copy ships its own .NET runtime (the merged variant). A framework
    /// dependent publish has no coreclr.dll beside the app; a self-contained one does.</summary>
    private static bool IsSelfContained =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "coreclr.dll"));

    private async Task<string> FetchLatestAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var mirror in Mirrors)
        {
            try
            {
                return await _http.GetStringAsync(ApplyMirror(mirror, ReleasesApi), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
            }
        }

        throw last ?? new InvalidOperationException("No GitHub mirror reachable.");
    }

    private static string ApplyMirror(string mirror, string url) =>
        string.IsNullOrEmpty(mirror) ? url : $"{mirror}/{url}";

    private async Task DownloadFileAsync(string url, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var mirror in Mirrors)
        {
            try
            {
                var target = ApplyMirror(mirror, url);
                using var response = await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? 0;
                await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using var dst = File.Create(dest);

                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    if (total > 0)
                        progress?.Report((double)received / total);
                }

                if (total > 0 && received < total)
                    throw new IOException("下载不完整");

                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                last = ex;
                try { if (File.Exists(dest)) File.Delete(dest); } catch { }
            }
        }

        throw last ?? new InvalidOperationException("下载失败");
    }

    private static (string? Tag, string? HtmlUrl, List<ReleaseAsset> Assets) ParseLatest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        var html = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;

        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                    assets.Add(new ReleaseAsset(name, url));
            }
        }

        return (tag, html, assets);
    }

    private sealed record ReleaseAsset(string Name, string Url);
}
