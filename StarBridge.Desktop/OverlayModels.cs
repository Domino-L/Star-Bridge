using System.Collections.ObjectModel;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace StarBridge.Desktop;

public enum OverlayMemberNameMode
{
    CallsignAndGameName,
    CallsignOnly,
    GameNameOnly
}

public enum OverlayVisualTheme
{
    Default,
    Anvil,
    Drake
}

public sealed record OverlayDisplaySettings(
    bool HideMissionWhenIdle,
    OverlayMemberNameMode MemberNameMode,
    bool HideOfflineMembers,
    bool HideSquadIcons,
    bool EnableTrayMode,
    double Opacity,
    bool ShowNotice,
    bool ShowSquads,
    bool ShowMission,
    bool ShowMembers,
    OverlayVisualTheme Theme)
{
    public static OverlayDisplaySettings Default { get; } = new(
        HideMissionWhenIdle: false,
        MemberNameMode: OverlayMemberNameMode.CallsignAndGameName,
        HideOfflineMembers: false,
        HideSquadIcons: false,
        EnableTrayMode: false,
        Opacity: 0.85,
        ShowNotice: true,
        ShowSquads: true,
        ShowMission: true,
        ShowMembers: true,
        Theme: OverlayVisualTheme.Default);

    public string Serialize()
    {
        return string.Join(
            ",",
            HideMissionWhenIdle ? "1" : "0",
            MemberNameMode,
            HideOfflineMembers ? "1" : "0",
            HideSquadIcons ? "1" : "0",
            EnableTrayMode ? "1" : "0",
            Opacity.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ShowNotice ? "1" : "0",
            ShowSquads ? "1" : "0",
            ShowMission ? "1" : "0",
            ShowMembers ? "1" : "0",
            Theme);
    }

    public static OverlayDisplaySettings Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Default;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 5)
        {
            return Default;
        }

        return new OverlayDisplaySettings(
            parts[0] == "1",
            Enum.TryParse<OverlayMemberNameMode>(parts[1], out var mode) ? mode : OverlayMemberNameMode.CallsignAndGameName,
            parts[2] == "1",
            parts[3] == "1",
            parts[4] == "1",
            parts.Length > 5 && double.TryParse(parts[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var opacity)
                ? Math.Clamp(opacity, 0.15, 1.0)
                : Default.Opacity,
            parts.Length <= 6 || parts[6] == "1",
            parts.Length <= 7 || parts[7] == "1",
            parts.Length <= 8 || parts[8] == "1",
            parts.Length <= 9 || parts[9] == "1",
            parts.Length > 10 && Enum.TryParse<OverlayVisualTheme>(parts[10], out var theme)
                ? theme
                : Default.Theme);
    }
}

public sealed record OverlayCommandState(
    string? NoticeTitle,
    string? NoticeText,
    string? FleetTaskTitle,
    string? FleetTaskBrief,
    string? RallyPoint,
    string? RequiredShip);

public sealed class SquadRow : System.ComponentModel.INotifyPropertyChanged
{
    private string _commander = "Unassigned";
    private string? _emblemPath;
    private bool _isExpanded;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; set; } = "Unnamed";

    public string Icon { get; set; } = "?";

    public string Commander
    {
        get => _commander;
        set
        {
            _commander = value;
            OnChanged(nameof(Commander));
            RefreshComputed();
        }
    }

    public string Mission { get; set; } = "Standby";

    public string RallyPoint { get; set; } = "Use Global";

    public string Description { get; set; } = "No squad briefing yet.";

    public string Type { get; set; } = "Assault";

    public string? EmblemPath
    {
        get => _emblemPath;
        set
        {
            _emblemPath = value;
            OnChanged(nameof(EmblemPath));
        }
    }

    public ObservableCollection<MemberAvatarRow> Members { get; } = [];

    public ObservableCollection<MemberAvatarRow> PreviewMembers { get; } = [];

    public ObservableCollection<SquadMemberStatusRow> StatusMembers { get; } = [];

    public string CommanderLine => $"COMMANDER / {Commander}";

    public string MissionLine => $"MISSION / {Mission}";

    public string RallyLine => $"RALLY / {RallyPoint}";

    public string SummaryLine => $"{Members.Count} MEMBERS / {Members.Count(member => member.Status == "Online")} ONLINE";

    public string TypeLine => $"TYPE / {Type}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            _isExpanded = value;
            OnChanged(nameof(IsExpanded));
        }
    }

    public void RefreshComputed()
    {
        OnChanged(nameof(CommanderLine));
        OnChanged(nameof(MissionLine));
        OnChanged(nameof(RallyLine));
        OnChanged(nameof(SummaryLine));
        OnChanged(nameof(TypeLine));
    }

    private void OnChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}

public sealed record MemberAvatarRow(
    string Name,
    string Initials,
    string Status,
    string? AvatarPath = null,
    Brush? NameBrush = null,
    bool IsCommander = false);

public sealed class OverlayLayoutItem
{
    public OverlayLayoutItem(string key, string title, double x, double y, double width, double height, Brush brush)
    {
        Key = key;
        Title = title;
        X = x;
        Y = y;
        Width = width;
        Height = height;
        Brush = brush;
    }

    public string Key { get; }

    public string Title { get; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public Brush Brush { get; }

    public string Serialize()
    {
        return string.Join(",", Key, X.ToString("0.####"), Y.ToString("0.####"), Width.ToString("0.####"), Height.ToString("0.####"));
    }

    public static IEnumerable<OverlayLayoutItem> ParseMany(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (var item in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 5 ||
                !double.TryParse(parts[1], out var x) ||
                !double.TryParse(parts[2], out var y) ||
                !double.TryParse(parts[3], out var width) ||
                !double.TryParse(parts[4], out var height))
            {
                continue;
            }

            var key = parts[0];
            yield return new OverlayLayoutItem(
                key,
                GetTitle(key),
                Math.Clamp(x, 0, 0.95),
                Math.Clamp(y, 0, 0.95),
                Math.Clamp(width, 0.05, 1),
                Math.Clamp(height, 0.05, 1),
                GetBrush(key));
        }
    }

    private static string GetTitle(string key)
    {
        return key switch
        {
            "Notice" => "FLEET NOTICE",
            "Squads" => "FLEET / SQUADS",
            "Mission" => "MISSION PACKAGE",
            "Members" => "SQUAD MEMBERS",
            _ => key
        };
    }

    private static Brush GetBrush(string key)
    {
        return key switch
        {
            "Notice" => Brushes.Yellow,
            "Squads" => Brushes.DeepSkyBlue,
            "Mission" => Brushes.Red,
            "Members" => Brushes.Gray,
            _ => Brushes.DeepSkyBlue
        };
    }
}
