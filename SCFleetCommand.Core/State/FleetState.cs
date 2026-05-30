using SCFleetCommand.Core.Events;

namespace SCFleetCommand.Core.State;

public sealed class FleetState
{
    private readonly Dictionary<string, FleetPlayer> _players = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FleetPlayer> Players => _players.Values
        .OrderByDescending(player => player.Online)
        .ThenBy(player => player.Name)
        .ToArray();

    public void Apply(FleetEvent fleetEvent)
    {
        var player = GetOrCreate(fleetEvent.Player);
        player.LastSeen = fleetEvent.Timestamp ?? DateTimeOffset.Now;
        player.IsIdle = false;

        switch (fleetEvent.Type)
        {
            case FleetEventType.PlayerOnline:
                player.Online = true;
                break;
            case FleetEventType.PlayerOffline:
                player.Online = false;
                break;
            case FleetEventType.PlayerEnteredShip:
                player.Online = true;
                player.Ship = fleetEvent.Ship ?? player.Ship;
                break;
            case FleetEventType.PlayerExitedShip:
                player.Ship = "Unknown";
                break;
            case FleetEventType.PlayerControllingShip:
                player.Online = true;
                break;
            case FleetEventType.PlayerShipControlSignal:
                player.Online = true;
                break;
            case FleetEventType.PlayerStoppedDrivingShip:
                player.Online = true;
                break;
            case FleetEventType.PlayerLocationChanged:
                player.Online = true;
                player.Location = fleetEvent.Location ?? player.Location;
                break;
            case FleetEventType.CombatStateChanged:
                player.Online = true;
                player.CombatState = fleetEvent.CombatState ?? player.CombatState;
                break;
            case FleetEventType.NetworkStateChanged:
                player.NetworkState = fleetEvent.NetworkState ?? player.NetworkState;
                break;
        }
    }

    public FleetSummary GetSummary()
    {
        var players = Players;
        return new FleetSummary(
            TotalPlayers: players.Count,
            OnlinePlayers: players.Count(player => player.Online),
            ShipsKnown: players.Count(player => player.Ship != "Unknown"),
            LocationsKnown: players.Count(player => player.Location != "Unknown"));
    }

    private FleetPlayer GetOrCreate(string name)
    {
        if (_players.TryGetValue(name, out var player))
        {
            return player;
        }

        player = new FleetPlayer { Name = name };
        _players.Add(name, player);
        return player;
    }
}
