$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
& (Join-Path $scriptsDir "Install Star Bridge.ps1")
