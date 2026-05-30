using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
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

    private readonly OverlayViewModel _viewModel;
    private readonly IReadOnlyDictionary<string, OverlayLayoutItem> _layout;

    public OverlayWindow(IEnumerable<SquadRow> squads, IEnumerable<PlayerRow> players, IEnumerable<OverlayLayoutItem> layout, OverlayDisplaySettings settings, string language)
    {
        InitializeComponent();
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        _layout = layout.ToDictionary(item => item.Key, StringComparer.OrdinalIgnoreCase);
        _viewModel = new OverlayViewModel(squads, players, settings, language);
        DataContext = _viewModel;
        Loaded += (_, _) => ApplyLayout();
        SizeChanged += (_, _) => ApplyLayout();
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
        if (!_layout.TryGetValue(key, out var item))
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

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public OverlayViewModel(IEnumerable<SquadRow> squads, IEnumerable<PlayerRow> players, OverlayDisplaySettings settings, string language)
    {
        var zh = string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase);
        var squadArray = squads.ToArray();
        Squads = new ObservableCollection<OverlaySquadRow>(
            squadArray.Select(squad => new OverlaySquadRow(
                squad.Name,
                squad.Icon,
                squad.CommanderLine,
                squad.EmblemPath,
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible)));

        var playerRows = settings.HideOfflineMembers
            ? players.Where(player => player.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
            : players;
        Members = new ObservableCollection<OverlayMemberRow>(
            playerRows.Select(player => new OverlayMemberRow(
                FormatMemberName(player, settings.MemberNameMode),
                player.Status,
                player.Ship,
                player.Location)));

        if (Members.Count == 0)
        {
            Members.Add(new OverlayMemberRow(zh ? "未分配" : "Unassigned", zh ? "离线" : "Offline", zh ? "未知" : "Unknown", zh ? "未知" : "Unknown"));
        }

        var primarySquad = squadArray.FirstOrDefault();
        FleetNoticeTitle = zh ? "舰队通知" : "FLEET NOTICE";
        SquadsTitle = zh ? "舰队 / 小队" : "FLEET / SQUADS";
        MissionTitle = zh ? "任务包" : "MISSION PACKAGE";
        MembersTitle = zh ? "小队成员" : "SQUAD MEMBERS";
        HotkeyToggleLabel = zh ? "热键切换" : "HOTKEY TOGGLE";
        FleetNotice = zh ? "前往指定集结点，等待小队任务。" : "Rally at assigned marker. Await squad-specific tasking.";
        FleetMission = zh ? "舰队任务 / 待命" : "Fleet Task / Standby";
        SquadMission = zh
            ? $"小队任务 / {primarySquad?.Mission ?? "待命"}"
            : $"Squad Task / {primarySquad?.Mission ?? "Standby"}";
        RallyPoint = zh
            ? $"集结点 / {primarySquad?.RallyPoint ?? "未分配"}"
            : $"Rally / {primarySquad?.RallyPoint ?? "Unassigned"}";
        MissionVisibility = settings.HideMissionWhenIdle && IsMissionIdle(primarySquad)
            ? Visibility.Collapsed
            : Visibility.Visible;

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

    public ObservableCollection<OverlaySquadRow> Squads { get; }

    public ObservableCollection<OverlayMemberRow> Members { get; }

    public string FleetNoticeTitle { get; }

    public string SquadsTitle { get; }

    public string MissionTitle { get; }

    public string MembersTitle { get; }

    public string HotkeyToggleLabel { get; }

    public string FleetNotice { get; }

    public string FleetMission { get; }

    public string SquadMission { get; }

    public string RallyPoint { get; }

    public Visibility MissionVisibility { get; }

    public string NoticeTimerLabel => $"{_noticeSecondsRemaining}s";

    public Visibility NotificationVisibility => _noticeSecondsRemaining > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    private void OnChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
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

public sealed record OverlaySquadRow(string Name, string Icon, string CommanderLine, string? EmblemPath, Visibility IconVisibility);

public sealed record OverlayMemberRow(string DisplayName, string Status, string Ship, string Location)
{
    public System.Windows.Media.Brush StatusBrush => Status.Equals("Online", StringComparison.OrdinalIgnoreCase)
        ? Brushes.LawnGreen
        : Brushes.IndianRed;
}
