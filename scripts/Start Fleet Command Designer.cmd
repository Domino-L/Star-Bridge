@echo off
set ROOT=%~dp0..
set DOTNET_CLI_HOME=%ROOT%\.dotnet-home
set APPDATA=%ROOT%\.appdata
set LOCALAPPDATA=%ROOT%\.localappdata
dotnet run --project "%ROOT%\SCFleetCommand.Desktop\SCFleetCommand.Desktop.csproj"
