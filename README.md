# Star Citizen Fleet Command System

Prototype verification build for the fleet command concept.

## Current Prototype

This version proves the first core loop:

```text
Game.log
  -> log watcher
  -> event parser
  -> fleet state
  -> live command display
```

It does not use injection, hooks, memory reading, or game automation. It only reads a log file and renders the interpreted fleet state.

## Run

Desktop app:

```powershell
.\scripts\Start Fleet Command Designer.cmd
```

Install normal Windows shortcuts:

```powershell
.\scripts\Install Fleet Command Shortcuts.ps1
```

The shortcut opens the published desktop app when available and falls back to the development build if needed.

To adjust the current app colors and visual style, edit:

```text
SCFleetCommand.Desktop/MainWindow.xaml
SCFleetCommand.Desktop/Themes/AppTheme.xaml
```

Design notes live in `docs/FRONTEND_DESIGN.md`.

The older Windows Forms prototype remains in `SCFleetCommand.App` for reference.

Console prototype:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
dotnet run --project .\SCFleetCommand.Console -- --replay
```

On first launch, the prototype asks you to paste the full Star Citizen `Game.log` path. It stores that path in `fleet-command.config` and reads from the same log on future launches.

To choose a different log file:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
dotnet run --project .\SCFleetCommand.Console -- --select-log --replay
```

To watch a custom log file:

```powershell
$env:DOTNET_CLI_HOME = "$PWD\.dotnet-home"
dotnet run --project .\SCFleetCommand.Console -- --log "C:\Path\To\Game.log" --replay
```

## Prototype Log Format

The first parser accepts simple verification lines:

```text
PLAYER_ONLINE player="domino_CN"
PLAYER_ENTER_SHIP player="domino_CN" ship="RSI Polaris"
PLAYER_LOCATION player="domino_CN" location="Stanton"
COMBAT_STATE player="domino_CN" state="Combat"
PLAYER_OFFLINE player="domino_CN"
```

The parser also includes a few human-readable fallback patterns so real `Game.log` samples can be mapped incrementally.

## Next Milestone

1. Collect real Star Citizen `Game.log` samples.
2. Add dedicated parsers: player, ship, location, combat, network.
3. Add a local host mode or SignalR server for multi-client sync.
4. Add a WPF commander dashboard after the event pipeline is stable.
