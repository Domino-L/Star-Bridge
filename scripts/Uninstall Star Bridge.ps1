$ErrorActionPreference = "Stop"

$installDir = Join-Path $env:LOCALAPPDATA "Programs\Star Bridge"
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
