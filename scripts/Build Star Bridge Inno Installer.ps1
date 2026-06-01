$ErrorActionPreference = "Stop"

$scriptsDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptsDir
$project = Join-Path $root "StarBridge.Desktop\StarBridge.Desktop.csproj"
$serverProject = Join-Path $root "StarBridge.Server\StarBridge.Server.csproj"
$innoScript = Join-Path $root "installer\StarBridge.iss"
$nugetConfig = Join-Path $root "NuGet.Config"
$publishDir = Join-Path $root "StarBridge.Desktop\bin\Release\net8.0-windows\win-x64\publish"
$serverPublishDir = Join-Path $root "StarBridge.Server\bin\Release\net8.0\win-x64\publish"
$isccCandidates = @(
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1

if (-not $iscc) {
    $pathIscc = Get-Command "iscc.exe" -ErrorAction SilentlyContinue
    if ($pathIscc) {
        $iscc = $pathIscc.Source
    }
}

if (-not $iscc) {
    throw "Inno Setup 6 compiler was not found. Install Inno Setup 6 or add ISCC.exe to PATH."
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "Project not found: $project"
}

if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Relay server project not found: $serverProject"
}

if (-not (Test-Path -LiteralPath $innoScript)) {
    throw "Inno script not found: $innoScript"
}

$env:DOTNET_CLI_HOME = Join-Path $root ".dotnet-home"
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
$env:APPDATA = Join-Path $root ".appdata"
$env:NUGET_PACKAGES = Join-Path $root ".nuget-packages"

Write-Host "Publishing full self-contained Star Bridge..."
dotnet publish $project -c Release -r win-x64 --self-contained true --configfile $nugetConfig -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Self-contained publish failed."
    Write-Host "If the error is NU1100, run this script while connected to the internet so .NET can download the runtime packs."
    throw "Publish failed."
}

if (-not (Test-Path -LiteralPath (Join-Path $publishDir "Star Bridge.exe"))) {
    throw "Published app was not found: $publishDir"
}

$publishConfigDir = Join-Path $publishDir "config"
if (Test-Path -LiteralPath $publishConfigDir) {
    Remove-Item -LiteralPath $publishConfigDir -Recurse -Force
}

Write-Host "Publishing full self-contained Star Bridge Relay Server..."
dotnet publish $serverProject -c Release -r win-x64 --self-contained true --configfile $nugetConfig -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) {
    throw "Relay server publish failed."
}

if (-not (Test-Path -LiteralPath (Join-Path $serverPublishDir "Star Bridge Relay Server.exe"))) {
    throw "Published relay server was not found: $serverPublishDir"
}

Write-Host "Building Inno Setup installer..."
& $iscc $innoScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE."
}

Write-Host ""
Write-Host "Installer created:"
Write-Host (Join-Path $root "dist\StarBridge-0.2.2-win-x64-setup.exe")
