using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MediaBrush = System.Windows.Media.Brush;

namespace StarBridge.Desktop;

public sealed record PlayerRow(
    string Name,
    string Status,
    string Ship,
    string ShipInfo,
    string Location,
    string? Callsign = null,
    string? AvatarPath = null,
    string Initials = "?",
    string SquadName = "Unassigned",
    string Role = "Member",
    MediaBrush? NameBrush = null,
    string RawShip = "Unknown",
    string ShipConfidence = "None");

public sealed record SquadMemberStatusRow(
    string Avatar,
    string? AvatarPath,
    string Role,
    string Callsign,
    string GameId,
    string OnlineStatus,
    string ShipStatus,
    string Location,
    MediaBrush? NameBrush = null);

public sealed record FleetShipInventoryRow(
    int Number,
    string ShipName,
    string ShipCode,
    string OwnerDisplay,
    string OwnerCallsign,
    string OwnerGameId,
    string OwnerSquad,
    string? OwnerAvatarPath,
    string OwnerInitials,
    string ImportedAtText);

public sealed record FleetTaskHistoryRow(
    string Key,
    string Title,
    string Brief,
    string Status,
    string Participants,
    string Rally,
    string RequiredShip,
    string PublishedAtText);

public sealed record FleetEventLogRow(
    string Id,
    DateTimeOffset Timestamp,
    string Type,
    string Title,
    string Detail)
{
    public string TimestampText => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
}

public sealed class FleetMemberManagementRow : INotifyPropertyChanged
{
    private string _roleTitle = "成员";
    private bool _permissionEnabled;
    private bool _canRemoveMembers;
    private bool _canPublishTasks;
    private bool _canPublishPlans;
    private bool _canManageFleetInfo;

    public string GameName { get; init; } = "";
    public string Callsign { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Initials { get; init; } = "?";
    public string? AvatarPath { get; init; }
    public string SquadName { get; init; } = "Unassigned";
    public string OnlineStatus { get; init; } = "Offline";
    public bool IsSelf { get; init; }
    public bool IsCommander { get; init; }
    public MediaBrush? RoleBrush { get; init; }
    public string HeaderLine => $"{SquadName} / {OnlineStatus}";
    public bool CanEditPermissions => !IsCommander;
    public bool ShowPermissionControls => IsCommander || PermissionEnabled;
    public bool ShowRoleEditor => PermissionEnabled && CanEditPermissions;
    public bool ShowRoleSummary => IsCommander || PermissionEnabled;
    public bool ShowSavePermissions => PermissionEnabled && CanEditPermissions;
    public bool CanTransferCommander => !IsSelf && !IsCommander;
    public bool CanRemoveFromFleet => !IsSelf && !IsCommander;

    public string RoleTitle
    {
        get => _roleTitle;
        set
        {
            if (_roleTitle == value)
            {
                return;
            }

            _roleTitle = value;
            OnChanged(nameof(RoleTitle));
        }
    }

    public bool PermissionEnabled
    {
        get => _permissionEnabled;
        set
        {
            if (_permissionEnabled == value)
            {
                return;
            }

            _permissionEnabled = value;
            OnChanged(nameof(PermissionEnabled));
            OnChanged(nameof(ShowPermissionControls));
            OnChanged(nameof(ShowRoleEditor));
            OnChanged(nameof(ShowRoleSummary));
            OnChanged(nameof(ShowSavePermissions));
        }
    }

    public bool CanRemoveMembers
    {
        get => _canRemoveMembers;
        set
        {
            if (_canRemoveMembers == value)
            {
                return;
            }

            _canRemoveMembers = value;
            OnChanged(nameof(CanRemoveMembers));
        }
    }

    public bool CanPublishTasks
    {
        get => _canPublishTasks;
        set
        {
            if (_canPublishTasks == value)
            {
                return;
            }

            _canPublishTasks = value;
            OnChanged(nameof(CanPublishTasks));
        }
    }

    public bool CanPublishPlans
    {
        get => _canPublishPlans;
        set
        {
            if (_canPublishPlans == value)
            {
                return;
            }

            _canPublishPlans = value;
            OnChanged(nameof(CanPublishPlans));
        }
    }

    public bool CanManageFleetInfo
    {
        get => _canManageFleetInfo;
        set
        {
            if (_canManageFleetInfo == value)
            {
                return;
            }

            _canManageFleetInfo = value;
            OnChanged(nameof(CanManageFleetInfo));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class FleetActionPlanRow : INotifyPropertyChanged
{
    public FleetActionPlanRow(string id, string title, string content, DateTime startTime, bool notifyMembers)
    {
        Id = id;
        Title = title;
        Content = content;
        StartTime = startTime;
        NotifyMembers = notifyMembers;
        RefreshParticipantSummary();
    }

    public string Id { get; }
    public string Title { get; }
    public string Content { get; }
    public DateTime StartTime { get; }
    public bool NotifyMembers { get; }
    public ObservableCollection<ActionPlanParticipantRow> Participants { get; } = [];
    public string StartTimeText => $"行动时间 / {StartTime:yyyy-MM-dd HH:mm}";
    public string NotifyText => NotifyMembers ? "通知 / 启用" : "通知 / 未启用";
    public string ParticipantCountText => $"参与 / {Participants.Count}";
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshParticipantSummary()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ParticipantCountText)));
    }
}

public sealed record ActionPlanParticipantRow(
    string Callsign,
    string GameName,
    string? AvatarPath,
    string Initials);

public sealed record NetworkFleetCard(
    NetworkFleetSnapshot Snapshot,
    string Name,
    string LogoText,
    string? LogoImageData,
    Visibility LogoTextVisibility,
    string CodeLine,
    string CommanderLine,
    string JoinPolicyLine,
    string Description,
    string TypeLine,
    string ActiveTimeLine,
    string MembersLine,
    bool CanJoin,
    string JoinButtonText,
    int SearchScore = 1)
{
    public static NetworkFleetCard FromSnapshot(
        NetworkFleetSnapshot snapshot,
        string currentFleetName,
        string currentFleetCode,
        bool hasFleet)
    {
        var code = string.IsNullOrWhiteSpace(snapshot.Code) ? "N/A" : snapshot.Code;
        var commander = string.IsNullOrWhiteSpace(snapshot.Commander) ? "Unassigned" : snapshot.Commander;
        var joinPolicy = string.IsNullOrWhiteSpace(snapshot.JoinPolicy) ? "Open" : snapshot.JoinPolicy;
        var description = string.IsNullOrWhiteSpace(snapshot.Description) ? "No fleet description." : snapshot.Description;
        var type = string.IsNullOrWhiteSpace(snapshot.Type) ? "Unknown" : snapshot.Type;
        var activeTime = string.IsNullOrWhiteSpace(snapshot.ActiveTime) ? "Unassigned" : snapshot.ActiveTime;
        var isCurrentFleet = hasFleet &&
                             (snapshot.Name.Equals(currentFleetName, StringComparison.OrdinalIgnoreCase) ||
                              code.Equals(currentFleetCode, StringComparison.OrdinalIgnoreCase));
        var hasLogoImage = !string.IsNullOrWhiteSpace(snapshot.LogoImageData);

        return new NetworkFleetCard(
            snapshot,
            snapshot.Name,
            string.IsNullOrWhiteSpace(snapshot.LogoText) ? code : snapshot.LogoText!,
            snapshot.LogoImageData,
            hasLogoImage ? Visibility.Collapsed : Visibility.Visible,
            $"识别码 / {code}",
            $"指挥官 / {commander}",
            joinPolicy.Equals("需要申请", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Equals("Application", StringComparison.OrdinalIgnoreCase)
                ? "加入 / 需要申请"
                : "加入 / 无门槛",
            description!,
            $"类型 / {type}",
            $"活动时间 / {activeTime}",
            $"{snapshot.OnlineMembers} 在线 / {snapshot.TotalMembers} 成员",
            !isCurrentFleet,
            isCurrentFleet ? "已加入" : "加入");
    }
}
