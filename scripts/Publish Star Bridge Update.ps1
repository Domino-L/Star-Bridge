param(
    [string]$ServerHost = "198.13.49.128",
    [string]$ServerUser = "starbridge-deploy",
    [int]$SshPort = 22,
    [string]$SshKeyPath = "",
    [string]$RemoteServerDir = "/opt/starbridge/server",
    [string]$GitHubRepo = "Domino-L/Star-Bridge",
    [string]$ApiBaseUrl = "https://api.scstarbridge.com",
    [string]$ReleaseNotes = "__DEFAULT_RELEASE_NOTES__",
    [switch]$SkipGitHubRelease,
    [switch]$SkipServerDeploy
)

$ErrorActionPreference = "Stop"

if ($ReleaseNotes -eq "__DEFAULT_RELEASE_NOTES__") {
    $ReleaseNotes = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("5L+u5aSN6L2v5Lu25YaF6KaG55uW5pu05paw5py65Yi244CC"))
}

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$serverProject = Join-Path $root "StarBridge.Server\StarBridge.Server.csproj"
$packageScript = Join-Path $scriptsDir "Package Star Bridge.ps1"
$installerScript = Join-Path $scriptsDir "Build Star Bridge Inno Installer.ps1"
$serverPublishDir = Join-Path $root "publish\server"
$distDir = Join-Path $root "dist"

function Get-StarBridgeVersion {
    [xml]$projectXml = Get-Content -LiteralPath $project -Encoding UTF8
    $version = [string]($projectXml.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
    if ([string]::IsNullOrWhiteSpace($version)) {
        throw "Project version was not found in $project"
    }

    return $version
}

function ConvertTo-BashSingleQuoted([string]$Value) {
    return "'" + $Value.Replace("'", "'`"`"'") + "'"
}

function Resolve-StarBridgeSshKeyPath {
    if (-not [string]::IsNullOrWhiteSpace($SshKeyPath)) {
        return [System.IO.Path]::GetFullPath($SshKeyPath)
    }

    return Join-Path $env:USERPROFILE ".ssh\starbridge_deploy"
}

function Get-StarBridgeSshArgs {
    $resolvedKeyPath = Resolve-StarBridgeSshKeyPath
    if (-not (Test-Path -LiteralPath $resolvedKeyPath)) {
        throw "SSH key was not found: $resolvedKeyPath. Run scripts\Setup Star Bridge Key Deploy.ps1 once, or pass -SshKeyPath."
    }

    return @(
        "-i", $resolvedKeyPath,
        "-p", "$SshPort",
        "-o", "IdentitiesOnly=yes",
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=accept-new"
    )
}

function Get-StarBridgeScpArgs {
    $resolvedKeyPath = Resolve-StarBridgeSshKeyPath
    if (-not (Test-Path -LiteralPath $resolvedKeyPath)) {
        throw "SSH key was not found: $resolvedKeyPath. Run scripts\Setup Star Bridge Key Deploy.ps1 once, or pass -SshKeyPath."
    }

    return @(
        "-i", $resolvedKeyPath,
        "-P", "$SshPort",
        "-o", "IdentitiesOnly=yes",
        "-o", "BatchMode=yes",
        "-o", "StrictHostKeyChecking=accept-new"
    )
}

$version = Get-StarBridgeVersion
$tag = "v$version"
$installerPath = Join-Path $distDir "StarBridge-$version-win-x64-setup.exe"
$updateZipPath = Join-Path $distDir "StarBridge-$version-win-x64-update.zip"
$downloadUrl = "https://github.com/$GitHubRepo/releases/download/$tag/StarBridge-$version-win-x64-setup.exe"
$packageUrl = "https://github.com/$GitHubRepo/releases/download/$tag/StarBridge-$version-win-x64-update.zip"

Write-Host "Star Bridge release version: $version"

Write-Host "Building update package..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $packageScript
if ($LASTEXITCODE -ne 0) {
    throw "Update package build failed."
}

Write-Host "Building installer..."
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Installer build failed."
}

if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer not found: $installerPath"
}

if (-not (Test-Path -LiteralPath $updateZipPath)) {
    throw "Update zip not found: $updateZipPath"
}

$installerSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $installerPath).Hash.ToLowerInvariant()
$updateZipSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $updateZipPath).Hash.ToLowerInvariant()
Write-Host "Installer SHA256: $installerSha256"
Write-Host "Update zip SHA256: $updateZipSha256"

if (-not $SkipGitHubRelease) {
    $gh = Get-Command "gh.exe" -ErrorAction SilentlyContinue
    if (-not $gh) {
        throw "GitHub CLI was not found. Install gh, run gh auth login, or rerun with -SkipGitHubRelease."
    }

    Write-Host "Uploading GitHub release $tag..."
    $releaseExists = $false
    try {
        & gh release view $tag --repo $GitHubRepo *> $null
        $releaseExists = $LASTEXITCODE -eq 0
    }
    catch {
        $releaseExists = $false
    }

    if ($releaseExists) {
        & gh release upload $tag $installerPath $updateZipPath --repo $GitHubRepo --clobber
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub release upload failed."
        }

        & gh release edit $tag --repo $GitHubRepo --title "StarBridge $version" --notes $ReleaseNotes
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub release edit failed."
        }
    }
    else {
        & gh release create $tag $installerPath $updateZipPath --repo $GitHubRepo --title "StarBridge $version" --notes $ReleaseNotes
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub release create failed."
        }
    }
}

if (-not $SkipServerDeploy) {
    $sshTarget = "${ServerUser}@${ServerHost}"
    $sshArgs = Get-StarBridgeSshArgs
    $scpArgs = Get-StarBridgeScpArgs

    Write-Host "Publishing relay server..."
    if (Test-Path -LiteralPath $serverPublishDir) {
        Remove-Item -LiteralPath $serverPublishDir -Recurse -Force
    }

    dotnet publish $serverProject -c Release -r linux-x64 --self-contained false -o $serverPublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Relay server publish failed."
    }

    Write-Host "Uploading relay server to $sshTarget..."
    & scp @scpArgs -r "$serverPublishDir\*" "${sshTarget}:$RemoteServerDir/"
    if ($LASTEXITCODE -ne 0) {
        throw "Relay server upload failed."
    }

    $remoteScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "starbridge-release-$version.sh"
    $releaseNotesBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ReleaseNotes))
    $quotedApiBaseUrl = ConvertTo-BashSingleQuoted $ApiBaseUrl

    $remoteScript = @"
#!/usr/bin/env bash
set -euo pipefail

release_env="/tmp/starbridge-release-$version.env"
cat > "`$release_env" <<'STARBRIDGE_RELEASE_ENV'
STARBRIDGE_LATEST_VERSION=$version
STARBRIDGE_DOWNLOAD_URL=$downloadUrl
STARBRIDGE_PACKAGE_URL=$packageUrl
STARBRIDGE_DOWNLOAD_SHA256=$installerSha256
STARBRIDGE_PACKAGE_SHA256=$updateZipSha256
STARBRIDGE_RELEASE_NOTES_B64=$releaseNotesBase64
STARBRIDGE_UPDATE_REQUIRED=false
STARBRIDGE_RELEASE_ENV

helper_err="`$(mktemp -t starbridge-activate-helper.XXXXXX)"
if ! sudo /usr/local/sbin/starbridge-activate-release "`$release_env" 2>"`$helper_err"; then
  cat "`$helper_err" >&2 || true
  rm -f "`$helper_err"
  exit 1
fi
rm -f "`$helper_err"
rm -f "`$release_env"

local_manifest="`$(mktemp -t starbridge-latest.XXXXXX)"
local_curl_err="`$(mktemp -t starbridge-local-curl.XXXXXX)"
public_curl_err="`$(mktemp -t starbridge-public-curl.XXXXXX)"
trap 'rm -f "`$local_manifest" "`$local_curl_err" "`$public_curl_err"' EXIT

echo "Waiting for local relay..."
for i in {1..40}; do
  if curl -fs http://127.0.0.1:5058/api/updates/latest >"`$local_manifest" 2>"`$local_curl_err"; then
    cat "`$local_manifest"
    echo
    break
  fi

  if [[ "`$i" -eq 40 ]]; then
    echo "Local relay did not become ready."
    cat "`$local_curl_err" 2>/dev/null || true
    echo "Run on the server for details: sudo systemctl status starbridge-relay --no-pager -l"
    echo "Run on the server for logs: sudo journalctl -u starbridge-relay -n 80 --no-pager"
    exit 1
  fi

  sleep 1
done

echo "Waiting for public HTTPS endpoint..."
for i in {1..30}; do
  if curl -fs $quotedApiBaseUrl/api/updates/latest 2>"`$public_curl_err"; then
    echo
    exit 0
  fi

  if [[ "`$i" -eq 30 ]]; then
    echo "Public HTTPS endpoint did not become ready."
    cat "`$public_curl_err" 2>/dev/null || true
    echo "Run on the server for details: sudo systemctl status nginx --no-pager -l"
    echo "Run on the server for logs: sudo tail -n 80 /var/log/nginx/error.log"
    exit 1
  fi

  sleep 1
done
echo
"@
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($remoteScriptPath, $remoteScript, $utf8NoBom)

    $remoteActivationLog = Join-Path $distDir "starbridge-remote-activation.log"
    $remoteScriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
    $remoteDecodeCommand = "set -o pipefail; printf '%s' '$remoteScriptBase64' | base64 -d | tr -d '\r' | bash -s"
    $remoteActivationCommand = "bash -lc " + (ConvertTo-BashSingleQuoted $remoteDecodeCommand)
    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $remoteActivationOutput = ssh @sshArgs $sshTarget $remoteActivationCommand 2>&1
    $remoteActivationExitCode = $LASTEXITCODE
    $ErrorActionPreference = $previousErrorActionPreference
    $remoteActivationOutput | Tee-Object -FilePath $remoteActivationLog
    if ($remoteActivationExitCode -ne 0) {
        throw "Remote release activation failed. See: $remoteActivationLog"
    }

    Remove-Item -LiteralPath $remoteScriptPath -Force
}

Write-Host "Release finished."
Write-Host "Installer: $installerPath"
Write-Host "Installer SHA256: $installerSha256"
Write-Host "Update zip: $updateZipPath"
Write-Host "Update zip SHA256: $updateZipSha256"
Write-Host "Latest manifest: $ApiBaseUrl/api/updates/latest"
