using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace StarBridge.Desktop;

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
        bool hasFleet,
        OverlayCommandState commandState)
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _layout = layout;
        _viewModel = new OverlayViewModel(squads, players, settings, language, hasFleet, commandState);
        DataContext = _viewModel;
        Loaded += (_, _) => ApplyLayout();
        SizeChanged += (_, _) => ApplyLayout();
    }

    public void Refresh(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet,
        OverlayCommandState commandState)
    {
        _viewModel.Refresh(squads, players, settings, language, hasFleet, commandState);
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
    private Brush _panelBackgroundBrush = new SolidColorBrush(Color.FromArgb(176, 5, 10, 17));
    private Brush _panelBorderBrush = new SolidColorBrush(Color.FromRgb(69, 174, 255));
    private Brush _titleBrush = new SolidColorBrush(Color.FromRgb(83, 190, 255));
    private Brush _textBrush = new SolidColorBrush(Color.FromRgb(235, 247, 255));
    private Brush _mutedBrush = new SolidColorBrush(Color.FromRgb(142, 187, 220));
    private Brush _alertBrush = new SolidColorBrush(Color.FromRgb(255, 240, 0));
    private Brush _iconBackgroundBrush = new SolidColorBrush(Color.FromRgb(4, 16, 28));
    private Brush _onlineBrush = new SolidColorBrush(Color.FromRgb(121, 255, 158));
    private Brush _offlineBrush = new SolidColorBrush(Color.FromRgb(255, 105, 105));

    public OverlayViewModel(
        IEnumerable<SquadRow> squads,
        IEnumerable<PlayerRow> players,
        OverlayDisplaySettings settings,
        string language,
        bool hasFleet,
        OverlayCommandState commandState)
    {
        Refresh(squads, players, settings, language, hasFleet, commandState);

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

    public Brush PanelBackgroundBrush
    {
        get => _panelBackgroundBrush;
        private set => SetProperty(ref _panelBackgroundBrush, value);
    }

    public Brush PanelBorderBrush
    {
        get => _panelBorderBrush;
        private set => SetProperty(ref _panelBorderBrush, value);
    }

    public Brush TitleBrush
    {
        get => _titleBrush;
        private set => SetProperty(ref _titleBrush, value);
    }

    public Brush TextBrush
    {
        get => _textBrush;
        private set => SetProperty(ref _textBrush, value);
    }

    public Brush MutedBrush
    {
        get => _mutedBrush;
        private set => SetProperty(ref _mutedBrush, value);
    }

    public Brush AlertBrush
    {
        get => _alertBrush;
        private set => SetProperty(ref _alertBrush, value);
    }

    public Brush IconBackgroundBrush
    {
        get => _iconBackgroundBrush;
        private set => SetProperty(ref _iconBackgroundBrush, value);
    }

    public Brush OnlineBrush
    {
        get => _onlineBrush;
        private set => SetProperty(ref _onlineBrush, value);
    }

    public Brush OfflineBrush
    {
        get => _offlineBrush;
        private set => SetProperty(ref _offlineBrush, value);
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
        bool hasFleet,
        OverlayCommandState commandState)
    {
        var zh = language.Equals("zh", StringComparison.OrdinalIgnoreCase);
        var squadArray = hasFleet ? squads.ToArray() : [];
        _showNotice = settings.ShowNotice;
        OverlayOpacity = settings.Opacity;
        ApplyTheme(settings.Theme);
        SquadsVisibility = settings.ShowSquads ? Visibility.Visible : Visibility.Collapsed;
        MembersVisibility = settings.ShowMembers ? Visibility.Visible : Visibility.Collapsed;

        RefreshSquads(squadArray, settings, zh, hasFleet);
        RefreshMembers(players, settings, zh, hasFleet);

        var primarySquad = squadArray.FirstOrDefault();
        var nextNoticeTitle = string.IsNullOrWhiteSpace(commandState.NoticeTitle)
            ? zh ? "舰队通知" : "FLEET NOTICE"
            : commandState.NoticeTitle!;
        var nextNoticeText = string.IsNullOrWhiteSpace(commandState.NoticeText)
            ? hasFleet
                ? zh ? "前往指定集结点，等待小队任务。" : "Rally at assigned marker. Await squad-specific tasking."
                : zh ? "无舰队。请先加入或创建舰队。" : "No fleet. Join or create a fleet first."
            : commandState.NoticeText!;

        if (!FleetNoticeTitle.Equals(nextNoticeTitle, StringComparison.Ordinal) ||
            !FleetNotice.Equals(nextNoticeText, StringComparison.Ordinal))
        {
            _noticeSecondsRemaining = 15;
            _timer.Start();
            OnChanged(nameof(NoticeTimerLabel));
        }

        FleetNoticeTitle = nextNoticeTitle;
        SquadsTitle = zh ? "舰队 / 小队" : "FLEET / SQUADS";
        MissionTitle = zh ? "任务包" : "MISSION PACKAGE";
        MembersTitle = zh ? "小队成员" : "SQUAD MEMBERS";
        HotkeyToggleLabel = zh ? "热键切换" : "HOTKEY TOGGLE";
        FleetNotice = nextNoticeText;
        FleetMission = BuildFleetMissionText(commandState, zh, hasFleet);
        SquadMission = hasFleet
            ? zh
                ? $"小队任务 / {primarySquad?.Mission ?? "待命"}"
                : $"Squad Task / {primarySquad?.Mission ?? "Standby"}"
            : zh ? "小队任务 / 无小队" : "Squad Task / No Squad";
        RallyPoint = hasFleet && !string.IsNullOrWhiteSpace(commandState.RallyPoint)
            ? zh ? $"集结点 / {commandState.RallyPoint}" : $"Rally / {commandState.RallyPoint}"
            : hasFleet
                ? zh
                    ? $"集结点 / {primarySquad?.RallyPoint ?? "未分配"}"
                    : $"Rally / {primarySquad?.RallyPoint ?? "Unassigned"}"
                : zh ? "集结点 / 无" : "Rally / None";
        MissionVisibility = !settings.ShowMission || hasFleet && settings.HideMissionWhenIdle && IsMissionIdle(primarySquad, commandState)
            ? Visibility.Collapsed
            : Visibility.Visible;
        OnChanged(nameof(NotificationVisibility));
    }

    private void ApplyTheme(OverlayVisualTheme theme)
    {
        if (theme == OverlayVisualTheme.Anvil)
        {
            PanelBackgroundBrush = BrushFromArgb(190, 0, 18, 14);
            PanelBorderBrush = BrushFromRgb(0, 255, 141);
            TitleBrush = BrushFromRgb(78, 255, 171);
            TextBrush = BrushFromRgb(229, 255, 242);
            MutedBrush = BrushFromRgb(120, 221, 173);
            AlertBrush = BrushFromRgb(208, 255, 0);
            IconBackgroundBrush = BrushFromArgb(120, 0, 42, 28);
            OnlineBrush = BrushFromRgb(121, 255, 92);
            OfflineBrush = BrushFromRgb(255, 92, 76);
            return;
        }

        if (theme == OverlayVisualTheme.Drake)
        {
            PanelBackgroundBrush = BrushFromArgb(188, 22, 10, 0);
            PanelBorderBrush = BrushFromRgb(255, 138, 18);
            TitleBrush = BrushFromRgb(255, 178, 48);
            TextBrush = BrushFromRgb(255, 236, 196);
            MutedBrush = BrushFromRgb(230, 151, 62);
            AlertBrush = BrushFromRgb(255, 222, 89);
            IconBackgroundBrush = BrushFromArgb(132, 52, 22, 0);
            OnlineBrush = BrushFromRgb(255, 190, 52);
            OfflineBrush = BrushFromRgb(196, 72, 48);
            return;
        }

        PanelBackgroundBrush = BrushFromArgb(176, 5, 10, 17);
        PanelBorderBrush = BrushFromRgb(69, 174, 255);
        TitleBrush = BrushFromRgb(83, 190, 255);
        TextBrush = BrushFromRgb(235, 247, 255);
        MutedBrush = BrushFromRgb(142, 187, 220);
        AlertBrush = BrushFromRgb(255, 240, 0);
        IconBackgroundBrush = BrushFromRgb(4, 16, 28);
        OnlineBrush = BrushFromRgb(121, 255, 158);
        OfflineBrush = BrushFromRgb(255, 105, 105);
    }

    private static SolidColorBrush BrushFromRgb(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush BrushFromArgb(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private void RefreshSquads(IEnumerable<SquadRow> squads, OverlayDisplaySettings settings, bool zh, bool hasFleet)
    {
        Squads.Clear();
        if (!hasFleet)
        {
            Squads.Add(new OverlaySquadRow(
                zh ? "无舰队" : "No Fleet",
                "!",
                zh ? "请先加入或创建舰队" : "Join or create a fleet first",
                null,
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible));
            return;
        }

        foreach (var squad in squads)
        {
            Squads.Add(new OverlaySquadRow(
                squad.Name,
                squad.Icon,
                squad.CommanderLine,
                squad.EmblemPath,
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible));
        }
    }

    private void RefreshMembers(IEnumerable<PlayerRow> players, OverlayDisplaySettings settings, bool zh, bool hasFleet)
    {
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
                    player.Location,
                    player.Status.Equals("Online", StringComparison.OrdinalIgnoreCase) ? OnlineBrush : OfflineBrush));
            }
        }

        if (Members.Count > 0)
        {
            return;
        }

        Members.Add(new OverlayMemberRow(
            hasFleet ? zh ? "未分配" : "Unassigned" : zh ? "无舰队" : "No Fleet",
            hasFleet ? zh ? "离线" : "Offline" : "-",
            hasFleet ? zh ? "未知" : "Unknown" : "-",
            hasFleet ? zh ? "未知" : "Unknown" : "-",
            hasFleet ? OfflineBrush : MutedBrush));
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

    private static string BuildFleetMissionText(OverlayCommandState commandState, bool zh, bool hasFleet)
    {
        if (!hasFleet)
        {
            return zh ? "舰队任务 / 无舰队" : "Fleet Task / No Fleet";
        }

        if (string.IsNullOrWhiteSpace(commandState.FleetTaskTitle))
        {
            return zh ? "舰队任务 / 待命" : "Fleet Task / Standby";
        }

        var parts = new List<string> { commandState.FleetTaskTitle! };
        if (!string.IsNullOrWhiteSpace(commandState.FleetTaskBrief) &&
            !commandState.FleetTaskBrief!.Equals("未指定", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(commandState.FleetTaskBrief!);
        }

        if (!string.IsNullOrWhiteSpace(commandState.RequiredShip) &&
            !commandState.RequiredShip!.Equals("未指定", StringComparison.OrdinalIgnoreCase))
        {
            parts.Add(zh ? $"指定舰船 {commandState.RequiredShip}" : $"Required ship {commandState.RequiredShip}");
        }

        return string.Join(" / ", parts);
    }

    private static bool IsMissionIdle(SquadRow? squad, OverlayCommandState commandState)
    {
        if (!string.IsNullOrWhiteSpace(commandState.FleetTaskTitle) ||
            !string.IsNullOrWhiteSpace(commandState.RallyPoint))
        {
            return false;
        }

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

public sealed record OverlayMemberRow(
    string DisplayName,
    string Status,
    string Ship,
    string Location,
    System.Windows.Media.Brush StatusBrush);
