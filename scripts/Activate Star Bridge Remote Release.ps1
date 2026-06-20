param(
    [string]$ServerHost = "198.13.49.128",
    [string]$ServerUser = "starbridge-deploy",
    [int]$SshPort = 22,
    [string]$SshKeyPath = "",
    [string]$Version = "0.3.13",
    [string]$ApiBaseUrl = "https://api.scstarbridge.com",
    [string]$DownloadUrl = "https://github.com/Domino-L/Star-Bridge/releases/download/v0.3.13/StarBridge-0.3.13-win-x64-setup.exe",
    [string]$PackageUrl = "https://github.com/Domino-L/Star-Bridge/releases/download/v0.3.13/StarBridge-0.3.13-win-x64-update.zip",
    [string]$DownloadSha256 = "06614fb3a5d39326a3ed41a8087aeb82b60101843345eabb80d0ccee0f731f02",
    [string]$PackageSha256 = "9ef93ded4b81f09998c55a2487e85637baf572727d877ded12cc5069dfc21426",
    [string]$ReleaseNotes = "StarBridge 0.3.13 update."
)

$ErrorActionPreference = "Stop"

function ConvertTo-BashSingleQuoted {
    param([string]$Value)
    return "'" + ($Value -replace "'", "'\''") + "'"
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

$releaseNotesBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($ReleaseNotes))

$remoteScript = @"
set -euo pipefail

release_env="/tmp/starbridge-release-$Version.env"
cat > "`$release_env" <<'STARBRIDGE_RELEASE_ENV'
STARBRIDGE_LATEST_VERSION=$Version
STARBRIDGE_DOWNLOAD_URL=$DownloadUrl
STARBRIDGE_PACKAGE_URL=$PackageUrl
STARBRIDGE_DOWNLOAD_SHA256=$($DownloadSha256.ToLowerInvariant())
STARBRIDGE_PACKAGE_SHA256=$($PackageSha256.ToLowerInvariant())
STARBRIDGE_RELEASE_NOTES_B64=$releaseNotesBase64
STARBRIDGE_UPDATE_REQUIRED=false
STARBRIDGE_RELEASE_ENV

helper_err=/tmp/starbridge-activate-helper.err
if ! sudo /usr/local/sbin/starbridge-activate-release "`$release_env" 2>"`$helper_err"; then
  cat "`$helper_err" >&2 || true
  rm -f "`$helper_err"
  exit 1
fi
rm -f "`$helper_err"
rm -f "`$release_env"

echo "Local manifest:"
for i in {1..30}; do
  if curl -fs http://127.0.0.1:5058/api/updates/latest 2>/tmp/starbridge-local-curl.err; then
    echo
    break
  fi
  if [ "`$i" -eq 30 ]; then
    echo "Relay did not become ready." >&2
    cat /tmp/starbridge-local-curl.err 2>/dev/null >&2 || true
    echo "Run on the server for details: sudo systemctl status starbridge-relay --no-pager -l" >&2
    echo "Run on the server for logs: sudo journalctl -u starbridge-relay -n 80 --no-pager" >&2
    exit 1
  fi
  sleep 1
done
echo

echo "Public manifest:"
for i in {1..30}; do
  if curl -fs $ApiBaseUrl/api/updates/latest 2>/tmp/starbridge-public-curl.err; then
    echo
    break
  fi
  if [ "`$i" -eq 30 ]; then
    echo "Public HTTPS endpoint did not become ready." >&2
    cat /tmp/starbridge-public-curl.err 2>/dev/null >&2 || true
    echo "Run on the server for details: sudo systemctl status nginx --no-pager -l" >&2
    echo "Run on the server for logs: sudo tail -n 80 /var/log/nginx/error.log" >&2
    exit 1
  fi
  sleep 1
done
echo
"@

$tempScript = Join-Path $env:TEMP "starbridge-activate-$Version.sh"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($tempScript, $remoteScript, $utf8NoBom)

Write-Host "Activating StarBridge $Version on $ServerUser@$ServerHost ..."
Write-Host "Using key: $(Resolve-StarBridgeSshKeyPath)"
$sshArgs = Get-StarBridgeSshArgs
$root = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$remoteActivationLog = Join-Path $distDir "starbridge-remote-activation.log"
$remoteScriptBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($remoteScript))
$remoteDecodeCommand = "set -o pipefail; printf '%s' '$remoteScriptBase64' | base64 -d | tr -d '\r' | bash -s"
$remoteActivationCommand = "bash -lc " + (ConvertTo-BashSingleQuoted $remoteDecodeCommand)
$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
$remoteActivationOutput = ssh @sshArgs "$ServerUser@$ServerHost" $remoteActivationCommand 2>&1
$remoteActivationExitCode = $LASTEXITCODE
$ErrorActionPreference = $previousErrorActionPreference
$remoteActivationOutput | Tee-Object -FilePath $remoteActivationLog
if ($remoteActivationExitCode -ne 0) {
    throw "Remote activation failed. See: $remoteActivationLog"
}

Remove-Item -LiteralPath $tempScript -Force -ErrorAction SilentlyContinue
Write-Host "Remote release activation finished."
