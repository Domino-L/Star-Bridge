param(
    [string]$ServerHost = "198.13.49.128",
    [string]$ServerUser = "root",
    [string]$RemoteServerDir = "/opt/starbridge/server",
    [string]$GitHubRepo = "Domino-L/Star-Bridge",
    [string]$ApiBaseUrl = "https://api.scstarbridge.com",
    [string]$ReleaseNotes = "修复软件内覆盖更新机制。",
    [switch]$SkipGitHubRelease,
    [switch]$SkipServerDeploy
)

$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$serverProject = Join-Path $root "StarBridge.Server\StarBridge.Server.csproj"
$packageScript = Join-Path $scriptsDir "Package Star Bridge.ps1"
$installerScript = Join-Path $scriptsDir "Build Star Bridge Inno Installer.ps1"
$serverPublishDir = Join-Path $root "publish\server"

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

$version = Get-StarBridgeVersion
$tag = "v$version"
$installerPath = Join-Path $root "dist\StarBridge-$version-win-x64-setup.exe"
$updateZipPath = Join-Path $root "dist\StarBridge-$version-win-x64-update.zip"
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
    Write-Host "Publishing relay server..."
    if (Test-Path -LiteralPath $serverPublishDir) {
        Remove-Item -LiteralPath $serverPublishDir -Recurse -Force
    }

    dotnet publish $serverProject -c Release -r linux-x64 --self-contained false -o $serverPublishDir
    if ($LASTEXITCODE -ne 0) {
        throw "Relay server publish failed."
    }

    Write-Host "Uploading relay server to $ServerHost..."
    & scp -r "$serverPublishDir\*" "${ServerUser}@${ServerHost}:$RemoteServerDir/"
    if ($LASTEXITCODE -ne 0) {
        throw "Relay server upload failed."
    }

    $remoteScriptPath = Join-Path ([System.IO.Path]::GetTempPath()) "starbridge-release-$version.sh"
    $quotedVersion = ConvertTo-BashSingleQuoted $version
    $quotedDownloadUrl = ConvertTo-BashSingleQuoted $downloadUrl
    $quotedPackageUrl = ConvertTo-BashSingleQuoted $packageUrl
    $quotedReleaseNotes = ConvertTo-BashSingleQuoted $ReleaseNotes
    $quotedApiBaseUrl = ConvertTo-BashSingleQuoted $ApiBaseUrl

    @"
#!/usr/bin/env bash
set -euo pipefail

env_file="/etc/starbridge/relay.env"
if [[ ! -f "`$env_file" ]]; then
  echo "Missing `$env_file. Create it before publishing updates."
  exit 1
fi

set_env() {
  local key="`$1"
  local value="`$2"
  if sudo grep -q "^`$key=" "`$env_file"; then
    local escaped_value
    escaped_value=`$(printf '%s' "`$value" | sed 's/[|&]/\\&/g')
    sudo sed -i "s|^`$key=.*|`$key=`$escaped_value|" "`$env_file"
  else
    printf '%s=%s\n' "`$key" "`$value" | sudo tee -a "`$env_file" >/dev/null
  fi
}

set_env STARBRIDGE_LATEST_VERSION $quotedVersion
set_env STARBRIDGE_DOWNLOAD_URL $quotedDownloadUrl
set_env STARBRIDGE_PACKAGE_URL $quotedPackageUrl
set_env STARBRIDGE_RELEASE_NOTES $quotedReleaseNotes
set_env STARBRIDGE_UPDATE_REQUIRED "false"

sudo systemctl restart starbridge-relay

echo "Waiting for local relay..."
for i in {1..40}; do
  if curl -fsS http://127.0.0.1:5058/api/updates/latest >/tmp/starbridge-latest.json; then
    cat /tmp/starbridge-latest.json
    echo
    break
  fi

  if [[ "`$i" -eq 40 ]]; then
    echo "Local relay did not become ready."
    sudo systemctl status starbridge-relay --no-pager -l || true
    sudo journalctl -u starbridge-relay -n 80 --no-pager || true
    exit 1
  fi

  sleep 1
done

echo "Waiting for public HTTPS endpoint..."
for i in {1..30}; do
  if curl -fsS $quotedApiBaseUrl/api/updates/latest; then
    echo
    exit 0
  fi

  if [[ "`$i" -eq 30 ]]; then
    echo "Public HTTPS endpoint did not become ready."
    sudo systemctl status nginx --no-pager -l || true
    sudo tail -n 80 /var/log/nginx/error.log || true
    exit 1
  fi

  sleep 1
done
echo
"@ | Set-Content -LiteralPath $remoteScriptPath -Encoding UTF8

    & scp $remoteScriptPath "${ServerUser}@${ServerHost}:/tmp/starbridge-release-$version.sh"
    if ($LASTEXITCODE -ne 0) {
        throw "Remote release script upload failed."
    }

    & ssh "${ServerUser}@${ServerHost}" "bash /tmp/starbridge-release-$version.sh && rm -f /tmp/starbridge-release-$version.sh"
    if ($LASTEXITCODE -ne 0) {
        throw "Remote release activation failed."
    }

    Remove-Item -LiteralPath $remoteScriptPath -Force
}

Write-Host "Release finished."
Write-Host "Installer: $installerPath"
Write-Host "Update zip: $updateZipPath"
Write-Host "Latest manifest: $ApiBaseUrl/api/updates/latest"
