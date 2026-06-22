namespace StarBridge.Desktop;

public enum FleetInfoPanelKind
{
    Notice,
    CurrentTask,
    ActionPlan
}

public sealed record NetworkPlayerSnapshot(
    string Name,
    string? Callsign,
    string? Fleet,
    string? Squad,
    bool Online,
    string? Ship,
    string? ShipConfidence,
    string? Location,
    string? LocationConfidence,
    DateTimeOffset LastUpdated,
    string? AvatarImageData = null,
    NetworkOwnedShipSnapshot[]? OwnedShips = null);

public sealed record NetworkOwnedShipSnapshot(
    string Code,
    string DisplayName,
    string Source,
    DateTimeOffset ImportedAt);

public sealed record NetworkFleetSnapshot(
    string Name,
    string Code,
    string? Commander,
    string? Description,
    string? Type,
    string? ActiveTime,
    string? JoinPolicy,
    string? LogoText,
    string? LogoImageData,
    NetworkSquadSnapshot[]? Squads,
    int OnlineMembers,
    int TotalMembers,
    string? NoticeTitle,
    string? NoticeContent,
    string? CurrentTaskTitle,
    string? CurrentTaskBrief,
    string? CurrentTaskParticipants,
    string? CurrentTaskRally,
    string? CurrentTaskShip,
    DateTime? CurrentTaskTime,
    NetworkActionPlanSnapshot[]? ActionPlans,
    DateTimeOffset LastUpdated,
    string? OwnerAccount = null,
    NetworkFleetMemberPermissionSnapshot[]? MemberPermissions = null,
    NetworkFleetMemberSnapshot[]? Members = null,
    NetworkFleetEventLogSnapshot[]? EventLog = null,
    int CurrentTaskNoticeRevision = 0,
    NetworkFleetShipSnapshot[]? Ships = null,
    NetworkFleetTaskHistorySnapshot[]? TaskHistory = null,
    NetworkFleetApplicationSnapshot[]? Applications = null);

public sealed record NetworkFleetApplicationSnapshot(
    string Id,
    string ApplicantGameName,
    string? ApplicantCallsign,
    string? ApplicantAccount,
    string? Message,
    string Status,
    DateTimeOffset CreatedAt,
    string? AvatarImageData = null);

public sealed record NetworkFleetMemberPermissionSnapshot(
    string GameName,
    string? Callsign,
    string RoleTitle,
    bool PermissionEnabled,
    bool CanRemoveMembers,
    bool CanPublishTasks,
    bool CanPublishPlans,
    bool CanManageFleetInfo,
    DateTimeOffset UpdatedAt);

public sealed record NetworkFleetMemberSnapshot(
    string GameName,
    string? Callsign,
    string RoleTitle,
    string SquadName,
    bool Online,
    string? Ship,
    string? Location,
    DateTimeOffset LastUpdated,
    string? AvatarImageData = null,
    string? LocationConfidence = null);

public sealed record NetworkFleetShipSnapshot(
    string Code,
    string DisplayName,
    string OwnerGameName,
    string? OwnerCallsign,
    string? OwnerSquad,
    string? OwnerAvatarImageData,
    DateTimeOffset ImportedAt);

public sealed record NetworkFleetEventLogSnapshot(
    string Id,
    DateTimeOffset Timestamp,
    string Type,
    string Title,
    string Detail);

public sealed record NetworkFleetTaskHistorySnapshot(
    string Key,
    string Title,
    string Brief,
    string Status,
    string Participants,
    string Rally,
    string RequiredShip,
    string PublishedAtText);

public sealed record NetworkSquadSnapshot(
    string Name,
    string? Commander,
    string? Type,
    string? Description,
    string? Mission = null,
    string? RallyPoint = null,
    string? EmblemImageData = null,
    DateTimeOffset UpdatedAt = default);

public sealed record FleetSquadMemberMutationRequest(
    string FleetCode,
    string SquadName,
    string TargetGameName,
    string? TargetCallsign = null);

public sealed record FleetSquadLeaveRequest(
    string FleetCode,
    string SquadName,
    string? SuccessorGameName = null,
    string? SuccessorCallsign = null);

public sealed record NetworkActionPlanSnapshot(
    string Id,
    string Title,
    string Content,
    DateTime StartTime,
    bool NotifyMembers,
    NetworkActionPlanParticipantSnapshot[]? Participants);

public sealed record NetworkActionPlanParticipantSnapshot(
    string Callsign,
    string GameName,
    string? AvatarPath,
    string Initials,
    string? AvatarImageData = null);

public sealed record AuthRequest(
    string UserName,
    string Password,
    string? GameName,
    string? Email = null,
    string? VerificationCode = null,
    string? Callsign = null);

public sealed record AuthResponse(
    string UserName,
    string? Email,
    string? Callsign,
    string? GameName,
    string Token,
    bool AllowEmailNotifications = true);

public sealed record EmailVerificationRequest(
    string Email);

public sealed record ProfileUpdateRequest(
    string? Callsign,
    bool? AllowEmailNotifications = null);

public sealed record FeedbackRequest(
    string? Contact,
    string? GameName,
    string? Callsign,
    string Message);

public sealed record UpdateManifest(
    string Version,
    string? DownloadUrl,
    string? PackageUrl,
    string? Notes,
    bool Required = false,
    DateTimeOffset? PublishedAt = null,
    string? DownloadSha256 = null,
    string? PackageSha256 = null);

public sealed record FleetNotificationRequest(
    string FleetCode,
    string Subject,
    string Body);

public sealed record FleetNoticeUpdateRequest(
    string FleetCode,
    string? Title,
    string? Content,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetTaskUpdateRequest(
    string FleetCode,
    string? Title,
    string? Brief,
    string? Participants,
    string? Rally,
    string? Ship,
    DateTime? Time,
    int NoticeRevision,
    NetworkFleetTaskHistorySnapshot[]? TaskHistory = null,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetActionPlansUpdateRequest(
    string FleetCode,
    NetworkActionPlanSnapshot[]? ActionPlans,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetActionPlanJoinRequest(
    string FleetCode,
    string PlanId,
    NetworkActionPlanParticipantSnapshot Participant);

public sealed record FleetActionPlanLeaveRequest(
    string FleetCode,
    string PlanId);

public sealed record FleetJoinApplicationRequest(
    string FleetCode,
    string? Message = null);

public sealed record FleetApplicationDecisionRequest(
    string FleetCode,
    string ApplicationId,
    bool Approve);

public sealed record FleetLeaveRequest(
    string FleetCode,
    string? TransferCommanderTo = null,
    bool ConfirmDisbandIfOwnerAlone = false);

public sealed record FleetMemberPermissionUpdateRequest(
    string FleetCode,
    NetworkFleetMemberPermissionSnapshot Permission,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetInfoUpdateRequest(
    string FleetCode,
    string? Description,
    string? Type,
    string? ActiveTime,
    string? JoinPolicy,
    string? LogoText,
    string? LogoImageData,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetSquadsUpdateRequest(
    string FleetCode,
    NetworkSquadSnapshot[]? Squads,
    NetworkFleetEventLogSnapshot[]? EventLog = null);

public sealed record FleetDisbandRequest(
    string FleetCode,
    string Password);

public sealed record FleetMemberMutationRequest(
    string FleetCode,
    string TargetGameName);

public sealed record FleetCommanderTransferRequest(
    string FleetCode,
    string TargetGameName);
