@echo off
setlocal
set "ROOT=%~dp0.."
set "INSTALLED_SERVER=%ROOT%\RelayServer\Star Bridge Relay Server.exe"

if exist "%INSTALLED_SERVER%" (
    echo Starting Star Bridge Relay Server on http://0.0.0.0:5058
    echo Keep this window open while testing network sync.
    "%INSTALLED_SERVER%" http://0.0.0.0:5058
    pause
    exit /b
)

set "DOTNET_CLI_HOME=%ROOT%\.dotnet-home"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
set "APPDATA=%ROOT%\.appdata"
set "NUGET_PACKAGES=%ROOT%\.nuget-packages"
dotnet run --project "%ROOT%\StarBridge.Server\StarBridge.Server.csproj" -- http://0.0.0.0:5058
pause
