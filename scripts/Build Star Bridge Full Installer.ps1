$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$nugetConfig = Join-Path $root "NuGet.Config"
$distDir = Join-Path $root "dist"
$version = "0.3.1"
$publishDir = Join-Path $root "StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish"
$stageDir = Join-Path $distDir "StarBridge-$version-win-x64-full"
$installerExe = Join-Path $distDir "StarBridge-$version-win-x64-full-installer.exe"
$sedPath = Join-Path $distDir "StarBridge-$version-win-x64-full-installer.sed"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:APPDATA = Join-Path $root ".appdata"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

Write-Host "Publishing full self-contained Star Bridge..."
dotnet publish $project -c Release -r win-x64 --self-contained true --configfile $nugetConfig -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Full installer build needs the .NET self-contained runtime packs."
    Write-Host "If this fails with NU1100, run this script once on a machine with internet access."
    throw "Self-contained publish failed."
}

if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Star Bridge.exe"))) {
    throw "Published app was not found: $publishDir"
}

if (Test-Path -LiteralPath $stageDir) {
    Remove-Item -LiteralPath $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir "*") -Destination $stageDir -Recurse -Force
$stageConfigDir = Join-Path $stageDir "config"
if (Test-Path -LiteralPath $stageConfigDir) {
    Remove-Item -LiteralPath $stageConfigDir -Recurse -Force
}

@'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0InstallFromPackage.ps1"
'@ | Set-Content -LiteralPath (Join-Path $stageDir "Install Star Bridge.cmd") -Encoding ASCII

@'
@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0UninstallFromPackage.ps1"
pause
'@ | Set-Content -LiteralPath (Join-Path $stageDir "Uninstall Star Bridge.cmd") -Encoding ASCII

@'
$ErrorActionPreference = "Stop"

$sourceDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs\Star Bridge"
$exePath = Join-Path $installDir "Star Bridge.exe"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Star Bridge.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Star Bridge"
$startMenuShortcut = Join-Path $startMenuDir "Star Bridge.lnk"

if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -LiteralPath (Join-Path $sourceDir "*") -Destination $installDir -Recurse -Force

New-Item -ItemType Directory -Force -Path $startMenuDir | Out-Null
$shell = New-Object -ComObject WScript.Shell

foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $exePath
    $shortcut.WorkingDirectory = $installDir
    $shortcut.IconLocation = "$exePath,0"
    $shortcut.Description = "Star Bridge - æ˜Ÿæµ·èˆ°æ¡¥"
    $shortcut.Save()
}

Start-Process -FilePath $exePath -WorkingDirectory $installDir
'@ | Set-Content -LiteralPath (Join-Path $stageDir "InstallFromPackage.ps1") -Encoding UTF8

@'
$ErrorActionPreference = "Stop"

$installDir = Join-Path ([Environment]::GetFolderPath("LocalApplicationData")) "Programs\Star Bridge"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Star Bridge.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Star Bridge"
$startMenuShortcut = Join-Path $startMenuDir "Star Bridge.lnk"

foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}

if (Test-Path -LiteralPath $startMenuDir) {
    $remaining = Get-ChildItem -LiteralPath $startMenuDir -Force -ErrorAction SilentlyContinue
    if (-not $remaining) {
        Remove-Item -LiteralPath $startMenuDir -Force
    }
}

Write-Host "Star Bridge removed."
'@ | Set-Content -LiteralPath (Join-Path $stageDir "UninstallFromPackage.ps1") -Encoding UTF8

@"
Star Bridge / æ˜Ÿæµ·èˆ°æ¡¥ $version

This is the full Windows x64 installer build.
It includes the .NET runtime files produced by self-contained publishing.

Install:
- Run "StarBridge-$version-win-x64-full-installer.exe"

Uninstall:
- Use "%LOCALAPPDATA%\Programs\Star Bridge\Uninstall Star Bridge.cmd"
"@ | Set-Content -LiteralPath (Join-Path $stageDir "README.txt") -Encoding UTF8

$files = Get-ChildItem -LiteralPath $stageDir -File | Sort-Object Name
$fileStrings = New-Object System.Text.StringBuilder
$sourceEntries = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt $files.Count; $i++) {
    [void]$fileStrings.AppendLine("FILE$i=`"$($files[$i].Name)`"")
    [void]$sourceEntries.AppendLine("%FILE$i%=")
}

$escapedInstallerExe = $installerExe.Replace("\", "\\")
$escapedStageDir = $stageDir.Replace("\", "\\")
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=Star Bridge installed.
TargetName=$escapedInstallerExe
FriendlyName=Star Bridge $version
AppLaunched=Install Star Bridge.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
$fileStrings
[SourceFiles]
SourceFiles0=$escapedStageDir
[SourceFiles0]
$sourceEntries
"@

$sed | Set-Content -LiteralPath $sedPath -Encoding ASCII

Write-Host "Building installer exe..."
$iexpress = Join-Path $env:SystemRoot "System32\iexpress.exe"
& $iexpress /N $sedPath
if ($LASTEXITCODE -ne 0) {
    throw "IExpress failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath $installerExe)) {
    throw "Installer was not created: $installerExe"
}

Write-Host "Full installer created: $installerExe"
