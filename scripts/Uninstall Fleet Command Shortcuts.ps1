$ErrorActionPreference = "Stop"

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "SC Fleet Command.lnk"
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\SC Fleet Command.lnk"

foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath
    }
}

Write-Host "SC Fleet Command shortcuts removed."
