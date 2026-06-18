param(
    [string]$ServerHost = "198.13.49.128",
    [string]$RootUser = "root",
    [string]$DeployUser = "starbridge-deploy",
    [int]$SshPort = 22,
    [string]$SshKeyPath = "",
    [switch]$SkipRemoteInstall
)

$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installScript = Join-Path $scriptsDir "install-starbridge-deploy-user.sh"

if ([string]::IsNullOrWhiteSpace($SshKeyPath)) {
    $SshKeyPath = Join-Path $env:USERPROFILE ".ssh\starbridge_deploy"
}

$SshKeyPath = [System.IO.Path]::GetFullPath($SshKeyPath)
$publicKeyPath = "$SshKeyPath.pub"
$sshDir = Split-Path -Parent $SshKeyPath

if (-not (Test-Path -LiteralPath $sshDir)) {
    New-Item -ItemType Directory -Path $sshDir | Out-Null
}

if (-not (Test-Path -LiteralPath $SshKeyPath)) {
    Write-Host "Generating SSH key: $SshKeyPath"
    "`n`n" | & ssh-keygen -t ed25519 -f $SshKeyPath -C "starbridge-deploy"
    if ($LASTEXITCODE -ne 0) {
        throw "ssh-keygen failed."
    }
}

if (-not (Test-Path -LiteralPath $publicKeyPath)) {
    throw "Public key was not found: $publicKeyPath"
}

if (-not (Test-Path -LiteralPath $installScript)) {
    throw "Server install script was not found: $installScript"
}

$publicKey = (Get-Content -LiteralPath $publicKeyPath -Raw).Trim()
$publicKeyB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($publicKey))

if (-not $SkipRemoteInstall) {
    Write-Host "Installing deploy user on $RootUser@$ServerHost ..."
    Write-Host "You will be asked for the root password once. Future publish runs use the SSH key."
    Get-Content -LiteralPath $installScript -Raw |
        ssh -p $SshPort -o StrictHostKeyChecking=accept-new "$RootUser@$ServerHost" "tr -d '\r' | env STARBRIDGE_DEPLOY_PUBLIC_KEY_B64='$publicKeyB64' DEPLOY_USER='$DeployUser' bash -s"

    if ($LASTEXITCODE -ne 0) {
        throw "Deploy user installation failed. Check the SSH output above. If it mentions permission denied, use the server root password. If it mentions sudoers or visudo, the server rejected the deploy helper setup."
    }
}

Write-Host "Testing deploy key login..."
& ssh -i $SshKeyPath -p $SshPort -o IdentitiesOnly=yes -o BatchMode=yes -o StrictHostKeyChecking=accept-new "$DeployUser@$ServerHost" "whoami && sudo -n /usr/local/sbin/starbridge-activate-release --check"
if ($LASTEXITCODE -ne 0) {
    throw "Deploy key test failed."
}

Write-Host ""
Write-Host "Semi-automatic deployment is ready."
Write-Host "Use:"
Write-Host "  & `".\scripts\Publish Star Bridge Update.ps1`""
Write-Host ""
Write-Host "Key path:"
Write-Host "  $SshKeyPath"
