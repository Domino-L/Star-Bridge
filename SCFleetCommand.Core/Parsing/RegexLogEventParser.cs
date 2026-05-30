using System.Text.RegularExpressions;
using SCFleetCommand.Core.Events;

namespace SCFleetCommand.Core.Parsing;

public sealed class RegexLogEventParser : ILogEventParser
{
    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly ParserRule[] _rules =
    [
        new(FleetEventType.PlayerEnteredShip, new Regex(@"<SHUDEvent_OnNotification>\s+Added notification ""[^""]*(?:joined|åŠ |加入)[^""]*(?:channel|é¢‘é“|频道)\s+'(?<ship>[^:']+)\s+:\s+(?<player>[^']+)'", Options), PlayerIsShipOwner: true),
        new(FleetEventType.PlayerExitedShip, new Regex(@"<SHUDEvent_OnNotification>\s+Added notification ""[^""]*(?:left|é€€|退出)[^""]*(?:channel|é¢‘é“|频道)\s+'(?<ship>[^:']+)\s+:\s+(?<player>[^']+)'", Options), PlayerIsShipOwner: true),
        new(FleetEventType.PlayerOnline, new Regex(@"nickname=""(?<player>[^""]+)""\s+playerGEID\s*=?\s*""?(?<playerId>\d+)?", Options)),
        new(FleetEventType.PlayerOffline, new Regex(@"PLAYER_OFFLINE\s+player=""?(?<player>[^""\s]+)""?", Options)),
        new(FleetEventType.PlayerLocationChanged, new Regex(@"<RequestLocationInventory>\s+Player\[(?<player>[^\]]+)\]\s+requested inventory for Location\[(?<location>[A-Za-z0-9_]+)\]", Options)),
        new(FleetEventType.PlayerEnteredShip, new Regex(@"PLAYER_ENTER_SHIP\s+player=""?(?<player>[^""\s]+)""?\s+ship=""?(?<ship>[^""]+?)""?$", Options)),
        new(FleetEventType.PlayerExitedShip, new Regex(@"PLAYER_EXIT_SHIP\s+player=""?(?<player>[^""\s]+)""?\s+ship=""?(?<ship>[^""]+?)""?$", Options)),
        new(FleetEventType.PlayerLocationChanged, new Regex(@"PLAYER_LOCATION\s+player=""?(?<player>[^""\s]+)""?\s+location=""?(?<location>[^""]+?)""?$", Options)),
        new(FleetEventType.CombatStateChanged, new Regex(@"COMBAT_STATE\s+player=""?(?<player>[^""\s]+)""?\s+state=""?(?<combat>[^""\s]+)""?", Options)),
        new(FleetEventType.NetworkStateChanged, new Regex(@"NETWORK_STATE\s+player=""?(?<player>[^""\s]+)""?\s+state=""?(?<network>[^""\s]+)""?", Options)),
        new(FleetEventType.PlayerStoppedDrivingShip, new Regex(@"ClearDriver:.*?Local client node.*?'(?<ship>[A-Za-z0-9_]+)_\d+'", Options)),
        new(FleetEventType.PlayerControllingShip, new Regex(@"SetDriver:.*?Local client node.*?'(?<ship>[A-Za-z0-9_]+)_\d+'", Options)),
        new(FleetEventType.PlayerControllingShip, new Regex(@"Local client node.*?(acquiring|taking|received).*?control token.*?'(?<ship>[A-Za-z0-9_]+)_\d+'", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Failed to get starmap route data!>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_\d+\[\d+\]\|CSCItemNavigation::GetStarmapRouteSegmentData", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Player (Requested Fuel to Quantum Target|Selected Quantum Target).*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_\d+\[\d+\]\|CSCItemNavigation", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Calculate Route>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_\d+\[\d+\]\|CSCItemNavigation::CalculateRoute", Options)),
    ];

    public FleetEvent? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        foreach (var rule in _rules)
        {
            var match = rule.Pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var player = Value(match, "player");
            var ship = Value(match, "ship");
            string? shipOwner = null;

            if (ship is not null)
            {
                ship = NormalizeShipName(ship);
            }

            if (rule.PlayerIsShipOwner)
            {
                shipOwner = player;
                player = "LocalPlayer";

                if (!string.IsNullOrWhiteSpace(ship) && !string.IsNullOrWhiteSpace(shipOwner))
                {
                    ship = $"{shipOwner}'s {ship}";
                }
            }

            if (string.IsNullOrWhiteSpace(player))
            {
                player = "LocalPlayer";
            }

            return new FleetEvent(
                rule.Type,
                player,
                Ship: ship,
                Location: Value(match, "location"),
                CombatState: Value(match, "combat"),
                NetworkState: Value(match, "network"),
                Timestamp: DateTimeOffset.Now,
                SourceLine: line,
                PlayerId: Value(match, "playerId"),
                ShipOwner: shipOwner);
        }

        return null;
    }

    private static string? Value(Match match, string name)
    {
        var group = match.Groups[name];
        return group.Success ? group.Value.Trim() : null;
    }

    private static string NormalizeShipName(string raw)
    {
        var index = raw.LastIndexOf('_');

        if (index <= 0)
        {
            return raw;
        }

        var suffix = raw[(index + 1)..];

        return suffix.All(char.IsDigit)
            ? raw[..index]
            : raw;
    }

    private sealed record ParserRule(FleetEventType Type, Regex Pattern, bool PlayerIsShipOwner = false);
}
