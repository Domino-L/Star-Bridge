using System.Text.RegularExpressions;
using StarBridge.Core.Events;

namespace StarBridge.Core.Parsing;

public sealed class RegexLogEventParser : ILogEventParser
{
    private static readonly RegexOptions Options =
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private readonly ParserRule[] _rules =
    [
        new(FleetEventType.PlayerEnteredShip, new Regex(@"<SHUDEvent_OnNotification>\s+Added notification ""[^""]*(?:joined|加入)[^""]*(?:channel|频道)\s+'(?<ship>[^:']+)\s+:\s+(?<player>[^']+)'", Options), PlayerIsShipOwner: true),
        new(FleetEventType.PlayerExitedShip, new Regex(@"<SHUDEvent_OnNotification>\s+Added notification ""[^""]*(?:left|退出|离开)[^""]*(?:channel|频道)\s+'(?<ship>[^:']+)\s+:\s+(?<player>[^']+)'", Options), PlayerIsShipOwner: true),
        new(FleetEventType.PlayerOnline, new Regex(@"nickname=""(?<player>[^""]+)""\s+playerGEID\s*=?\s*""?(?<playerId>\d+)?", Options)),
        new(FleetEventType.PlayerOffline, new Regex(@"PLAYER_OFFLINE\s+player=""?(?<player>[^""\s]+)""?", Options)),
        new(FleetEventType.PlayerLocationChanged, new Regex(@"<RequestLocationInventory>\s+Player\[(?<player>[^\]]+)\]\s+requested inventory for Location\[(?<location>[A-Za-z0-9_-]+)\]", Options), LocationEvidenceScore: 95, LocationEvidence: "Location inventory context", ClearsShipState: true),
        new(FleetEventType.PlayerNavigationTargetChanged, new Regex(@"<Player Selected Quantum Target - Local>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|.*?Player has selected point (?<target>[A-Za-z0-9_-]+) as their destination", Options)),
        new(FleetEventType.PlayerNavigationTargetChanged, new Regex(@"<Calculate Route>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|.*?Projected Start Location is (?<location>.+?) for route to destination (?<target>[A-Za-z0-9_-]+)", Options)),
        new(FleetEventType.PlayerNavigationTargetChanged, new Regex(@"<Calculate Route>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|.*?route to destination (?<target>[A-Za-z0-9_-]+)", Options)),
        new(FleetEventType.PlayerNavigationTargetChanged, new Regex(@"<Calculate Route>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|.*?Successfully calculated route to (?<target>[A-Za-z0-9_-]+)", Options)),
        new(FleetEventType.PlayerLocationChanged, new Regex(@"<Quantum Drive Arrived - Arrived at Final Destination>.*?CSCItemNavigation::OnQuantumDriveArrived", Options), DefaultLocation: "Arrived - awaiting location confirmation", LocationEvidenceScore: 45, LocationEvidence: "Quantum arrival"),
        new(FleetEventType.PlayerEnteredShip, new Regex(@"PLAYER_ENTER_SHIP\s+player=""?(?<player>[^""\s]+)""?\s+ship=""?(?<ship>[^""]+?)""?$", Options)),
        new(FleetEventType.PlayerExitedShip, new Regex(@"PLAYER_EXIT_SHIP\s+player=""?(?<player>[^""\s]+)""?\s+ship=""?(?<ship>[^""]+?)""?$", Options)),
        new(FleetEventType.PlayerLocationChanged, new Regex(@"PLAYER_LOCATION\s+player=""?(?<player>[^""\s]+)""?\s+location=""?(?<location>[^""]+?)""?$", Options), LocationEvidenceScore: 90, LocationEvidence: "Explicit player location"),
        new(FleetEventType.CombatStateChanged, new Regex(@"COMBAT_STATE\s+player=""?(?<player>[^""\s]+)""?\s+state=""?(?<combat>[^""\s]+)""?", Options)),
        new(FleetEventType.NetworkStateChanged, new Regex(@"NETWORK_STATE\s+player=""?(?<player>[^""\s]+)""?\s+state=""?(?<network>[^""\s]+)""?", Options)),
        new(FleetEventType.PlayerStoppedDrivingShip, new Regex(@"ClearDriver:.*?Local client node.*?'(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)'", Options)),
        new(FleetEventType.PlayerControllingShip, new Regex(@"SetDriver:.*?Local client node.*?'(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)'", Options)),
        new(FleetEventType.PlayerControllingShip, new Regex(@"Local client node.*?(acquiring|taking|received).*?control token.*?'(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)'", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Failed to get starmap route data!>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|CSCItemNavigation::GetStarmapRouteSegmentData", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Player (Requested Fuel to Quantum Target|Selected Quantum Target).*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|CSCItemNavigation", Options)),
        new(FleetEventType.PlayerShipControlSignal, new Regex(@"<Calculate Route>.*?\|\s*(?:NOT AUTH|AUTH)\s*\|\s*(?<ship>[A-Za-z0-9_]+)_(?<shipId>\d+)\[\d+\]\|CSCItemNavigation::CalculateRoute", Options)),
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
            var shipInstanceId = Value(match, "shipId");
            var location = Value(match, "location");
            var navigationTarget = Value(match, "target") ?? location;
            string? shipOwner = null;

            if (ship is not null)
            {
                ship = NormalizeShipName(ship);
            }

            if (rule.PlayerIsShipOwner)
            {
                shipOwner = player;
                player = "LocalPlayer";
            }

            if (string.IsNullOrWhiteSpace(player))
            {
                player = "LocalPlayer";
            }

            if (!string.IsNullOrWhiteSpace(location) && !string.IsNullOrWhiteSpace(rule.LocationPrefix))
            {
                location = $"{rule.LocationPrefix}{location}";
            }

            if (string.IsNullOrWhiteSpace(location))
            {
                location = rule.DefaultLocation;
            }

            return new FleetEvent(
                rule.Type,
                player,
                Ship: ship,
                Location: location,
                CombatState: Value(match, "combat"),
                NetworkState: Value(match, "network"),
                Timestamp: DateTimeOffset.Now,
                SourceLine: line,
                PlayerId: Value(match, "playerId"),
                ShipOwner: shipOwner,
                ShipInstanceId: shipInstanceId,
                NavigationTarget: rule.Type == FleetEventType.PlayerNavigationTargetChanged ? navigationTarget : null,
                LocationEvidenceScore: GetLocationEvidenceScore(rule, location),
                LocationEvidence: GetLocationEvidence(rule, location),
                ClearsShipState: rule.ClearsShipState);
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

    private sealed record ParserRule(
        FleetEventType Type,
        Regex Pattern,
        bool PlayerIsShipOwner = false,
        string? LocationPrefix = null,
        string? DefaultLocation = null,
        int LocationEvidenceScore = 0,
        string? LocationEvidence = null,
        bool ClearsShipState = false);

    private static int GetLocationEvidenceScore(ParserRule rule, string? location)
    {
        if (rule.Type == FleetEventType.PlayerNavigationTargetChanged && !string.IsNullOrWhiteSpace(location))
        {
            return 60;
        }

        return rule.LocationEvidenceScore;
    }

    private static string? GetLocationEvidence(ParserRule rule, string? location)
    {
        if (rule.Type == FleetEventType.PlayerNavigationTargetChanged && !string.IsNullOrWhiteSpace(location))
        {
            return "Quantum route start location";
        }

        return rule.LocationEvidence;
    }
}
