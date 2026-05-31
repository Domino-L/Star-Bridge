$ErrorActionPreference = "Stop"

$desktopShortcut = Join-Path ([Environment]::GetFolderPath("DesktopDirectory")) "Star Bridge.lnk"
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath("StartMenu")) "Programs\Star Bridge.lnk"

foreach ($shortcutPath in @($desktopShortcut, $startMenuShortcut)) {
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath
    }
}

Write-Host "Star Bridge shortcuts removed."
