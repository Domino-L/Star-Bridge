using StarBridge.Core.Events;

namespace StarBridge.Core.State;

public sealed class FleetState
{
    private static readonly TimeSpan ShipEvidenceDecayWindow = TimeSpan.FromMinutes(5);
    private const int MaxShipInferenceScore = 100;
    private const int ShipSignalRefreshBonus = 25;
    private const double ShipScoreDecayPerMinute = 8;
    private const double PostControlSeatExitDecayPerMinute = 10;
    private const int MaxLocationInferenceScore = 100;
    private const double LocationScoreDecayPerMinute = 4;
    private readonly Dictionary<string, FleetPlayer> _players = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FleetPlayer> Players => _players.Values
        .OrderByDescending(player => player.Online)
        .ThenBy(player => player.Name)
        .ToArray();

    public void Apply(FleetEvent fleetEvent)
    {
        var player = GetOrCreate(fleetEvent.Player);
        var timestamp = fleetEvent.Timestamp ?? DateTimeOffset.Now;
        player.LastSeen = timestamp;
        player.IsIdle = false;

        switch (fleetEvent.Type)
        {
            case FleetEventType.PlayerOnline:
                player.Online = true;
                RestoreLowConfidenceState(player, timestamp);
                break;
            case FleetEventType.PlayerOffline:
                player.Online = false;
                ClearActiveStateForOffline(player);
                break;
            case FleetEventType.PlayerEnteredShip:
                player.Online = true;
                AddShipEvidence(player, fleetEvent.Ship, fleetEvent.ShipInstanceId, 80, "Ship channel joined", timestamp);
                break;
            case FleetEventType.PlayerExitedShip:
                ClearShipInference(player, "Ship channel left");
                break;
            case FleetEventType.PlayerControllingShip:
                player.Online = true;
                AddShipEvidence(player, fleetEvent.Ship, fleetEvent.ShipInstanceId, 90, "Vehicle control token", timestamp);
                player.LastControlSeatLeftAt = null;
                break;
            case FleetEventType.PlayerShipControlSignal:
                player.Online = true;
                AddShipEvidence(player, fleetEvent.Ship, fleetEvent.ShipInstanceId, 35, "Navigation system context", timestamp);
                break;
            case FleetEventType.PlayerStoppedDrivingShip:
                player.Online = true;
                AddShipEvidence(player, fleetEvent.Ship, fleetEvent.ShipInstanceId, 20, "Left control seat; ship not confirmed left", timestamp);
                player.LastControlSeatLeftAt = timestamp;
                break;
            case FleetEventType.PlayerLocationChanged:
                player.Online = true;
                AddLocationEvidence(
                    player,
                    fleetEvent.Location,
                    fleetEvent.LocationEvidenceScore,
                    fleetEvent.LocationEvidence ?? "Location signal",
                    timestamp);
                if (fleetEvent.ClearsShipState)
                {
                    ClearShipInference(player, "Location inventory context");
                }
                break;
            case FleetEventType.PlayerNavigationTargetChanged:
                player.Online = true;
                AddLocationEvidence(
                    player,
                    fleetEvent.Location,
                    fleetEvent.LocationEvidenceScore,
                    fleetEvent.LocationEvidence ?? "Quantum route start location",
                    timestamp);
                player.NavigationTarget = fleetEvent.NavigationTarget ?? player.NavigationTarget;
                AddShipEvidence(player, fleetEvent.Ship, fleetEvent.ShipInstanceId, 45, "Quantum route context", timestamp);
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

    public void RefreshShipInferences(DateTimeOffset now)
    {
        foreach (var player in _players.Values)
        {
            DecayShipInference(player, now);
            DecayLocationInference(player, now);
        }
    }

    public void SetPlayerOnlineState(string name, bool online, DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(name) || !_players.TryGetValue(name, out var player))
        {
            return;
        }

        if (online)
        {
            player.Online = true;
            RestoreLowConfidenceState(player, timestamp);
            return;
        }

        player.Online = false;
        ClearActiveStateForOffline(player);
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

    private static void AddShipEvidence(
        FleetPlayer player,
        string? ship,
        string? shipInstanceId,
        int score,
        string evidence,
        DateTimeOffset timestamp)
    {
        DecayShipInference(player, timestamp);

        var isDifferentShip = !string.IsNullOrWhiteSpace(ship) &&
                              !player.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                              !player.Ship.Equals(ship, StringComparison.OrdinalIgnoreCase);
        var isDifferentShipInstance = !string.IsNullOrWhiteSpace(shipInstanceId) &&
                                      !string.IsNullOrWhiteSpace(player.ShipInstanceId) &&
                                      !player.ShipInstanceId.Equals(shipInstanceId, StringComparison.OrdinalIgnoreCase);

        if (isDifferentShip || isDifferentShipInstance)
        {
            player.ShipInferenceScore = 0;
            player.LastControlSeatLeftAt = null;
        }

        var sameShipInstanceSeenRecently = !string.IsNullOrWhiteSpace(shipInstanceId) &&
                                           player.ShipInstanceId?.Equals(shipInstanceId, StringComparison.OrdinalIgnoreCase) == true &&
                                           player.LastShipInstanceSeenAt is not null &&
                                           timestamp - player.LastShipInstanceSeenAt.Value <= ShipEvidenceDecayWindow;

        if (!string.IsNullOrWhiteSpace(ship))
        {
            player.Ship = ship;
        }

        if (!string.IsNullOrWhiteSpace(shipInstanceId))
        {
            player.ShipInstanceId = shipInstanceId;
            player.LastShipInstanceSeenAt = timestamp;
        }

        if (player.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        player.ShipInferenceScore = Math.Min(
            MaxShipInferenceScore,
            player.ShipInferenceScore + score + (sameShipInstanceSeenRecently ? ShipSignalRefreshBonus : 0));
        player.ShipConfidence = GetShipConfidence(player);
        player.ShipEvidence = evidence;
        player.LastShipEvidenceAt = timestamp;
        player.LastShipScoreUpdatedAt = timestamp;
    }

    private static void ClearShipInference(FleetPlayer player, string evidence)
    {
        player.Ship = "Unknown";
        player.ShipConfidence = "None";
        player.ShipEvidence = evidence;
        player.ShipInferenceScore = 0;
        player.ShipInstanceId = null;
        player.LastShipEvidenceAt = null;
        player.LastShipScoreUpdatedAt = null;
        player.LastShipInstanceSeenAt = null;
        player.LastControlSeatLeftAt = null;
    }

    private static void ClearActiveStateForOffline(FleetPlayer player)
    {
        if (!player.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            player.LastKnownShip = player.Ship;
            player.LastKnownShipInstanceId = player.ShipInstanceId;
        }

        if (!player.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            player.LastKnownLocation = player.Location;
        }

        ClearShipInference(player, "Player offline");
        ClearLocationInference(player, "Player offline");
        player.NavigationTarget = "None";
    }

    private static void RestoreLowConfidenceState(FleetPlayer player, DateTimeOffset timestamp)
    {
        if (player.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(player.LastKnownShip))
        {
            player.Ship = player.LastKnownShip;
            player.ShipInstanceId = player.LastKnownShipInstanceId;
            player.ShipInferenceScore = 15;
            player.ShipConfidence = "Low";
            player.ShipEvidence = "Restored after reconnect";
            player.LastShipEvidenceAt = timestamp;
            player.LastShipScoreUpdatedAt = timestamp;
        }

        if (player.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(player.LastKnownLocation))
        {
            player.Location = player.LastKnownLocation;
            player.LocationInferenceScore = 15;
            player.LocationConfidence = "Low";
            player.LocationEvidence = "Restored after reconnect";
            player.LastLocationEvidenceAt = timestamp;
            player.LastLocationScoreUpdatedAt = timestamp;
        }
    }

    private static void ClearLocationInference(FleetPlayer player, string evidence)
    {
        player.Location = "Unknown";
        player.LocationConfidence = "None";
        player.LocationEvidence = evidence;
        player.LocationInferenceScore = 0;
        player.LastLocationEvidenceAt = null;
        player.LastLocationScoreUpdatedAt = null;
    }

    private static void DecayShipInference(FleetPlayer player, DateTimeOffset now)
    {
        if (player.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (player.LastShipScoreUpdatedAt is not null)
        {
            var elapsedMinutes = Math.Max(0, (now - player.LastShipScoreUpdatedAt.Value).TotalMinutes);
            var decayPerMinute = ShipScoreDecayPerMinute +
                                 (player.LastControlSeatLeftAt is not null ? PostControlSeatExitDecayPerMinute : 0);
            var decayedScore = player.ShipInferenceScore - (int)Math.Floor(elapsedMinutes * decayPerMinute);
            player.ShipInferenceScore = Math.Max(0, decayedScore);
            player.LastShipScoreUpdatedAt = now;
        }

        var hasRecentSameShipSignal = player.LastShipInstanceSeenAt is not null &&
                                      now - player.LastShipInstanceSeenAt.Value < ShipEvidenceDecayWindow;

        if (player.LastControlSeatLeftAt is not null &&
            now - player.LastControlSeatLeftAt.Value >= ShipEvidenceDecayWindow &&
            !hasRecentSameShipSignal)
        {
            player.ShipInferenceScore = Math.Min(player.ShipInferenceScore, 44);
            player.ShipInferenceScore = Math.Max(player.ShipInferenceScore, 15);
            player.ShipEvidence = string.IsNullOrWhiteSpace(player.ShipInstanceId)
                ? "Left control seat over 5 minutes ago"
                : $"Ship ID {player.ShipInstanceId} not seen for 5+ minutes after leaving control seat";
        }

        player.ShipConfidence = GetShipConfidence(player);
        if (player.ShipConfidence.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            player.Ship = "Unknown";
            player.ShipEvidence = "Ship evidence expired";
            player.ShipInstanceId = null;
        }
    }

    private static string GetShipConfidence(FleetPlayer player)
    {
        if (player.ShipInferenceScore >= 80)
        {
            return "High";
        }

        if (player.ShipInferenceScore >= 45)
        {
            return "Medium";
        }

        if (player.ShipInferenceScore >= 15)
        {
            return "Low";
        }

        return "None";
    }

    private static void AddLocationEvidence(
        FleetPlayer player,
        string? location,
        int score,
        string evidence,
        DateTimeOffset timestamp)
    {
        if (string.IsNullOrWhiteSpace(location) || score <= 0)
        {
            return;
        }

        DecayLocationInference(player, timestamp);

        var isDifferentLocation = !player.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase) &&
                                  !player.Location.Equals(location, StringComparison.OrdinalIgnoreCase);

        if (isDifferentLocation)
        {
            player.LocationInferenceScore = 0;
        }

        player.Location = location;
        player.LocationInferenceScore = Math.Min(MaxLocationInferenceScore, player.LocationInferenceScore + score);
        player.LocationConfidence = GetLocationConfidence(player);
        player.LocationEvidence = evidence;
        player.LastLocationEvidenceAt = timestamp;
        player.LastLocationScoreUpdatedAt = timestamp;
    }

    private static void DecayLocationInference(FleetPlayer player, DateTimeOffset now)
    {
        if (player.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
            player.LastLocationScoreUpdatedAt is null)
        {
            return;
        }

        var elapsedMinutes = Math.Max(0, (now - player.LastLocationScoreUpdatedAt.Value).TotalMinutes);
        var decayedScore = player.LocationInferenceScore - (int)Math.Floor(elapsedMinutes * LocationScoreDecayPerMinute);
        player.LocationInferenceScore = Math.Max(0, decayedScore);
        player.LastLocationScoreUpdatedAt = now;
        player.LocationConfidence = GetLocationConfidence(player);

        if (player.LocationConfidence.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            player.Location = "Unknown";
            player.LocationEvidence = "Location evidence expired";
        }
    }

    private static string GetLocationConfidence(FleetPlayer player)
    {
        if (player.LocationInferenceScore >= 80)
        {
            return "High";
        }

        if (player.LocationInferenceScore >= 45)
        {
            return "Medium";
        }

        if (player.LocationInferenceScore >= 15)
        {
            return "Low";
        }

        return "None";
    }
}
