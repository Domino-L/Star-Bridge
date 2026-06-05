$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$publishDir = Join-Path $root "StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish"
$distDir = Join-Path $root "dist"
$packageDir = Join-Path $distDir "StarBridge-0.3.1"
$zipPath = Join-Path $distDir "StarBridge-0.3.1-win-x64.zip"

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:APPDATA = Join-Path $root ".appdata"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"

Write-Host "Publishing Star Bridge..."
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false | Write-Host

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $packageDir | Out-Null
Copy-Item -LiteralPath (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force
$packageConfigDir = Join-Path $packageDir "config"
if (Test-Path -LiteralPath $packageConfigDir) {
    Remove-Item -LiteralPath $packageConfigDir -Recurse -Force
}

@"
Star Bridge / 星海舰桥 0.3.1

How to run:
1. Extract this zip.
2. Double-click "Star Bridge.exe".

Requirement:
- .NET 8 Desktop Runtime is required for this test package.
- Later installer builds can include the runtime automatically.
"@ | Set-Content -LiteralPath (Join-Path $packageDir "README.txt") -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -LiteralPath (Join-Path $packageDir "*") -DestinationPath $zipPath -Force

Write-Host "Package created: $zipPath"
