$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$launcher = Join-Path $scriptsDir "Start Fleet Command App.vbs"
$publishedExe = Join-Path $root "SCFleetCommand.Desktop\bin\Release\net8.0-windows\win-x64\publish\Star Bridge.exe"

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher not found: $launcher"
}

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("DesktopDirectory")
$startMenu = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs"

$targets = @(
    Join-Path $desktop "Star Bridge.lnk",
    Join-Path $startMenu "Star Bridge.lnk"
)

foreach ($shortcutPath in $targets) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = "wscript.exe"
    $shortcut.Arguments = "`"$launcher`""
    $shortcut.WorkingDirectory = $root
    if (Test-Path -LiteralPath $publishedExe) {
        $shortcut.IconLocation = "$publishedExe,0"
    }
    $shortcut.Save()
}

Write-Host "Star Bridge shortcuts installed."
