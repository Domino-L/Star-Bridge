$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $scriptsDir "Uninstall Star Bridge.ps1")
