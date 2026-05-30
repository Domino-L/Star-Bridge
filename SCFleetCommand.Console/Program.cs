using SCFleetCommand.Core.Events;
using SCFleetCommand.Core.LogWatching;
using SCFleetCommand.Core.Parsing;
using SCFleetCommand.Core.State;

var options = PrototypeOptions.Resolve(args);
var parser = new RegexLogEventParser();
var fleetState = new FleetState();

Console.Title = "SC Fleet Command Prototype";
Console.WriteLine("SC Fleet Command - Prototype Verification");
Console.WriteLine($"Watching: {options.LogPath}");
Console.WriteLine(options.ReplayExistingLines
    ? "Mode: replay existing lines, then watch for new lines"
    : "Mode: watch new lines only");
Console.WriteLine();

using var watcher = new GameLogWatcher(options.LogPath, options.ReplayExistingLines, line =>
{
    var fleetEvent = parser.TryParse(line);
    if (fleetEvent is null)
    {
        return;
    }

    fleetState.Apply(fleetEvent);
    Render(fleetState, fleetEvent);
});

watcher.Start();

Console.WriteLine("Append lines to the log file to update fleet state. Press Ctrl+C to exit.");
await Task.Delay(Timeout.InfiniteTimeSpan);

static void Render(FleetState fleetState, FleetEvent latestEvent)
{
    if (!Console.IsOutputRedirected)
    {
        Console.Clear();
    }

    var summary = fleetState.GetSummary();

    Console.WriteLine("SC Fleet Command - Prototype Verification");
    Console.WriteLine($"Latest Event: {latestEvent.Type} | Player: {latestEvent.Player}");
    Console.WriteLine($"Fleet: {summary.OnlinePlayers}/{summary.TotalPlayers} online | Ships: {summary.ShipsKnown} | Locations: {summary.LocationsKnown}");
    Console.WriteLine();
    Console.WriteLine($"{"Player",-18} {"Online",-8} {"Ship",-24} {"Location",-18} {"Combat",-10} {"Network",-10}");
    Console.WriteLine(new string('-', 94));

    foreach (var player in fleetState.Players)
    {
        Console.WriteLine($"{Trim(player.Name, 18),-18} {Status(player.Online),-8} {Trim(player.Ship, 24),-24} {Trim(player.Location, 18),-18} {Trim(player.CombatState, 10),-10} {Trim(player.NetworkState, 10),-10}");
    }

    Console.WriteLine();
    Console.WriteLine("Append lines to the log file to update fleet state. Press Ctrl+C to exit.");
}

static string Status(bool online) => online ? "Online" : "Offline";

static string Trim(string value, int length)
{
    return value.Length <= length ? value : value[..Math.Max(0, length - 1)] + ".";
}

internal sealed record PrototypeOptions(string LogPath, bool ReplayExistingLines)
{
    public static PrototypeOptions Resolve(string[] args)
    {
        var replay = args.Contains("--replay", StringComparer.OrdinalIgnoreCase);
        var config = UserConfig.Load();
        var explicitLogPath = GetValue(args, "--log");
        var shouldSelectLog = args.Contains("--select-log", StringComparer.OrdinalIgnoreCase);
        var logPath = explicitLogPath;

        if (string.IsNullOrWhiteSpace(logPath) && (shouldSelectLog || string.IsNullOrWhiteSpace(config.LogPath)))
        {
            logPath = LogPathSelector.SelectGameLog(config.LogPath);
            if (!string.IsNullOrWhiteSpace(logPath))
            {
                UserConfig.Save(config with { LogPath = logPath });
            }
        }

        logPath ??= config.LogPath;
        logPath ??= Path.Combine(AppContext.BaseDirectory, "samples", "Game.log");

        if (!string.IsNullOrWhiteSpace(explicitLogPath))
        {
            UserConfig.Save(config with { LogPath = explicitLogPath });
        }

        return new PrototypeOptions(Path.GetFullPath(logPath), ReplayExistingLines: replay || !args.Contains("--log"));
    }

    private static string? GetValue(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}

internal sealed record UserConfig(string? LogPath)
{
    private static readonly string ConfigPath = Path.Combine(Environment.CurrentDirectory, "fleet-command.config");

    public static UserConfig Load()
    {
        if (!File.Exists(ConfigPath))
        {
            return new UserConfig(LogPath: null);
        }

        var logPath = File.ReadLines(ConfigPath)
            .Select(line => line.Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

        return new UserConfig(logPath);
    }

    public static void Save(UserConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.LogPath))
        {
            return;
        }

        File.WriteAllText(ConfigPath, config.LogPath);
    }
}

internal static class LogPathSelector
{
    public static string? SelectGameLog(string? currentPath)
    {
        Console.WriteLine("Select Star Citizen Game.log");
        Console.WriteLine("Paste the full Game.log path, then press Enter.");

        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            Console.WriteLine($"Current path: {currentPath}");
            Console.WriteLine("Press Enter without typing to keep the current path.");
        }

        Console.Write("> ");
        var input = Console.ReadLine()?.Trim().Trim('"');

        if (string.IsNullOrWhiteSpace(input))
        {
            return currentPath;
        }

        if (!File.Exists(input))
        {
            Console.WriteLine("That file does not exist. The prototype will use the sample log for now.");
            return null;
        }

        return input;
    }
}
