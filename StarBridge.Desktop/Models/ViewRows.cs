using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaSolidBrush = System.Windows.Media.SolidColorBrush;

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
    string ShipConfidence = "None",
    string LocationConfidence = "None",
    string RawLocation = "Unknown");

public sealed record SquadMemberStatusRow(
    string Avatar,
    string? AvatarPath,
    string Role,
    string Callsign,
    string GameId,
    string OnlineStatus,
    string ShipStatus,
    string Location,
    MediaBrush? NameBrush = null,
    bool CanRemoveFromSquad = false)
{
    public Visibility RemoveButtonVisibility => CanRemoveFromSquad ? Visibility.Visible : Visibility.Collapsed;
}

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
    string ImportedAtText,
    string ShipSpec,
    string ShipRole,
    string ShipStatus,
    string ShipPrice)
{
    public string ShipMetaLine => $"{ShipSpec} / {ShipStatus} / {ShipPrice}";
    public string ShipRoleLine => string.IsNullOrWhiteSpace(ShipRole) ? "定位待补充" : $"定位 / {ShipRole}";
    public string ShipRoleTag => string.IsNullOrWhiteSpace(ShipRole) ? "待补充" : ShipRole;
    public string ShipPriceTag => string.IsNullOrWhiteSpace(ShipPrice) ? "未公布" : ShipPrice;
    public decimal? ShipPriceValue => TryReadPrice(ShipPrice, out var price) ? price : null;
    public MediaBrush ShipSpecBrush => ShipSpec switch
    {
        "旗舰级" => CapitalShipBrush,
        "大型" => LargeShipBrush,
        "中型" => MediumShipBrush,
        "小型" => SmallShipBrush,
        _ => NeutralShipBrush
    };

    public MediaBrush ShipStatusBrush => ShipStatus == "可飞" ? FlyableShipBrush : ConceptShipBrush;

    public MediaBrush ShipPriceBrush
    {
        get
        {
            if (!TryReadPrice(ShipPrice, out var price))
            {
                return NeutralShipBrush;
            }

            return price switch
            {
                >= 700 => PremiumShipBrush,
                >= 300 => ExpensiveShipBrush,
                >= 120 => MidPriceShipBrush,
                _ => EntryPriceShipBrush
            };
        }
    }

    public MediaBrush ShipRoleBrush
    {
        get
        {
            var role = ShipRole.ToLowerInvariant();
            if (role.Contains("combat") || role.Contains("fighter") || role.Contains("gunship") || role.Contains("bomber") || role.Contains("attack") ||
                role.Contains("战斗", StringComparison.Ordinal) || role.Contains("炮艇", StringComparison.Ordinal) ||
                role.Contains("轰炸", StringComparison.Ordinal) || role.Contains("攻击", StringComparison.Ordinal) ||
                role.Contains("拦截", StringComparison.Ordinal))
            {
                return CombatShipBrush;
            }

            if (role.Contains("cargo") || role.Contains("freight") || role.Contains("transport") || role.Contains("hauler") ||
                role.Contains("货运", StringComparison.Ordinal) || role.Contains("运输", StringComparison.Ordinal) ||
                role.Contains("客运", StringComparison.Ordinal) || role.Contains("快递", StringComparison.Ordinal))
            {
                return CargoShipBrush;
            }

            if (role.Contains("exploration") || role.Contains("scanning") || role.Contains("pathfinder") ||
                role.Contains("探索", StringComparison.Ordinal) || role.Contains("扫描", StringComparison.Ordinal) ||
                role.Contains("探路", StringComparison.Ordinal) || role.Contains("远征", StringComparison.Ordinal) ||
                role.Contains("侦察", StringComparison.Ordinal))
            {
                return ExplorationShipBrush;
            }

            if (role.Contains("medical") || role.Contains("rescue") || role.Contains("repair") || role.Contains("refuel") ||
                role.Contains("医疗", StringComparison.Ordinal) || role.Contains("救援", StringComparison.Ordinal) ||
                role.Contains("维修", StringComparison.Ordinal) || role.Contains("加油", StringComparison.Ordinal) ||
                role.Contains("支援", StringComparison.Ordinal))
            {
                return SupportShipBrush;
            }

            if (role.Contains("mining") || role.Contains("industrial") || role.Contains("salvage") ||
                role.Contains("采矿", StringComparison.Ordinal) || role.Contains("工业", StringComparison.Ordinal) ||
                role.Contains("打捞", StringComparison.Ordinal) || role.Contains("建造", StringComparison.Ordinal))
            {
                return IndustrialShipBrush;
            }

            return UtilityShipBrush;
        }
    }

    private static bool TryReadPrice(string value, out decimal price)
    {
        var normalized = value.Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal).Trim();
        return decimal.TryParse(normalized, out price);
    }

    private static MediaBrush Brush(byte red, byte green, byte blue)
    {
        var brush = new MediaSolidBrush(MediaColor.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static readonly MediaBrush CapitalShipBrush = Brush(170, 110, 255);
    private static readonly MediaBrush LargeShipBrush = Brush(77, 190, 255);
    private static readonly MediaBrush MediumShipBrush = Brush(77, 255, 213);
    private static readonly MediaBrush SmallShipBrush = Brush(141, 220, 105);
    private static readonly MediaBrush FlyableShipBrush = Brush(103, 255, 164);
    private static readonly MediaBrush ConceptShipBrush = Brush(255, 196, 87);
    private static readonly MediaBrush PremiumShipBrush = Brush(255, 117, 163);
    private static readonly MediaBrush ExpensiveShipBrush = Brush(255, 184, 83);
    private static readonly MediaBrush MidPriceShipBrush = Brush(87, 206, 255);
    private static readonly MediaBrush EntryPriceShipBrush = Brush(132, 227, 255);
    private static readonly MediaBrush CombatShipBrush = Brush(255, 112, 112);
    private static readonly MediaBrush CargoShipBrush = Brush(255, 180, 74);
    private static readonly MediaBrush ExplorationShipBrush = Brush(96, 192, 255);
    private static readonly MediaBrush SupportShipBrush = Brush(116, 255, 203);
    private static readonly MediaBrush IndustrialShipBrush = Brush(219, 206, 91);
    private static readonly MediaBrush UtilityShipBrush = Brush(188, 208, 232);
    private static readonly MediaBrush NeutralShipBrush = Brush(156, 183, 208);
}

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

public sealed record FleetNotificationCenterItemRow(
    string Kind,
    string Title,
    string Detail,
    string TimeText,
    string ActionText,
    string ActionKey,
    MediaBrush? AccentBrush);

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
    public bool CanCurrentUserEditPermissions { get; init; }
    public bool CanCurrentUserRemove { get; init; }
    public bool CanCurrentUserTransferCommand { get; init; }
    public MediaBrush? RoleBrush { get; init; }
    public string HeaderLine => $"{SquadName} / {OnlineStatus}";
    public bool CanEditPermissions => CanCurrentUserEditPermissions && !IsCommander;
    public bool ShowPermissionControls => IsCommander || PermissionEnabled;
    public bool ShowRoleEditor => PermissionEnabled && CanEditPermissions;
    public bool ShowRoleSummary => IsCommander || PermissionEnabled;
    public bool ShowSavePermissions => PermissionEnabled && CanEditPermissions;
    public bool CanTransferCommander => CanCurrentUserTransferCommand && !IsSelf && !IsCommander;
    public bool CanRemoveFromFleet => CanCurrentUserRemove && !IsSelf && !IsCommander;

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
            OnChanged(nameof(CanEditPermissions));
            OnChanged(nameof(CanTransferCommander));
            OnChanged(nameof(CanRemoveFromFleet));
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

public sealed record FleetApplicationRow(
    string Id,
    string DisplayName,
    string GameName,
    string Callsign,
    string? Account,
    string Message,
    string Status,
    string CreatedAtText,
    string Initials,
    string? AvatarPath);

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
    bool RequiresApplication,
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
        var requiresApplication =
            joinPolicy.Contains("申请", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Contains("审核", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Contains("Application", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Contains("Apply", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Contains("Request", StringComparison.OrdinalIgnoreCase);
        return new NetworkFleetCard(
            snapshot,
            snapshot.Name,
            string.IsNullOrWhiteSpace(snapshot.LogoText) ? code : snapshot.LogoText!,
            snapshot.LogoImageData,
            hasLogoImage ? Visibility.Collapsed : Visibility.Visible,
            $"识别码 / {code}",
            $"指挥官 / {commander}",
            requiresApplication
                ? "加入 / 需要申请"
                : "加入 / 无门槛",
            description!,
            $"类型 / {type}",
            $"活动时间 / {activeTime}",
            $"{snapshot.OnlineMembers} 在线 / {snapshot.TotalMembers} 成员",
            requiresApplication,
            !isCurrentFleet,
            isCurrentFleet ? "已加入" : requiresApplication ? "申请加入" : hasFleet ? "切换舰队" : "加入");
    }
}
