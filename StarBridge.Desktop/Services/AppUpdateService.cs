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
            ReportLastPortableUpdateResult(silent);

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
            var updateRoot = GetUpdateRoot();
            Directory.CreateDirectory(updateRoot);
            var logPath = GetPortableUpdateLogPath(updateRoot);
            var resultPath = GetPortableUpdateResultPath(updateRoot);
            TryDeleteFile(logPath);
            TryDeleteFile(resultPath);

            var safeVersion = string.Join("_", manifest.Version.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
            var packagePath = Path.Combine(updateRoot, $"StarBridge-{safeVersion}-win-x64-update.zip");
            await DownloadUpdateFileAsync(packageUri, packagePath, manifest.Version);

            _setStatus("下载完成，正在准备覆盖更新...");
            var appDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var exePath = Environment.ProcessPath ?? Path.Combine(appDir, "Star Bridge.exe");
            var scriptPath = CreatePortableUpdateScript(updateRoot, packagePath, appDir, exePath, resultPath, logPath);

            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -TargetProcessId {Environment.ProcessId}"
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
            var updateRoot = GetUpdateRoot();
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

    private static string CreatePortableUpdateScript(
        string updateRoot,
        string packagePath,
        string appDir,
        string exePath,
        string resultPath,
        string logPath)
    {
        var scriptPath = Path.Combine(updateRoot, "apply-starbridge-update.ps1");
        var extractDir = Path.Combine(updateRoot, "extracted");
        var escapedPackage = EscapePowerShellSingleQuoted(packagePath);
        var escapedExtract = EscapePowerShellSingleQuoted(extractDir);
        var escapedAppDir = EscapePowerShellSingleQuoted(appDir);
        var escapedExe = EscapePowerShellSingleQuoted(exePath);
        var escapedResult = EscapePowerShellSingleQuoted(resultPath);
        var escapedLog = EscapePowerShellSingleQuoted(logPath);

        var script = $$"""
param([int]$TargetProcessId)
$ErrorActionPreference = 'Stop'
$packagePath = '{{escapedPackage}}'
$extractDir = '{{escapedExtract}}'
$appDir = '{{escapedAppDir}}'
$exePath = '{{escapedExe}}'
$resultPath = '{{escapedResult}}'
$logPath = '{{escapedLog}}'

function Write-UpdateLog([string]$Message) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    Add-Content -LiteralPath $logPath -Value "[$stamp] $Message" -Encoding UTF8
}

function Set-UpdateResult([string]$State, [string]$Message) {
    Set-Content -LiteralPath $resultPath -Value "$State $Message" -Encoding UTF8
}

function Invoke-WithRetry([scriptblock]$Action, [string]$Name, [int]$Attempts = 30) {
    for ($i = 1; $i -le $Attempts; $i++) {
        try {
            & $Action
            return
        } catch {
            if ($i -eq $Attempts) {
                throw "$Name failed after $Attempts attempts. $($_.Exception.Message)"
            }

            Write-UpdateLog "$Name failed on attempt $i. Retrying..."
            Start-Sleep -Milliseconds 500
        }
    }
}

function Get-StarBridgeProcess {
    Get-Process -Name 'Star Bridge' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_.Id -ne $PID -and $_.Path -eq $exePath
        } catch {
            $false
        }
    }
}

function Wait-StarBridgeExit {
    for ($i = 0; $i -lt 180; $i++) {
        $target = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
        $sameApp = @(Get-StarBridgeProcess)
        if (-not $target -and $sameApp.Count -eq 0) {
            return
        }

        Start-Sleep -Milliseconds 500
    }

    throw 'Star Bridge did not exit in time.'
}

try {
    Write-UpdateLog 'Portable update started.'
    Wait-StarBridgeExit

    Invoke-WithRetry -Name 'remove extract directory' -Action {
        if (Test-Path -LiteralPath $extractDir) {
            Remove-Item -LiteralPath $extractDir -Recurse -Force -ErrorAction Stop
        }
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
        throw 'Update package does not contain Star Bridge.exe.'
    }

    Get-ChildItem -LiteralPath $sourceDir -Force | ForEach-Object {
        $itemPath = $_.FullName
        $itemName = $_.Name
        Invoke-WithRetry -Name "copy $itemName" -Action {
            Copy-Item -LiteralPath $itemPath -Destination $appDir -Recurse -Force -ErrorAction Stop
        }
    }

    if (-not (Test-Path -LiteralPath $exePath)) {
        throw 'Updated Star Bridge.exe was not found after copy.'
    }

    Set-UpdateResult 'OK' 'Portable update applied.'
    Write-UpdateLog 'Portable update completed.'
    Start-Process -FilePath $exePath -WorkingDirectory $appDir
    Start-Sleep -Seconds 2
    try {
        Remove-Item -LiteralPath $extractDir -Recurse -Force
        Remove-Item -LiteralPath $packagePath -Force
    } catch {}
} catch {
    Write-UpdateLog "FAILED: $($_.Exception.Message)"
    Set-UpdateResult 'FAILED' $_.Exception.Message
    try {
        if (Test-Path -LiteralPath $exePath) {
            Start-Process -FilePath $exePath -WorkingDirectory $appDir
        }
    } catch {}
}
""";

        File.WriteAllText(scriptPath, script, new UTF8Encoding(false));
        return scriptPath;
    }

    private static string EscapePowerShellSingleQuoted(string value)
    {
        return value.Replace("'", "''");
    }

    private void ReportLastPortableUpdateResult(bool silent)
    {
        try
        {
            var updateRoot = GetUpdateRoot();
            var resultPath = GetPortableUpdateResultPath(updateRoot);
            if (!File.Exists(resultPath))
            {
                return;
            }

            var result = File.ReadAllText(resultPath, Encoding.UTF8).Trim();
            if (string.IsNullOrWhiteSpace(result))
            {
                return;
            }

            if (result.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            {
                if (!silent)
                {
                    _setStatus("上次覆盖更新已完成。");
                }

                TryDeleteFile(resultPath);
                return;
            }

            if (result.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase))
            {
                _setStatus($"上次覆盖更新失败。日志：{GetPortableUpdateLogPath(updateRoot)}");
            }
        }
        catch
        {
        }
    }

    private static string GetUpdateRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarBridge",
            "Updates");
    }

    private static string GetPortableUpdateResultPath(string updateRoot)
    {
        return Path.Combine(updateRoot, "last-update-result.txt");
    }

    private static string GetPortableUpdateLogPath(string updateRoot)
    {
        return Path.Combine(updateRoot, "update.log");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
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
