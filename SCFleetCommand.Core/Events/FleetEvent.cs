namespace SCFleetCommand.Core.Events;

public sealed record FleetEvent(
    FleetEventType Type,
    string Player,
    string? Ship = null,
    string? Location = null,
    string? CombatState = null,
    string? NetworkState = null,
    DateTimeOffset? Timestamp = null,
    string? SourceLine = null,
    string? PlayerId = null,
    string? ShipOwner = null);
