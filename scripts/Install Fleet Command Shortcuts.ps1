$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$launcher = Join-Path $scriptsDir "Start Fleet Command App.vbs"
$publishedExe = Join-Path $root "SCFleetCommand.App\bin\Release\net8.0-windows\win-x64\publish\SC Fleet Command.exe"

if (-not (Test-Path -LiteralPath $launcher)) {
    throw "Launcher not found: $launcher"
}

$shell = New-Object -ComObject WScript.Shell
$desktop = [Environment]::GetFolderPath("DesktopDirectory")
$startMenu = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs"

$targets = @(
    Join-Path $desktop "SC Fleet Command.lnk",
    Join-Path $startMenu "SC Fleet Command.lnk"
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

Write-Host "SC Fleet Command shortcuts installed."
