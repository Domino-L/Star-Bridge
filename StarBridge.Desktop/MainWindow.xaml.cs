using Microsoft.Win32;
using StarBridge.Core.Events;
using StarBridge.Core.LogWatching;
using StarBridge.Core.Parsing;
using StarBridge.Core.State;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

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
    DateTimeOffset LastUpdated);

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
    string? OwnerAccount = null);

public sealed record NetworkSquadSnapshot(
    string Name,
    string? Commander,
    string? Type,
    string? Description);

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
    string Initials);

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
    string Token);

public sealed record EmailVerificationRequest(
    string Email);

public sealed record ProfileUpdateRequest(
    string? Callsign);

public sealed record FeedbackRequest(
    string? Contact,
    string? GameName,
    string? Callsign,
    string Message);

public sealed record FleetNotificationRequest(
    string FleetCode,
    string Subject,
    string Body);

public sealed record FleetDisbandRequest(
    string FleetCode,
    string Password);

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
    LocalFleetEventLog[]? EventLog);

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

public sealed class ImagePathConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}

public sealed class ImageDataConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is not string data || string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            var payload = data;
            var commaIndex = payload.IndexOf(',');
            if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                payload = payload[(commaIndex + 1)..];
            }

            var bytes = System.Convert.FromBase64String(payload);
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        return System.Windows.Data.Binding.DoNothing;
    }
}

public partial class MainWindow : Window
{
    private const int OverlayHotkeyId = 0x5343;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const string OverlayPresetCombat = "combat";
    private const string OverlayPresetCompact = "compact";
    private const string OverlayPresetCommand = "command";
    private const string OverlayPresetCustom = "custom";

    private readonly RegexLogEventParser _parser = new();
    private readonly FleetState _fleetState = new();
    private readonly ObservableCollection<PlayerRow> _players = [];
    private readonly ObservableCollection<SquadRow> _squads = [];
    private readonly ObservableCollection<SquadMemberStatusRow> _mySquadMembers = [];
    private readonly ObservableCollection<NetworkFleetCard> _networkFleets = [];
    private readonly List<NetworkFleetCard> _allNetworkFleets = [];
    private readonly ObservableCollection<OwnedShipRecord> _ownedShips = [];
    private readonly ObservableCollection<FleetShipInventoryRow> _fleetShipInventory = [];
    private readonly ObservableCollection<FleetTaskHistoryRow> _fleetTaskHistory = [];
    private readonly ObservableCollection<FleetActionPlanRow> _fleetActionPlans = [];
    private readonly ObservableCollection<FleetEventLogRow> _fleetEventLogs = [];
    private readonly List<FleetEventLogRow> _allFleetEventLogs = [];
    private readonly Dictionary<string, NetworkPlayerSnapshot> _networkSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _joinedActionPlanIds = new(StringComparer.OrdinalIgnoreCase);
    private SquadRow? _selectedSquad;
    private SquadRow? _joinedSquad;
    private readonly GridViewColumn PlayerNameColumn = new();
    private readonly GridViewColumn PlayerStatusColumn = new();
    private readonly GridViewColumn PlayerShipColumn = new();
    private readonly GridViewColumn PlayerLocationColumn = new();
    private readonly List<OverlayLayoutItem> _overlayLayout = [];
    private readonly DispatcherTimer _gameProcessTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _networkSyncTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly HttpClient _networkClient = new() { Timeout = TimeSpan.FromSeconds(4) };
    private GameLogWatcher? _watcher;
    private string? _logPath;
    private string? _localPlayer;
    private string? _localPlayerId;
    private string? _accountName;
    private string? _authToken;
    private string? _avatarPath;
    private string? _fleetLogoPath;
    private string _fleetName = "No Fleet";
    private string _fleetCode = "N/A";
    private string _fleetChiefCommander = "Unassigned";
    private string _fleetDeputyCommander = "Unassigned";
    private string _fleetDescription = "No fleet description.";
    private string _fleetType = "Combat";
    private string _fleetJoinPolicy = "Open";
    private string _fleetActiveTime = "20:00 - 23:59 UTC+8";
    private FleetInfoPanelKind _selectedFleetInfoPanel = FleetInfoPanelKind.CurrentTask;
    private string _fleetNoticeTitle = "";
    private string _fleetNoticeContent = "";
    private string _fleetCurrentTaskTitle = "";
    private string _fleetCurrentTaskBrief = "";
    private string _fleetCurrentTaskParticipants = "";
    private string _fleetCurrentTaskRally = "";
    private string _fleetCurrentTaskShip = "";
    private bool _fleetCurrentTaskEmailCall;
    private DateTime? _fleetCurrentTaskTime;
    private string _fleetCurrentTaskHistoryKey = "";
    private int _fleetCurrentTaskNoticeRevision;
    private string _fleetActionTitle = "";
    private string _fleetActionContent = "";
    private DateTime? _fleetActionStartTime;
    private bool _fleetActionNotifyMembers;
    private string _selectedActionPlanId = "";
    private bool _joinActionNotifyMe;
    private string? _callsign;
    private OverlayWindow? _overlayWindow;
    private OverlayLayoutItem? _activeOverlayItem;
    private FrameworkElement? _activeOverlayEditorElement;
    private bool _isOverlayResize;
    private System.Windows.Point _lastOverlayEditorPoint;
    private OverlayDisplaySettings _overlaySettings = OverlayDisplaySettings.Default;
    private string _activeOverlayPreset = OverlayPresetCombat;
    private string _language = "zh";
    private bool _isGameProcessRunning;
    private bool _isLoadingSettings;
    private bool _startupLoginPromptShown;
    private bool _isLoginDialogOpen;
    private HwndSource? _hotkeySource;
    private bool _hotkeyRegistered;
    private bool _hasFleet;
    private bool _isCreatingFleet;

    public MainWindow()
    {
        InitializeComponent();
        WindowTitleText.Text = $"星海舰桥 V{GetAppVersion()}";
        NavigateToMyFleet();
        PlayersList.ItemsSource = _players;
        SquadsList.ItemsSource = _squads;
        SquadSelectionList.ItemsSource = _squads;
        MySquadMembersList.ItemsSource = _mySquadMembers;
        FindFleetResults.ItemsSource = _networkFleets;
        OwnedShipsList.ItemsSource = _ownedShips;
        FleetShipInventoryList.ItemsSource = _fleetShipInventory;
        FleetShipDatabaseList.ItemsSource = _fleetShipInventory;
        FleetTaskHistoryList.ItemsSource = _fleetTaskHistory;
        FleetActionPlanList.ItemsSource = _fleetActionPlans;
        FleetEventLogList.ItemsSource = _fleetEventLogs;

        _isLoadingSettings = true;
        var config = DesktopAppConfig.Load();
        _logPath = config.LogPath;
        _localPlayer = config.PlayerName;
        _localPlayerId = config.PlayerId;
        _accountName = config.AccountName;
        _authToken = config.AuthToken;
        _avatarPath = config.AvatarPath;
        _callsign = config.Callsign;
        if (string.IsNullOrWhiteSpace(_authToken))
        {
            _callsign = null;
        }
        _language = "zh";
        _activeOverlayPreset = NormalizeOverlayPreset(DesktopAppConfig.LoadActiveOverlayPreset());
        _overlaySettings = OverlayDisplaySettings.Parse(
            DesktopAppConfig.LoadOverlayPresetSettings(_activeOverlayPreset) ??
            DesktopAppConfig.LoadOverlaySettings() ??
            config.OverlaySettings);
        LoadOverlayLayout(
            DesktopAppConfig.LoadOverlayPresetLayout(_activeOverlayPreset) ??
            DesktopAppConfig.LoadOverlayLayout() ??
            config.OverlayLayout);
        ApplyOverlaySettingsToControls();
        ApplyLanguageToControls();
        OverlayHotkeyBox.Text = string.IsNullOrWhiteSpace(config.OverlayHotkey)
            ? "Ctrl+Shift+O"
            : config.OverlayHotkey;
        NetworkServerUrlBox.Text = string.IsNullOrWhiteSpace(config.NetworkServerUrl)
            ? "http://198.13.49.128:5058"
            : config.NetworkServerUrl;
        NetworkServerKeyBox.Password = config.NetworkServerKey ?? "";
        CallsignBox.Text = _callsign ?? "";
        LoadFleetState(config.FleetStateJson);
        RefreshAccountPanel();
        RenderCachedIdentity();
        LoadAvatarPreview();
        LoadOwnedShips();
        _isLoadingSettings = false;

        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        UpdateFleetEntryPanels();
        Loaded += (_, _) =>
        {
            RenderOverlayEditor();
            if (!string.IsNullOrWhiteSpace(_logPath) && File.Exists(_logPath))
            {
                StartWatching(_logPath);
            }

            _ = InitializeLoginAndNetworkAsync();
        };
        _gameProcessTimer.Tick += (_, _) => UpdateLocalOnlineStateFromGameProcess();
        _gameProcessTimer.Start();
        _networkSyncTimer.Tick += async (_, _) => await NetworkAutoSyncAsync();
        AppendOutput("Designer WPF shell ready. Select Game.log to start.");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hotkeySource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _hotkeySource?.AddHook(MainWindowProc);
        RegisterOverlayHotkey();
    }

    private void FindFleetNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = FindFleetTab;
        SetActiveNav(FindFleetNavButton);
        _ = PullNetworkFleetsAsync(silent: true);
    }

    private void MyFleetNav_Click(object sender, RoutedEventArgs e)
    {
        NavigateToMyFleet();
    }

    private void MySquadNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = MySquadTab;
        SetActiveNav(MySquadNavButton);
    }

    private void OverlayNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = OverlayEditTab;
        SetActiveNav(OverlayNavButton);
    }

    private void PersonalNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = PersonalTab;
        SetActiveNav(PersonalNavButton);
    }

    private void BrandLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        MainTabs.SelectedItem = SupportTab;
        SetActiveNav(null);
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = SupportTab;
        SetActiveNav(null);
    }

    private async void SendFeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        var message = FeedbackMessageBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            FeedbackStatusText.Text = "请先填写反馈内容。";
            return;
        }

        FeedbackStatusText.Text = "正在发送反馈...";
        try
        {
            var request = new FeedbackRequest(
                FeedbackContactBox.Text.Trim(),
                _localPlayer,
                _callsign,
                message);
            var response = await PostNetworkJsonAsync("api/feedback", request);
            if (!response.IsSuccessStatusCode)
            {
                FeedbackStatusText.Text = response.StatusCode == HttpStatusCode.NotFound
                    ? "发送失败：当前服务器未更新反馈接口，请部署 0.3.1 服务器后重试。"
                    : $"发送失败：{await ReadResponseErrorAsync(response)}";
                return;
            }

            FeedbackMessageBox.Clear();
            FeedbackStatusText.Text = "反馈已发送，感谢。";
        }
        catch (Exception ex)
        {
            FeedbackStatusText.Text = $"发送失败：{ex.Message}";
        }
    }

    private void NetworkTestNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = MonitorTab;
        SetActiveNav(null);
    }

    private bool IsLoggedIn => !string.IsNullOrWhiteSpace(_authToken);

    private bool EnsureLoggedIn(string message)
    {
        if (IsLoggedIn)
        {
            return true;
        }

        LoginStatusText.Text = message;
        NetworkStatusText.Text = "浏览模式：请先登录";
        MainTabs.SelectedItem = PersonalTab;
        SetActiveNav(PersonalNavButton);
        _ = ShowLoginDialogAsync();
        return false;
    }

    private void RefreshAccountPanel()
    {
        if (AccountNameText is null)
        {
            return;
        }

        if (IsLoggedIn)
        {
            AccountNameText.Text = _accountName ?? "已登录";
            AccountModeText.Text = "已连接星海舰桥服务器，可同步舰队与玩家状态";
            LoginButton.Content = "切换账号";
            LogoutButton.IsEnabled = true;
            LoginStatusText.Text = string.IsNullOrWhiteSpace(_accountName)
                ? "已登录"
                : $"已登录：{_accountName}";
            CallsignBox.IsReadOnly = false;
            CallsignBox.IsEnabled = true;
            CallsignBox.Text = _callsign ?? "";
            ChooseAvatarButton.IsEnabled = true;
            OpenHangarReaderButton.IsEnabled = true;
            ImportHangarButton.IsEnabled = true;
            ClearShipDatabaseButton.IsEnabled = true;
            RenderCachedIdentity();
            LoadAvatarPreview();
            LoadOwnedShips();
            return;
        }

        AccountNameText.Text = "访客模式";
        AccountModeText.Text = "只能浏览，无法同步或管理舰队";
        LoginButton.Content = "登录 / 注册";
        LogoutButton.IsEnabled = false;
        LoginStatusText.Text = "未登录";
        CallsignBox.IsReadOnly = true;
        CallsignBox.IsEnabled = false;
        CallsignBox.Text = "";
        GameNameText.Text = "请登录后查看";
        PlayerIdText.Text = "请登录后查看";
        ProfileStatusText.Text = "浏览模式";
        ChooseAvatarButton.IsEnabled = false;
        OpenHangarReaderButton.IsEnabled = false;
        ImportHangarButton.IsEnabled = false;
        ClearShipDatabaseButton.IsEnabled = false;
        LoadAvatarPreview();
        LoadOwnedShips();
    }

    private void NavigateToMyFleet()
    {
        MainTabs.SelectedItem = FleetTab;
        FleetSubTabs.SelectedItem = AllPlayersTab;
        UpdateFleetEntryPanels();
        SetActiveNav(MyFleetNavButton);
    }

    private void FleetFindButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = FindFleetTab;
        SetActiveNav(FindFleetNavButton);
    }

    private void FleetCreateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        _isCreatingFleet = true;
        MainTabs.SelectedItem = FleetTab;
        SetActiveNav(MyFleetNavButton);
        UpdateFleetEntryPanels();
        CreateFleetNameBox.Focus();
    }

    private void CreateFleetCancel_Click(object sender, RoutedEventArgs e)
    {
        _isCreatingFleet = false;
        UpdateFleetEntryPanels();
    }

    private void CreateFleetSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!ValidateCreateFleetForm())
        {
            return;
        }

        _hasFleet = true;
        _isCreatingFleet = false;
        _fleetName = CreateFleetNameBox.Text.Trim();
        _fleetCode = CreateFleetCodeBox.Text.Trim();
        _fleetChiefCommander = FormatCommanderName(_callsign, _localPlayer);
        _fleetDeputyCommander = "Unassigned";
        _fleetDescription = NormalizeOptionalField(CreateFleetIntroBox.Text);
        _fleetType = GetSelectedRadioContent("FleetType") ?? "Combat";
        _fleetJoinPolicy = GetSelectedRadioContent("FleetJoinPolicy") ?? "Open";
        _fleetActiveTime = $"{CreateFleetOnlineFromBox.Text.Trim()} - {CreateFleetOnlineToBox.Text.Trim()} UTC+8";
        LocalFleetText.Text = $"{CreateFleetNameBox.Text.Trim()} [{CreateFleetCodeBox.Text.Trim()}]";
        RefreshFleetHeader();
        UpdateFleetEntryPanels();
        AddFleetLog("舰队", "创建舰队", $"{FormatCommanderName(_callsign, _localPlayer)} 创建 {_fleetName}");
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
        _ = PushLocalSnapshotAsync(silent: true);
    }

    private void CreateFleetField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (CreateFleetValidationText is null)
        {
            return;
        }

        ValidateCreateFleetForm(showRequiredErrors: false);
    }

    private void UpdateFleetEntryPanels()
    {
        if (FleetEmptyPanel is null || FleetCreatePanel is null)
        {
            return;
        }

        FleetCreatePanel.Visibility = !_hasFleet && _isCreatingFleet
            ? Visibility.Visible
            : Visibility.Collapsed;
        FleetEmptyPanel.Visibility = !_hasFleet && !_isCreatingFleet
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (SquadRequiresFleetPanel is not null)
        {
            SquadRequiresFleetPanel.Visibility = _hasFleet
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        RefreshOverlayWindow();
    }

    private bool ValidateCreateFleetForm(bool showRequiredErrors = true)
    {
        var name = CreateFleetNameBox.Text.Trim();
        var code = CreateFleetCodeBox.Text.Trim();
        var activeFrom = CreateFleetOnlineFromBox.Text.Trim();
        var activeTo = CreateFleetOnlineToBox.Text.Trim();
        var nameValid = name.Length <= 30 && IsEnglishFleetText(name, allowSpaces: true);
        var codeValid = code.Length <= 10 && IsEnglishFleetText(code, allowSpaces: false);
        var activeFromValid = IsValidTime24(activeFrom);
        var activeToValid = IsValidTime24(activeTo);

        var message = "";
        if (showRequiredErrors && string.IsNullOrWhiteSpace(name))
        {
            message = "请输入舰队名称。";
        }
        else if (showRequiredErrors && string.IsNullOrWhiteSpace(code))
        {
            message = "请输入舰队简称。";
        }
        else if (!nameValid)
        {
            message = "舰队名称仅允许英文、数字、空格、-、_，最多 30 个字符。";
        }
        else if (!codeValid)
        {
            message = "舰队简称仅允许英文、数字、-、_，最多 10 个字符。";
        }
        else if (!activeFromValid || !activeToValid)
        {
            message = "活动时间段必须使用 24 小时制 HH:mm，例如 20:00 到 23:59。";
        }

        CreateFleetValidationText.Text = message;
        return string.IsNullOrWhiteSpace(message) &&
               !string.IsNullOrWhiteSpace(name) &&
               !string.IsNullOrWhiteSpace(code) &&
               activeFromValid &&
               activeToValid;
    }

    private static bool IsEnglishFleetText(string value, bool allowSpaces)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.All(character =>
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z' ||
            character is >= '0' and <= '9' ||
            character is '-' or '_' ||
            allowSpaces && character == ' ');
    }

    private static bool IsValidTime24(string value)
    {
        if (value.Length != 5 || value[2] != ':')
        {
            return false;
        }

        return int.TryParse(value[..2], out var hour) &&
               int.TryParse(value[3..], out var minute) &&
               hour is >= 0 and <= 23 &&
               minute is >= 0 and <= 59;
    }

    private string? GetSelectedRadioContent(string groupName)
    {
        return FindVisualChildren<System.Windows.Controls.RadioButton>(this)
            .FirstOrDefault(radio => radio.GroupName == groupName && radio.IsChecked == true)
            ?.Content
            ?.ToString();
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
        where T : DependencyObject
    {
        if (parent is null)
        {
            yield break;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void SetActiveNav(System.Windows.Controls.Button? activeButton)
    {
        FindFleetNavButton.Tag = null;
        MyFleetNavButton.Tag = null;
        MySquadNavButton.Tag = null;
        OverlayNavButton.Tag = null;
        PersonalNavButton.Tag = null;
        if (activeButton is not null)
        {
            activeButton.Tag = "Active";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterOverlayHotkey();
        _hotkeySource?.RemoveHook(MainWindowProc);
        _hotkeySource = null;
        CloseOverlayWindow();
        _watcher?.Dispose();
        _gameProcessTimer.Stop();
        base.OnClosed(e);
    }

    private void SelectLog_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select Star Citizen Game.log",
            Filter = "Star Citizen Game.log|Game.log|Log files (*.log)|*.log|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        StartWatching(dialog.FileName);
    }

    private void StartWatching(string logPath)
    {
        _watcher?.Dispose();
        _logPath = logPath;
        LogPathBox.Text = logPath;
        SaveCurrentConfig();
        AppendOutput($"Watching: {logPath}");

        foreach (var line in ReadSharedLines(logPath))
        {
            ApplyLine(line, output: false);
        }

        RenderState();

        _watcher = new GameLogWatcher(logPath, replayExistingLines: false, line =>
        {
            Dispatcher.Invoke(() => ApplyLine(line, output: true));
        });
        _watcher.Start();
    }

    private void ApplyLine(string line, bool output)
    {
        var fleetEvent = _parser.TryParse(line);
        if (fleetEvent is null)
        {
            return;
        }

        if (fleetEvent.Player.Equals("LocalPlayer", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(_localPlayer))
        {
            fleetEvent = fleetEvent with { Player = _localPlayer };
        }

        if (fleetEvent.Type == FleetEventType.PlayerOnline)
        {
            _localPlayer = fleetEvent.Player;
            _localPlayerId = fleetEvent.PlayerId;
            if (IsLoggedIn)
            {
                GameNameText.Text = _localPlayer;
                PlayerIdText.Text = string.IsNullOrWhiteSpace(_localPlayerId)
                    ? "Unknown"
                    : _localPlayerId;
                ProfileStatusText.Text = "Online";
            }
            SaveCurrentConfig();
            LoadOwnedShips();
        }

        _fleetState.Apply(fleetEvent);
        RenderState();

        if (output)
        {
            AppendOutput($"{fleetEvent.Type} | {fleetEvent.Player} | {fleetEvent.Ship ?? ""} | {fleetEvent.Location ?? fleetEvent.NavigationTarget ?? ""}");
        }
    }

    private void RenderState()
    {
        _isGameProcessRunning = IsStarCitizenRunning();
        if (!string.IsNullOrWhiteSpace(_localPlayer))
        {
            _fleetState.SetPlayerOnlineState(_localPlayer, _isGameProcessRunning, DateTimeOffset.Now);
        }

        _fleetState.RefreshShipInferences(DateTimeOffset.Now);
        _players.Clear();

        foreach (var player in _fleetState.Players)
        {
            var isLocalPlayer = !string.IsNullOrWhiteSpace(_localPlayer) &&
                                player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase);
            _networkSnapshots.TryGetValue(player.Name, out var networkSnapshot);
            var online = player.Online && (!isLocalPlayer || _isGameProcessRunning);
            var rawShip = online ? ShipNameLocalizer.NormalizeCode(player.Ship) : "Unknown";
            var shipConfidence = player.ShipConfidence;
            var displayLocation = online ? FormatLocation(player.Location, player.NavigationTarget) : "Unknown";
            if (!isLocalPlayer && networkSnapshot is not null)
            {
                rawShip = online ? ShipNameLocalizer.NormalizeCode(networkSnapshot.Ship) : "Unknown";
                shipConfidence = string.IsNullOrWhiteSpace(networkSnapshot.ShipConfidence)
                    ? "Low"
                    : networkSnapshot.ShipConfidence!;
                displayLocation = online && !string.IsNullOrWhiteSpace(networkSnapshot.Location)
                    ? FormatLocation(networkSnapshot.Location!, "")
                    : displayLocation;
            }

            var displayShip = ShipNameLocalizer.DisplayName(rawShip, _language);
            var playerSquadName = isLocalPlayer
                ? _joinedSquad?.Name ?? "Unassigned"
                : networkSnapshot?.Squad ?? "Unassigned";
            var playerCallsign = isLocalPlayer ? _callsign : networkSnapshot?.Callsign;
            _players.Add(new PlayerRow(
                player.Name,
                online ? "Online" : "Offline",
                displayShip,
                online ? FormatShipInference(displayShip, shipConfidence) : "Ship: Unknown",
                displayLocation,
                playerCallsign,
                IsLocalPlayer(player.Name) ? _avatarPath : null,
                GetInitials(player.Name),
                playerSquadName,
                GetFleetRole(player.Name, playerCallsign),
                GetFleetNameBrush(player.Name),
                rawShip,
                shipConfidence));
        }

        TotalMembersText.Text = _players.Count.ToString();
        OnlineMembersText.Text = _players.Count(player => player.Status == "Online").ToString();
        RefreshFleetShipInventory();
        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();
        if (IsLoggedIn)
        {
            _ = PushFleetDirectoryAsync(silent: true);
            _ = PushLocalSnapshotAsync(silent: true);
        }

        var local = _players.FirstOrDefault(player =>
            player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase));
        if (local is not null)
        {
            ShipStateText.Text =
                $"Current Ship: {local.Ship}{Environment.NewLine}" +
                $"Location: {local.Location}{Environment.NewLine}" +
                $"Status: {local.Status}";
        }
    }

    private static string FormatShipInference(string ship, string confidence)
    {
        if (string.IsNullOrWhiteSpace(ship) ||
            ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "Ship: Unknown";
        }

        return $"Ship: {ship} [{confidence}]";
    }

    private string GetFleetRole(string playerName, string? callsign)
    {
        return IsFleetCommander(playerName, callsign) ? "舰队指挥官" : "成员";
    }

    private System.Windows.Media.Brush GetFleetNameBrush(string playerName)
    {
        return IsFleetCommander(playerName, IsLocalPlayer(playerName) ? _callsign : null)
            ? FindBrush("FleetCommanderNameBrush", Brushes.Gold)
            : FindBrush("PrimaryTextBrush", Brushes.White);
    }

    private bool IsFleetCommander(string playerName, string? callsign)
    {
        return playerName.Equals(GetGameNameFromDisplayName(_fleetChiefCommander), StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(callsign) &&
                callsign.Equals(GetCallsignFromDisplayName(_fleetChiefCommander), StringComparison.OrdinalIgnoreCase));
    }

    private System.Windows.Media.Brush GetSquadNameBrush(SquadRow squad, PlayerRow player)
    {
        return IsSquadCommander(squad, player)
            ? FindBrush("SquadCommanderNameBrush", Brushes.Aquamarine)
            : FindBrush("PrimaryTextBrush", Brushes.White);
    }

    private static System.Windows.Media.Brush FindBrush(string key, System.Windows.Media.Brush fallback)
    {
        return System.Windows.Application.Current.TryFindResource(key) as System.Windows.Media.Brush ?? fallback;
    }

    private static string FormatLocation(string location, string navigationTarget)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return "Unknown";
        }

        var separator = location.IndexOf(" -> ", StringComparison.Ordinal);
        return separator > 0
            ? location[..separator].Trim()
            : location.Trim();
    }

    private void UpdateLocalOnlineStateFromGameProcess()
    {
        _isGameProcessRunning = IsStarCitizenRunning();
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            RenderState();
            return;
        }

        if (_isGameProcessRunning)
        {
            RenderState();
            return;
        }

        _fleetState.Apply(new FleetEvent(FleetEventType.PlayerOffline, _localPlayer));
        RenderState();
        ProfileStatusText.Text = "Offline - game process not detected";
    }

    private static bool IsStarCitizenRunning()
    {
        string[] processNames =
        [
            "StarCitizen",
            "StarCitizen_LIVE",
            "StarCitizen_PTUR",
            "StarCitizen_EPTU",
            "StarCitizen_TECH-PREVIEW"
        ];

        return Process.GetProcesses()
            .Any(process =>
            {
                try
                {
                    return processNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                           process.ProcessName.StartsWith("StarCitizen", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            });
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    private void AppendOutput(string message)
    {
        OutputBox.AppendText(message + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private async void NetworkTestButton_Click(object sender, RoutedEventArgs e)
    {
        await TestNetworkAsync();
    }

    private async void LoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowLoginDialogAsync();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        _authToken = null;
        _accountName = null;
        _callsign = null;
        NetworkAutoSyncCheck.IsChecked = false;
        _networkSyncTimer.Stop();
        SaveCurrentConfig();
        RefreshAccountPanel();
        LoginStatusText.Text = "已退出登录，当前为浏览模式";
    }

    private async void NetworkPushButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("上传本机状态需要先登录。"))
        {
            return;
        }

        await PushFleetDirectoryAsync();
        await PushLocalSnapshotAsync();
    }

    private async void NetworkPullButton_Click(object sender, RoutedEventArgs e)
    {
        await PullNetworkFleetsAsync();
        await PullNetworkSnapshotsAsync();
    }

    private void NetworkAutoSyncCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (NetworkAutoSyncCheck.IsChecked == true)
        {
            if (!EnsureLoggedIn("自动同步需要先登录。"))
            {
                NetworkAutoSyncCheck.IsChecked = false;
                return;
            }

            _networkSyncTimer.Start();
            NetworkStatusText.Text = "自动同步已开启";
            return;
        }

        _networkSyncTimer.Stop();
        NetworkStatusText.Text = "自动同步已关闭";
    }

    private async Task NetworkAutoSyncAsync()
    {
        if (NetworkAutoSyncCheck.IsChecked != true || !IsLoggedIn)
        {
            return;
        }

        await PushFleetDirectoryAsync(silent: true);
        await PushLocalSnapshotAsync(silent: true);
        await PullNetworkFleetsAsync(silent: true);
        await PullNetworkSnapshotsAsync(silent: true);
    }

    private async Task InitializeLoginAndNetworkAsync()
    {
        if (IsLoggedIn)
        {
            await AutoConnectNetworkAsync();
            return;
        }

        if (_startupLoginPromptShown)
        {
            return;
        }

        _startupLoginPromptShown = true;
        await ShowLoginDialogAsync();
    }

    private async Task AutoConnectNetworkAsync()
    {
        if (!IsLoggedIn && string.IsNullOrWhiteSpace(NetworkServerKeyBox.Password))
        {
            LoginStatusText.Text = "未登录";
            RefreshAccountPanel();
            return;
        }

        await TestNetworkAsync(silent: true);
        NetworkAutoSyncCheck.IsChecked = true;
        _networkSyncTimer.Start();
        LoginStatusText.Text = string.IsNullOrWhiteSpace(_accountName)
            ? "已连接服务器"
            : $"已登录：{_accountName}";
        RefreshAccountPanel();
    }

    private async Task ShowLoginDialogAsync()
    {
        if (_isLoginDialogOpen)
        {
            return;
        }

        _isLoginDialogOpen = true;
        var dialog = new LoginWindow(_accountName) { Owner = this };
        dialog.SendVerificationCodeAsync = RequestVerificationCodeAsync;
        try
        {
            var result = dialog.ShowDialog();
            if (result != true)
            {
                RefreshAccountPanel();
                return;
            }

            if (dialog.IsSkipped)
            {
                LoginStatusText.Text = "已进入浏览模式";
                RefreshAccountPanel();
                return;
            }

            var path = dialog.IsRegisterMode ? "api/auth/register" : "api/auth/login";
            var actionName = dialog.IsRegisterMode ? "注册" : "登录";
            await AuthenticateAsync(
                path,
                actionName,
                dialog.IsRegisterMode ? dialog.RegisterEmail : dialog.LoginEmail,
                dialog.IsRegisterMode ? dialog.RegisterPassword : dialog.LoginPassword,
                dialog.IsRegisterMode ? dialog.RegisterEmail : dialog.LoginEmail,
                dialog.IsRegisterMode ? dialog.RegisterVerificationCode : null,
                dialog.IsRegisterMode ? dialog.RegisterCallsign : null);
        }
        finally
        {
            _isLoginDialogOpen = false;
        }
    }

    private async Task<string> RequestVerificationCodeAsync(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return "请输入邮箱地址。";
        }

        try
        {
            var request = new EmailVerificationRequest(email.Trim());
            var response = await _networkClient.PostAsJsonAsync(BuildNetworkUri("api/auth/send-code"), request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                return $"发送失败：{error}";
            }

            return "验证码已发送，10 分钟内有效。";
        }
        catch (Exception ex)
        {
            return $"发送失败：{ex.Message}";
        }
    }

    private async Task AuthenticateAsync(
        string path,
        string actionName,
        string email,
        string password,
        string? authEmail,
        string? verificationCode,
        string? callsign)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            LoginStatusText.Text = "请输入登录邮箱和密码";
            await ShowLoginDialogAsync();
            return;
        }

        if (path.EndsWith("register", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(authEmail) ||
             string.IsNullOrWhiteSpace(verificationCode) ||
             string.IsNullOrWhiteSpace(callsign)))
        {
            LoginStatusText.Text = "注册需要登录邮箱、呼号和验证码";
            await ShowLoginDialogAsync();
            return;
        }

        try
        {
            var request = new AuthRequest(email.Trim(), password, _localPlayer, authEmail?.Trim(), verificationCode?.Trim(), callsign?.Trim());
            var response = await _networkClient.PostAsJsonAsync(BuildNetworkUri(path), request);
            response.EnsureSuccessStatusCode();
            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
            if (auth is null || string.IsNullOrWhiteSpace(auth.Token))
            {
                LoginStatusText.Text = $"{actionName}失败：服务器响应无效";
                return;
            }

            _accountName = auth.Email ?? auth.UserName;
            _authToken = auth.Token;
            _callsign = auth.Callsign;
            CallsignBox.Text = _callsign ?? "";
            LoginStatusText.Text = $"{actionName}成功：{_accountName}";
            NetworkStatusText.Text = "已登录并连接服务器";
            SaveCurrentConfig();
            RefreshAccountPanel();
            NetworkAutoSyncCheck.IsChecked = true;
            _networkSyncTimer.Start();
            await PullNetworkFleetsAsync(silent: true);
            await PushLocalSnapshotAsync(silent: true);
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = $"{actionName}失败：{ex.Message}";
            NetworkStatusText.Text = $"{actionName}失败";
        }
    }

    private async Task UpdateProfileAsync()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync("api/auth/profile", new ProfileUpdateRequest(_callsign));
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Ignore transient relay errors and keep local profile changes.
        }
    }

    private async Task TestNetworkAsync(bool silent = false)
    {
        try
        {
            var response = await _networkClient.GetAsync(BuildNetworkUri("health"));
            response.EnsureSuccessStatusCode();
            NetworkStatusText.Text = "连接成功";
            if (!silent)
            {
                AppendOutput($"NETWORK | connected={NetworkServerUrlBox.Text.Trim()}");
            }
            await PullNetworkFleetsAsync(silent: true);
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"连接失败：{ex.Message}";
            if (!silent)
            {
                AppendOutput($"NETWORK | connect failed={ex.Message}");
            }
        }
    }

    private async Task PushLocalSnapshotAsync(bool silent = false)
    {
        if (!IsLoggedIn)
        {
            if (!silent)
            {
                NetworkStatusText.Text = "浏览模式：登录后才能上传状态";
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            NetworkStatusText.Text = "需要先从日志识别玩家名";
            return;
        }

        var local = _players.FirstOrDefault(player => player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase));
        var snapshot = new NetworkPlayerSnapshot(
            _localPlayer,
            _callsign,
            _hasFleet ? _fleetName : "No Fleet",
            _joinedSquad?.Name ?? "Unassigned",
            local?.Status.Equals("Online", StringComparison.OrdinalIgnoreCase) == true,
            local?.RawShip ?? "Unknown",
            local?.ShipConfidence ?? "None",
            local?.Location ?? "Unknown",
            "Low",
            DateTimeOffset.UtcNow);

        try
        {
            var response = await PostNetworkJsonAsync("api/players", snapshot);
            response.EnsureSuccessStatusCode();
            await PushFleetDirectoryAsync(silent: true);
            NetworkStatusText.Text = $"已上传：{snapshot.Name}";
            if (!silent)
            {
                AppendOutput($"NETWORK | pushed={snapshot.Name}");
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"上传失败：{ex.Message}";
            if (!silent)
            {
                AppendOutput($"NETWORK | push failed={ex.Message}");
            }
        }
    }

    private async Task PullNetworkSnapshotsAsync(bool silent = false)
    {
        try
        {
            var snapshots = await _networkClient.GetFromJsonAsync<NetworkPlayerSnapshot[]>(BuildNetworkUri("api/players")) ?? [];
            foreach (var snapshot in snapshots)
            {
                ApplyNetworkSnapshot(snapshot);
            }

            RenderState();
            NetworkStatusText.Text = $"已拉取：{snapshots.Length} 名玩家";
            if (!silent)
            {
                AppendOutput($"NETWORK | pulled players={snapshots.Length}");
            }
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"拉取失败：{ex.Message}";
            if (!silent)
            {
                AppendOutput($"NETWORK | pull failed={ex.Message}");
            }
        }
    }

    private async Task PushFleetDirectoryAsync(bool silent = false)
    {
        if (!IsLoggedIn)
        {
            if (!silent)
            {
                NetworkStatusText.Text = "浏览模式：登录后才能发布舰队";
            }

            return;
        }

        if (!_hasFleet)
        {
            return;
        }

        var snapshot = BuildLocalFleetSnapshot();
        try
        {
            var response = await PostNetworkJsonAsync("api/fleets", snapshot);
            response.EnsureSuccessStatusCode();
            if (!silent)
            {
                NetworkStatusText.Text = $"已发布舰队：{snapshot.Name}";
                AppendOutput($"NETWORK | pushed fleet={snapshot.Name}");
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                NetworkStatusText.Text = $"发布舰队失败：{ex.Message}";
                AppendOutput($"NETWORK | push fleet failed={ex.Message}");
            }
        }
    }

    private async Task PullNetworkFleetsAsync(bool silent = false)
    {
        try
        {
            var snapshots = await _networkClient.GetFromJsonAsync<NetworkFleetSnapshot[]>(BuildNetworkUri("api/fleets")) ?? [];
            _allNetworkFleets.Clear();
            foreach (var snapshot in snapshots)
            {
                if (IsSameFleet(snapshot.Name) || IsSameFleet(snapshot.Code))
                {
                    MergeNetworkFleetState(snapshot);
                }

                _allNetworkFleets.Add(NetworkFleetCard.FromSnapshot(snapshot, _fleetName, _fleetCode, _hasFleet));
            }

            ApplyFleetSearchFilter();

            if (!silent)
            {
                NetworkStatusText.Text = $"已拉取：{snapshots.Length} 个舰队";
                AppendOutput($"NETWORK | pulled fleets={snapshots.Length}");
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                NetworkStatusText.Text = $"拉取舰队失败：{ex.Message}";
                AppendOutput($"NETWORK | pull fleets failed={ex.Message}");
            }
        }
    }

    private void ApplyFleetSearchFilter()
    {
        if (FindFleetSearchBox is null)
        {
            return;
        }

        var query = FindFleetSearchBox.Text.Trim();
        var matches = _allNetworkFleets
            .Select(card => card with { SearchScore = CalculateFleetSearchScore(card, query) })
            .Where(card => string.IsNullOrWhiteSpace(query) || card.SearchScore > 0)
            .OrderByDescending(card => card.SearchScore)
            .ThenByDescending(card => card.Snapshot.OnlineMembers)
            .ThenBy(card => card.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _networkFleets.Clear();
        foreach (var card in matches)
        {
            _networkFleets.Add(card);
        }

        if (FindFleetSearchCountText is not null)
        {
            FindFleetSearchCountText.Text = string.IsNullOrWhiteSpace(query)
                ? $"{_networkFleets.Count} 个舰队"
                : $"匹配 {_networkFleets.Count}";
        }
    }

    private static int CalculateFleetSearchScore(NetworkFleetCard card, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 1;
        }

        var name = card.Name ?? "";
        var code = card.Snapshot.Code ?? "";
        if (code.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 95;
        }

        if (code.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        if (code.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        return name.Contains(query, StringComparison.OrdinalIgnoreCase) ? 40 : 0;
    }

    private void FindFleetSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFleetSearchFilter();
    }

    private void MergeNetworkFleetState(NetworkFleetSnapshot snapshot)
    {
        MergeNetworkFleetSquads(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.Name) || string.IsNullOrWhiteSpace(snapshot.Code))
        {
            return;
        }

        _fleetName = snapshot.Name;
        _fleetCode = snapshot.Code;
        _fleetChiefCommander = string.IsNullOrWhiteSpace(snapshot.Commander) ? _fleetChiefCommander : snapshot.Commander!;
        _fleetDescription = string.IsNullOrWhiteSpace(snapshot.Description) ? _fleetDescription : snapshot.Description!;
        _fleetType = string.IsNullOrWhiteSpace(snapshot.Type) ? _fleetType : snapshot.Type!;
        _fleetActiveTime = string.IsNullOrWhiteSpace(snapshot.ActiveTime) ? _fleetActiveTime : snapshot.ActiveTime!;
        _fleetJoinPolicy = string.IsNullOrWhiteSpace(snapshot.JoinPolicy) ? _fleetJoinPolicy : snapshot.JoinPolicy!;
        _fleetLogoPath = SaveNetworkFleetLogo(snapshot);

        var isCommander = IsCurrentUserFleetCommander();
        var remoteHasNotice = !string.IsNullOrWhiteSpace(snapshot.NoticeTitle) ||
                              !string.IsNullOrWhiteSpace(snapshot.NoticeContent);
        var localHasNotice = !string.IsNullOrWhiteSpace(_fleetNoticeTitle) ||
                             !string.IsNullOrWhiteSpace(_fleetNoticeContent);
        if (remoteHasNotice || !localHasNotice || !isCommander)
        {
            _fleetNoticeTitle = snapshot.NoticeTitle ?? "";
            _fleetNoticeContent = snapshot.NoticeContent ?? "";
        }

        var remoteHasTask = !string.IsNullOrWhiteSpace(snapshot.CurrentTaskTitle);
        var localHasTask = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle);
        if (remoteHasTask || !localHasTask || !isCommander)
        {
            _fleetCurrentTaskTitle = snapshot.CurrentTaskTitle ?? "";
            _fleetCurrentTaskBrief = snapshot.CurrentTaskBrief ?? "";
            _fleetCurrentTaskParticipants = snapshot.CurrentTaskParticipants ?? "";
            _fleetCurrentTaskRally = snapshot.CurrentTaskRally ?? "";
            _fleetCurrentTaskShip = snapshot.CurrentTaskShip ?? "";
            _fleetCurrentTaskTime = snapshot.CurrentTaskTime;
        }

        var remotePlans = snapshot.ActionPlans ?? [];
        if (remotePlans.Length > 0 || _fleetActionPlans.Count == 0 || !isCommander)
        {
            _fleetActionPlans.Clear();
            foreach (var actionPlan in remotePlans)
            {
                var row = new FleetActionPlanRow(
                    actionPlan.Id,
                    actionPlan.Title,
                    actionPlan.Content,
                    actionPlan.StartTime,
                    actionPlan.NotifyMembers);

                foreach (var participant in actionPlan.Participants ?? [])
                {
                    row.Participants.Add(new ActionPlanParticipantRow(
                        participant.Callsign,
                        participant.GameName,
                        participant.AvatarPath,
                        participant.Initials));
                }

                row.RefreshParticipantSummary();
                _fleetActionPlans.Add(row);
            }
        }

        SaveCurrentConfig();
        RefreshOverlayWindow();
        RenderState();
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        SelectFeaturedActionPlan();
    }

    private void ApplyNetworkSnapshot(NetworkPlayerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Name) ||
            snapshot.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _networkSnapshots.TryGetValue(snapshot.Name, out var previousSnapshot);
        var wasInFleet = previousSnapshot is not null && IsSameFleet(previousSnapshot.Fleet);
        var isInFleet = IsSameFleet(snapshot.Fleet);
        _networkSnapshots[snapshot.Name] = snapshot;

        if (_hasFleet)
        {
            var displayName = FormatCommanderName(snapshot.Callsign, snapshot.Name, snapshot.Name);
            if (!wasInFleet && isInFleet)
            {
                AddFleetLog("成员", "玩家加入", $"{displayName} 加入舰队");
            }
            else if (wasInFleet && !isInFleet)
            {
                AddFleetLog("成员", "玩家离开", $"{displayName} 离开舰队");
            }
            else if (isInFleet &&
                     previousSnapshot is not null &&
                     previousSnapshot.Online &&
                     !snapshot.Online)
            {
                AddFleetLog("成员", "玩家离线", $"{displayName} 离线");
            }
        }

        if (_hasFleet && !isInFleet)
        {
            return;
        }

        _fleetState.Apply(new FleetEvent(
            snapshot.Online ? FleetEventType.PlayerOnline : FleetEventType.PlayerOffline,
            snapshot.Name,
            Timestamp: snapshot.LastUpdated));

        if (!snapshot.Online)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Location) &&
            !snapshot.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _fleetState.Apply(new FleetEvent(
                FleetEventType.PlayerLocationChanged,
                snapshot.Name,
                Location: snapshot.Location,
                LocationEvidenceScore: 55,
                LocationEvidence: "Network relay",
                Timestamp: snapshot.LastUpdated));
        }
    }

    private bool IsSameFleet(string? fleet)
    {
        if (!_hasFleet || string.IsNullOrWhiteSpace(fleet))
        {
            return false;
        }

        return fleet.Equals(_fleetName, StringComparison.OrdinalIgnoreCase) ||
               fleet.Equals(_fleetCode, StringComparison.OrdinalIgnoreCase);
    }

    private void MergeNetworkFleetSquads(NetworkFleetSnapshot snapshot)
    {
        var changed = false;
        foreach (var squadSnapshot in snapshot.Squads ?? [])
        {
            if (string.IsNullOrWhiteSpace(squadSnapshot.Name))
            {
                continue;
            }

            var existing = _squads.FirstOrDefault(squad =>
                squad.Name.Equals(squadSnapshot.Name, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                _squads.Add(new SquadRow
                {
                    Name = squadSnapshot.Name,
                    Icon = GetInitials(squadSnapshot.Name),
                    Commander = string.IsNullOrWhiteSpace(squadSnapshot.Commander) ? "Unassigned" : squadSnapshot.Commander!,
                    Type = string.IsNullOrWhiteSpace(squadSnapshot.Type) ? "Assault" : squadSnapshot.Type!,
                    Description = string.IsNullOrWhiteSpace(squadSnapshot.Description) ? "No squad briefing yet." : squadSnapshot.Description!
                });
                changed = true;
                continue;
            }

            existing.Commander = string.IsNullOrWhiteSpace(squadSnapshot.Commander) ? existing.Commander : squadSnapshot.Commander!;
            existing.Type = string.IsNullOrWhiteSpace(squadSnapshot.Type) ? existing.Type : squadSnapshot.Type!;
            existing.Description = string.IsNullOrWhiteSpace(squadSnapshot.Description) ? existing.Description : squadSnapshot.Description!;
            existing.RefreshComputed();
        }

        if (changed)
        {
            RenderSquads();
            RenderMySquad();
        }
    }

    private Uri BuildNetworkUri(string path)
    {
        var baseUrl = NetworkServerUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = "http://127.0.0.1:5058";
        }

        if (!baseUrl.EndsWith("/", StringComparison.Ordinal))
        {
            baseUrl += "/";
        }

        return new Uri(new Uri(baseUrl), path);
    }

    private Task<HttpResponseMessage> PostNetworkJsonAsync<T>(string path, T payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildNetworkUri(path))
        {
            Content = JsonContent.Create(payload)
        };
        var key = NetworkServerKeyBox.Password.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Add("X-StarBridge-Key", key);
        }
        if (!string.IsNullOrWhiteSpace(_authToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _authToken);
        }

        return _networkClient.SendAsync(request);
    }

    private async Task SendFleetEmailNotificationAsync(string subject, string body, bool silent = false)
    {
        if (!IsLoggedIn || !_hasFleet || string.IsNullOrWhiteSpace(_fleetCode))
        {
            return;
        }

        try
        {
            var request = new FleetNotificationRequest(_fleetCode, subject, body);
            var response = await PostNetworkJsonAsync("api/fleets/notify", request);
            if (!response.IsSuccessStatusCode)
            {
                if (!silent)
                {
                    var error = response.StatusCode == HttpStatusCode.NotFound
                        ? "server notify endpoint is not available; deploy StarBridge 0.3.1 relay"
                        : await ReadResponseErrorAsync(response);
                    AppendOutput($"Fleet email notification failed: {error}");
                }

                return;
            }

            if (!silent)
            {
                AppendOutput("Fleet email notification sent.");
            }
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                AppendOutput($"Fleet email notification failed: {ex.Message}");
            }
        }
    }

    private static async Task<string> ReadResponseErrorAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!string.IsNullOrWhiteSpace(body))
            {
                return $"{(int)response.StatusCode} {body}";
            }
        }
        catch
        {
            // Fall back to the HTTP status line when the server body is unavailable.
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    private NetworkFleetSnapshot BuildLocalFleetSnapshot()
    {
        var squads = _squads
            .Select(squad => new NetworkSquadSnapshot(
                squad.Name,
                squad.Commander,
                squad.Type,
                squad.Description))
            .ToArray();

        var actionPlans = _fleetActionPlans
            .Select(plan => new NetworkActionPlanSnapshot(
                plan.Id,
                plan.Title,
                plan.Content,
                plan.StartTime,
                plan.NotifyMembers,
                plan.Participants
                    .Select(participant => new NetworkActionPlanParticipantSnapshot(
                        participant.Callsign,
                        participant.GameName,
                        participant.AvatarPath,
                        participant.Initials))
                    .ToArray()))
            .ToArray();

        return new NetworkFleetSnapshot(
            _fleetName,
            _fleetCode,
            _fleetChiefCommander,
            _fleetDescription,
            _fleetType,
            _fleetActiveTime,
            _fleetJoinPolicy,
            string.IsNullOrWhiteSpace(_fleetCode) ? "LOGO" : _fleetCode,
            BuildFleetLogoImageData(),
            squads,
            _players.Count(player => player.Status.Equals("Online", StringComparison.OrdinalIgnoreCase)),
            Math.Max(1, _players.Count),
            _fleetNoticeTitle,
            _fleetNoticeContent,
            _fleetCurrentTaskTitle,
            _fleetCurrentTaskBrief,
            _fleetCurrentTaskParticipants,
            _fleetCurrentTaskRally,
            _fleetCurrentTaskShip,
            _fleetCurrentTaskTime,
            actionPlans,
            DateTimeOffset.UtcNow,
            _accountName);
    }

    private string? BuildFleetLogoImageData()
    {
        if (string.IsNullOrWhiteSpace(_fleetLogoPath) || !File.Exists(_fleetLogoPath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(_fleetLogoPath);
            if (bytes.Length > 768 * 1024)
            {
                return null;
            }

            return Convert.ToBase64String(bytes);
        }
        catch
        {
            return null;
        }
    }

    private string? SaveNetworkFleetLogo(NetworkFleetSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.LogoImageData))
        {
            return null;
        }

        try
        {
            var payload = snapshot.LogoImageData;
            var commaIndex = payload.IndexOf(',');
            if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                payload = payload[(commaIndex + 1)..];
            }

            var bytes = Convert.FromBase64String(payload);
            if (bytes.Length == 0 || bytes.Length > 768 * 1024)
            {
                return null;
            }

            var directory = Path.Combine(DesktopAppConfig.ConfigDirectory, "Images");
            Directory.CreateDirectory(directory);
            var safeCode = new string(snapshot.Code.Where(char.IsLetterOrDigit).ToArray());
            if (string.IsNullOrWhiteSpace(safeCode))
            {
                safeCode = "fleet";
            }

            var path = Path.Combine(directory, $"fleet-{safeCode}-logo.png");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private async void RefreshFleetDirectory_Click(object sender, RoutedEventArgs e)
    {
        await PushFleetDirectoryAsync(silent: true);
        await PullNetworkFleetsAsync();
    }

    private async void JoinNetworkFleet_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("加入舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not NetworkFleetCard card)
        {
            return;
        }

        if (!card.CanJoin || IsSameFleet(card.Snapshot.Name) || IsSameFleet(card.Snapshot.Code))
        {
            NetworkStatusText.Text = "你已经在该舰队中。";
            return;
        }

        var confirmText = _hasFleet
            ? $"当前已经加入 {_fleetName}。是否退出当前舰队并加入 {card.Name}？"
            : $"是否加入舰队 {card.Name}？";
        var result = System.Windows.MessageBox.Show(
            this,
            confirmText,
            "确认加入舰队",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        if (_hasFleet)
        {
            AddFleetLog("成员", "离开舰队", $"{FormatCommanderName(_callsign, _localPlayer)} 离开 {_fleetName}");
        }

        JoinNetworkFleet(card.Snapshot);
        AddFleetLog("成员", "加入舰队", $"{FormatCommanderName(_callsign, _localPlayer)} 加入 {card.Name}");
        await PushLocalSnapshotAsync(silent: true);
        await PullNetworkSnapshotsAsync(silent: true);
        NetworkStatusText.Text = $"已加入舰队：{card.Name}";
        NavigateToMyFleet();
    }

    private void JoinNetworkFleet(NetworkFleetSnapshot snapshot)
    {
        _hasFleet = true;
        _isCreatingFleet = false;
        _fleetName = snapshot.Name;
        _fleetCode = snapshot.Code;
        _fleetChiefCommander = string.IsNullOrWhiteSpace(snapshot.Commander) ? "Unassigned" : snapshot.Commander!;
        _fleetDeputyCommander = "Unassigned";
        _fleetDescription = string.IsNullOrWhiteSpace(snapshot.Description) ? "No fleet description." : snapshot.Description!;
        _fleetType = string.IsNullOrWhiteSpace(snapshot.Type) ? "Combat" : snapshot.Type!;
        _fleetJoinPolicy = string.IsNullOrWhiteSpace(snapshot.JoinPolicy) ? "Open" : snapshot.JoinPolicy!;
        _fleetActiveTime = string.IsNullOrWhiteSpace(snapshot.ActiveTime) ? "20:00 - 23:59 UTC+8" : snapshot.ActiveTime!;
        _fleetLogoPath = SaveNetworkFleetLogo(snapshot);
        LocalFleetText.Text = $"{_fleetName} [{_fleetCode}]";

        _squads.Clear();
        foreach (var squad in snapshot.Squads ?? [])
        {
            if (string.IsNullOrWhiteSpace(squad.Name))
            {
                continue;
            }

            _squads.Add(new SquadRow
            {
                Name = squad.Name,
                Icon = GetInitials(squad.Name),
                Commander = string.IsNullOrWhiteSpace(squad.Commander) ? "Unassigned" : squad.Commander!,
                Type = string.IsNullOrWhiteSpace(squad.Type) ? "Assault" : squad.Type!,
                Description = string.IsNullOrWhiteSpace(squad.Description) ? "No squad briefing yet." : squad.Description!
            });
        }

        _selectedSquad = _squads.FirstOrDefault();
        _joinedSquad = null;
        SquadSelectionList.SelectedItem = _selectedSquad;
        UpdateFleetEntryPanels();
        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        SaveCurrentConfig();
        RefreshOverlayWindow();
    }

    private async void DisbandFleetButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("解散舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!_hasFleet || string.IsNullOrWhiteSpace(_fleetCode))
        {
            DisbandFleetStatusText.Text = "当前没有可解散的舰队。";
            return;
        }

        var password = DisbandFleetPasswordBox.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            DisbandFleetStatusText.Text = "请输入账号密码后再解散舰队。";
            return;
        }

        try
        {
            var request = new FleetDisbandRequest(_fleetCode, password);
            var response = await PostNetworkJsonAsync("api/fleets/disband", request);
            response.EnsureSuccessStatusCode();

            ClearFleetState();
            DisbandFleetPasswordBox.Password = "";
            DisbandFleetStatusText.Text = "舰队已解散。";
            NetworkStatusText.Text = "舰队已从服务器移除";
            SaveCurrentConfig();
            await PullNetworkSnapshotsAsync(silent: true);
        }
        catch (Exception ex)
        {
            DisbandFleetStatusText.Text = $"解散失败：{ex.Message}";
        }
    }

    private void ClearFleetState()
    {
        _hasFleet = false;
        _isCreatingFleet = false;
        _fleetName = "No Fleet";
        _fleetCode = "N/A";
        _fleetChiefCommander = "Unassigned";
        _fleetDeputyCommander = "Unassigned";
        _fleetDescription = "No fleet description.";
        _fleetType = "Combat";
        _fleetJoinPolicy = "Open";
        _fleetActiveTime = "20:00 - 23:59 UTC+8";
        _fleetLogoPath = null;
        _fleetNoticeTitle = "";
        _fleetNoticeContent = "";
        _fleetCurrentTaskTitle = "";
        _fleetCurrentTaskBrief = "";
        _fleetCurrentTaskParticipants = "";
        _fleetCurrentTaskRally = "";
        _fleetCurrentTaskShip = "";
        _fleetCurrentTaskEmailCall = false;
        _fleetCurrentTaskTime = null;
        _fleetCurrentTaskHistoryKey = "";
        _fleetCurrentTaskNoticeRevision = 0;
        _fleetActionTitle = "";
        _fleetActionContent = "";
        _fleetActionStartTime = null;
        _fleetActionNotifyMembers = false;
        _selectedActionPlanId = "";
        _joinActionNotifyMe = false;
        _squads.Clear();
        _fleetTaskHistory.Clear();
        _fleetActionPlans.Clear();
        _joinedActionPlanIds.Clear();
        _allFleetEventLogs.Clear();
        _fleetEventLogs.Clear();
        _joinedSquad = null;
        _selectedSquad = null;
        LocalFleetText.Text = "未加入舰队";
        RefreshFleetHeader();
        UpdateFleetEntryPanels();
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();
    }

    private void AddFleetLog(string type, string title, string detail)
    {
        if (!_hasFleet)
        {
            return;
        }

        var row = new FleetEventLogRow(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            type,
            title,
            detail);
        _allFleetEventLogs.Insert(0, row);
        ApplyFleetEventLogFilter();
        SaveCurrentConfig();
    }

    private void ApplyFleetEventLogFilter()
    {
        if (FleetLogFilterBox is null)
        {
            return;
        }

        var selectedType = "All";
        if (FleetLogFilterBox.SelectedItem is ComboBoxItem { Tag: string tag })
        {
            selectedType = tag;
        }

        var query = FleetLogSearchBox?.Text.Trim() ?? "";
        var rows = _allFleetEventLogs
            .Where(row => selectedType.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                          row.Type.Equals(selectedType, StringComparison.OrdinalIgnoreCase))
            .Where(row => string.IsNullOrWhiteSpace(query) ||
                          row.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                          row.Detail.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(row => row.Timestamp)
            .ToArray();

        _fleetEventLogs.Clear();
        foreach (var row in rows)
        {
            _fleetEventLogs.Add(row);
        }

        if (FleetLogEmptyText is not null)
        {
            FleetLogEmptyText.Visibility = _fleetEventLogs.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void FleetLogFilter_Changed(object sender, EventArgs e)
    {
        ApplyFleetEventLogFilter();
    }

    private void PlayersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {

    }

    private void RenderCachedIdentity()
    {
        if (!IsLoggedIn)
        {
            GameNameText.Text = "请登录后查看";
            PlayerIdText.Text = "请登录后查看";
            ProfileStatusText.Text = "浏览模式";
            return;
        }

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            LogPathBox.Text = _logPath;
        }

        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            GameNameText.Text = _language == "zh" ? "等待 Game.log 身份信息" : "Waiting for Game.log identity";
            PlayerIdText.Text = _language == "zh" ? "等待 playerGEID" : "Waiting for playerGEID";
            ProfileStatusText.Text = _language == "zh" ? "需要身份信息" : "Identity Required";
            return;
        }

        GameNameText.Text = _localPlayer;
        PlayerIdText.Text = string.IsNullOrWhiteSpace(_localPlayerId) ? "Unknown" : _localPlayerId;
        ProfileStatusText.Text = _language == "zh" ? "已缓存身份" : "Cached Identity";
        _fleetState.Apply(new FleetEvent(FleetEventType.PlayerOffline, _localPlayer));
        RenderState();
    }

    private void SaveCurrentConfig()
    {
        var overlaySettings = _overlaySettings.Serialize();
        var overlayLayout = SerializeOverlayLayout();
        var fleetStateJson = SerializeFleetState();
        DesktopAppConfig.Save(new DesktopAppConfig(
            _logPath,
            _localPlayer,
            _localPlayerId,
            _avatarPath,
            OverlayHotkeyBox.Text,
            overlayLayout,
            _callsign,
            overlaySettings,
            _language,
            NetworkServerUrlBox.Text.Trim(),
            NetworkServerKeyBox.Password,
            _accountName,
            _authToken,
            fleetStateJson));
        DesktopAppConfig.SaveOverlaySettings(overlaySettings);
        DesktopAppConfig.SaveOverlayLayout(overlayLayout);
        DesktopAppConfig.SaveActiveOverlayPreset(_activeOverlayPreset);
        DesktopAppConfig.SaveOverlayPresetSettings(_activeOverlayPreset, overlaySettings);
        DesktopAppConfig.SaveOverlayPresetLayout(_activeOverlayPreset, overlayLayout);
    }

    private string SerializeFleetState()
    {
        var cache = new LocalFleetState(
            _hasFleet,
            _fleetName,
            _fleetCode,
            _fleetChiefCommander,
            _fleetDeputyCommander,
            _fleetDescription,
            _fleetType,
            _fleetJoinPolicy,
            _fleetActiveTime,
            _fleetLogoPath,
            _fleetNoticeTitle,
            _fleetNoticeContent,
            _fleetCurrentTaskTitle,
            _fleetCurrentTaskBrief,
            _fleetCurrentTaskParticipants,
            _fleetCurrentTaskRally,
            _fleetCurrentTaskShip,
            _fleetCurrentTaskEmailCall,
            _fleetCurrentTaskTime,
            _fleetCurrentTaskHistoryKey,
            _fleetCurrentTaskNoticeRevision,
            _fleetTaskHistory.Select(item => new LocalFleetTaskHistory(
                item.Key,
                item.Title,
                item.Brief,
                item.Status,
                item.Participants,
                item.Rally,
                item.RequiredShip,
                item.PublishedAtText)).ToArray(),
            _squads.Select(squad => new LocalSquadState(
                squad.Name,
                squad.Icon,
                squad.Commander,
                squad.Mission,
                squad.RallyPoint,
                squad.Description,
                squad.Type,
                squad.EmblemPath)).ToArray(),
            _joinedSquad?.Name,
            _fleetActionPlans.Select(plan => new LocalFleetActionPlan(
                plan.Id,
                plan.Title,
                plan.Content,
                plan.StartTime,
                plan.NotifyMembers,
                plan.Participants.ToArray())).ToArray(),
            _joinedActionPlanIds.ToArray(),
            _allFleetEventLogs.Select(row => new LocalFleetEventLog(
                row.Id,
                row.Timestamp,
                row.Type,
                row.Title,
                row.Detail)).ToArray());
        return JsonSerializer.Serialize(cache);
    }

    private void LoadFleetState(string? fleetStateJson)
    {
        if (string.IsNullOrWhiteSpace(fleetStateJson))
        {
            return;
        }

        try
        {
            var cache = JsonSerializer.Deserialize<LocalFleetState>(fleetStateJson);
            if (cache is null)
            {
                return;
            }

            _hasFleet = cache.HasFleet;
            _fleetName = string.IsNullOrWhiteSpace(cache.FleetName) ? "No Fleet" : cache.FleetName;
            _fleetCode = string.IsNullOrWhiteSpace(cache.FleetCode) ? "N/A" : cache.FleetCode;
            _fleetChiefCommander = string.IsNullOrWhiteSpace(cache.FleetChiefCommander) ? "Unassigned" : cache.FleetChiefCommander;
            _fleetDeputyCommander = string.IsNullOrWhiteSpace(cache.FleetDeputyCommander) ? "Unassigned" : cache.FleetDeputyCommander;
            _fleetDescription = string.IsNullOrWhiteSpace(cache.FleetDescription) ? "No fleet description." : cache.FleetDescription;
            _fleetType = string.IsNullOrWhiteSpace(cache.FleetType) ? "Combat" : cache.FleetType;
            _fleetJoinPolicy = string.IsNullOrWhiteSpace(cache.FleetJoinPolicy) ? "Open" : cache.FleetJoinPolicy;
            _fleetActiveTime = string.IsNullOrWhiteSpace(cache.FleetActiveTime) ? "20:00 - 23:59 UTC+8" : cache.FleetActiveTime;
            _fleetLogoPath = cache.FleetLogoPath;
            _fleetNoticeTitle = cache.FleetNoticeTitle ?? "";
            _fleetNoticeContent = cache.FleetNoticeContent ?? "";
            _fleetCurrentTaskTitle = cache.FleetCurrentTaskTitle ?? "";
            _fleetCurrentTaskBrief = cache.FleetCurrentTaskBrief ?? "";
            _fleetCurrentTaskParticipants = cache.FleetCurrentTaskParticipants ?? "";
            _fleetCurrentTaskRally = cache.FleetCurrentTaskRally ?? "";
            _fleetCurrentTaskShip = cache.FleetCurrentTaskShip ?? "";
            _fleetCurrentTaskEmailCall = cache.FleetCurrentTaskEmailCall;
            _fleetCurrentTaskTime = cache.FleetCurrentTaskTime;
            _fleetCurrentTaskHistoryKey = cache.FleetCurrentTaskHistoryKey ?? "";
            _fleetCurrentTaskNoticeRevision = cache.FleetCurrentTaskNoticeRevision;

            _fleetTaskHistory.Clear();
            foreach (var item in cache.TaskHistory ?? [])
            {
                _fleetTaskHistory.Add(new FleetTaskHistoryRow(
                    item.Key,
                    item.Title,
                    item.Brief,
                    item.Status,
                    item.Participants,
                    item.Rally,
                    item.RequiredShip,
                    item.PublishedAtText));
            }

            _squads.Clear();
            foreach (var squad in cache.Squads ?? [])
            {
                _squads.Add(new SquadRow
                {
                    Name = squad.Name,
                    Icon = string.IsNullOrWhiteSpace(squad.Icon) ? GetInitials(squad.Name) : squad.Icon,
                    Commander = string.IsNullOrWhiteSpace(squad.Commander) ? "Unassigned" : squad.Commander,
                    Mission = string.IsNullOrWhiteSpace(squad.Mission) ? "Standby" : squad.Mission,
                    RallyPoint = string.IsNullOrWhiteSpace(squad.RallyPoint) ? "Use Global" : squad.RallyPoint,
                    Description = string.IsNullOrWhiteSpace(squad.Description) ? "No squad briefing yet." : squad.Description,
                    Type = string.IsNullOrWhiteSpace(squad.Type) ? "Assault" : squad.Type,
                    EmblemPath = squad.EmblemPath
                });
            }

            _joinedSquad = _squads.FirstOrDefault(squad =>
                squad.Name.Equals(cache.JoinedSquadName, StringComparison.OrdinalIgnoreCase));
            _selectedSquad = _joinedSquad ?? _squads.FirstOrDefault();
            if (SquadSelectionList is not null)
            {
                SquadSelectionList.SelectedItem = _selectedSquad;
            }

            _fleetActionPlans.Clear();
            foreach (var plan in cache.ActionPlans ?? [])
            {
                var row = new FleetActionPlanRow(
                    plan.Id,
                    plan.Title,
                    plan.Content,
                    plan.StartTime,
                    plan.NotifyMembers);
                foreach (var participant in plan.Participants ?? [])
                {
                    row.Participants.Add(participant);
                }

                row.RefreshParticipantSummary();
                _fleetActionPlans.Add(row);
            }

            _joinedActionPlanIds.Clear();
            foreach (var id in cache.JoinedActionPlanIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _joinedActionPlanIds.Add(id);
                }
            }

            _allFleetEventLogs.Clear();
            foreach (var item in cache.EventLog ?? [])
            {
                _allFleetEventLogs.Add(new FleetEventLogRow(
                    item.Id,
                    item.Timestamp,
                    item.Type,
                    item.Title,
                    item.Detail));
            }

            ApplyFleetEventLogFilter();

            LocalFleetText.Text = _hasFleet ? $"{_fleetName} [{_fleetCode}]" : "未加入舰队";
        }
        catch
        {
            // ignore invalid cache and continue with current in-memory defaults
        }
    }

    private void LoadOwnedShips()
    {
        _ownedShips.Clear();
        if (!IsLoggedIn)
        {
            UpdateShipDatabaseSummary();
            RefreshFleetShipInventory();
            return;
        }

        foreach (var ship in ShipDatabaseStore.Load(GetShipDatabaseOwnerKey()))
        {
            _ownedShips.Add(ship);
        }

        UpdateShipDatabaseSummary();
        RefreshFleetShipInventory();
    }

    private void SaveOwnedShips()
    {
        ShipDatabaseStore.Save(GetShipDatabaseOwnerKey(), _ownedShips);
        RefreshFleetShipInventory();
    }

    private string GetShipDatabaseOwnerKey()
    {
        if (!string.IsNullOrWhiteSpace(_accountName))
        {
            return $"account:{_accountName}";
        }

        if (!string.IsNullOrWhiteSpace(_localPlayerId))
        {
            return _localPlayerId;
        }

        if (!string.IsNullOrWhiteSpace(_localPlayer))
        {
            return _localPlayer;
        }

        return "local";
    }

    private void UpdateShipDatabaseSummary(int? matchedCodes = null, int? matchedNames = null)
    {
        if (!IsLoggedIn)
        {
            ShipDatabaseStatusText.Text = "请登录后使用舰船数据库";
            return;
        }

        var text = $"已验证舰船：{_ownedShips.Count}";
        if (matchedCodes is not null || matchedNames is not null)
        {
            text += $" / 官网舰船条目 {matchedCodes ?? 0} / 已识别 {matchedNames ?? 0}";
        }

        ShipDatabaseStatusText.Text = text;
    }

    private void RefreshFleetShipInventory()
    {
        if (FleetShipCountText is not null)
        {
            FleetShipCountText.Text = _ownedShips.Count.ToString();
        }

        if (FleetShipInventoryCountText is null)
        {
            return;
        }

        _fleetShipInventory.Clear();

        var ownerName = string.IsNullOrWhiteSpace(_localPlayer) ? "Unknown" : _localPlayer;
        var ownerCallsign = string.IsNullOrWhiteSpace(_callsign) ? ownerName : _callsign!;
        var ownerDisplay = FormatCommanderName(_callsign, _localPlayer, ownerName);
        var ownerSquad = _joinedSquad?.Name ?? "未加入小队";
        var index = 1;
        foreach (var ship in _ownedShips.OrderBy(ship => ship.ImportedAt).ThenBy(ship => ship.DisplayName))
        {
            _fleetShipInventory.Add(new FleetShipInventoryRow(
                index++,
                ship.DisplayName,
                ship.Code,
                ownerDisplay,
                ownerCallsign,
                ownerName,
                ownerSquad,
                _avatarPath,
                GetInitials(ownerName),
                ship.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
        }

        FleetShipInventoryCountText.Text = $"已上传舰船 / {_fleetShipInventory.Count}";
        FleetShipInventoryEmptyText.Visibility = _fleetShipInventory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        FleetShipDatabaseCountText.Text = _fleetShipInventory.Count.ToString();
        FleetShipDatabaseCapitalText.Text = "待分类";
        FleetShipDatabaseLargeText.Text = "待分类";
        FleetShipDatabaseMediumText.Text = "待分类";
        FleetShipDatabaseSmallText.Text = "待分类";
        FleetShipDatabaseTopOwnerText.Text = _fleetShipInventory.Count == 0
            ? "-"
            : ownerDisplay;
        FleetShipDatabaseAceText.Text = "待评定";
        FleetShipDatabaseEmptyText.Visibility = _fleetShipInventory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentConfig();
        CloseOverlayWindow();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateMaximizeButtonText();
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string language })
        {
            _language = NormalizeLanguage(language);
            ApplyLanguageToControls();
            RenderState();
            RefreshOverlayWindow();
            if (!_isLoadingSettings)
            {
                SaveCurrentConfig();
            }
        }
    }

    private void OverlayHotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        OverlayHotkeyBox.SelectAll();
    }

    private void OverlayHotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        OverlayHotkeyBox.Text = FormatHotkey(Keyboard.Modifiers, key);
        if (!_isLoadingSettings)
        {
            RegisterOverlayHotkey();
            SaveCurrentConfig();
        }
    }

    private void OpenOverlay_Click(object sender, RoutedEventArgs e)
    {
        ToggleOverlayWindow();
    }

    private void ToggleOverlayWindow()
    {
        RenderSquads();
        if (_overlayWindow is { IsVisible: true })
        {
            _overlayWindow.Close();
            return;
        }

        _overlayWindow = new OverlayWindow(_squads, GetOverlayPlayers(), _overlayLayout, _overlaySettings, _language, _hasFleet, BuildOverlayCommandState());
        _overlayWindow.Closed += (_, _) => _overlayWindow = null;
        _overlayWindow.Show();
    }

    private IntPtr MainWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == OverlayHotkeyId)
        {
            handled = true;
            ToggleOverlayWindow();
        }

        return IntPtr.Zero;
    }

    private void RegisterOverlayHotkey()
    {
        UnregisterOverlayHotkey();

        if (!TryParseHotkey(OverlayHotkeyBox.Text, out var modifiers, out var key))
        {
            AppendOutput($"HOTKEY | invalid={OverlayHotkeyBox.Text}");
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (handle == IntPtr.Zero || virtualKey == 0)
        {
            return;
        }

        _hotkeyRegistered = RegisterHotKey(handle, OverlayHotkeyId, modifiers | ModNoRepeat, (uint)virtualKey);
        if (!_hotkeyRegistered)
        {
            AppendOutput($"HOTKEY | register failed={OverlayHotkeyBox.Text} | error={Marshal.GetLastWin32Error()}");
            return;
        }

        AppendOutput($"HOTKEY | registered={OverlayHotkeyBox.Text}");
    }

    private void UnregisterOverlayHotkey()
    {
        if (!_hotkeyRegistered)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            UnregisterHotKey(handle, OverlayHotkeyId);
        }

        _hotkeyRegistered = false;
    }

    private static string FormatHotkey(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    private static bool TryParseHotkey(string? text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModControl;
                continue;
            }

            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModAlt;
                continue;
            }

            if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModShift;
                continue;
            }

            if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModWin;
                continue;
            }

            if (!Enum.TryParse(part, ignoreCase: true, out key))
            {
                return false;
            }
        }

        return key is not Key.None
            and not Key.LeftCtrl
            and not Key.RightCtrl
            and not Key.LeftAlt
            and not Key.RightAlt
            and not Key.LeftShift
            and not Key.RightShift
            and not Key.LWin
            and not Key.RWin;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr handle, int id);

    private void CloseOverlayWindow()
    {
        var overlayWindow = _overlayWindow;
        if (overlayWindow is null)
        {
            return;
        }

        _overlayWindow = null;
        try
        {
            overlayWindow.Close();
        }
        catch (Exception exception)
        {
            App.WriteCrashLog(exception);
        }
    }

    private void RefreshOverlayWindow()
    {
        if (_overlayWindow is not { IsVisible: true })
        {
            return;
        }

        RenderSquads();
        _overlayWindow.Refresh(_squads, GetOverlayPlayers(), _overlaySettings, _language, _hasFleet, BuildOverlayCommandState());
    }

    private OverlayCommandState BuildOverlayCommandState()
    {
        var noticeTitle = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle)
            ? "任务发布"
            : _fleetNoticeTitle;
        var noticeText = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle)
            ? $"{_fleetCurrentTaskTitle} / {NormalizeOptionalField(_fleetCurrentTaskBrief)}{BuildInvisibleNoticeRevision()}"
            : _fleetNoticeContent;

        return new OverlayCommandState(
            noticeTitle,
            noticeText,
            _fleetCurrentTaskTitle,
            _fleetCurrentTaskBrief,
            _fleetCurrentTaskRally,
            _fleetCurrentTaskShip);
    }

    private string BuildInvisibleNoticeRevision()
    {
        if (_fleetCurrentTaskNoticeRevision <= 0)
        {
            return "";
        }

        return new string('\u200B', (_fleetCurrentTaskNoticeRevision % 2) + 1);
    }

    private IEnumerable<PlayerRow> GetOverlayPlayers()
    {
        if (_joinedSquad is null)
        {
            return _players;
        }

        return _players.Where(player =>
            player.SquadName.Equals(_joinedSquad.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void SaveOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();
        RefreshOverlayWindow();
        AppendOutput($"Overlay preset saved: {_activeOverlayPreset}.");
    }

    private void ResetOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        _overlayLayout.Clear();
        _overlayLayout.AddRange(CreateDefaultOverlayLayout(_activeOverlayPreset));
        _overlaySettings = CreateDefaultOverlaySettings(_activeOverlayPreset);
        _isLoadingSettings = true;
        ApplyOverlaySettingsToControls();
        _isLoadingSettings = false;
        RenderOverlayEditor();
        SaveCurrentConfig();
        RefreshOverlayWindow();
        AppendOutput($"Overlay preset reset: {_activeOverlayPreset}.");
    }

    private void OverlayEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderOverlayEditor();
    }

    private void OverlaySetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (HideMissionWhenIdleCheck is null ||
            HideOfflineMembersCheck is null ||
            HideSquadIconsCheck is null ||
            TrayModeCheck is null ||
            OverlayOpacitySlider is null ||
            ShowNoticePanelCheck is null ||
            ShowSquadsPanelCheck is null ||
            ShowMissionPanelCheck is null ||
            ShowMembersPanelCheck is null ||
            OverlayThemeBox is null)
        {
            return;
        }

        var mode = ShowCallsignOnlyRadio.IsChecked == true
            ? OverlayMemberNameMode.CallsignOnly
            : ShowGameNameOnlyRadio.IsChecked == true
                ? OverlayMemberNameMode.GameNameOnly
                : OverlayMemberNameMode.CallsignAndGameName;

        _overlaySettings = new OverlayDisplaySettings(
            HideMissionWhenIdleCheck.IsChecked == true,
            mode,
            HideOfflineMembersCheck.IsChecked == true,
            HideSquadIconsCheck.IsChecked == true,
            TrayModeCheck.IsChecked == true,
            Math.Clamp(OverlayOpacitySlider.Value / 100.0, 0.15, 1.0),
            ShowNoticePanelCheck.IsChecked == true,
            ShowSquadsPanelCheck.IsChecked == true,
            ShowMissionPanelCheck.IsChecked == true,
            ShowMembersPanelCheck.IsChecked == true,
            OverlayThemeBox.SelectedIndex switch
            {
                1 => OverlayVisualTheme.Anvil,
                2 => OverlayVisualTheme.Drake,
                _ => OverlayVisualTheme.Default
            });

        SaveCurrentConfig();
        RefreshOverlayWindow();
    }

    private void OverlayOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OverlayOpacityValueText is not null)
        {
            OverlayOpacityValueText.Text = $"{Math.Round(OverlayOpacitySlider.Value)}%";
        }

        if (_isLoadingSettings)
        {
            return;
        }

        OverlaySetting_Changed(sender, new RoutedEventArgs());
    }

    private void OverlayThemeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        OverlaySetting_Changed(sender, new RoutedEventArgs());
    }

    private void OverlayPresetBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingSettings ||
            OverlayPresetBox.SelectedItem is not ComboBoxItem { Tag: string preset })
        {
            return;
        }

        LoadOverlayPreset(preset);
    }

    private void CallsignBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        if (!IsLoggedIn)
        {
            return;
        }

        var limited = LimitCallsign(CallsignBox.Text);
        if (!CallsignBox.Text.Equals(limited, StringComparison.Ordinal))
        {
            var caret = Math.Min(CallsignBox.CaretIndex, limited.Length);
            CallsignBox.Text = limited;
            CallsignBox.CaretIndex = caret;
            return;
        }

        _callsign = string.IsNullOrWhiteSpace(limited)
            ? null
            : limited.Trim();
        SaveCurrentConfig();
        RenderState();
        _ = UpdateProfileAsync();
        _ = PushLocalSnapshotAsync(silent: true);
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private static string LimitCallsign(string value)
    {
        var total = 0;
        var builder = new System.Text.StringBuilder();
        foreach (var character in value)
        {
            var weight = IsCjk(character) ? 2 : 1;
            if (total + weight > 10)
            {
                break;
            }

            builder.Append(character);
            total += weight;
        }

        return builder.ToString();
    }

    private static bool IsCjk(char character)
    {
        return (character >= '\u4E00' && character <= '\u9FFF') ||
               (character >= '\u3400' && character <= '\u4DBF') ||
               (character >= '\uF900' && character <= '\uFAFF');
    }

    private void MySquadDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || MySquadDescriptionBox is null)
        {
            return;
        }

        var squad = _selectedSquad;
        if (squad is null)
        {
            return;
        }

        squad.Description = MySquadDescriptionBox.Text.Trim();
        squad.RefreshComputed();
        RefreshOverlayWindow();
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private void CreateSquad_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (_joinedSquad is not null &&
            _joinedSquad.Commander.Equals(FormatCommanderName(_callsign, _localPlayer), StringComparison.OrdinalIgnoreCase))
        {
            JoinSquadHintText.Text = "每人只能创建一个小队";
            return;
        }

        CreateSquadPanel.Visibility = Visibility.Visible;
        CreateSquadValidationText.Text = "";
        CreateSquadNameBox.Text = "";
        CreateSquadTypeBox.SelectedIndex = 0;
        CreateSquadCustomTypeBox.Text = "";
        CreateSquadCustomTypeBox.Visibility = Visibility.Collapsed;
        CreateSquadDescriptionBox.Text = "No squad briefing yet.";
        CreateSquadNameBox.Focus();
    }

    private void CreateSquadCancel_Click(object sender, RoutedEventArgs e)
    {
        CreateSquadPanel.Visibility = Visibility.Collapsed;
    }

    private void CreateSquadConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (_joinedSquad is not null &&
            _joinedSquad.Commander.Equals(FormatCommanderName(_callsign, _localPlayer), StringComparison.OrdinalIgnoreCase))
        {
            CreateSquadValidationText.Text = "每人只能创建一个小队。";
            return;
        }

        var name = CreateSquadNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            CreateSquadValidationText.Text = "请输入小队名称。";
            return;
        }

        if (_squads.Any(squad => squad.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            CreateSquadValidationText.Text = "已经存在同名小队。";
            return;
        }

        var squadType = GetCreateSquadType();
        if (string.IsNullOrWhiteSpace(squadType))
        {
            CreateSquadValidationText.Text = "请输入自定义小队类型。";
            return;
        }

        if (IsCustomSquadTypeSelected() && !IsValidChineseText(squadType, maxLength: 5))
        {
            CreateSquadValidationText.Text = "自定义类型仅限 5 个中文字以内。";
            return;
        }

        var squad = new SquadRow
        {
            Name = name,
            Icon = GetInitials(name),
            Commander = FormatCommanderName(_callsign, _localPlayer),
            Type = squadType,
            Description = string.IsNullOrWhiteSpace(CreateSquadDescriptionBox.Text)
                ? "No squad briefing yet."
                : CreateSquadDescriptionBox.Text.Trim()
        };

        _squads.Add(squad);
        _selectedSquad = squad;
        _joinedSquad = squad;
        SquadSelectionList.SelectedItem = squad;
        CreateSquadPanel.Visibility = Visibility.Collapsed;
        JoinSquadHintText.Text = $"已创建并加入 {squad.Name}";
        AddFleetLog("成员", "创建小队", $"{FormatCommanderName(_callsign, _localPlayer)} 创建 {squad.Name}");
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();
    }

    private void CreateSquadTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CreateSquadCustomTypeBox is null)
        {
            return;
        }

        CreateSquadCustomTypeBox.Visibility = IsCustomSquadTypeSelected()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private string GetCreateSquadType()
    {
        return IsCustomSquadTypeSelected()
            ? CreateSquadCustomTypeBox.Text.Trim()
            : (CreateSquadTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "突击";
    }

    private bool IsCustomSquadTypeSelected()
    {
        return string.Equals(
            (CreateSquadTypeBox.SelectedItem as ComboBoxItem)?.Content?.ToString(),
            "自定义",
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidChineseText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            return false;
        }

        return value.All(character => character >= '\u4e00' && character <= '\u9fff');
    }

    private void JoinSquad_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("加入小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (_selectedSquad is null)
        {
            JoinSquadHintText.Text = "请选择一个小队";
            return;
        }

        _joinedSquad = _selectedSquad;
        JoinSquadHintText.Text = $"已加入 {_joinedSquad.Name}";
        AddFleetLog("成员", "加入小队", $"{FormatCommanderName(_callsign, _localPlayer)} 加入 {_joinedSquad.Name}");
        RenderState();
        _ = PushLocalSnapshotAsync(silent: true);
    }

    private void SquadSelectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedSquad = SquadSelectionList.SelectedItem as SquadRow;
        JoinSquadButton.IsEnabled = _selectedSquad is not null;
        JoinSquadHintText.Text = _selectedSquad is null ? "请选择一个小队" : "可以加入所选小队";
        RenderMySquad();
    }

    private void OpenPublishTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布任务需要先登录星海舰桥账号。"))
        {
            return;
        }

        OpenPublishTaskPanel();
    }

    private void OpenPublishRallyButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布集结点需要先登录星海舰桥账号。"))
        {
            return;
        }

        OpenPublishTaskPanel(rallyOnly: true);
    }

    private void OpenPublishTaskPanel(bool rallyOnly = false, bool editCurrent = false)
    {
        PublishTaskValidationText.Text = "";
        PublishTaskObjectiveBox.Text = editCurrent ? _fleetCurrentTaskTitle : rallyOnly ? "舰队集结" : "战术打击";
        PublishTaskBriefBox.Text = editCurrent ? _fleetCurrentTaskBrief : rallyOnly
            ? "舰队集结，等待进一步指令。"
            : "简要说明任务目标、行动范围或注意事项。";
        PublishTaskRallyCheck.IsChecked = editCurrent ? !string.IsNullOrWhiteSpace(_fleetCurrentTaskRally) : rallyOnly;
        PublishTaskRallyBox.Text = editCurrent ? NormalizeOptionalField(_fleetCurrentTaskRally) : "未指定";
        PublishTaskShipRequiredCheck.IsChecked = editCurrent && !string.IsNullOrWhiteSpace(_fleetCurrentTaskShip);
        PublishTaskShipBox.Text = editCurrent ? NormalizeOptionalField(_fleetCurrentTaskShip) : "未指定";
        PublishTaskEmailCallCheck.IsChecked = editCurrent ? _fleetCurrentTaskEmailCall : false;

        PublishTaskSquadList.Items.Clear();
        foreach (var squad in _squads)
        {
            PublishTaskSquadList.Items.Add(squad.Name);
        }

        if (editCurrent && !string.IsNullOrWhiteSpace(_fleetCurrentTaskParticipants))
        {
            var selectedSquads = _fleetCurrentTaskParticipants
                .Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < PublishTaskSquadList.Items.Count; index++)
            {
                if (PublishTaskSquadList.Items[index] is { } item &&
                    selectedSquads.Contains(item.ToString() ?? ""))
                {
                    PublishTaskSquadList.SelectedItems.Add(item);
                }
            }
        }

        PublishTaskPanel.Visibility = Visibility.Visible;
    }

    private void CancelPublishTaskButton_Click(object sender, RoutedEventArgs e)
    {
        PublishTaskPanel.Visibility = Visibility.Collapsed;
    }

    private void PublishFleetTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布任务需要先登录星海舰桥账号。"))
        {
            return;
        }

        var objective = PublishTaskObjectiveBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(objective))
        {
            PublishTaskValidationText.Text = "请输入任务目标。";
            return;
        }

        var selectedSquads = PublishTaskSquadList.SelectedItems
            .Cast<object>()
            .Select(item => item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        var participants = selectedSquads.Length == 0
            ? "全员参与"
            : string.Join("、", selectedSquads);
        var brief = PublishTaskBriefBox.Text.Trim();
        var rallyEnabled = PublishTaskRallyCheck.IsChecked == true;
        var rally = PublishTaskRallyBox.Text.Trim();
        var shipRequired = PublishTaskShipRequiredCheck.IsChecked == true;
        var ship = PublishTaskShipBox.Text.Trim();
        var emailCall = PublishTaskEmailCallCheck.IsChecked == true;

        _fleetCurrentTaskTitle = objective;
        _fleetCurrentTaskBrief = NormalizeOptionalField(brief);
        _fleetCurrentTaskParticipants = participants;
        _fleetCurrentTaskRally = rallyEnabled ? NormalizeOptionalField(rally) : "";
        _fleetCurrentTaskShip = shipRequired ? NormalizeOptionalField(ship) : "";
        _fleetCurrentTaskEmailCall = emailCall;
        _fleetCurrentTaskTime = DateTime.Now;
        _fleetCurrentTaskNoticeRevision++;
        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskHistoryKey))
        {
            _fleetCurrentTaskHistoryKey = Guid.NewGuid().ToString("N");
        }

        UpsertCurrentTaskHistory("进行中");
        AddFleetLog("任务", "任务发布", $"{objective} / {participants}");
        _selectedFleetInfoPanel = FleetInfoPanelKind.CurrentTask;
        PublishTaskPanel.Visibility = Visibility.Collapsed;
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
        if (emailCall)
        {
            _ = SendFleetEmailNotificationAsync(
                $"StarBridge 舰队任务：{objective}",
                $"""
舰队：{_fleetName} ({_fleetCode})
任务目标：{objective}
任务简述：{NormalizeOptionalField(_fleetCurrentTaskBrief)}
参与范围：{participants}
集结点：{NormalizeOptionalField(_fleetCurrentTaskRally)}
指定舰船：{NormalizeOptionalField(_fleetCurrentTaskShip)}
发布时间：{_fleetCurrentTaskTime:yyyy-MM-dd HH:mm}
""",
                silent: true);
        }

        AppendOutput($"Fleet task published: {objective}");
    }

    private void EditCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("编辑任务需要先登录。"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        OpenPublishTaskPanel(editCurrent: true);
    }

    private void CompleteCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("完成任务需要先登录。"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        UpsertCurrentTaskHistory("已完成");
        AddFleetLog("任务", "任务完成", _fleetCurrentTaskTitle);
        ClearCurrentTask();
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private void DeleteCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("删除任务需要先登录。"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        UpsertCurrentTaskHistory("已删除");
        AddFleetLog("任务", "任务删除", _fleetCurrentTaskTitle);
        ClearCurrentTask();
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private void RenotifyCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("再次通知需要先登录。"))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        _selectedFleetInfoPanel = FleetInfoPanelKind.CurrentTask;
        _fleetCurrentTaskNoticeRevision++;
        AddFleetLog("任务", "再次通知", _fleetCurrentTaskTitle);
        RefreshFleetInfoPanel();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        _ = SendFleetEmailNotificationAsync(
            $"StarBridge 舰队任务提醒：{_fleetCurrentTaskTitle}",
            $"""
舰队：{_fleetName} ({_fleetCode})
任务目标：{_fleetCurrentTaskTitle}
任务简述：{NormalizeOptionalField(_fleetCurrentTaskBrief)}
参与范围：{NormalizeOptionalField(_fleetCurrentTaskParticipants)}
集结点：{NormalizeOptionalField(_fleetCurrentTaskRally)}
指定舰船：{NormalizeOptionalField(_fleetCurrentTaskShip)}
""",
            silent: true);
        AppendOutput($"Fleet task re-notified: {_fleetCurrentTaskTitle}");
    }

    private void OpenFleetNoticeEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("编辑舰队公告需要先登录。"))
        {
            return;
        }

        OpenFleetNoticeEditor();
    }

    private void OpenActionPlanEditorButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建行动计划需要先登录。"))
        {
            return;
        }

        OpenActionPlanEditor();
    }

    private void ClearCurrentTask()
    {
        _fleetCurrentTaskTitle = "";
        _fleetCurrentTaskBrief = "";
        _fleetCurrentTaskParticipants = "";
        _fleetCurrentTaskRally = "";
        _fleetCurrentTaskShip = "";
        _fleetCurrentTaskEmailCall = false;
        _fleetCurrentTaskTime = null;
        _fleetCurrentTaskHistoryKey = "";
        _fleetCurrentTaskNoticeRevision++;
    }

    private void UpsertCurrentTaskHistory(string status)
    {
        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskHistoryKey))
        {
            _fleetCurrentTaskHistoryKey = Guid.NewGuid().ToString("N");
        }

        var row = new FleetTaskHistoryRow(
            _fleetCurrentTaskHistoryKey,
            _fleetCurrentTaskTitle,
            NormalizeOptionalField(_fleetCurrentTaskBrief),
            status,
            $"参与范围 / {NormalizeOptionalField(_fleetCurrentTaskParticipants)}",
            string.IsNullOrWhiteSpace(_fleetCurrentTaskRally) ? "集结点 / 未发布" : $"集结点 / {_fleetCurrentTaskRally}",
            string.IsNullOrWhiteSpace(_fleetCurrentTaskShip) ? "指定舰船 / 无" : $"指定舰船 / {_fleetCurrentTaskShip}",
            (_fleetCurrentTaskTime ?? DateTime.Now).ToString("yyyy-MM-dd HH:mm"));

        var existingIndex = _fleetTaskHistory
            .Select((task, index) => new { task, index })
            .FirstOrDefault(item => item.task.Key.Equals(_fleetCurrentTaskHistoryKey, StringComparison.OrdinalIgnoreCase))
            ?.index;
        if (existingIndex is int index)
        {
            _fleetTaskHistory[index] = row;
        }
        else
        {
            _fleetTaskHistory.Insert(0, row);
        }
    }

    private static string NormalizeOptionalField(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "未指定" : value;
    }

    private void CustomTitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowMaximize();
            return;
        }

        DragMove();
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowMaximize();
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
        UpdateMaximizeButtonText();
    }

    private void UpdateMaximizeButtonText()
    {
        if (MaximizeWindowButton is not null)
        {
            MaximizeWindowButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        }
    }

    private static string GetAppVersion()
    {
        return Assembly.GetExecutingAssembly().GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "0.3.1";
    }

    private void FleetActionPlanCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectedFleetInfoPanel == FleetInfoPanelKind.ActionPlan)
        {
            if (!EnsureLoggedIn("编辑行动计划需要先登录。"))
            {
                return;
            }

            OpenActionPlanEditor();
        }
        else if (_selectedFleetInfoPanel == FleetInfoPanelKind.CurrentTask)
        {
            OpenCurrentTaskDetail();
        }
        else if (_selectedFleetInfoPanel == FleetInfoPanelKind.Notice && IsCurrentUserFleetCommander())
        {
            OpenFleetNoticeEditor();
        }
    }

    private void OpenFleetNoticeEditor()
    {
        FleetNoticeTitleBox.Text = _fleetNoticeTitle;
        FleetNoticeContentBox.Text = _fleetNoticeContent;
        FleetNoticeValidationText.Text = "";
        FleetNoticeEditorPanel.Visibility = Visibility.Visible;
    }

    private void CancelFleetNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        FleetNoticeEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void PublishFleetNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布舰队公告需要先登录。"))
        {
            return;
        }

        var title = FleetNoticeTitleBox.Text.Trim();
        var content = FleetNoticeContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            FleetNoticeValidationText.Text = "请输入公告标题。";
            return;
        }

        _fleetNoticeTitle = title;
        _fleetNoticeContent = NormalizeOptionalField(content);
        AddFleetLog("公告", "公告更新", title);
        _selectedFleetInfoPanel = FleetInfoPanelKind.Notice;
        FleetNoticeEditorPanel.Visibility = Visibility.Collapsed;
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private void SaveFleetDescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("修改舰队介绍需要先登录。"))
        {
            return;
        }

        if (!IsCurrentUserFleetCommander())
        {
            FleetDescriptionStatusText.Text = "只有舰队指挥官可以修改舰队介绍。";
            return;
        }

        _fleetDescription = NormalizeOptionalField(FleetDescriptionEditBox.Text);
        FleetDescriptionStatusText.Text = "舰队介绍已保存。";
        AddFleetLog("舰队", "舰队介绍更新", _fleetDescription);
        RefreshFleetHeader();
        RefreshTaskManagementPanel();
        SaveCurrentConfig();
        _ = PushFleetDirectoryAsync(silent: true);
    }

    private void OpenCurrentTaskDetail()
    {
        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        CurrentTaskDetailTitleText.Text = _fleetCurrentTaskTitle;
        CurrentTaskDetailBriefText.Text = _fleetCurrentTaskBrief;
        CurrentTaskDetailParticipantsText.Text = $"参与范围 / {_fleetCurrentTaskParticipants}";
        CurrentTaskDetailRallyText.Text = string.IsNullOrWhiteSpace(_fleetCurrentTaskRally)
            ? "集结点 / 未发布"
            : $"集结点 / {_fleetCurrentTaskRally}";
        CurrentTaskDetailShipText.Text = string.IsNullOrWhiteSpace(_fleetCurrentTaskShip)
            ? "指定舰船 / 无"
            : $"指定舰船 / {_fleetCurrentTaskShip}";
        CurrentTaskDetailTimeText.Text = _fleetCurrentTaskTime is null
            ? ""
            : $"发布时间 / {_fleetCurrentTaskTime:yyyy-MM-dd HH:mm}";
        CurrentTaskDetailPanel.Visibility = Visibility.Visible;
    }

    private void CloseCurrentTaskDetailButton_Click(object sender, RoutedEventArgs e)
    {
        CurrentTaskDetailPanel.Visibility = Visibility.Collapsed;
    }

    private void FleetNoticeInfoTabButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedFleetInfoPanel = FleetInfoPanelKind.Notice;
        RefreshFleetInfoPanel();
    }

    private void FleetCurrentTaskInfoTabButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedFleetInfoPanel = FleetInfoPanelKind.CurrentTask;
        RefreshFleetInfoPanel();
    }

    private void FleetActionPlanInfoTabButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedFleetInfoPanel = FleetInfoPanelKind.ActionPlan;
        RefreshFleetInfoPanel();
    }

    private void OpenActionPlanEditor()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        ActionPlanTitleBox.Text = _fleetActionTitle;
        ActionPlanContentBox.Text = _fleetActionContent;
        var start = _fleetActionStartTime ?? DateTime.Now.AddHours(1);
        ActionPlanDatePicker.SelectedDate = start.Date;
        ActionPlanTimeBox.Text = start.ToString("HH:mm");
        ActionPlanNotifyFleetCheck.IsChecked = _fleetActionNotifyMembers;
        ActionPlanValidationText.Text = "";
        ActionPlanEditorPanel.Visibility = Visibility.Visible;
    }

    private void CancelActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        ActionPlanEditorPanel.Visibility = Visibility.Collapsed;
    }

    private void PublishActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布行动计划需要先登录。"))
        {
            return;
        }

        var title = ActionPlanTitleBox.Text.Trim();
        var content = ActionPlanContentBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            ActionPlanValidationText.Text = "请输入行动标题。";
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            ActionPlanValidationText.Text = "请输入行动内容。";
            return;
        }

        if (!TryReadActionPlanStartTime(out var startTime, out var message))
        {
            ActionPlanValidationText.Text = message;
            return;
        }

        var plan = new FleetActionPlanRow(
            Guid.NewGuid().ToString("N"),
            title,
            content,
            startTime,
            ActionPlanNotifyFleetCheck.IsChecked == true);
        _fleetActionPlans.Add(plan);
        AddFleetLog("计划", "行动计划创建", $"{title} / {startTime:yyyy-MM-dd HH:mm}");
        SelectFeaturedActionPlan();
        _selectedFleetInfoPanel = FleetInfoPanelKind.ActionPlan;
        ActionPlanEditorPanel.Visibility = Visibility.Collapsed;
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshOverlayWindow();
        _ = PushFleetDirectoryAsync(silent: true);
        if (plan.NotifyMembers)
        {
            _ = SendFleetEmailNotificationAsync(
                $"StarBridge 行动计划：{title}",
                $"""
舰队：{_fleetName} ({_fleetCode})
行动标题：{title}
行动内容：{content}
行动时间：{startTime:yyyy-MM-dd HH:mm}
""",
                silent: true);
        }
    }

    private bool TryReadActionPlanStartTime(out DateTime startTime, out string message)
    {
        startTime = default;
        message = "";
        if (ActionPlanDatePicker.SelectedDate is not { } selectedDate)
        {
            message = "请选择行动日期。";
            return false;
        }

        if (!IsValidTime24(ActionPlanTimeBox.Text.Trim()))
        {
            message = "行动时间必须使用 24 小时制 HH:mm。";
            return false;
        }

        var hour = int.Parse(ActionPlanTimeBox.Text[..2]);
        var minute = int.Parse(ActionPlanTimeBox.Text[3..]);
        startTime = selectedDate.Date.AddHours(hour).AddMinutes(minute);
        var now = DateTime.Now;
        if (startTime < now)
        {
            message = "行动时间不能早于当前时间。";
            return false;
        }

        if (startTime > now.AddDays(7))
        {
            message = "行动时间只能设定在现在开始的 7 天之内。";
            return false;
        }

        return true;
    }

    private void JoinFleetActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("预约行动计划需要先登录。"))
        {
            return;
        }

        e.Handled = true;
        JoinActionPlanTitleText.Text = string.IsNullOrWhiteSpace(_fleetActionTitle)
            ? "行动计划"
            : _fleetActionTitle;
        JoinActionNotifyCheck.IsChecked = _joinActionNotifyMe;
        JoinActionPlanPanel.Visibility = Visibility.Visible;
    }

    private void CancelJoinActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        JoinActionPlanPanel.Visibility = Visibility.Collapsed;
    }

    private void ConfirmJoinActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("预约行动计划需要先登录。"))
        {
            return;
        }

        _joinActionNotifyMe = JoinActionNotifyCheck.IsChecked == true;
        JoinSelectedActionPlan();
        JoinActionPlanPanel.Visibility = Visibility.Collapsed;
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        AppendOutput(_joinActionNotifyMe
            ? "Action joined. Email reminder requested for 5 minutes before start."
            : "Action joined.");
    }

    private void JoinSelectedActionPlan()
    {
        if (string.IsNullOrWhiteSpace(_selectedActionPlanId))
        {
            return;
        }

        var plan = _fleetActionPlans.FirstOrDefault(plan =>
            plan.Id.Equals(_selectedActionPlanId, StringComparison.OrdinalIgnoreCase));
        if (plan is null || !_joinedActionPlanIds.Add(plan.Id))
        {
            return;
        }

        var gameName = string.IsNullOrWhiteSpace(_localPlayer) ? "Unknown" : _localPlayer!;
        var callsign = string.IsNullOrWhiteSpace(_callsign) ? gameName : _callsign!;
        plan.Participants.Add(new ActionPlanParticipantRow(
            callsign,
            gameName,
            _avatarPath,
            GetInitials(gameName)));
        plan.RefreshParticipantSummary();
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var croppedPath = ChooseAndCropImage("Choose player avatar", "player-avatar.png");
        if (croppedPath is null)
        {
            return;
        }

        _avatarPath = croppedPath;
        SaveCurrentConfig();
        LoadAvatarPreview();
        RenderState();
        AppendOutput("Profile avatar updated.");
    }

    private void ImportHangarButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("导入舰船数据库需要先登录。"))
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 RSI 官网机库页面快照",
            Filter = "Hangar snapshot (*.html;*.htm;*.txt;*.csv)|*.html;*.htm;*.txt;*.csv|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        string content;
        try
        {
            content = File.ReadAllText(dialog.FileName);
        }
        catch (Exception exception)
        {
            ShipDatabaseStatusText.Text = $"读取失败：{exception.Message}";
            return;
        }

        var result = HangarShipImporter.ImportOfficialHangarSnapshot(content, _language);
        _ownedShips.Clear();
        foreach (var ship in result.Ships)
        {
            _ownedShips.Add(ship);
        }

        SaveOwnedShips();
        UpdateShipDatabaseSummary(result.MatchedCodes, result.MatchedNames);
        AppendOutput($"Ship database imported. ships={_ownedShips.Count}, codeMatches={result.MatchedCodes}, nameMatches={result.MatchedNames}");
    }

    private void OpenHangarReaderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("读取官网机库需要先登录。"))
        {
            return;
        }

        var reader = new HangarReaderWindow(_language)
        {
            Owner = this
        };

        if (reader.ShowDialog() != true)
        {
            return;
        }

        _ownedShips.Clear();
        foreach (var ship in reader.ImportedShips)
        {
            _ownedShips.Add(ship);
        }

        SaveOwnedShips();
        UpdateShipDatabaseSummary(reader.ImportedShips.Count, reader.ImportedShips.Count);
        AppendOutput($"Ship database imported from WebView2 reader. ships={_ownedShips.Count}");
    }

    private void ClearShipDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("清空舰船数据库需要先登录。"))
        {
            return;
        }

        _ownedShips.Clear();
        SaveOwnedShips();
        UpdateShipDatabaseSummary();
        AppendOutput("Ship database cleared.");
    }

    private void ChooseFleetLogo_Click(object sender, RoutedEventArgs e)
    {
        if (_hasFleet && !IsCurrentUserFleetCommander())
        {
            AppendOutput("Only the fleet commander can update the fleet logo.");
            return;
        }

        var croppedPath = ChooseAndCropImage("Choose fleet logo", "fleet-logo.png");
        if (croppedPath is null)
        {
            return;
        }

        _fleetLogoPath = croppedPath;
        LoadCreateFleetLogoPreview();
        LoadFleetHeaderLogoPreview();
        AppendOutput("Fleet logo updated.");
    }

    private void FleetHeaderLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hasFleet || !IsCurrentUserFleetCommander())
        {
            return;
        }

        ChooseFleetLogo_Click(sender, new RoutedEventArgs());
    }

    private void FleetSquadBanner_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindVisualParent<System.Windows.Controls.Button>(e.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        if ((sender as FrameworkElement)?.Tag is not SquadRow squad)
        {
            return;
        }

        squad.IsExpanded = !squad.IsExpanded;
    }

    private void ChooseSquadEmblem_Click(object sender, RoutedEventArgs e)
    {
        var squad = (sender as FrameworkElement)?.Tag as SquadRow ?? _squads.FirstOrDefault();
        ChooseSquadEmblem(squad);
    }

    private void MySquadEmblem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ChooseSquadEmblem(_selectedSquad);
    }

    private void ChooseSquadEmblem(SquadRow? squad)
    {
        if (squad is null)
        {
            return;
        }

        var safeName = string.Concat(squad.Name.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var croppedPath = ChooseAndCropImage("Choose squad emblem", $"squad-{safeName}-emblem.png");
        if (croppedPath is null)
        {
            return;
        }

        squad.EmblemPath = croppedPath;
        squad.RefreshComputed();
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();
        AppendOutput($"Squad emblem updated: {squad.Name}");
    }

    private string? ChooseAndCropImage(string title, string fileName)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        var cropWindow = new SquadEmblemCropWindow(dialog.FileName)
        {
            Owner = this
        };

        if (cropWindow.ShowDialog() != true || cropWindow.CroppedImage is null)
        {
            return null;
        }

        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarBridge",
            "Images");
        Directory.CreateDirectory(directory);

        var outputPath = Path.Combine(directory, fileName);
        using var stream = File.Create(outputPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropWindow.CroppedImage));
        encoder.Save(stream);
        return outputPath;
    }

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
            {
                return target;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }

    private void RenderOverlayEditor()
    {
        if (OverlayEditorCanvas is null)
        {
            return;
        }

        OverlayEditorCanvas.Children.Clear();

        foreach (var item in _overlayLayout.Where(IsOverlayEditorItemVisible))
        {
            var panel = CreateOverlayEditorPanel(item);
            Canvas.SetLeft(panel, item.X * OverlayEditorCanvas.Width);
            Canvas.SetTop(panel, item.Y * OverlayEditorCanvas.Height);
            panel.Width = item.Width * OverlayEditorCanvas.Width;
            panel.Height = item.Height * OverlayEditorCanvas.Height;
            OverlayEditorCanvas.Children.Add(panel);
        }
    }

    private bool IsOverlayEditorItemVisible(OverlayLayoutItem item)
    {
        return item.Key switch
        {
            "Notice" => _overlaySettings.ShowNotice,
            "Squads" => _overlaySettings.ShowSquads,
            "Mission" => _overlaySettings.ShowMission,
            "Members" => _overlaySettings.ShowMembers,
            _ => true
        };
    }

    private FrameworkElement CreateOverlayEditorPanel(OverlayLayoutItem item)
    {
        var border = new Border
        {
            Tag = item,
            Background = new SolidColorBrush(Color.FromArgb(170, 5, 10, 17)),
            BorderBrush = item.Brush,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(10),
            Cursor = Cursors.SizeAll,
            MinWidth = 80,
            MinHeight = 50
        };

        var title = new TextBlock
        {
            Text = item.Title,
            Foreground = item.Brush,
            FontWeight = FontWeights.SemiBold,
            FontSize = 18
        };
        var hint = new TextBlock
        {
            Text = _language == "zh" ? "拖拽 / 缩放" : "drag / resize",
            Foreground = Brushes.Gray,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(title);
        content.Children.Add(hint);
        Grid.SetRow(hint, 1);

        var handle = new Border
        {
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = item.Brush,
            Cursor = Cursors.SizeNWSE,
            Opacity = 0.9
        };
        handle.MouseLeftButtonDown += OverlayResize_MouseLeftButtonDown;

        var wrapper = new Grid();
        wrapper.Children.Add(content);
        wrapper.Children.Add(handle);
        border.Child = wrapper;

        border.MouseLeftButtonDown += OverlayPanel_MouseLeftButtonDown;
        border.MouseMove += OverlayPanel_MouseMove;
        border.MouseLeftButtonUp += OverlayPanel_MouseLeftButtonUp;
        return border;
    }

    private void OverlayPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not OverlayLayoutItem item)
        {
            return;
        }

        _activeOverlayItem = item;
        _activeOverlayEditorElement = element;
        _isOverlayResize = false;
        _lastOverlayEditorPoint = e.GetPosition(OverlayEditorCanvas);
        element.CaptureMouse();
        e.Handled = true;
    }

    private void OverlayResize_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement handle ||
            FindParentEditorPanel(handle) is not FrameworkElement panel ||
            panel.Tag is not OverlayLayoutItem item)
        {
            return;
        }

        _activeOverlayItem = item;
        _activeOverlayEditorElement = panel;
        _isOverlayResize = true;
        _lastOverlayEditorPoint = e.GetPosition(OverlayEditorCanvas);
        panel.CaptureMouse();
        e.Handled = true;
    }

    private void OverlayPanel_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_activeOverlayItem is null || _activeOverlayEditorElement is null || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var point = e.GetPosition(OverlayEditorCanvas);
        var dx = (point.X - _lastOverlayEditorPoint.X) / OverlayEditorCanvas.Width;
        var dy = (point.Y - _lastOverlayEditorPoint.Y) / OverlayEditorCanvas.Height;

        if (_isOverlayResize)
        {
            _activeOverlayItem.Width = Math.Clamp(_activeOverlayItem.Width + dx, 0.06, 1 - _activeOverlayItem.X);
            _activeOverlayItem.Height = Math.Clamp(_activeOverlayItem.Height + dy, 0.05, 1 - _activeOverlayItem.Y);
        }
        else
        {
            _activeOverlayItem.X = Math.Clamp(_activeOverlayItem.X + dx, 0, 1 - _activeOverlayItem.Width);
            _activeOverlayItem.Y = Math.Clamp(_activeOverlayItem.Y + dy, 0, 1 - _activeOverlayItem.Height);
        }

        _lastOverlayEditorPoint = point;
        Canvas.SetLeft(_activeOverlayEditorElement, _activeOverlayItem.X * OverlayEditorCanvas.Width);
        Canvas.SetTop(_activeOverlayEditorElement, _activeOverlayItem.Y * OverlayEditorCanvas.Height);
        _activeOverlayEditorElement.Width = _activeOverlayItem.Width * OverlayEditorCanvas.Width;
        _activeOverlayEditorElement.Height = _activeOverlayItem.Height * OverlayEditorCanvas.Height;
        e.Handled = true;
    }

    private void OverlayPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _activeOverlayEditorElement?.ReleaseMouseCapture();
        _activeOverlayItem = null;
        _activeOverlayEditorElement = null;
        _isOverlayResize = false;
        SaveCurrentConfig();
        RefreshOverlayWindow();
    }

    private static FrameworkElement? FindParentEditorPanel(DependencyObject element)
    {
        var current = VisualTreeHelper.GetParent(element);
        while (current is not null)
        {
            if (current is Border { Tag: OverlayLayoutItem } border)
            {
                return border;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void LoadOverlayPreset(string preset)
    {
        _activeOverlayPreset = NormalizeOverlayPreset(preset);
        _overlaySettings = OverlayDisplaySettings.Parse(
            DesktopAppConfig.LoadOverlayPresetSettings(_activeOverlayPreset) ??
            CreateDefaultOverlaySettings(_activeOverlayPreset).Serialize());

        LoadOverlayLayout(
            DesktopAppConfig.LoadOverlayPresetLayout(_activeOverlayPreset) ??
            SerializeOverlayLayout(CreateDefaultOverlayLayout(_activeOverlayPreset)));

        _isLoadingSettings = true;
        ApplyOverlaySettingsToControls();
        _isLoadingSettings = false;
        RenderOverlayEditor();
        SaveCurrentConfig();
        RefreshOverlayWindow();
        AppendOutput($"Overlay preset loaded: {_activeOverlayPreset}.");
    }

    private static string NormalizeOverlayPreset(string? preset)
    {
        return preset?.Trim().ToLowerInvariant() switch
        {
            OverlayPresetCompact => OverlayPresetCompact,
            OverlayPresetCommand => OverlayPresetCommand,
            OverlayPresetCustom => OverlayPresetCustom,
            _ => OverlayPresetCombat
        };
    }

    private static OverlayDisplaySettings CreateDefaultOverlaySettings(string preset)
    {
        return NormalizeOverlayPreset(preset) switch
        {
            OverlayPresetCompact => OverlayDisplaySettings.Default with
            {
                HideMissionWhenIdle = true,
                HideOfflineMembers = true,
                HideSquadIcons = true,
                Opacity = 0.78,
                ShowNotice = true,
                ShowSquads = true,
                ShowMission = true,
                ShowMembers = true
            },
            OverlayPresetCommand => OverlayDisplaySettings.Default with
            {
                HideMissionWhenIdle = false,
                HideOfflineMembers = false,
                HideSquadIcons = false,
                Opacity = 0.9,
                ShowNotice = true,
                ShowSquads = true,
                ShowMission = true,
                ShowMembers = true
            },
            _ => OverlayDisplaySettings.Default
        };
    }

    private void LoadOverlayLayout(string? serialized)
    {
        _overlayLayout.Clear();
        var parsed = OverlayLayoutItem.ParseMany(serialized).ToArray();
        _overlayLayout.AddRange(parsed.Length == 0 ? CreateDefaultOverlayLayout(_activeOverlayPreset) : parsed);
    }

    private string SerializeOverlayLayout()
    {
        return SerializeOverlayLayout(_overlayLayout);
    }

    private static string SerializeOverlayLayout(IEnumerable<OverlayLayoutItem> layout)
    {
        return string.Join(";", layout.Select(item => item.Serialize()));
    }

    private void ApplyOverlaySettingsToControls()
    {
        if (OverlayPresetBox is not null)
        {
            OverlayPresetBox.SelectedIndex = _activeOverlayPreset switch
            {
                OverlayPresetCompact => 1,
                OverlayPresetCommand => 2,
                OverlayPresetCustom => 3,
                _ => 0
            };
        }

        HideMissionWhenIdleCheck.IsChecked = _overlaySettings.HideMissionWhenIdle;
        HideOfflineMembersCheck.IsChecked = _overlaySettings.HideOfflineMembers;
        HideSquadIconsCheck.IsChecked = _overlaySettings.HideSquadIcons;
        TrayModeCheck.IsChecked = _overlaySettings.EnableTrayMode;
        ShowNoticePanelCheck.IsChecked = _overlaySettings.ShowNotice;
        ShowSquadsPanelCheck.IsChecked = _overlaySettings.ShowSquads;
        ShowMissionPanelCheck.IsChecked = _overlaySettings.ShowMission;
        ShowMembersPanelCheck.IsChecked = _overlaySettings.ShowMembers;
        OverlayThemeBox.SelectedIndex = _overlaySettings.Theme switch
        {
            OverlayVisualTheme.Anvil => 1,
            OverlayVisualTheme.Drake => 2,
            _ => 0
        };
        OverlayOpacitySlider.Value = Math.Clamp(_overlaySettings.Opacity, 0.15, 1.0) * 100.0;
        OverlayOpacityValueText.Text = $"{Math.Round(OverlayOpacitySlider.Value)}%";
        ShowCallsignOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignOnly;
        ShowGameNameOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.GameNameOnly;
        ShowCallsignAndNameRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignAndGameName;
    }

    private void ApplyLanguageToControls()
    {
        var zh = _language == "zh";
        Title = zh ? "星海舰桥" : "Star Bridge";

        LanguageBox.SelectionChanged -= LanguageBox_SelectionChanged;
        LanguageBox.SelectedIndex = zh ? 1 : 0;
        LanguageBox.SelectionChanged += LanguageBox_SelectionChanged;

        FindFleetNavText.Text = zh ? "寻找舰队" : "Find Fleet";
        MyFleetNavText.Text = zh ? "我的舰队" : "My Fleet";
        MySquadNavText.Text = zh ? "我的小队" : "My Squad";
        OverlayNavText.Text = "Overlay";
        PersonalNavText.Text = zh ? "个人" : "Personal";
        FindFleetTab.Header = zh ? "寻找舰队" : "Find Fleet";
        FindFleetTitleText.Text = zh ? "寻找舰队" : "Find Fleet";
        FindFleetPlaceholderText.Text = zh
            ? "从 Relay Server 拉取舰队目录，选择一个舰队加入后即可进行第一阶段同步测试。"
            : "Pull fleet directory from the relay server, then join a fleet for first-stage sync testing.";
        RefreshFleetDirectoryButton.Content = zh ? "刷新舰队" : "Refresh Fleets";
        SelectLogButton.Content = zh ? "选择日志" : "Select Log";
        ToggleOverlayButton.Content = zh ? "切换 Overlay" : "Toggle Overlay";
        NetworkTestNavButton.Content = zh ? "联网测试 / 监控" : "Network / Monitor";
        HotkeyLimitHintText.Text = zh
            ? "由于游戏限制，星际公民在前台时无法使用热键开关 Overlay。"
            : "Due to game restrictions, hotkeys cannot toggle Overlay while Star Citizen is in front.";
        FleetTab.Header = zh ? "舰队" : "Fleet";
        MySquadTab.Header = zh ? "我的小队" : "My Squad";
        AllPlayersTab.Header = zh ? "全成员" : "All Players";
        SquadsTab.Header = zh ? "小队" : "Squads";
        FleetShipDatabaseTab.Header = zh ? "舰船" : "Ships";
        ManageFleetTab.Header = zh ? "管理舰队" : "Manage Fleet";
        OverlayEditTab.Header = zh ? "Overlay 编辑" : "Overlay Edit";
        PersonalTab.Header = zh ? "个人" : "Personal";
        MonitorTab.Header = zh ? "监控" : "Monitor";
        TotalMembersLabel.Text = zh ? "总人数" : "TOTAL MEMBERS";
        OnlineLabel.Text = zh ? "在线" : "ONLINE";
        FleetShipsLabel.Text = zh ? "舰船数量" : "SHIPS";
        FleetNoticeInfoTabButton.Content = zh ? "舰队公告" : "Fleet Notice";
        FleetCurrentTaskInfoTabButton.Content = zh ? "当前任务" : "Current Task";
        FleetActionPlanInfoTabButton.Content = zh ? "行动计划" : "Action Plan";
        PlayerNameColumn.Header = zh ? "游戏名" : "Name";
        PlayerStatusColumn.Header = zh ? "状态" : "Status";
        PlayerShipColumn.Header = zh ? "飞船" : "Ship";
        PlayerLocationColumn.Header = zh ? "位置" : "Location";
        MySquadEmblemHintText.Text = zh ? "点击更换" : "Click to change";
        MySquadAvatarColumn.Header = zh ? "头像" : "Avatar";
        MySquadRoleColumn.Header = zh ? "职位" : "Role";
        MySquadCallsignColumn.Header = zh ? "呼号" : "Callsign";
        MySquadGameIdColumn.Header = zh ? "游戏 ID" : "Game ID";
        MySquadOnlineColumn.Header = zh ? "在线状态" : "Online";
        MySquadShipColumn.Header = zh ? "飞船状态" : "Ship Status";
        MySquadLocationColumn.Header = zh ? "地点信息" : "Location";
        OverlayEditHintText.Text = zh
            ? "拖拽面板移动位置，拖拽右下角手柄缩放。保存后的布局会应用到全屏 Overlay。"
            : "Drag panels to move. Drag the lower-right handle to resize. Saved layout is applied to fullscreen Overlay.";
        OverlayPresetLabel.Text = zh ? "Overlay 预设" : "OVERLAY PRESET";
        OverlayHotkeyLabel.Text = zh ? "Overlay 热键" : "OVERLAY HOTKEY";
        if (OverlayPresetBox.Items.Count >= 4)
        {
            ((ComboBoxItem)OverlayPresetBox.Items[0]).Content = zh ? "战斗" : "Combat";
            ((ComboBoxItem)OverlayPresetBox.Items[1]).Content = zh ? "精简" : "Compact";
            ((ComboBoxItem)OverlayPresetBox.Items[2]).Content = zh ? "指挥" : "Command";
            ((ComboBoxItem)OverlayPresetBox.Items[3]).Content = zh ? "自定义" : "Custom";
        }
        SaveLayoutButton.Content = zh ? "保存布局" : "Save Layout";
        ResetLayoutButton.Content = zh ? "重置" : "Reset";
        OverlayOptionsLabel.Text = zh ? "OVERLAY 选项" : "OVERLAY OPTIONS";
        HideMissionWhenIdleCheck.Content = zh ? "无任务时隐藏任务 Overlay" : "No task: hide mission overlay";
        HideOfflineMembersCheck.Content = zh ? "隐藏离线小队成员" : "Hide offline squad members";
        HideSquadIconsCheck.Content = zh ? "左侧不显示小队图标" : "Left panel: hide squad icons";
        MemberNameDisplayLabel.Text = zh ? "成员名字显示" : "MEMBER NAME DISPLAY";
        ShowCallsignAndNameRadio.Content = zh ? "显示呼号和游戏名" : "Show callsign and game name";
        ShowCallsignOnlyRadio.Content = zh ? "只显示呼号" : "Only callsign";
        ShowGameNameOnlyRadio.Content = zh ? "只显示游戏名" : "Only game name";
        OverlayThemeLabel.Text = zh ? "外观风格" : "APPEARANCE";
        if (OverlayThemeBox.Items.Count >= 3)
        {
            ((ComboBoxItem)OverlayThemeBox.Items[0]).Content = zh ? "默认" : "Default";
            ((ComboBoxItem)OverlayThemeBox.Items[1]).Content = zh ? "铁砧" : "Anvil";
            ((ComboBoxItem)OverlayThemeBox.Items[2]).Content = zh ? "德雷克" : "Drake";
        }
        BackgroundModeLabel.Text = zh ? "后台模式" : "BACKGROUND";
        TrayModeCheck.Content = zh ? "窗口最小化时仍然可以显示 Overlay" : "Keep Overlay visible when minimized";
        TrayModeHintText.Text = zh
            ? "启用后，主窗口最小化时不会隐藏已经开启的 Overlay。"
            : "When enabled, minimizing the main window will not hide an active Overlay.";
        AvatarPlaceholderText.Text = zh ? "头像" : "AVATAR";
        ChooseAvatarButton.Content = zh ? "选择头像" : "Choose Avatar";
        FeedbackButton.Content = zh ? "反馈" : "Feedback";
        PlayerNameLabel.Text = zh ? "游戏名" : "Player Name";
        PlayerIdLabel.Text = zh ? "玩家 ID" : "Player ID";
        CallsignLabel.Text = zh ? "呼号" : "Callsign";
        FleetLabel.Text = zh ? "舰队" : "Fleet";
        LocalFleetText.Text = zh ? "本地舰队" : "Local Fleet";
        StatusLabel.Text = zh ? "状态" : "Status";
        ShipDatabaseTitleText.Text = zh ? "舰船数据库" : "Ship Database";
        ShipDatabaseHintText.Text = zh
            ? "仅官网机库读取器或官网快照识别出的飞船计入个人舰船。不会保存 RSI 账号密码。"
            : "Only ships recognized from the hangar reader or an official snapshot count as personal ships. RSI credentials are never saved.";
        OpenHangarReaderButton.Content = zh ? "读取官网机库" : "Read Hangar";
        ImportHangarButton.Content = zh ? "导入快照" : "Import Snapshot";
        ClearShipDatabaseButton.Content = zh ? "清空舰船库" : "Clear Ships";
        OwnedShipNameColumn.Header = zh ? "舰船" : "Ship";
        OwnedShipCodeColumn.Header = zh ? "代码" : "Code";
        OwnedShipSourceColumn.Header = zh ? "来源" : "Source";
        OwnedShipImportedAtColumn.Header = zh ? "导入时间" : "Imported";
        UpdateShipDatabaseSummary();
        RenderOverlayEditor();
    }

    private static string NormalizeLanguage(string? language)
    {
        return "zh";
    }

    private void LoadAvatarPreview()
    {
        if (!IsLoggedIn)
        {
            AvatarImage.Source = null;
            AvatarPlaceholderText.Text = "请登录";
            AvatarPlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        if (string.IsNullOrWhiteSpace(_avatarPath) || !File.Exists(_avatarPath))
        {
            AvatarImage.Source = null;
            AvatarPlaceholderText.Text = _language == "zh" ? "头像" : "AVATAR";
            AvatarPlaceholderText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(_avatarPath);
        image.EndInit();
        image.Freeze();

        AvatarImage.Source = image;
        AvatarPlaceholderText.Visibility = Visibility.Collapsed;
    }

    private void LoadCreateFleetLogoPreview()
    {
        if (CreateFleetLogoImage is null || CreateFleetLogoText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetLogoPath) || !File.Exists(_fleetLogoPath))
        {
            CreateFleetLogoImage.Source = null;
            CreateFleetLogoText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(_fleetLogoPath);
        image.EndInit();
        image.Freeze();

        CreateFleetLogoImage.Source = image;
        CreateFleetLogoText.Visibility = Visibility.Collapsed;
    }

    private void LoadFleetHeaderLogoPreview()
    {
        if (FleetHeaderLogoImage is null || FleetHeaderLogoText is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetLogoPath) || !File.Exists(_fleetLogoPath))
        {
            FleetHeaderLogoImage.Source = null;
            FleetHeaderLogoText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(_fleetLogoPath);
        image.EndInit();
        image.Freeze();

        FleetHeaderLogoImage.Source = image;
        FleetHeaderLogoText.Visibility = Visibility.Collapsed;
    }

    private void RefreshFleetHeader()
    {
        if (FleetHeaderNameText is null)
        {
            return;
        }

        FleetHeaderNameText.Text = _hasFleet ? _fleetName : "No Fleet";
        FleetHeaderCodeText.Text = _hasFleet ? _fleetCode : "N/A";
        FleetCommanderText.Text = $"首席指挥官 / {FormatCommanderName(_callsign, _localPlayer, _fleetChiefCommander)}";
        FleetDeputyCommanderText.Text = $"副指挥官 / {_fleetDeputyCommander}";
        FleetTypeSummaryText.Text = _hasFleet
            ? $"活动时间 / {_fleetActiveTime}"
            : "活动时间 / 未设置";
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();

        LoadFleetHeaderLogoPreview();
        ManageFleetTab.Visibility = _hasFleet && IsCurrentUserFleetCommander()
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static string FormatCommanderName(string? callsign, string? gameName, string fallback = "Unassigned")
    {
        if (string.IsNullOrWhiteSpace(gameName))
        {
            return string.IsNullOrWhiteSpace(callsign) ? fallback : callsign.Trim();
        }

        if (string.IsNullOrWhiteSpace(callsign) ||
            callsign.Equals(gameName, StringComparison.OrdinalIgnoreCase))
        {
            return gameName.Trim();
        }

        return $"{callsign.Trim()} ({gameName.Trim()})";
    }

    private void RefreshFleetInfoPanel()
    {
        if (FleetActionPlanTitleText is null)
        {
            return;
        }

        FleetNoticeInfoTabButton.Tag = _selectedFleetInfoPanel == FleetInfoPanelKind.Notice ? "Active" : null;
        FleetCurrentTaskInfoTabButton.Tag = _selectedFleetInfoPanel == FleetInfoPanelKind.CurrentTask ? "Active" : null;
        FleetActionPlanInfoTabButton.Tag = _selectedFleetInfoPanel == FleetInfoPanelKind.ActionPlan ? "Active" : null;

        switch (_selectedFleetInfoPanel)
        {
            case FleetInfoPanelKind.Notice:
                RefreshNoticePanel();
                break;
            case FleetInfoPanelKind.CurrentTask:
                RefreshCurrentTaskPanel();
                break;
            default:
                RefreshActionPlanPanel();
                break;
        }
    }

    private void RefreshNoticePanel()
    {
        var hasNotice = !string.IsNullOrWhiteSpace(_fleetNoticeTitle);
        if (!hasNotice)
        {
            FleetActionPlanTitleText.Text = "暂无舰队公告";
            FleetActionPlanSummaryText.Text = "等待舰队指挥发布公告";
            FleetActionPlanTimeText.Text = "";
            JoinFleetActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        FleetActionPlanTitleText.Text = _fleetNoticeTitle;
        FleetActionPlanSummaryText.Text = _fleetNoticeContent;
        FleetActionPlanTimeText.Text = IsCurrentUserFleetCommander()
            ? "点击编辑公告"
            : "";
        JoinFleetActionButton.Visibility = Visibility.Collapsed;
    }

    private void RefreshCurrentTaskPanel()
    {
        var hasTask = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle);
        if (!hasTask)
        {
            FleetActionPlanTitleText.Text = IsCurrentUserFleetCommander()
                ? "暂无当前任务"
                : "当前无任务";
            FleetActionPlanSummaryText.Text = IsCurrentUserFleetCommander()
                ? "请前往 管理舰队-发布任务 进行任务发布"
                : "";
            FleetActionPlanTimeText.Text = "";
            JoinFleetActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        FleetActionPlanTitleText.Text = _fleetCurrentTaskTitle;
        FleetActionPlanSummaryText.Text = FormatCurrentTaskPreview();
        FleetActionPlanTimeText.Text = _fleetCurrentTaskTime is null
            ? "点击查看任务详情"
            : $"发布时间 / {_fleetCurrentTaskTime:yyyy-MM-dd HH:mm}    点击查看任务详情";
        JoinFleetActionButton.Visibility = Visibility.Collapsed;
    }

    private string FormatCurrentTaskPreview()
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(_fleetCurrentTaskBrief))
        {
            lines.Add($"任务简述 / {_fleetCurrentTaskBrief}");
        }

        if (!string.IsNullOrWhiteSpace(_fleetCurrentTaskRally))
        {
            lines.Add($"集结点 / {_fleetCurrentTaskRally}");
        }

        if (!string.IsNullOrWhiteSpace(_fleetCurrentTaskShip))
        {
            lines.Add($"指定舰船 / {_fleetCurrentTaskShip}");
        }

        lines.Add($"参与范围 / {_fleetCurrentTaskParticipants}");
        return string.Join(Environment.NewLine, lines);
    }

    private void RefreshActionPlanPanel()
    {
        SelectFeaturedActionPlan();
        var hasAction = !string.IsNullOrWhiteSpace(_fleetActionTitle);
        if (!hasAction)
        {
            FleetActionPlanTitleText.Text = "暂无行动计划，等待下一步指挥";
            FleetActionPlanSummaryText.Text = "";
            FleetActionPlanTimeText.Text = "";
            JoinFleetActionButton.Visibility = Visibility.Collapsed;
            JoinFleetActionButton.Content = "参与";
            JoinFleetActionButton.IsEnabled = true;
            return;
        }

        FleetActionPlanTitleText.Text = _fleetActionTitle;
        FleetActionPlanSummaryText.Text = _fleetActionContent;
        FleetActionPlanTimeText.Text = _fleetActionStartTime is null
            ? ""
            : $"开始时间 / {_fleetActionStartTime:yyyy-MM-dd HH:mm}";
        JoinFleetActionButton.Visibility = Visibility.Visible;
        var joined = _joinedActionPlanIds.Contains(_selectedActionPlanId);
        JoinFleetActionButton.Content = joined ? "已预约" : "参与";
        JoinFleetActionButton.IsEnabled = !joined;
    }

    private void SelectFeaturedActionPlan()
    {
        var visiblePlans = GetVisibleActionPlans().ToArray();
        var now = DateTime.Now;
        var selected = visiblePlans
            .Where(plan => plan.StartTime >= now)
            .OrderBy(plan => plan.StartTime)
            .FirstOrDefault() ??
            visiblePlans
                .OrderByDescending(plan => plan.StartTime)
                .FirstOrDefault();

        if (selected is null)
        {
            _selectedActionPlanId = "";
            _fleetActionTitle = "";
            _fleetActionContent = "";
            _fleetActionStartTime = null;
            _fleetActionNotifyMembers = false;
            return;
        }

        _selectedActionPlanId = selected.Id;
        _fleetActionTitle = selected.Title;
        _fleetActionContent = selected.Content;
        _fleetActionStartTime = selected.StartTime;
        _fleetActionNotifyMembers = selected.NotifyMembers;
    }

    private IEnumerable<FleetActionPlanRow> GetVisibleActionPlans()
    {
        var now = DateTime.Now;
        var from = now.AddDays(-2);
        var to = now.AddDays(7);
        return _fleetActionPlans
            .Where(plan => plan.StartTime >= from && plan.StartTime <= to)
            .OrderBy(plan => plan.StartTime);
    }

    private void RefreshTaskManagementPanel()
    {
        if (ManageCurrentTaskTitleText is null)
        {
            return;
        }

        ManageFleetNoticeTitleText.Text = string.IsNullOrWhiteSpace(_fleetNoticeTitle)
            ? "暂无舰队公告"
            : _fleetNoticeTitle;
        ManageFleetNoticeSummaryText.Text = string.IsNullOrWhiteSpace(_fleetNoticeContent)
            ? "舰队公告会显示在我的舰队信息栏与 Overlay 通知区域。"
            : _fleetNoticeContent;
        if (FleetDescriptionEditBox is not null && !FleetDescriptionEditBox.IsKeyboardFocusWithin)
        {
            FleetDescriptionEditBox.Text = _fleetDescription;
        }

        SelectFeaturedActionPlan();
        foreach (var plan in _fleetActionPlans)
        {
            plan.RefreshParticipantSummary();
        }

        FleetActionPlansEmptyText.Visibility = GetVisibleActionPlans().Any()
            ? Visibility.Collapsed
            : Visibility.Visible;

        var hasTask = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle);
        ManageCurrentTaskTitleText.Text = hasTask ? _fleetCurrentTaskTitle : "暂无当前任务";
        ManageCurrentTaskSummaryText.Text = hasTask
            ? FormatCurrentTaskPreview()
            : "请前往 管理舰队-发布任务 进行任务发布";
        ManageCurrentTaskMetaText.Text = hasTask && _fleetCurrentTaskTime is not null
            ? $"发布时间 / {_fleetCurrentTaskTime:yyyy-MM-dd HH:mm}"
            : "";

        EditCurrentTaskButton.IsEnabled = hasTask;
        CompleteCurrentTaskButton.IsEnabled = hasTask;
        DeleteCurrentTaskButton.IsEnabled = hasTask;
        RenotifyCurrentTaskButton.IsEnabled = hasTask;
        FleetTaskHistoryEmptyText.Visibility = _fleetTaskHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyFleetEventLogFilter();
    }

    private bool IsLocalPlayer(string playerName)
    {
        return !string.IsNullOrWhiteSpace(_localPlayer) &&
               playerName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCurrentUserFleetCommander()
    {
        return !string.IsNullOrWhiteSpace(_localPlayer) &&
               IsFleetCommander(_localPlayer, _callsign);
    }

    private void SeedSquads()
    {
        if (_squads.Count > 0)
        {
            return;
        }
    }

    private void RenderSquads()
    {
        foreach (var squad in _squads)
        {
            squad.Members.Clear();
            squad.PreviewMembers.Clear();
            squad.StatusMembers.Clear();
            if (_hasFleet)
            {
                foreach (var player in _players.Where(player =>
                             player.SquadName.Equals(squad.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    squad.Members.Add(CreateSquadAvatarRow(squad, player));
                    squad.StatusMembers.Add(CreateSquadStatusRow(squad, player));
                }
            }

            foreach (var member in squad.Members
                         .OrderByDescending(member => member.IsCommander)
                         .Take(6))
            {
                squad.PreviewMembers.Add(member);
            }

            squad.RefreshComputed();
        }

        NoSquadsPanel.Visibility = _squads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SquadSelectionList.Visibility = _squads.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private MemberAvatarRow CreateSquadAvatarRow(SquadRow squad, PlayerRow player)
    {
        var isCommander = IsSquadCommander(squad, player);
        return new MemberAvatarRow(
            player.Callsign ?? player.Name,
            GetInitials(player.Name),
            player.Status,
            player.AvatarPath,
            GetSquadNameBrush(squad, player),
            isCommander);
    }

    private SquadMemberStatusRow CreateSquadStatusRow(SquadRow squad, PlayerRow player)
    {
        return new SquadMemberStatusRow(
            GetInitials(player.Name),
            player.AvatarPath,
            GetSquadRole(squad, player),
            player.Callsign ?? "-",
            player.Name,
            player.Status,
            player.Ship,
            player.Location,
            GetSquadNameBrush(squad, player));
    }

    private void RenderMySquad()
    {
        var squad = _selectedSquad;
        if (squad is null)
        {
            MySquadEmptyDetailPanel.Visibility = Visibility.Visible;
            MySquadDetailPanel.Visibility = Visibility.Collapsed;
            if (!MySquadDescriptionBox.IsKeyboardFocusWithin)
            {
                MySquadDescriptionBox.Text = "";
            }
            _mySquadMembers.Clear();
            return;
        }

        MySquadEmptyDetailPanel.Visibility = Visibility.Collapsed;
        MySquadDetailPanel.Visibility = Visibility.Visible;
        MySquadNameText.Text = squad.Name;
        MySquadCommanderText.Text = $"Commander / {squad.Commander}";
        MySquadMemberCountText.Text = $"{squad.Members.Count(member => member.Status == "Online")}/{squad.Members.Count} Online";
        MySquadTypeText.Text = $"Type / {squad.Type}";
        if (!MySquadDescriptionBox.IsKeyboardFocusWithin)
        {
            MySquadDescriptionBox.Text = squad.Description;
        }
        MySquadIconText.Text = squad.Icon;
        LoadMySquadEmblem(squad.EmblemPath);

        _mySquadMembers.Clear();
        if (ReferenceEquals(squad, _joinedSquad))
        {
            foreach (var player in _players.Where(player =>
                         player.SquadName.Equals(squad.Name, StringComparison.OrdinalIgnoreCase)))
            {
                _mySquadMembers.Add(CreateSquadStatusRow(squad, player));
            }
        }
    }

    private static string GetSquadRole(SquadRow squad, PlayerRow player)
    {
        return IsSquadCommander(squad, player)
            ? "小队指挥官"
            : "成员";
    }

    private static bool IsSquadCommander(SquadRow squad, PlayerRow player)
    {
        return player.Name.Equals(GetGameNameFromDisplayName(squad.Commander), StringComparison.OrdinalIgnoreCase) ||
               player.Callsign?.Equals(GetCallsignFromDisplayName(squad.Commander), StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string GetGameNameFromDisplayName(string value)
    {
        var start = value.LastIndexOf('(');
        var end = value.LastIndexOf(')');
        return start >= 0 && end > start
            ? value[(start + 1)..end].Trim()
            : value.Trim();
    }

    private static string GetCallsignFromDisplayName(string value)
    {
        var start = value.LastIndexOf('(');
        return start > 0 ? value[..start].Trim() : value.Trim();
    }

    private static string GetInitials(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Length >= 2 ? name[..2].ToUpperInvariant() : name[..1].ToUpperInvariant();
    }

    private void LoadMySquadEmblem(string? emblemPath)
    {
        if (string.IsNullOrWhiteSpace(emblemPath) || !File.Exists(emblemPath))
        {
            MySquadEmblemImage.Source = null;
            MySquadIconText.Visibility = Visibility.Visible;
            MySquadEmblemHintText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(emblemPath);
        image.EndInit();
        image.Freeze();

        MySquadEmblemImage.Source = image;
        MySquadIconText.Visibility = Visibility.Collapsed;
        MySquadEmblemHintText.Visibility = Visibility.Collapsed;
    }

    private static IEnumerable<OverlayLayoutItem> CreateDefaultOverlayLayout()
    {
        return CreateDefaultOverlayLayout(OverlayPresetCombat);
    }

    private static IEnumerable<OverlayLayoutItem> CreateDefaultOverlayLayout(string preset)
    {
        switch (NormalizeOverlayPreset(preset))
        {
            case OverlayPresetCompact:
                yield return new OverlayLayoutItem("Notice", "FLEET NOTICE", 0.34, 0.02, 0.32, 0.055, Brushes.Yellow);
                yield return new OverlayLayoutItem("Squads", "FLEET / SQUADS", 0.01, 0.36, 0.13, 0.24, Brushes.DeepSkyBlue);
                yield return new OverlayLayoutItem("Mission", "MISSION PACKAGE", 0.85, 0.30, 0.14, 0.11, Brushes.Red);
                yield return new OverlayLayoutItem("Members", "SQUAD MEMBERS", 0.84, 0.58, 0.15, 0.18, Brushes.Gray);
                break;
            case OverlayPresetCommand:
                yield return new OverlayLayoutItem("Notice", "FLEET NOTICE", 0.285, 0.01, 0.43, 0.075, Brushes.Yellow);
                yield return new OverlayLayoutItem("Squads", "FLEET / SQUADS", 0.01, 0.30, 0.18, 0.42, Brushes.DeepSkyBlue);
                yield return new OverlayLayoutItem("Mission", "MISSION PACKAGE", 0.79, 0.24, 0.20, 0.17, Brushes.Red);
                yield return new OverlayLayoutItem("Members", "SQUAD MEMBERS", 0.78, 0.54, 0.21, 0.26, Brushes.Gray);
                break;
            default:
                yield return new OverlayLayoutItem("Notice", "FLEET NOTICE", 0.305, 0.01, 0.39, 0.07, Brushes.Yellow);
                yield return new OverlayLayoutItem("Squads", "FLEET / SQUADS", 0.01, 0.32, 0.16, 0.36, Brushes.DeepSkyBlue);
                yield return new OverlayLayoutItem("Mission", "MISSION PACKAGE", 0.835, 0.26, 0.16, 0.14, Brushes.Red);
                yield return new OverlayLayoutItem("Members", "SQUAD MEMBERS", 0.82, 0.56, 0.18, 0.22, Brushes.Gray);
                break;
        }
    }
}

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
    System.Windows.Media.Brush? NameBrush = null,
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
    System.Windows.Media.Brush? NameBrush = null);

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
            $"Code / {code}",
            $"Commander / {commander}",
            joinPolicy.Equals("需要申请", StringComparison.OrdinalIgnoreCase) ||
            joinPolicy.Equals("Application", StringComparison.OrdinalIgnoreCase)
                ? "Join / Requires application"
                : "Join / Open",
            description!,
            $"Type / {type}",
            $"Active / {activeTime}",
            $"{snapshot.OnlineMembers} Online / {snapshot.TotalMembers} Members",
            !isCurrentFleet,
            isCurrentFleet ? "已加入" : "加入");
    }
}

