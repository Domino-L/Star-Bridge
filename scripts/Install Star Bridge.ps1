$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$userLocalAppData = [Environment]::GetFolderPath("LocalApplicationData")
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$publishDir = Join-Path $root "StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish"
$installDir = Join-Path $userLocalAppData "Programs\Star Bridge"
$exePath = Join-Path $installDir "Star Bridge.exe"
$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Star Bridge.lnk"
$startMenuDir = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Star Bridge"
$startMenuShortcut = Join-Path $startMenuDir "Star Bridge.lnk"

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:APPDATA = Join-Path $root ".appdata"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"

Write-Host "Publishing Star Bridge..."
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false | Write-Host

if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Star Bridge.exe"))) {
    throw "Published app was not found: $publishDir"
}

Write-Host "Installing to $installDir..."
if (Test-Path -LiteralPath $installDir) {
    Remove-Item -LiteralPath $installDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $installDir | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir "*") -Destination $installDir -Recurse -Force

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

Write-Host ""
Write-Host "Star Bridge installed."
Write-Host "Desktop shortcut: $desktopShortcut"
Write-Host "Start menu shortcut: $startMenuShortcut"
