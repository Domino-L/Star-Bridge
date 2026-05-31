using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;

namespace SCFleetCommand.Desktop;

public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;

    private readonly IEnumerable<OverlayLayoutItem> _layout;
    private readonly OverlayViewModel _viewModel;

    public OverlayWindow(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        IEnumerable<OverlayLayoutItem> layout,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet)
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _layout = layout;
        _viewModel = new OverlayViewModel(squads, players, settings, language, hasFleet);
        DataContext = _viewModel;
        Loaded += (_, _) => ApplyLayout();
        SizeChanged += (_, _) => ApplyLayout();
    }

    public void Refresh(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet)
    {
        _viewModel.Refresh(squads, players, settings, language, hasFleet);
        ApplyLayout();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(OverlayWindowProc);
        }

        EnableMouseClickThrough();
    }

    private void OverlayWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void ApplyLayout()
    {
        ApplyPanel("Notice", NoticePanel);
        ApplyPanel("Squads", SquadsPanel);
        ApplyPanel("Mission", MissionPanel);
        ApplyPanel("Members", MembersPanel);
    }

    private void ApplyPanel(string key, FrameworkElement panel)
    {
        var item = _layout.FirstOrDefault(layoutItem =>
            layoutItem.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            return;
        }

        panel.Width = Math.Max(80, ActualWidth * item.Width);
        panel.Height = Math.Max(50, ActualHeight * item.Height);
        Canvas.SetLeft(panel, ActualWidth * item.X);
        Canvas.SetTop(panel, ActualHeight * item.Y);
    }

    private void EnableMouseClickThrough()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var style = GetWindowLong(handle, GwlExStyle);
            var nextStyle = style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate;
            SetWindowLong(handle, GwlExStyle, nextStyle);
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    private static IntPtr OverlayWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            handled = true;
            return new IntPtr(HtTransparent);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong(IntPtr handle, int index, int newLong);
}

public sealed class OverlayViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _noticeSecondsRemaining = 15;
    private string _fleetNoticeTitle = "";
    private string _squadsTitle = "";
    private string _missionTitle = "";
    private string _membersTitle = "";
    private string _hotkeyToggleLabel = "";
    private string _fleetNotice = "";
    private string _fleetMission = "";
    private string _squadMission = "";
    private string _rallyPoint = "";
    private Visibility _missionVisibility = Visibility.Visible;
    private Visibility _squadsVisibility = Visibility.Visible;
    private Visibility _membersVisibility = Visibility.Visible;
    private bool _showNotice = true;
    private double _overlayOpacity = 0.85;

    public OverlayViewModel(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet)
    {
        Refresh(squads, players, settings, language, hasFleet);

        _timer.Tick += (_, _) =>
        {
            if (_noticeSecondsRemaining > 0)
            {
                _noticeSecondsRemaining--;
                OnChanged(nameof(NoticeTimerLabel));
                OnChanged(nameof(NotificationVisibility));
            }
            else
            {
                _timer.Stop();
            }
        };
        _timer.Start();
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<OverlaySquadRow> Squads { get; } = [];

    public ObservableCollection<OverlayMemberRow> Members { get; } = [];

    public string FleetNoticeTitle
    {
        get => _fleetNoticeTitle;
        private set => SetProperty(ref _fleetNoticeTitle, value);
    }

    public string SquadsTitle
    {
        get => _squadsTitle;
        private set => SetProperty(ref _squadsTitle, value);
    }

    public string MissionTitle
    {
        get => _missionTitle;
        private set => SetProperty(ref _missionTitle, value);
    }

    public string MembersTitle
    {
        get => _membersTitle;
        private set => SetProperty(ref _membersTitle, value);
    }

    public string HotkeyToggleLabel
    {
        get => _hotkeyToggleLabel;
        private set => SetProperty(ref _hotkeyToggleLabel, value);
    }

    public string FleetNotice
    {
        get => _fleetNotice;
        private set => SetProperty(ref _fleetNotice, value);
    }

    public string FleetMission
    {
        get => _fleetMission;
        private set => SetProperty(ref _fleetMission, value);
    }

    public string SquadMission
    {
        get => _squadMission;
        private set => SetProperty(ref _squadMission, value);
    }

    public string RallyPoint
    {
        get => _rallyPoint;
        private set => SetProperty(ref _rallyPoint, value);
    }

    public Visibility MissionVisibility
    {
        get => _missionVisibility;
        private set => SetProperty(ref _missionVisibility, value);
    }

    public Visibility SquadsVisibility
    {
        get => _squadsVisibility;
        private set => SetProperty(ref _squadsVisibility, value);
    }

    public Visibility MembersVisibility
    {
        get => _membersVisibility;
        private set => SetProperty(ref _membersVisibility, value);
    }

    public double OverlayOpacity
    {
        get => _overlayOpacity;
        private set => SetProperty(ref _overlayOpacity, value);
    }

    public string NoticeTimerLabel => $"{_noticeSecondsRemaining}s";

    public Visibility NotificationVisibility => _showNotice && _noticeSecondsRemaining > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void Refresh(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet)
    {
        var zh = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase);
        var squadArray = hasFleet ? squads.ToArray() : [];
        _showNotice = settings.ShowNotice;
        OverlayOpacity = settings.Opacity;
        SquadsVisibility = settings.ShowSquads ? Visibility.Visible : Visibility.Collapsed;
        MembersVisibility = settings.ShowMembers ? Visibility.Visible : Visibility.Collapsed;

        Squads.Clear();
        if (!hasFleet)
        {
            Squads.Add(new OverlaySquadRow(
                zh ? "无舰队" : "No Fleet",
                "!",
                zh ? "请先加入或创建舰队" : "Join or create a fleet first",
                null,
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible));
        }
        else
        {
            foreach (var squad in squadArray)
            {
                Squads.Add(new OverlaySquadRow(
                    squad.Name,
                    squad.Icon,
                    squad.CommanderLine,
                    squad.EmblemPath,
                    settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible));
            }
        }

        Members.Clear();
        if (hasFleet)
        {
            var playerRows = settings.HideOfflineMembers
                ? players.Where(player => player.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
                : players;

            foreach (var player in playerRows)
            {
                Members.Add(new OverlayMemberRow(
                    FormatMemberName(player, settings.MemberNameMode),
                    player.Status,
                    player.ShipInfo,
                    player.Location));
            }
        }

        if (Members.Count == 0)
        {
            Members.Add(new OverlayMemberRow(
                hasFleet ? zh ? "未分配" : "Unassigned" : zh ? "无舰队" : "No Fleet",
                hasFleet ? zh ? "离线" : "Offline" : "-",
                hasFleet ? zh ? "未知" : "Unknown" : "-",
                hasFleet ? zh ? "未知" : "Unknown" : "-"));
        }

        var primarySquad = squadArray.FirstOrDefault();
        FleetNoticeTitle = zh ? "舰队通知" : "FLEET NOTICE";
        SquadsTitle = zh ? "舰队 / 小队" : "FLEET / SQUADS";
        MissionTitle = zh ? "任务包" : "MISSION PACKAGE";
        MembersTitle = zh ? "小队成员" : "SQUAD MEMBERS";
        HotkeyToggleLabel = zh ? "热键切换" : "HOTKEY TOGGLE";
        FleetNotice = hasFleet
            ? zh ? "前往指定集结点，等待小队任务。" : "Rally at assigned marker. Await squad-specific tasking."
            : zh ? "无舰队。请先加入或创建舰队。" : "No fleet. Join or create a fleet first.";
        FleetMission = hasFleet
            ? zh ? "舰队任务 / 待命" : "Fleet Task / Standby"
            : zh ? "舰队任务 / 无舰队" : "Fleet Task / No Fleet";
        SquadMission = hasFleet
            ? zh
                ? $"小队任务 / {primarySquad?.Mission ?? "待命"}"
                : $"Squad Task / {primarySquad?.Mission ?? "Standby"}"
            : zh ? "小队任务 / 无小队" : "Squad Task / No Squad";
        RallyPoint = hasFleet
            ? zh
                ? $"集结点 / {primarySquad?.RallyPoint ?? "未分配"}"
                : $"Rally / {primarySquad?.RallyPoint ?? "Unassigned"}"
            : zh ? "集结点 / 无" : "Rally / None";
        MissionVisibility = !settings.ShowMission || hasFleet && settings.HideMissionWhenIdle && IsMissionIdle(primarySquad)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OnChanged(nameof(NotificationVisibility));
    }

    private void OnChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnChanged(propertyName);
    }

    private static string FormatMemberName(PlayerRow player, OverlayMemberNameMode mode)
    {
        var callsign = string.IsNullOrWhiteSpace(player.Callsign) ? player.Name : player.Callsign;
        return mode switch
        {
            OverlayMemberNameMode.CallsignOnly => callsign,
            OverlayMemberNameMode.GameNameOnly => player.Name,
            _ => callsign.Equals(player.Name, StringComparison.OrdinalIgnoreCase)
                ? player.Name
                : $"{callsign} ({player.Name})"
        };
    }

    private static bool IsMissionIdle(SquadRow? squad)
    {
        if (squad is null)
        {
            return true;
        }

        var missionIdle = string.IsNullOrWhiteSpace(squad.Mission) ||
                          squad.Mission.Equals("Standby", StringComparison.OrdinalIgnoreCase);
        var rallyIdle = string.IsNullOrWhiteSpace(squad.RallyPoint) ||
                        squad.RallyPoint.Equals("Use Global", StringComparison.OrdinalIgnoreCase) ||
                        squad.RallyPoint.Equals("Unassigned", StringComparison.OrdinalIgnoreCase);
        return missionIdle && rallyIdle;
    }
}

public sealed record OverlaySquadRow(
    string Name,
    string Icon,
    string CommanderLine,
    string? EmblemPath,
    Visibility IconVisibility);

public sealed record OverlayMemberRow(string DisplayName, string Status, string Ship, string Location)
{
    public System.Windows.Media.Brush StatusBrush => Status.Equals("Online", StringComparison.OrdinalIgnoreCase)
        ? Brushes.LawnGreen
        : Brushes.IndianRed;
}
