namespace SCFleetCommand.Core.State;

public sealed class FleetPlayer
{
    public required string Name { get; init; }
    public string Ship { get; set; } = "Unknown";
    public string Location { get; set; } = "Unknown";
    public string Role { get; set; } = "Unassigned";
    public string CombatState { get; set; } = "Idle";
    public string NetworkState { get; set; } = "Unknown";
    public bool Online { get; set; }
    public bool IsIdle { get; set; }
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.Now;
}
