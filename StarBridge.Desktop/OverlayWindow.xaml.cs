using System.Collections.ObjectModel;
using System.Globalization;
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
    private Brush _crosshairBrush = new SolidColorBrush(Color.FromRgb(235, 247, 255));
    private Brush _crosshairAlertBrush = new SolidColorBrush(Color.FromRgb(255, 240, 0));
    private Visibility _crosshairVisibility = Visibility.Collapsed;
    private Visibility _simpleCrosshairVisibility = Visibility.Collapsed;
    private Visibility _techCrosshairVisibility = Visibility.Collapsed;
    private double _crosshairSize = 96;
    private double _crosshairOpacity = 0.85;
    private double _simpleCrosshairStrokeThickness = 2;
    private double _simpleCrosshairDotSize = 4;
    private double _techCrosshairStrokeThickness = 1.6;
    private double _techCrosshairThinStrokeThickness = 1.2;
    private double _techCrosshairCornerStrokeThickness = 1.2;

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

    public Brush CrosshairBrush
    {
        get => _crosshairBrush;
        private set => SetProperty(ref _crosshairBrush, value);
    }

    public Brush CrosshairAlertBrush
    {
        get => _crosshairAlertBrush;
        private set => SetProperty(ref _crosshairAlertBrush, value);
    }

    public Visibility CrosshairVisibility
    {
        get => _crosshairVisibility;
        private set => SetProperty(ref _crosshairVisibility, value);
    }

    public Visibility SimpleCrosshairVisibility
    {
        get => _simpleCrosshairVisibility;
        private set => SetProperty(ref _simpleCrosshairVisibility, value);
    }

    public Visibility TechCrosshairVisibility
    {
        get => _techCrosshairVisibility;
        private set => SetProperty(ref _techCrosshairVisibility, value);
    }

    public double CrosshairSize
    {
        get => _crosshairSize;
        private set => SetProperty(ref _crosshairSize, value);
    }

    public double CrosshairOpacity
    {
        get => _crosshairOpacity;
        private set => SetProperty(ref _crosshairOpacity, value);
    }

    public double SimpleCrosshairStrokeThickness
    {
        get => _simpleCrosshairStrokeThickness;
        private set => SetProperty(ref _simpleCrosshairStrokeThickness, value);
    }

    public double SimpleCrosshairDotSize
    {
        get => _simpleCrosshairDotSize;
        private set => SetProperty(ref _simpleCrosshairDotSize, value);
    }

    public double TechCrosshairStrokeThickness
    {
        get => _techCrosshairStrokeThickness;
        private set => SetProperty(ref _techCrosshairStrokeThickness, value);
    }

    public double TechCrosshairThinStrokeThickness
    {
        get => _techCrosshairThinStrokeThickness;
        private set => SetProperty(ref _techCrosshairThinStrokeThickness, value);
    }

    public double TechCrosshairCornerStrokeThickness
    {
        get => _techCrosshairCornerStrokeThickness;
        private set => SetProperty(ref _techCrosshairCornerStrokeThickness, value);
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
        ApplyCrosshairSettings(settings);
        CrosshairVisibility = settings.ShowCrosshair ? Visibility.Visible : Visibility.Collapsed;
        SimpleCrosshairVisibility = settings.ShowCrosshair && settings.CrosshairMode == OverlayCrosshairMode.Simple
            ? Visibility.Visible
            : Visibility.Collapsed;
        TechCrosshairVisibility = settings.ShowCrosshair && settings.CrosshairMode == OverlayCrosshairMode.Tech
            ? Visibility.Visible
            : Visibility.Collapsed;
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

    private void ApplyCrosshairSettings(OverlayDisplaySettings settings)
    {
        var size = Math.Clamp(settings.CrosshairSize, 48, 240);
        var thickness = Math.Clamp(settings.CrosshairThickness, 1, 8);
        CrosshairSize = size;
        CrosshairOpacity = Math.Clamp(settings.CrosshairOpacity, 0.2, 1.0);

        var simpleCompensation = 96.0 / size;
        SimpleCrosshairStrokeThickness = thickness * simpleCompensation;
        SimpleCrosshairDotSize = Math.Max(3, thickness * 2.0) * simpleCompensation;

        var techCompensation = 142.0 / size;
        TechCrosshairStrokeThickness = thickness * techCompensation;
        TechCrosshairThinStrokeThickness = Math.Max(1, thickness * 0.72) * techCompensation;
        TechCrosshairCornerStrokeThickness = Math.Max(0.8, thickness * 0.6) * techCompensation;

        if (!settings.CrosshairUseThemeColor &&
            TryParseHexColor(settings.CrosshairColor, out var customColor))
        {
            CrosshairBrush = BrushFromArgb(215, customColor.R, customColor.G, customColor.B);
        }
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
            SetCrosshairBrushes(78, 255, 171, 208, 255, 0);
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
            SetCrosshairBrushes(255, 178, 48, 255, 222, 89);
            return;
        }

        if (theme == OverlayVisualTheme.Argo)
        {
            PanelBackgroundBrush = BrushFromArgb(184, 23, 12, 3);
            PanelBorderBrush = BrushFromRgb(255, 111, 55);
            TitleBrush = BrushFromRgb(255, 132, 73);
            TextBrush = BrushFromRgb(255, 235, 211);
            MutedBrush = BrushFromRgb(255, 167, 113);
            AlertBrush = BrushFromRgb(142, 255, 116);
            IconBackgroundBrush = BrushFromArgb(118, 64, 22, 8);
            OnlineBrush = BrushFromRgb(125, 255, 126);
            OfflineBrush = BrushFromRgb(255, 78, 61);
            SetCrosshairBrushes(255, 132, 73, 142, 255, 116);
            return;
        }

        if (theme == OverlayVisualTheme.Musashi)
        {
            PanelBackgroundBrush = BrushFromArgb(188, 20, 17, 5);
            PanelBorderBrush = BrushFromRgb(255, 212, 98);
            TitleBrush = BrushFromRgb(255, 228, 128);
            TextBrush = BrushFromRgb(255, 246, 214);
            MutedBrush = BrushFromRgb(131, 242, 221);
            AlertBrush = BrushFromRgb(91, 255, 230);
            IconBackgroundBrush = BrushFromArgb(124, 48, 40, 12);
            OnlineBrush = BrushFromRgb(94, 255, 225);
            OfflineBrush = BrushFromRgb(255, 111, 95);
            SetCrosshairBrushes(255, 228, 128, 91, 255, 230);
            return;
        }

        if (theme == OverlayVisualTheme.Mirai)
        {
            PanelBackgroundBrush = BrushFromArgb(184, 5, 20, 30);
            PanelBorderBrush = BrushFromRgb(83, 196, 255);
            TitleBrush = BrushFromRgb(134, 225, 255);
            TextBrush = BrushFromRgb(235, 250, 255);
            MutedBrush = BrushFromRgb(122, 191, 220);
            AlertBrush = BrushFromRgb(255, 92, 72);
            IconBackgroundBrush = BrushFromArgb(120, 8, 44, 64);
            OnlineBrush = BrushFromRgb(105, 255, 218);
            OfflineBrush = BrushFromRgb(255, 91, 74);
            SetCrosshairBrushes(134, 225, 255, 255, 92, 72);
            return;
        }

        if (theme == OverlayVisualTheme.Crusader)
        {
            PanelBackgroundBrush = BrushFromArgb(178, 4, 16, 34);
            PanelBorderBrush = BrushFromRgb(20, 145, 255);
            TitleBrush = BrushFromRgb(110, 205, 255);
            TextBrush = BrushFromRgb(240, 250, 255);
            MutedBrush = BrushFromRgb(146, 202, 255);
            AlertBrush = BrushFromRgb(84, 255, 107);
            IconBackgroundBrush = BrushFromArgb(110, 3, 32, 68);
            OnlineBrush = BrushFromRgb(97, 255, 126);
            OfflineBrush = BrushFromRgb(255, 104, 122);
            SetCrosshairBrushes(110, 205, 255, 84, 255, 107);
            return;
        }

        if (theme == OverlayVisualTheme.Aegis)
        {
            PanelBackgroundBrush = BrushFromArgb(186, 0, 18, 16);
            PanelBorderBrush = BrushFromRgb(55, 224, 214);
            TitleBrush = BrushFromRgb(84, 245, 232);
            TextBrush = BrushFromRgb(224, 255, 250);
            MutedBrush = BrushFromRgb(112, 201, 193);
            AlertBrush = BrushFromRgb(255, 51, 41);
            IconBackgroundBrush = BrushFromArgb(118, 0, 44, 42);
            OnlineBrush = BrushFromRgb(92, 255, 185);
            OfflineBrush = BrushFromRgb(255, 63, 55);
            SetCrosshairBrushes(84, 245, 232, 255, 51, 41);
            return;
        }

        if (theme == OverlayVisualTheme.Rsi)
        {
            PanelBackgroundBrush = BrushFromArgb(184, 20, 12, 34);
            PanelBorderBrush = BrushFromRgb(150, 143, 255);
            TitleBrush = BrushFromRgb(214, 201, 255);
            TextBrush = BrushFromRgb(250, 246, 255);
            MutedBrush = BrushFromRgb(187, 166, 220);
            AlertBrush = BrushFromRgb(255, 151, 58);
            IconBackgroundBrush = BrushFromArgb(124, 35, 22, 64);
            OnlineBrush = BrushFromRgb(116, 238, 210);
            OfflineBrush = BrushFromRgb(255, 112, 86);
            SetCrosshairBrushes(214, 201, 255, 255, 151, 58);
            return;
        }

        if (theme == OverlayVisualTheme.Origin)
        {
            PanelBackgroundBrush = BrushFromArgb(178, 7, 17, 28);
            PanelBorderBrush = BrushFromRgb(88, 170, 255);
            TitleBrush = BrushFromRgb(176, 219, 255);
            TextBrush = BrushFromRgb(245, 250, 255);
            MutedBrush = BrushFromRgb(132, 185, 232);
            AlertBrush = BrushFromRgb(255, 96, 83);
            IconBackgroundBrush = BrushFromArgb(116, 16, 36, 58);
            OnlineBrush = BrushFromRgb(135, 255, 180);
            OfflineBrush = BrushFromRgb(255, 104, 94);
            SetCrosshairBrushes(176, 219, 255, 255, 96, 83);
            return;
        }

        if (theme == OverlayVisualTheme.Aopoa)
        {
            PanelBackgroundBrush = BrushFromArgb(182, 4, 28, 30);
            PanelBorderBrush = BrushFromRgb(77, 255, 225);
            TitleBrush = BrushFromRgb(126, 255, 237);
            TextBrush = BrushFromRgb(230, 255, 250);
            MutedBrush = BrushFromRgb(116, 211, 198);
            AlertBrush = BrushFromRgb(171, 255, 67);
            IconBackgroundBrush = BrushFromArgb(122, 0, 58, 62);
            OnlineBrush = BrushFromRgb(156, 255, 77);
            OfflineBrush = BrushFromRgb(255, 72, 64);
            SetCrosshairBrushes(126, 255, 237, 171, 255, 67);
            return;
        }

        if (theme == OverlayVisualTheme.Esperia)
        {
            PanelBackgroundBrush = BrushFromArgb(184, 30, 6, 20);
            PanelBorderBrush = BrushFromRgb(255, 60, 78);
            TitleBrush = BrushFromRgb(255, 92, 112);
            TextBrush = BrushFromRgb(255, 228, 236);
            MutedBrush = BrushFromRgb(211, 125, 162);
            AlertBrush = BrushFromRgb(168, 77, 255);
            IconBackgroundBrush = BrushFromArgb(126, 70, 8, 34);
            OnlineBrush = BrushFromRgb(255, 108, 128);
            OfflineBrush = BrushFromRgb(152, 74, 255);
            SetCrosshairBrushes(255, 92, 112, 168, 77, 255);
            return;
        }

        if (theme == OverlayVisualTheme.Gatac)
        {
            PanelBackgroundBrush = BrushFromArgb(184, 24, 10, 32);
            PanelBorderBrush = BrushFromRgb(255, 176, 210);
            TitleBrush = BrushFromRgb(255, 205, 230);
            TextBrush = BrushFromRgb(255, 238, 246);
            MutedBrush = BrushFromRgb(203, 147, 221);
            AlertBrush = BrushFromRgb(255, 122, 76);
            IconBackgroundBrush = BrushFromArgb(124, 54, 18, 64);
            OnlineBrush = BrushFromRgb(255, 190, 230);
            OfflineBrush = BrushFromRgb(255, 117, 76);
            SetCrosshairBrushes(255, 205, 230, 255, 122, 76);
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
        SetCrosshairBrushes(235, 247, 255, 255, 240, 0);
    }

    private void SetCrosshairBrushes(byte red, byte green, byte blue, byte alertRed, byte alertGreen, byte alertBlue)
    {
        CrosshairBrush = BrushFromArgb(215, red, green, blue);
        CrosshairAlertBrush = BrushFromArgb(225, alertRed, alertGreen, alertBlue);
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

    private static bool TryParseHexColor(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.StartsWith('#'))
        {
            text = text[1..];
        }

        if (text.Length == 3)
        {
            text = string.Concat(text.Select(ch => $"{ch}{ch}"));
        }

        if (text.Length == 6 &&
            byte.TryParse(text[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var red) &&
            byte.TryParse(text.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var green) &&
            byte.TryParse(text.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var blue))
        {
            color = Color.FromRgb(red, green, blue);
            return true;
        }

        return false;
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
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible,
                Visibility.Visible));
            return;
        }

        foreach (var squad in squads)
        {
            var hasEmblem = !string.IsNullOrWhiteSpace(squad.EmblemPath);
            Squads.Add(new OverlaySquadRow(
                squad.Name,
                squad.Icon,
                squad.CommanderLine,
                squad.EmblemPath,
                settings.HideSquadIcons ? Visibility.Collapsed : Visibility.Visible,
                hasEmblem ? Visibility.Collapsed : Visibility.Visible));
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
    Visibility IconVisibility,
    Visibility IconTextVisibility);

public sealed record OverlayMemberRow(
    string DisplayName,
    string Status,
    string Ship,
    string Location,
    System.Windows.Media.Brush StatusBrush);
