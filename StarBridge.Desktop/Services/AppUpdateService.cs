namespace StarBridge.Desktop;

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Windows;

internal sealed class AppUpdateService
{
    private readonly HttpClient _httpClient;
    private readonly Func<string, Uri> _buildUri;
    private readonly Window _owner;
    private readonly Action<string> _setStatus;
    private readonly Action<bool> _setCheckButtonEnabled;

    public AppUpdateService(
        HttpClient httpClient,
        Func<string, Uri> buildUri,
        Window owner,
        Action<string> setStatus,
        Action<bool> setCheckButtonEnabled)
    {
        _httpClient = httpClient;
        _buildUri = buildUri;
        _owner = owner;
        _setStatus = setStatus;
        _setCheckButtonEnabled = setCheckButtonEnabled;
    }

    public async Task CheckForInstallerUpdateAsync(bool silent, string currentVersion)
    {
        try
        {
            if (!silent)
            {
                _setStatus($"正在检查更新... 当前版本 V{currentVersion}");
            }

            var manifest = await _httpClient.GetFromJsonAsync<UpdateManifest>(_buildUri("api/updates/latest"));
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
            {
                if (!silent)
                {
                    _setStatus($"服务器没有返回更新信息。当前版本 V{currentVersion}");
                }

                return;
            }

            if (!IsNewerVersion(manifest.Version, currentVersion))
            {
                _setStatus($"当前已是最新版本 V{currentVersion}。");
                return;
            }

            var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "无版本说明。" : manifest.Notes.Trim();
            _setStatus($"发现新版本 V{manifest.Version}。{notes}");
            if (string.IsNullOrWhiteSpace(manifest.PackageUrl) && string.IsNullOrWhiteSpace(manifest.DownloadUrl))
            {
                _setStatus($"发现新版本 V{manifest.Version}。{notes} 服务器尚未配置下载地址。");
                return;
            }

            var updateMode = string.IsNullOrWhiteSpace(manifest.PackageUrl) ? "完整安装包更新" : "软件内覆盖更新";
            var message = $"发现新版本 V{manifest.Version}。\n\n{notes}\n\n更新方式：{updateMode}\n是否现在更新？";
            if (MessageBox.Show(_owner, message, "星海舰桥更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            {
                if (!string.IsNullOrWhiteSpace(manifest.PackageUrl))
                {
                    await DownloadAndApplyPackageUpdateAsync(manifest);
                }
                else
                {
                    await DownloadAndRunInstallerUpdateAsync(manifest);
                }
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                _setStatus($"检查更新失败：{ex.Message}");
            }
        }
    }

    private async Task DownloadAndApplyPackageUpdateAsync(UpdateManifest manifest)
    {
        if (!Uri.TryCreate(manifest.PackageUrl, UriKind.Absolute, out var packageUri))
        {
            _setStatus("更新失败：服务器返回的覆盖更新包地址无效。");
            return;
        }

        _setCheckButtonEnabled(false);
        try
        {
            var updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StarBridge",
                "Updates");
            Directory.CreateDirectory(updateRoot);

            var safeVersion = string.Join("_", manifest.Version.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var packagePath = Path.Combine(updateRoot, $"StarBridge-{safeVersion}-win-x64-update.zip");
            await DownloadUpdateFileAsync(packageUri, packagePath, manifest.Version);

            _setStatus("下载完成，正在准备覆盖更新...");
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "Star Bridge.exe");
            var scriptPath = CreatePortableUpdateScript(updateRoot, packagePath, appDir, exePath);

            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -ProcessId {Environment.ProcessId}"
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _setStatus($"更新失败：{ex.Message}");
            _setCheckButtonEnabled(true);
        }
    }

    private async Task DownloadAndRunInstallerUpdateAsync(UpdateManifest manifest)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var downloadUri))
        {
            _setStatus("更新失败：服务器返回的安装包地址无效。");
            return;
        }

        _setCheckButtonEnabled(false);
        try
        {
            var updateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "StarBridge",
                "Updates");
            Directory.CreateDirectory(updateRoot);

            var safeVersion = string.Join("_", manifest.Version.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var installerPath = Path.Combine(updateRoot, $"StarBridge-{safeVersion}-win-x64-setup.exe");

            await DownloadUpdateFileAsync(downloadUri, installerPath, manifest.Version);

            _setStatus("下载完成，正在启动覆盖安装...");
            Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                Arguments = "/CLOSEAPPLICATIONS /NORESTART /SP-"
            });

            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            _setStatus($"更新失败：{ex.Message}");
            _setCheckButtonEnabled(true);
        }
    }

    private async Task DownloadUpdateFileAsync(Uri downloadUri, string destinationPath, string version)
    {
        _setStatus($"正在下载 V{version} 更新...");
        using var updateClient = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        using var response = await updateClient.GetAsync(downloadUri, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[128 * 1024];
        long receivedBytes = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read));
            receivedBytes += read;
            if (totalBytes is > 0)
            {
                var percent = Math.Clamp(receivedBytes * 100 / totalBytes.Value, 0, 100);
                _setStatus($"正在下载 V{version} 更新... {percent}%");
            }
        }
    }

    private static string CreatePortableUpdateScript(string updateRoot, string packagePath, string appDir, string exePath)
    {
        var scriptPath = Path.Combine(updateRoot, "apply-starbridge-update.ps1");
        var extractDir = Path.Combine(updateRoot, "extracted");
        var escapedPackage = EscapePowerShellSingleQuoted(packagePath);
        var escapedExtract = EscapePowerShellSingleQuoted(extractDir);
        var escapedAppDir = EscapePowerShellSingleQuoted(appDir);
        var escapedExe = EscapePowerShellSingleQuoted(exePath);

        var script = $$"""
param([int]$ProcessId)
$ErrorActionPreference = 'Stop'
$packagePath = '{{escapedPackage}}'
$extractDir = '{{escapedExtract}}'
$appDir = '{{escapedAppDir}}'
$exePath = '{{escapedExe}}'

try {
    Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
} catch {}

if (Test-Path -LiteralPath $extractDir) {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $extractDir | Out-Null
Expand-Archive -LiteralPath $packagePath -DestinationPath $extractDir -Force

$sourceDir = $extractDir
if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'Star Bridge.exe'))) {
    $candidate = Get-ChildItem -LiteralPath $extractDir -Directory -Recurse |
        Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'Star Bridge.exe') } |
        Select-Object -First 1
    if ($candidate) {
        $sourceDir = $candidate.FullName
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'Star Bridge.exe'))) {
    throw '更新包中没有找到 Star Bridge.exe。'
}

Get-ChildItem -LiteralPath $sourceDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $appDir -Recurse -Force
}

Start-Process -FilePath $exePath -WorkingDirectory $appDir
Start-Sleep -Seconds 2
try {
    Remove-Item -LiteralPath $extractDir -Recurse -Force
    Remove-Item -LiteralPath $packagePath -Force
} catch {}
""";

        File.WriteAllText(scriptPath, script, Encoding.UTF8);
        return scriptPath;
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private static bool IsNewerVersion(string remoteVersion, string currentVersion)
    {
        return Version.TryParse(NormalizeVersionForCompare(remoteVersion), out var remote) &&
               Version.TryParse(NormalizeVersionForCompare(currentVersion), out var current) &&
               remote > current;
    }

    private static string NormalizeVersionForCompare(string value)
    {
        var version = value.Trim().TrimStart('v', 'V');
        var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => version
        };
    }
}
