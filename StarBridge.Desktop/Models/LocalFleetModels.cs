namespace StarBridge.Desktop;

public sealed record LocalFleetState(
    bool HasFleet,
    string? FleetName,
    string? FleetCode,
    string? FleetChiefCommander,
    string? FleetDeputyCommander,
    string? FleetDescription,
    string? FleetType,
    string? FleetJoinPolicy,
    string? FleetActiveTime,
    string? FleetLogoPath,
    string? FleetNoticeTitle,
    string? FleetNoticeContent,
    string? FleetCurrentTaskTitle,
    string? FleetCurrentTaskBrief,
    string? FleetCurrentTaskParticipants,
    string? FleetCurrentTaskRally,
    string? FleetCurrentTaskShip,
    bool FleetCurrentTaskEmailCall,
    DateTime? FleetCurrentTaskTime,
    string? FleetCurrentTaskHistoryKey,
    int FleetCurrentTaskNoticeRevision,
    LocalFleetTaskHistory[]? TaskHistory,
    LocalSquadState[]? Squads,
    string? JoinedSquadName,
    LocalFleetActionPlan[]? ActionPlans,
    string[]? JoinedActionPlanIds,
    LocalFleetEventLog[]? EventLog,
    LocalFleetMemberPermission[]? MemberPermissions = null);

public sealed record LocalFleetMemberPermission(
    string GameName,
    string? Callsign,
    string RoleTitle,
    bool PermissionEnabled,
    bool CanRemoveMembers,
    bool CanPublishTasks,
    bool CanPublishPlans,
    bool CanManageFleetInfo,
    DateTimeOffset UpdatedAt);

public sealed record LocalFleetTaskHistory(
    string Key,
    string Title,
    string Brief,
    string Status,
    string Participants,
    string Rally,
    string RequiredShip,
    string PublishedAtText);

public sealed record LocalSquadState(
    string Name,
    string Icon,
    string Commander,
    string Mission,
    string RallyPoint,
    string Description,
    string Type,
    string? EmblemPath);

public sealed record LocalFleetActionPlan(
    string Id,
    string Title,
    string Content,
    DateTime StartTime,
    bool NotifyMembers,
    ActionPlanParticipantRow[]? Participants);

public sealed record LocalFleetEventLog(
    string Id,
    DateTimeOffset Timestamp,
    string Type,
    string Title,
    string Detail);
