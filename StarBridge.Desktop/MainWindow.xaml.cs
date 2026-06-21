using Microsoft.Win32;
using StarBridge.Core.Events;
using StarBridge.Core.LogWatching;
using StarBridge.Core.Parsing;
using StarBridge.Core.State;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
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
using WinForms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;

namespace StarBridge.Desktop;

public partial class MainWindow : Window, IAppUpdateUi
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
    private const string DefaultRelayUrl = "https://api.scstarbridge.com";
    private static readonly TimeSpan LocalSquadEditProtectionWindow = TimeSpan.FromSeconds(45);
    private static readonly Regex JoinPuShardRegex = new(
        @"<Join PU>.*?\bshard\[(?<shard>[^\]]+)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private enum FleetShipSortColumn
    {
        Spec,
        Name,
        Status,
        Price,
        Owner,
        Squad,
        Role
    }

    private readonly RegexLogEventParser _parser = new();
    private readonly FleetState _fleetState = new();
    private readonly ObservableCollection<PlayerRow> _players = [];
    private readonly ObservableCollection<SquadRow> _squads = [];
    private readonly ObservableCollection<SquadMemberStatusRow> _mySquadMembers = [];
    private readonly ObservableCollection<NetworkFleetCard> _networkFleets = [];
    private readonly List<NetworkFleetCard> _allNetworkFleets = [];
    private readonly ObservableCollection<OwnedShipRecord> _ownedShips = [];
    private readonly ObservableCollection<FleetShipInventoryRow> _fleetShipInventory = [];
    private FleetShipSortColumn _fleetShipSortColumn = FleetShipSortColumn.Spec;
    private bool _fleetShipSortDescending = true;
    private readonly ObservableCollection<FleetTaskHistoryRow> _fleetTaskHistory = [];
    private readonly ObservableCollection<FleetActionPlanRow> _fleetActionPlans = [];
    private readonly ObservableCollection<FleetEventLogRow> _fleetEventLogs = [];
    private readonly ObservableCollection<FleetNotificationCenterItemRow> _fleetNotificationCenterItems = [];
    private readonly ObservableCollection<FleetMemberManagementRow> _fleetMemberRows = [];
    private readonly ObservableCollection<FleetApplicationRow> _fleetApplications = [];
    private readonly List<FleetEventLogRow> _allFleetEventLogs = [];
    private NetworkFleetApplicationSnapshot[] _fleetApplicationSnapshots = [];
    private readonly Dictionary<string, LocalFleetMemberPermission> _fleetMemberPermissions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkPlayerSnapshot> _networkSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _localSquadEditTimes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _localFleetLogoEditTime;
    private readonly HashSet<string> _joinedActionPlanIds = new(StringComparer.OrdinalIgnoreCase);
    private SquadRow? _selectedSquad;
    private SquadRow? _joinedSquad;
    private readonly GridViewColumn PlayerNameColumn = new();
    private readonly GridViewColumn PlayerStatusColumn = new();
    private readonly GridViewColumn PlayerShipColumn = new();
    private readonly GridViewColumn PlayerLocationColumn = new();
    private readonly List<OverlayLayoutItem> _overlayLayout = [];
    private readonly DispatcherTimer _gameProcessTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _networkSyncTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _profileSyncDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private readonly HttpClient _networkClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private readonly StarBridgeRelayClient _relayClient;
    private readonly AppUpdateService _appUpdateService;
    private GameLogWatcher? _watcher;
    private string? _logPath;
    private string? _localPlayer;
    private string? _localPlayerId;
    private string? _accountName;
    private string? _authToken;
    private string? _avatarPath;
    private string? _cachedAvatarImagePath;
    private DateTime _cachedAvatarImageWriteTimeUtc;
    private string? _cachedAvatarImageData;
    private string? _fleetLogoPath;
    private string? _createFleetLogoPath;
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
    private readonly Dictionary<string, NetworkFleetShipSnapshot> _remoteFleetShips = new(StringComparer.OrdinalIgnoreCase);
    private string _fleetActionTitle = "";
    private string _fleetActionContent = "";
    private DateTime? _fleetActionStartTime;
    private bool _fleetActionNotifyMembers;
    private string _selectedActionPlanId = "";
    private bool _joinActionNotifyMe;
    private string? _callsign;
    private bool _allowEmailNotifications = true;
    private string _gameServerRegion = "未知";
    private string _gameServerShard = "";
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
    private bool _isClosingAfterOfflineUpload;
    private bool _isNetworkSyncRunning;
    private bool _isRefreshingAccountPanel;
    private int _networkSyncFailureCount;
    private DateTimeOffset _nextNetworkSyncAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastProcessDrivenRenderAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _syncStatusOverlayCts;
    private TaskCompletionSource<bool>? _updateConfirmationSource;
    private bool _updateOverlayCanClose;
    private HwndSource? _hotkeySource;
    private bool _hotkeyRegistered;
    private bool _hasFleet;
    private bool _isCreatingFleet;

    public MainWindow()
    {
        InitializeComponent();
        _relayClient = new StarBridgeRelayClient(
            _networkClient,
            () => NetworkServerUrlBox.Text,
            () => NetworkServerKeyBox.Password,
            () => _authToken);
        _appUpdateService = new AppUpdateService(
            _networkClient,
            BuildNetworkUri,
            this,
            text => UpdateStatusText.Text = text,
            isEnabled => CheckUpdateButton.IsEnabled = isEnabled,
            this);
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
        UpdateFleetShipSortHeaderIndicators();
        FleetTaskHistoryList.ItemsSource = _fleetTaskHistory;
        FleetActionPlanList.ItemsSource = _fleetActionPlans;
        FleetEventLogList.ItemsSource = _fleetEventLogs;
        FleetNotificationCenterList.ItemsSource = _fleetNotificationCenterItems;
        FleetMemberManagementList.ItemsSource = _fleetMemberRows;
        FleetApplicationList.ItemsSource = _fleetApplications;

        _isLoadingSettings = true;
        var config = DesktopAppConfig.Load();
        var hasSavedSession = !string.IsNullOrWhiteSpace(config.AuthToken);
        _logPath = config.LogPath;
        _localPlayer = config.PlayerName;
        _localPlayerId = config.PlayerId;
        _accountName = hasSavedSession ? config.AccountName : null;
        _authToken = hasSavedSession ? config.AuthToken : null;
        _avatarPath = hasSavedSession ? config.AvatarPath : null;
        _callsign = hasSavedSession ? config.Callsign : null;
        _allowEmailNotifications = hasSavedSession ? config.AllowEmailNotifications : true;
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
        NetworkServerUrlBox.Text = NormalizeNetworkServerUrl(config.NetworkServerUrl);
        NetworkServerKeyBox.Password = config.NetworkServerKey ?? "";
        CallsignBox.Text = _callsign ?? "";
        if (hasSavedSession)
        {
            LoadFleetState(config.FleetStateJson);
        }
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

            _ = RunStartupFlowAsync();
            _ = _appUpdateService.CheckForInstallerUpdateAsync(silent: true, currentVersion: GetAppVersion());
        };
        _gameProcessTimer.Tick += (_, _) => UpdateLocalOnlineStateFromGameProcess();
        _gameProcessTimer.Start();
        _networkSyncTimer.Tick += async (_, _) => await NetworkAutoSyncAsync();
        _profileSyncDebounceTimer.Tick += async (_, _) =>
        {
            _profileSyncDebounceTimer.Stop();
            await FlushProfileSyncDebouncedAsync();
        };
        AppendOutput("请选择 Star Citizen 的 Game.log 开始读取。");
        RefreshHeaderStatusBar();
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
        if (_hasFleet)
        {
            ShowOneTimeGuideHint(
                "squad-page",
                "小队引导",
                "这里可以创建或加入小队。加入小队后，Overlay 会优先展示同小队成员，任务分配也会按小队组织。");
        }
    }

    private void OverlayNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = OverlayEditTab;
        SetActiveNav(OverlayNavButton);
        ShowOneTimeGuideHint(
            "overlay-page",
            "Overlay 引导",
            "这里可以拖拽布局、切换船厂风格、设置透明度、准星和显示模块。保存布局后，游戏内 Overlay 会使用这套配置。");
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
        RefreshOnboardingSupportPanel();
    }

    private void FeedbackButton_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = SupportTab;
        SetActiveNav(null);
        RefreshOnboardingSupportPanel();
    }

    private async void CheckUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        await _appUpdateService.CheckForInstallerUpdateAsync(silent: false, currentVersion: GetAppVersion());
    }

    public Task<bool> ConfirmUpdateAsync(UpdateManifest manifest, string currentVersion, string updateMode)
    {
        _updateConfirmationSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _updateOverlayCanClose = false;

        Dispatcher.Invoke(() =>
        {
            var notes = string.IsNullOrWhiteSpace(manifest.Notes) ? "无版本说明。" : manifest.Notes.Trim();
            UpdateOverlayTitleText.Text = $"发现新版本 V{manifest.Version}";
            UpdateOverlayVersionText.Text = $"当前版本 V{currentVersion}  ->  新版本 V{manifest.Version}";
            UpdateOverlayModeText.Text = $"更新方式：{updateMode}";
            UpdateOverlayNotesText.Text = notes;
            UpdateOverlayStatusText.Text = "确认后将开始下载更新。更新期间会锁定应用操作；下载完成后星海舰桥可能会自动关闭并重启。";
            UpdateOverlayProgressBar.IsIndeterminate = false;
            UpdateOverlayProgressBar.Value = 0;
            UpdateOverlayProgressText.Text = "0%";
            UpdateOverlayCancelButton.Visibility = Visibility.Visible;
            UpdateOverlayCancelButton.IsEnabled = true;
            UpdateOverlayPrimaryButton.Visibility = Visibility.Visible;
            UpdateOverlayPrimaryButton.IsEnabled = true;
            UpdateOverlayPrimaryButton.Content = "立即更新";
            UpdateProgressOverlay.Visibility = Visibility.Visible;
        });

        return _updateConfirmationSource.Task;
    }

    public void ReportProgress(string status, long? percent)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateOverlayStatusText.Text = status;
            UpdateOverlayCancelButton.Visibility = Visibility.Collapsed;
            UpdateOverlayPrimaryButton.Visibility = Visibility.Collapsed;
            UpdateOverlayProgressBar.IsIndeterminate = percent is null;
            if (percent is null)
            {
                UpdateOverlayProgressText.Text = "下载中";
                return;
            }

            var clamped = Math.Clamp(percent.Value, 0, 100);
            UpdateOverlayProgressBar.Value = clamped;
            UpdateOverlayProgressText.Text = $"{clamped}%";
            UpdateProgressOverlay.Visibility = Visibility.Visible;
        });
    }

    public void ReportCompleted(string status)
    {
        Dispatcher.Invoke(() =>
        {
            UpdateOverlayStatusText.Text = status;
            UpdateOverlayProgressBar.IsIndeterminate = false;
            UpdateOverlayProgressBar.Value = 100;
            UpdateOverlayProgressText.Text = "100%";
            UpdateOverlayCancelButton.Visibility = Visibility.Collapsed;
            UpdateOverlayPrimaryButton.Visibility = Visibility.Collapsed;
            UpdateProgressOverlay.Visibility = Visibility.Visible;
        });
    }

    public void ReportFailed(string status)
    {
        Dispatcher.Invoke(() =>
        {
            _updateOverlayCanClose = true;
            UpdateOverlayStatusText.Text = status;
            UpdateOverlayProgressBar.IsIndeterminate = false;
            UpdateOverlayCancelButton.Visibility = Visibility.Collapsed;
            UpdateOverlayPrimaryButton.Content = "关闭";
            UpdateOverlayPrimaryButton.Visibility = Visibility.Visible;
            UpdateOverlayPrimaryButton.IsEnabled = true;
            UpdateProgressOverlay.Visibility = Visibility.Visible;
        });
    }

    private void UpdateOverlayPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateConfirmationSource is not null)
        {
            var source = _updateConfirmationSource;
            _updateConfirmationSource = null;
            UpdateOverlayPrimaryButton.Visibility = Visibility.Collapsed;
            UpdateOverlayCancelButton.Visibility = Visibility.Collapsed;
            UpdateOverlayStatusText.Text = "正在准备更新...";
            source.TrySetResult(true);
            return;
        }

        if (_updateOverlayCanClose)
        {
            UpdateProgressOverlay.Visibility = Visibility.Collapsed;
            _updateOverlayCanClose = false;
        }
    }

    private void UpdateOverlayCancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_updateConfirmationSource is null)
        {
            return;
        }

        var source = _updateConfirmationSource;
        _updateConfirmationSource = null;
        UpdateProgressOverlay.Visibility = Visibility.Collapsed;
        source.TrySetResult(false);
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
                    ? "发送反馈失败：当前服务器未更新反馈接口，请联系管理员更新服务器。"
                    : FormatActionFailure("发送反馈", await ReadResponseErrorAsync(response));
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
        RefreshHeaderStatusBar();
        MainTabs.SelectedItem = PersonalTab;
        SetActiveNav(PersonalNavButton);
        _ = ShowLoginDialogAsync();
        return false;
    }

    private IdentityInitializationStatus GetIdentityInitializationStatus()
    {
        return IdentityInitialization.GetStatus(_logPath, _localPlayer, _localPlayerId);
    }

    private bool EnsureIdentityInitialized(string action)
    {
        var status = GetIdentityInitializationStatus();
        if (status.IsComplete)
        {
            return true;
        }

        LoginStatusText.Text = $"需要先完成身份初始化，才能{action}。";
        NetworkStatusText.Text = "请先选择 Game.log，并进入游戏读取玩家 ID";
        RefreshOnboardingSupportPanel();
        RefreshHeaderStatusBar();
        MainTabs.SelectedItem = SupportTab;
        SetActiveNav(null);

        var dialog = new GuideHintWindow(
            "需要身份初始化",
            $"{status.DetailText}\n\n完成后即可{action}。")
        {
            Owner = this
        };
        dialog.ShowDialog();
        return false;
    }

    private void ApplyAuthResponse(AuthResponse auth)
    {
        _accountName = auth.Email ?? auth.UserName;
        _authToken = auth.Token;
        _callsign = auth.Callsign;
        if (!string.IsNullOrWhiteSpace(auth.GameName))
        {
            _localPlayer = auth.GameName;
        }

        _allowEmailNotifications = auth.AllowEmailNotifications;
        CallsignBox.Text = _callsign ?? "";
        EmailNotificationsCheck.IsChecked = _allowEmailNotifications;
    }

    private static bool IsAuthorizationFailure(HttpStatusCode? statusCode)
    {
        return statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
    }

    private bool HandleAuthorizationFailure(HttpStatusCode? statusCode, string context, bool silent = false)
    {
        if (!IsAuthorizationFailure(statusCode))
        {
            return false;
        }

        _networkSyncTimer.Stop();
        if (NetworkAutoSyncCheck.IsChecked == true)
        {
            NetworkAutoSyncCheck.IsChecked = false;
        }

        ClearAuthenticatedLocalState();
        SaveCurrentConfig();
        RefreshAccountPanel();
        RenderState();
        LoginStatusText.Text = "登录已失效，请重新登录";
        NetworkStatusText.Text = $"{context}失败：登录已失效，本地缓存已清理";
        RefreshHeaderStatusBar();
        if (!silent)
        {
            AppendOutput($"NETWORK | auth expired during {context}; local authenticated cache cleared");
        }

        return true;
    }

    private bool HandleAuthorizationFailure(Exception exception, string context, bool silent = false)
    {
        return exception is HttpRequestException httpException &&
               HandleAuthorizationFailure(httpException.StatusCode, context, silent);
    }

    private void RefreshAccountPanel()
    {
        if (AccountNameText is null)
        {
            return;
        }

        _isRefreshingAccountPanel = true;
        try
        {
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
                EmailNotificationsCheck.IsEnabled = true;
                EmailNotificationsCheck.IsChecked = _allowEmailNotifications;
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
            EmailNotificationsCheck.IsEnabled = false;
            EmailNotificationsCheck.IsChecked = false;
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
        finally
        {
            _isRefreshingAccountPanel = false;
            RefreshHeaderStatusBar();
        }
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

        if (!EnsureIdentityInitialized("创建舰队"))
        {
            return;
        }

        _isCreatingFleet = true;
        MainTabs.SelectedItem = FleetTab;
        SetActiveNav(MyFleetNavButton);
        UpdateFleetEntryPanels();
        LoadCreateFleetLogoPreview();
        CreateFleetNameBox.Focus();
    }

    private void CreateFleetCancel_Click(object sender, RoutedEventArgs e)
    {
        _isCreatingFleet = false;
        UpdateFleetEntryPanels();
    }

    private async void CreateFleetSubmit_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!EnsureIdentityInitialized("创建舰队"))
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
        _fleetLogoPath = _createFleetLogoPath;
        _createFleetLogoPath = null;
        LocalFleetText.Text = $"{CreateFleetNameBox.Text.Trim()} [{CreateFleetCodeBox.Text.Trim()}]";
        RefreshFleetHeader();
        UpdateFleetEntryPanels();
        AddFleetLog("舰队", "创建舰队", $"{FormatCommanderName(_callsign, _localPlayer)} 创建 {_fleetName}");
        SaveCurrentConfig();
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
        await PushFleetDirectoryAsync(silent: true);
        ShowOneTimeGuideHint(
            "fleet-created-commander",
            "舰队指挥官引导",
            "舰队已经创建。下一步建议前往“管理舰队”设置公告、行动计划、任务、成员权限和舰船数据库，再邀请成员加入。");
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

    private void QuickScanLog_Click(object sender, RoutedEventArgs e)
    {
        QuickScanLogAndStart();
    }

    private bool QuickScanLogAndStart()
    {
        var logPath = IdentityInitialization.FindDefaultGameLog();
        if (string.IsNullOrWhiteSpace(logPath))
        {
            NetworkStatusText.Text = "快速扫描未找到 Game.log";
            RefreshOnboardingSupportPanel();

            var dialog = new GuideHintWindow(
                "未找到 Game.log",
                "没有在各磁盘的 StarCitizen\\LIVE\\Game.log 找到日志。请确认游戏安装位置，或点击“选择日志”手动选择。")
            {
                Owner = this
            };
            dialog.ShowDialog();
            return false;
        }

        StartWatching(logPath);
        NetworkStatusText.Text = $"已扫描到 Game.log：{logPath}";
        RefreshOnboardingSupportPanel();
        RefreshHeaderStatusBar();
        return true;
    }

    private void StartWatching(string logPath)
    {
        _watcher?.Dispose();
        _logPath = logPath;
        LogPathBox.Text = logPath;
        SaveCurrentConfig();
        AppendOutput($"正在读取日志：{Path.GetFileName(logPath)}");

        foreach (var line in ReadSharedLines(logPath))
        {
            ApplyLine(line, output: false);
        }

        RenderState();
        RefreshOnboardingSupportPanel();
        RefreshHeaderStatusBar();

        _watcher = new GameLogWatcher(logPath, replayExistingLines: false, line =>
        {
            Dispatcher.Invoke(() => ApplyLine(line, output: true));
        });
        _watcher.Start();
    }

    private void ApplyLine(string line, bool output)
    {
        var gameServerChanged = TryUpdateGameServerFromLine(line);
        var fleetEvent = _parser.TryParse(line);
        if (fleetEvent is null)
        {
            if (gameServerChanged)
            {
                RefreshHeaderStatusBar();
                if (output)
                {
                    AppendOutput($"游戏服务器：{_gameServerRegion}");
                }
            }

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
            _isGameProcessRunning = true;
            _lastProcessDrivenRenderAt = DateTimeOffset.Now;
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
            RefreshOnboardingSupportPanel();
            RefreshHeaderStatusBar();
        }

        _fleetState.Apply(fleetEvent);
        RenderState();

        if (output)
        {
            var userMessage = FormatLogEventForUser(fleetEvent);
            if (!string.IsNullOrWhiteSpace(userMessage))
            {
                AppendOutput(userMessage);
            }

            if (gameServerChanged)
            {
                AppendOutput($"游戏服务器：{_gameServerRegion}");
            }
        }
    }

    private void RenderState()
    {
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
            if (_hasFleet &&
                !isLocalPlayer &&
                networkSnapshot is not null &&
                !IsSameFleet(networkSnapshot.Fleet))
            {
                continue;
            }

            var online = player.Online && (!isLocalPlayer || _isGameProcessRunning);
            var rawShip = online ? ShipNameLocalizer.NormalizeCode(player.Ship) : "Unknown";
            var shipConfidence = player.ShipConfidence;
            var locationConfidence = player.LocationConfidence;
            var rawLocation = online ? FormatRawLocation(player.Location, player.NavigationTarget) : "Unknown";
            var displayLocation = online ? FormatLocationInference(rawLocation, locationConfidence) : "地点：未知";
            if (!isLocalPlayer && networkSnapshot is not null)
            {
                rawShip = online ? ShipNameLocalizer.NormalizeCode(networkSnapshot.Ship) : "Unknown";
                shipConfidence = string.IsNullOrWhiteSpace(networkSnapshot.ShipConfidence)
                    ? "Low"
                    : networkSnapshot.ShipConfidence!;
                locationConfidence = string.IsNullOrWhiteSpace(networkSnapshot.LocationConfidence)
                    ? "Low"
                    : networkSnapshot.LocationConfidence!;
                rawLocation = online && !string.IsNullOrWhiteSpace(networkSnapshot.Location)
                    ? FormatRawLocation(networkSnapshot.Location!, "")
                    : rawLocation;
                displayLocation = online ? FormatLocationInference(rawLocation, locationConfidence) : "地点：未知";
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
                online ? FormatShipInference(displayShip, shipConfidence) : "飞船：未知",
                displayLocation,
                playerCallsign,
                IsLocalPlayer(player.Name) ? _avatarPath : networkSnapshot?.AvatarImageData,
                GetInitials(player.Name),
                playerSquadName,
                GetFleetRole(player.Name, playerCallsign),
                GetFleetNameBrush(player.Name),
                rawShip,
                shipConfidence,
                locationConfidence,
                rawLocation));
        }

        TotalMembersText.Text = _players.Count.ToString();
        OnlineMembersText.Text = _players.Count(player => player.Status == "Online").ToString();
        RefreshFleetShipInventory();
        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        RefreshFleetMemberManagement();
        RefreshFleetApplications();
        RefreshSquadActionButtons();
        RefreshOverlayWindow();

        var local = _players.FirstOrDefault(player =>
            player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase));
        if (local is not null)
        {
            var shipText = FormatUnknownForUser(local.Ship);
            var locationText = local.Location;
            var statusText = local.Status.Equals("Online", StringComparison.OrdinalIgnoreCase)
                ? "在线"
                : "离线";
            ShipStateText.Text =
                $"飞船：{shipText}{Environment.NewLine}" +
                $"{locationText}{Environment.NewLine}" +
                $"状态：{statusText}";
        }

        RefreshHeaderStatusBar();
    }

    private static string FormatShipInference(string ship, string confidence)
    {
        if (string.IsNullOrWhiteSpace(ship) ||
            ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "飞船：未知";
        }

        return confidence.Equals("Low", StringComparison.OrdinalIgnoreCase)
            ? $"可能在：{ship}"
            : $"飞船：{ship}";
    }

    private string FormatLocationInference(string location, string? confidence)
    {
        var displayLocation = FormatLocationForUser(location);
        if (displayLocation.Equals("未知", StringComparison.OrdinalIgnoreCase))
        {
            return "地点：未知";
        }

        return confidence switch
        {
            { } value when value.Equals("High", StringComparison.OrdinalIgnoreCase) => $"地点：{displayLocation}",
            { } value when value.Equals("Medium", StringComparison.OrdinalIgnoreCase) => $"可能在：{displayLocation}",
            { } value when value.Equals("Low", StringComparison.OrdinalIgnoreCase) => $"可能离开：{displayLocation}",
            _ => $"可能离开：{displayLocation}"
        };
    }

    private static int LocationEvidenceScoreFromConfidence(string? confidence)
    {
        return confidence switch
        {
            { } value when value.Equals("High", StringComparison.OrdinalIgnoreCase) => 85,
            { } value when value.Equals("Medium", StringComparison.OrdinalIgnoreCase) => 55,
            { } value when value.Equals("Low", StringComparison.OrdinalIgnoreCase) => 20,
            _ => 15
        };
    }

    private string GetFleetRole(string playerName, string? callsign)
    {
        if (IsFleetCommander(playerName, callsign))
        {
            return "舰队指挥官";
        }

        var permission = GetFleetPermission(playerName, callsign);
        return permission is not null && permission.PermissionEnabled
            ? NormalizeRoleTitle(permission.RoleTitle)
            : "成员";
    }

    private System.Windows.Media.Brush GetFleetNameBrush(string playerName)
    {
        if (IsFleetCommander(playerName, IsLocalPlayer(playerName) ? _callsign : null))
        {
            return FindBrush("FleetCommanderNameBrush", Brushes.Gold);
        }

        var permission = GetFleetPermission(playerName);
        return permission is not null && permission.PermissionEnabled
            ? Brushes.MediumPurple
            : FindBrush("PrimaryTextBrush", Brushes.White);
    }

    private System.Windows.Media.Brush GetFleetRoleBrush(string playerName, string? callsign)
    {
        if (IsFleetCommander(playerName, callsign))
        {
            return FindBrush("FleetCommanderNameBrush", Brushes.Gold);
        }

        var permission = GetFleetPermission(playerName, callsign);
        return permission is not null && permission.PermissionEnabled
            ? Brushes.MediumPurple
            : FindBrush("MutedTextBrush", Brushes.LightGray);
    }

    private LocalFleetMemberPermission? GetFleetPermission(string? playerName, string? callsign = null)
    {
        var aliases = EnumerateIdentityAliases(playerName, callsign).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases)
        {
            if (_fleetMemberPermissions.TryGetValue(alias, out var direct))
            {
                return direct;
            }
        }

        return _fleetMemberPermissions.Values.FirstOrDefault(permission =>
            aliases.Contains(permission.GameName) ||
            (!string.IsNullOrWhiteSpace(permission.Callsign) && aliases.Contains(permission.Callsign)) ||
            aliases.Contains(FormatCommanderName(permission.Callsign, permission.GameName)));
    }

    private LocalFleetMemberPermission? GetCurrentUserFleetPermission()
    {
        foreach (var alias in EnumerateLocalIdentities())
        {
            var permission = GetFleetPermission(alias);
            if (permission is not null)
            {
                return permission;
            }
        }

        return null;
    }

    private bool HasCurrentUserFleetPermission(Func<LocalFleetMemberPermission, bool> predicate)
    {
        if (IsCurrentUserFleetCommander())
        {
            return true;
        }

        var permission = GetCurrentUserFleetPermission();
        return permission is not null &&
               permission.PermissionEnabled &&
               predicate(permission);
    }

    private bool CanCurrentUserOpenFleetManagement()
    {
        return _hasFleet &&
               (IsCurrentUserFleetCommander() ||
                HasCurrentUserFleetPermission(permission =>
                    permission.CanManageFleetInfo ||
                    permission.CanPublishTasks ||
                    permission.CanPublishPlans ||
                    permission.CanRemoveMembers));
    }

    private bool CanCurrentUserManageFleetInfo()
    {
        return HasCurrentUserFleetPermission(permission => permission.CanManageFleetInfo);
    }

    private bool CanCurrentUserPublishTasks()
    {
        return HasCurrentUserFleetPermission(permission => permission.CanPublishTasks);
    }

    private bool CanCurrentUserPublishPlans()
    {
        return HasCurrentUserFleetPermission(permission => permission.CanPublishPlans);
    }

    private bool CanCurrentUserRemoveMembers()
    {
        return HasCurrentUserFleetPermission(permission => permission.CanRemoveMembers);
    }

    private void RefreshFleetManagementPermissions()
    {
        if (ManageFleetTab is null)
        {
            return;
        }

        var canOpenManagement = CanCurrentUserOpenFleetManagement();
        ManageFleetTab.Visibility = canOpenManagement ? Visibility.Visible : Visibility.Collapsed;
        if (!canOpenManagement)
        {
            if (MainTabs is not null && MainTabs.SelectedItem == ManageFleetTab)
            {
                MainTabs.SelectedItem = FleetTab;
                SetActiveNav(MyFleetNavButton);
            }

            return;
        }

        var canManageFleetInfo = CanCurrentUserManageFleetInfo();
        var canPublishTasks = CanCurrentUserPublishTasks();
        var canPublishPlans = CanCurrentUserPublishPlans();
        var canRemoveMembers = CanCurrentUserRemoveMembers();
        var isCommander = IsCurrentUserFleetCommander();

        SetManageFleetTabVisibility(ManageFleetNoticeTab, canManageFleetInfo);
        SetManageFleetTabVisibility(ManageFleetProfileTab, canManageFleetInfo);
        SetManageFleetTabVisibility(FleetApplicationsTab, canManageFleetInfo);
        SetManageFleetTabVisibility(ManageFleetTaskTab, canPublishTasks);
        SetManageFleetTabVisibility(ManageFleetPlanTab, canPublishPlans);
        SetManageFleetTabVisibility(ManageFleetMembersTab, isCommander || canRemoveMembers);
        SetManageFleetTabVisibility(ManageFleetLogTab, true);
        SetManageFleetTabVisibility(ManageFleetShipsTab, true);
        SetManageFleetTabVisibility(ManageFleetDisbandTab, isCommander);
        SelectFirstVisibleManageFleetTab();
    }

    private static void SetManageFleetTabVisibility(TabItem? tab, bool isVisible)
    {
        if (tab is not null)
        {
            tab.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectFirstVisibleManageFleetTab()
    {
        if (ManageFleetTabs is null)
        {
            return;
        }

        if (ManageFleetTabs.SelectedItem is TabItem selected &&
            selected.Visibility == Visibility.Visible)
        {
            return;
        }

        foreach (var item in ManageFleetTabs.Items.OfType<TabItem>())
        {
            if (item.Visibility == Visibility.Visible)
            {
                ManageFleetTabs.SelectedItem = item;
                return;
            }
        }
    }

    private IEnumerable<string> EnumerateLocalIdentities()
    {
        return EnumerateIdentityAliases(_localPlayer, _callsign)
            .Concat(EnumerateIdentityAliases(_accountName, null));
    }

    private static IEnumerable<string> EnumerateIdentityAliases(string? playerName, string? callsign)
    {
        foreach (var value in new[] { playerName, callsign })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            yield return trimmed;

            var gameName = GetGameNameFromDisplayName(trimmed);
            if (!string.IsNullOrWhiteSpace(gameName))
            {
                yield return gameName;
            }

            var displayCallsign = GetCallsignFromDisplayName(trimmed);
            if (!string.IsNullOrWhiteSpace(displayCallsign))
            {
                yield return displayCallsign;
            }
        }

        if (!string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(callsign))
        {
            yield return FormatCommanderName(callsign, playerName);
        }
    }

    private static string NormalizeRoleTitle(string? value)
    {
        var role = string.IsNullOrWhiteSpace(value) ? "授权成员" : value.Trim();
        var builder = new StringBuilder();
        var weight = 0;
        foreach (var character in role)
        {
            var nextWeight = IsCjk(character) ? 2 : 1;
            if (weight + nextWeight > 14)
            {
                break;
            }

            builder.Append(character);
            weight += nextWeight;
        }

        return builder.Length == 0 ? "授权成员" : builder.ToString();
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

    private static string FormatRawLocation(string location, string navigationTarget)
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

    private string FormatLocation(string location)
    {
        return LocationNameLocalizer.DisplayName(location, _language);
    }

    private string FormatLogEventForUser(FleetEvent fleetEvent)
    {
        var player = FormatPlayerForUser(fleetEvent.Player);
        return fleetEvent.Type switch
        {
            FleetEventType.PlayerOnline => $"已识别玩家：{player}",
            FleetEventType.PlayerOffline => $"{player} 已离线",
            FleetEventType.PlayerEnteredShip => $"{player} 进入飞船：{FormatShipForUser(fleetEvent.Ship)}",
            FleetEventType.PlayerExitedShip => $"{player} 离开飞船：{FormatShipForUser(fleetEvent.Ship)}",
            FleetEventType.PlayerControllingShip => $"{player} 进入驾驶位：{FormatShipForUser(fleetEvent.Ship)}",
            FleetEventType.PlayerStoppedDrivingShip => $"{player} 离开驾驶位：{FormatShipForUser(fleetEvent.Ship)}",
            FleetEventType.PlayerLocationChanged => FormatLocationChangeForUser(player, fleetEvent),
            FleetEventType.PlayerNavigationTargetChanged => FormatNavigationTargetForUser(player, fleetEvent),
            FleetEventType.CombatStateChanged => $"{player} 状态：{FormatCombatStateForUser(fleetEvent.CombatState)}",
            FleetEventType.NetworkStateChanged => null,
            FleetEventType.PlayerShipControlSignal => null,
            _ => null
        } ?? string.Empty;
    }

    private string FormatLocationChangeForUser(string player, FleetEvent fleetEvent)
    {
        var location = fleetEvent.Location;
        if (IsQuantumArrivalPlaceholder(location))
        {
            location = _fleetState.Players
                .FirstOrDefault(candidate => candidate.Name.Equals(fleetEvent.Player, StringComparison.OrdinalIgnoreCase))
                ?.Location;
            return $"{player} 抵达：{FormatLocationForUser(location)}";
        }

        return $"{player} 位置更新：{FormatLocationForUser(location)}";
    }

    private string FormatNavigationTargetForUser(string player, FleetEvent fleetEvent)
    {
        var location = FormatLocationForUser(fleetEvent.Location);
        var target = FormatLocationForUser(fleetEvent.NavigationTarget);
        var hasLocation = !location.Equals("未知", StringComparison.OrdinalIgnoreCase);
        var hasTarget = !target.Equals("未知", StringComparison.OrdinalIgnoreCase);

        if (hasLocation && hasTarget)
        {
            return $"{player} 设置导航：{location} → {target}";
        }

        if (hasTarget)
        {
            return $"{player} 设置导航目标：{target}";
        }

        if (hasLocation)
        {
            return $"{player} 当前位置：{location}";
        }

        return string.Empty;
    }

    private string FormatShipForUser(string? ship)
    {
        var display = ShipNameLocalizer.DisplayName(ShipNameLocalizer.NormalizeCode(ship), _language);
        return FormatUnknownForUser(display);
    }

    private string FormatLocationForUser(string? location)
    {
        if (string.IsNullOrWhiteSpace(location) ||
            location.Equals("None", StringComparison.OrdinalIgnoreCase) ||
            location.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return "未知";
        }

        var rawLocation = FormatRawLocation(location, "");
        return FormatUnknownForUser(FormatLocation(rawLocation));
    }

    private static string FormatPlayerForUser(string? player)
    {
        return string.IsNullOrWhiteSpace(player) ||
               player.Equals("LocalPlayer", StringComparison.OrdinalIgnoreCase)
            ? "本地玩家"
            : player.Trim();
    }

    private static string FormatUnknownForUser(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("Unknown", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("None", StringComparison.OrdinalIgnoreCase)
            ? "未知"
            : value.Trim();
    }

    private static string FormatCombatStateForUser(string? combatState)
    {
        return combatState switch
        {
            null or "" => "待命",
            "Combat" => "战斗中",
            "Idle" => "待命",
            _ => combatState
        };
    }

    private static bool IsQuantumArrivalPlaceholder(string? location)
    {
        return location?.Equals("Arrived - awaiting location confirmation", StringComparison.OrdinalIgnoreCase) == true;
    }

    private bool TryUpdateGameServerFromLine(string line)
    {
        var match = JoinPuShardRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        var shard = match.Groups["shard"].Value.Trim();
        if (string.IsNullOrWhiteSpace(shard))
        {
            return false;
        }

        var region = MapGameServerRegion(shard);
        if (string.Equals(_gameServerShard, shard, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(_gameServerRegion, region, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _gameServerShard = shard;
        _gameServerRegion = region;
        return true;
    }

    private static string MapGameServerRegion(string shard)
    {
        var normalized = shard.ToLowerInvariant();

        if (normalized.Contains("use") ||
            normalized.Contains("usw") ||
            normalized.Contains("_us") ||
            normalized.Contains("pub_us"))
        {
            return "美服";
        }

        if (normalized.Contains("eu"))
        {
            return "欧服";
        }

        if (normalized.Contains("aus") ||
            normalized.Contains("_au") ||
            normalized.Contains("oce"))
        {
            return "澳服";
        }

        if (normalized.Contains("asia") ||
            normalized.Contains("apse") ||
            normalized.Contains("_ap") ||
            normalized.Contains("sg") ||
            normalized.Contains("jp") ||
            normalized.Contains("hk"))
        {
            return "亚服";
        }

        return "未知";
    }

    private void RefreshHeaderStatusBar()
    {
        if (HeaderAccountStatusText is null)
        {
            return;
        }

        HeaderAccountStatusText.Text = IsLoggedIn
            ? CompactHeaderText(
                !string.IsNullOrWhiteSpace(_callsign) ? _callsign! :
                string.IsNullOrWhiteSpace(_accountName) ? "已登录" : _accountName!,
                18)
            : "未登录";
        HeaderAccountStatusText.Foreground = IsLoggedIn
            ? FindBrush("AccentBrush", Brushes.DeepSkyBlue)
            : FindBrush("MutedTextBrush", Brushes.LightSlateGray);

        var connectionStatus = GetHeaderConnectionStatus();
        HeaderSyncStatusText.Text = connectionStatus;
        HeaderSyncStatusText.Foreground = connectionStatus is "连接正常" or "同步中"
            ? FindBrush("SquadCommanderNameBrush", Brushes.SpringGreen)
            : connectionStatus == "连接异常"
                ? FindBrush("DangerBrush", Brushes.IndianRed)
                : FindBrush("MutedTextBrush", Brushes.LightSlateGray);

        HeaderGameProcessStatusText.Text = _isGameProcessRunning ? "运行中" : "未运行";
        HeaderGameProcessStatusText.Foreground = _isGameProcessRunning
            ? FindBrush("SquadCommanderNameBrush", Brushes.SpringGreen)
            : FindBrush("MutedTextBrush", Brushes.LightSlateGray);

        HeaderGameServerRegionText.Text = string.IsNullOrWhiteSpace(_gameServerShard)
            ? "未知"
            : _gameServerRegion;
        HeaderGameServerRegionText.ToolTip = string.IsNullOrWhiteSpace(_gameServerShard)
            ? "从 Game.log 的 Join PU 信息识别游戏服务器区域"
            : $"服务器区域：{_gameServerRegion}";
        RefreshOnboardingSupportPanel();
    }

    private void RefreshOnboardingSupportPanel()
    {
        var identityStatus = GetIdentityInitializationStatus();

        if (HomeIdentityStatusText is not null)
        {
            HomeIdentityStatusText.Text = identityStatus.StatusText;
            HomeIdentityStatusText.ToolTip = identityStatus.DetailText;
        }

        if (HomeLoginButton is not null)
        {
            HomeLoginButton.Visibility = IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
        }

        var local = string.IsNullOrWhiteSpace(_localPlayer)
            ? null
            : _players.FirstOrDefault(player => player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase));

        if (HomeAccountStatusText is not null)
        {
            HomeAccountStatusText.Text = IsLoggedIn
                ? $"{(_callsign ?? _accountName ?? "星海舰桥账号")}"
                : "未登录";
        }

        if (HomeConnectionStatusText is not null)
        {
            HomeConnectionStatusText.Text = GetHeaderConnectionStatus();
        }

        if (HomeFleetStatusText is not null)
        {
            HomeFleetStatusText.Text = _hasFleet
                ? string.IsNullOrWhiteSpace(_fleetCode) ? _fleetName : $"{_fleetName} / {_fleetCode}"
                : "未加入舰队";
        }

        if (HomeSquadStatusText is not null)
        {
            HomeSquadStatusText.Text = _joinedSquad is not null
                ? _joinedSquad.Name
                : _hasFleet
                    ? "未加入小队"
                    : "需要先加入舰队";
        }

        if (HomeOnlineStatusText is not null)
        {
            HomeOnlineStatusText.Text = _isGameProcessRunning ? "游戏运行中" : "游戏未运行";
        }

        if (HomeShipStatusText is not null)
        {
            HomeShipStatusText.Text = string.IsNullOrWhiteSpace(local?.RawShip) ? "Unknown" : local.RawShip;
        }

        if (HomeLocationStatusText is not null)
        {
            HomeLocationStatusText.Text = string.IsNullOrWhiteSpace(local?.RawLocation) ? "Unknown" : local.RawLocation;
        }

        if (HomeOverlayStatusText is not null)
        {
            HomeOverlayStatusText.Text = _overlayWindow?.IsVisible == true
                ? "正在显示"
                : _overlayLayout.Count > 0
                    ? "已配置"
                    : "未配置";
        }

        if (GuideProgressPanel is null)
        {
            return;
        }

        var showGuideProgress = !OnboardingState.IsCompleted() || !identityStatus.IsComplete;
        GuideProgressPanel.Visibility = showGuideProgress ? Visibility.Visible : Visibility.Collapsed;

        if (HomeStatusPanel is not null)
        {
            Grid.SetColumn(HomeStatusPanel, showGuideProgress ? 1 : 0);
            Grid.SetColumnSpan(HomeStatusPanel, showGuideProgress ? 1 : 2);
            HomeStatusPanel.Margin = showGuideProgress ? new Thickness(9, 0, 0, 0) : new Thickness(0);
        }

        if (!showGuideProgress)
        {
            return;
        }

        GuideAccountStatusText.Text = IsLoggedIn
            ? $"已登录：{(_callsign ?? _accountName ?? "星海舰桥账号")}"
            : "未登录";
        GuideLogStatusText.Text = identityStatus.StatusText;
        GuideLogStatusText.ToolTip = identityStatus.DetailText;
        GuideFleetStatusText.Text = _hasFleet
            ? $"已加入：{_fleetName}"
            : "尚未加入舰队";
        GuideSquadStatusText.Text = _joinedSquad is not null
            ? $"已加入：{_joinedSquad.Name}"
            : _hasFleet
                ? "尚未加入小队"
                : "需要先加入舰队";
        GuideOverlayStatusText.Text = _overlayLayout.Count > 0
            ? "已保存 Overlay 布局"
            : "尚未保存 Overlay 布局";
    }

    private string GetHeaderConnectionStatus()
    {
        if (!IsLoggedIn)
        {
            return "浏览模式";
        }

        if (_isNetworkSyncRunning)
        {
            return "同步中";
        }

        var status = NetworkStatusText?.Text ?? "";
        if (status.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("异常", StringComparison.OrdinalIgnoreCase))
        {
            return "连接异常";
        }

        if (status.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("已登录", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("已完成", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("已上传", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("已拉取", StringComparison.OrdinalIgnoreCase) ||
            NetworkAutoSyncCheck.IsChecked == true)
        {
            return "连接正常";
        }

        return "待同步";
    }

    private static string CompactHeaderText(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var trimmed = value.Trim().Replace(Environment.NewLine, " / ");
        return trimmed.Length <= maxLength
            ? trimmed
            : $"{trimmed[..Math.Max(0, maxLength - 1)]}…";
    }

    private void UpdateLocalOnlineStateFromGameProcess()
    {
        var wasRunning = _isGameProcessRunning;
        _isGameProcessRunning = IsStarCitizenRunning();
        var now = DateTimeOffset.Now;
        var shouldRefresh = wasRunning != _isGameProcessRunning ||
                            now - _lastProcessDrivenRenderAt >= TimeSpan.FromSeconds(30);
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            if (shouldRefresh)
            {
                _lastProcessDrivenRenderAt = now;
                RenderState();
            }

            return;
        }

        if (_isGameProcessRunning)
        {
            if (shouldRefresh)
            {
                _lastProcessDrivenRenderAt = now;
                RenderState();
            }

            return;
        }

        if (wasRunning)
        {
            _fleetState.Apply(new FleetEvent(FleetEventType.PlayerOffline, _localPlayer));
        }

        if (shouldRefresh)
        {
            _lastProcessDrivenRenderAt = now;
            RenderState();
        }

        ProfileStatusText.Text = "Offline - game process not detected";
        RefreshHeaderStatusBar();
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

        foreach (var processName in processNames)
        {
            try
            {
                if (Process.GetProcessesByName(processName).Length > 0)
                {
                    return true;
                }
            }
            catch
            {
                // Ignore transient process-list access issues.
            }
        }

        return false;
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

    private void ClearAuthenticatedLocalState()
    {
        _authToken = null;
        _accountName = null;
        _callsign = null;
        _avatarPath = null;
        _allowEmailNotifications = true;
        _ownedShips.Clear();
        _players.Clear();
        _mySquadMembers.Clear();
        _networkSnapshots.Clear();
        _networkFleets.Clear();
        _allNetworkFleets.Clear();
        _remoteFleetShips.Clear();
        _fleetShipInventory.Clear();
        _fleetState.Clear();
        ClearFleetState();
    }

    private void LogoutButton_Click(object sender, RoutedEventArgs e)
    {
        NetworkAutoSyncCheck.IsChecked = false;
        _networkSyncTimer.Stop();
        ClearAuthenticatedLocalState();
        SaveCurrentConfig();
        RefreshAccountPanel();
        RenderState();
        LoginStatusText.Text = "已退出登录，当前为浏览模式";
        NetworkStatusText.Text = "浏览模式：同步已关闭";
        RefreshHeaderStatusBar();
    }

    private async void NetworkPushButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("上传本机状态需要先登录。"))
        {
            return;
        }

        await PushLocalSnapshotAsync(pushFleetDirectory: false);
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
            RefreshHeaderStatusBar();
            return;
        }

        if (_isNetworkSyncRunning)
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _nextNetworkSyncAt)
        {
            return;
        }

        _isNetworkSyncRunning = true;
        try
        {
            var pulledFleets = await PullNetworkFleetsAsync(silent: true);
            var pulledPlayers = await PullNetworkSnapshotsAsync(silent: true);
            var pushedLocal = await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
            if (pushedLocal || pulledFleets || pulledPlayers)
            {
                _networkSyncFailureCount = 0;
                _nextNetworkSyncAt = DateTimeOffset.MinValue;
                RefreshHeaderStatusBar();
            }
            else
            {
                DeferNetworkSync();
                RefreshHeaderStatusBar();
            }
        }
        catch
        {
            DeferNetworkSync();
            RefreshHeaderStatusBar();
        }
        finally
        {
            _isNetworkSyncRunning = false;
        }
    }

    private void DeferNetworkSync()
    {
        _networkSyncFailureCount = Math.Min(_networkSyncFailureCount + 1, 5);
        var delaySeconds = Math.Min(15 * _networkSyncFailureCount, 90);
        _nextNetworkSyncAt = DateTimeOffset.UtcNow.AddSeconds(delaySeconds);
    }

    private CancellationTokenSource BeginSyncStatusSlowNotice()
    {
        var previous = _syncStatusOverlayCts;
        _syncStatusOverlayCts = null;
        previous?.Cancel();
        previous?.Dispose();
        var cts = new CancellationTokenSource();
        _syncStatusOverlayCts = cts;
        _ = ShowSlowSyncNoticeAsync(cts.Token);
        return cts;
    }

    private async Task ShowSlowSyncNoticeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested || SyncStatusOverlay.Visibility != Visibility.Visible)
                {
                    return;
                }

                SyncOverlayTitleText.Text = "同步仍在进行";
                SyncOverlayDetailText.Text = "服务器响应较慢，你可以先浏览本地缓存。同步完成后界面会自动刷新。";
            });
        }
        catch (TaskCanceledException)
        {
            // Expected when the sync finishes quickly.
        }
    }

    private void ShowSyncStatusOverlay(string title, string detail, bool showRetry)
    {
        SyncOverlayTitleText.Text = title;
        SyncOverlayDetailText.Text = detail;
        SyncOverlayRetryButton.Visibility = showRetry ? Visibility.Visible : Visibility.Collapsed;
        SyncStatusOverlay.Visibility = Visibility.Visible;
    }

    private void HideSyncStatusOverlay()
    {
        var slowNotice = _syncStatusOverlayCts;
        _syncStatusOverlayCts = null;
        slowNotice?.Cancel();
        slowNotice?.Dispose();
        SyncStatusOverlay.Visibility = Visibility.Collapsed;
    }

    private async void SyncOverlayRetryButton_Click(object sender, RoutedEventArgs e)
    {
        SyncOverlayRetryButton.IsEnabled = false;
        try
        {
            await AutoConnectNetworkAsync();
        }
        finally
        {
            SyncOverlayRetryButton.IsEnabled = true;
        }
    }

    private async Task RunStartupFlowAsync()
    {
        if (OnboardingState.IsCompleted())
        {
            await InitializeLoginAndNetworkAsync();
            return;
        }

        var guide = new OnboardingWindow(
            IsLoggedIn,
            !string.IsNullOrWhiteSpace(_logPath) && File.Exists(_logPath),
            _hasFleet,
            _joinedSquad is not null,
            _overlayLayout.Count > 0)
        {
            Owner = this
        };

        var result = guide.ShowDialog();
        if (guide.ShouldMarkCompleted)
        {
            OnboardingState.MarkCompleted();
            RefreshOnboardingSupportPanel();
        }

        if (result == true)
        {
            await HandleOnboardingActionAsync(guide.NextAction);
            return;
        }

        if (IsLoggedIn)
        {
            await AutoConnectNetworkAsync();
        }

        RefreshOnboardingSupportPanel();
    }

    private async Task HandleOnboardingActionAsync(OnboardingNextAction action)
    {
        switch (action)
        {
            case OnboardingNextAction.Login:
                await ShowLoginDialogAsync();
                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                }
                break;
            case OnboardingNextAction.SelectLog:
                SelectLog_Click(this, new RoutedEventArgs());
                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                }
                break;
            case OnboardingNextAction.FindFleet:
                MainTabs.SelectedItem = FindFleetTab;
                SetActiveNav(FindFleetNavButton);
                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                    await PullNetworkFleetsAsync(silent: true);
                }
                break;
            case OnboardingNextAction.CreateFleet:
                if (!IsLoggedIn)
                {
                    await ShowLoginDialogAsync();
                }

                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                    FleetCreateButton_Click(this, new RoutedEventArgs());
                }
                break;
            case OnboardingNextAction.MySquad:
                MainTabs.SelectedItem = MySquadTab;
                SetActiveNav(MySquadNavButton);
                if (_hasFleet)
                {
                    ShowOneTimeGuideHint(
                        "squad-page",
                        "小队引导",
                        "这里可以创建或加入小队。加入小队后，Overlay 会优先展示同小队成员，任务分配也会按小队组织。");
                }

                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                }
                break;
            case OnboardingNextAction.Overlay:
                MainTabs.SelectedItem = OverlayEditTab;
                SetActiveNav(OverlayNavButton);
                RenderOverlayEditor();
                ShowOneTimeGuideHint(
                    "overlay-page",
                    "Overlay 引导",
                    "这里可以拖拽布局、切换船厂风格、设置透明度、准星和显示模块。保存布局后，游戏内 Overlay 会使用这套配置。");
                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                }
                break;
            default:
                if (IsLoggedIn)
                {
                    await AutoConnectNetworkAsync();
                }
                break;
        }

        RefreshOnboardingSupportPanel();
    }

    private void ShowOneTimeGuideHint(string hintId, string title, string message)
    {
        if (OnboardingState.IsHintCompleted(hintId))
        {
            return;
        }

        var dialog = new GuideHintWindow(title, message)
        {
            Owner = this
        };
        dialog.ShowDialog();
        OnboardingState.MarkHintCompleted(hintId);
        RefreshOnboardingSupportPanel();
    }

    private void GuideCompleteButton_Click(object sender, RoutedEventArgs e)
    {
        OnboardingState.MarkCompleted();
        RefreshOnboardingSupportPanel();
    }

    private async void GuideLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.Login);
    }

    private async void GuideSelectLogButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.SelectLog);
    }

    private async void GuideQuickScanLogButton_Click(object sender, RoutedEventArgs e)
    {
        QuickScanLogAndStart();
        if (IsLoggedIn)
        {
            await AutoConnectNetworkAsync();
        }
    }

    private async void GuideFindFleetButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.FindFleet);
    }

    private async void GuideCreateFleetButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.CreateFleet);
    }

    private async void GuideSquadButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.MySquad);
    }

    private async void GuideOverlayButton_Click(object sender, RoutedEventArgs e)
    {
        await HandleOnboardingActionAsync(OnboardingNextAction.Overlay);
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
            RefreshHeaderStatusBar();
            return;
        }

        ShowSyncStatusOverlay(
            "正在同步服务器数据",
            "正在同步账号、舰队、小队、任务和玩家状态...",
            showRetry: false);
        var slowNotice = BeginSyncStatusSlowNotice();

        var connected = await TestNetworkAsync(silent: true);
        var pulledFleets = false;
        var pulledPlayers = false;
        var pushedLocal = false;
        if (connected)
        {
            if (IsLoggedIn && !await ValidateSavedSessionAsync())
            {
                HideSyncStatusOverlay();
                return;
            }

            pulledFleets = await PullNetworkFleetsAsync(silent: true);
            pulledPlayers = await PullNetworkSnapshotsAsync(silent: true);
            pushedLocal = await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
        }

        if (connected || pulledFleets || pulledPlayers || pushedLocal)
        {
            NetworkAutoSyncCheck.IsChecked = true;
            _networkSyncTimer.Start();
            LoginStatusText.Text = string.IsNullOrWhiteSpace(_accountName)
                ? "已连接服务器"
                : $"已登录：{_accountName}";
            NetworkStatusText.Text = "已完成启动同步";
            RefreshAccountPanel();
            RefreshHeaderStatusBar();
            HideSyncStatusOverlay();
            return;
        }

        slowNotice.Cancel();
        slowNotice.Dispose();
        if (ReferenceEquals(_syncStatusOverlayCts, slowNotice))
        {
            _syncStatusOverlayCts = null;
        }

        NetworkStatusText.Text = "启动同步失败，当前显示本地缓存";
        RefreshHeaderStatusBar();
        ShowSyncStatusOverlay(
            "同步失败",
            "无法连接服务器，当前显示本地缓存。请检查网络后重试。",
            showRetry: true);
    }

    private async Task<bool> ValidateSavedSessionAsync()
    {
        if (!IsLoggedIn)
        {
            return true;
        }

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/auth/profile",
                new ProfileUpdateRequest(_callsign, _allowEmailNotifications));
            if (HandleAuthorizationFailure(response.StatusCode, "登录校验", silent: true))
            {
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                return true;
            }

            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
            if (auth is not null && !string.IsNullOrWhiteSpace(auth.Token))
            {
                ApplyAuthResponse(auth);
                SaveCurrentConfig();
            }

            return true;
        }
        catch (Exception ex) when (HandleAuthorizationFailure(ex, "登录校验", silent: true))
        {
            return false;
        }
        catch
        {
            return true;
        }
    }

    private Task ShowLoginDialogAsync()
    {
        if (_isLoginDialogOpen)
        {
            return Task.CompletedTask;
        }

        _isLoginDialogOpen = true;
        var dialog = new LoginWindow(_accountName) { Owner = this };
        dialog.SendVerificationCodeAsync = RequestVerificationCodeAsync;
        dialog.AuthenticateAsync = async request =>
        {
            var path = request.IsRegister ? "api/auth/register" : "api/auth/login";
            var actionName = request.IsRegister ? "注册" : "登录";
            var error = await AuthenticateAsync(
                path,
                actionName,
                request.Email,
                request.Password,
                request.Email,
                request.VerificationCode,
                request.Callsign);
            return new LoginWindowAuthResult(error is null, error ?? $"{actionName}成功");
        };
        try
        {
            var result = dialog.ShowDialog();
            if (result != true)
            {
                RefreshAccountPanel();
                return Task.CompletedTask;
            }

            if (dialog.IsSkipped)
            {
                LoginStatusText.Text = "已进入浏览模式";
                RefreshAccountPanel();
                return Task.CompletedTask;
            }
        }
        finally
        {
            _isLoginDialogOpen = false;
        }

        return Task.CompletedTask;
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
                var error = await ReadResponseErrorAsync(response);
                return FormatActionFailure("发送验证码", MapVerificationError(error));
            }

            return "验证码已发送，10 分钟内有效。";
        }
        catch (TaskCanceledException)
        {
            return "发送失败：连接服务器超时，请稍后再试。";
        }
        catch (Exception ex)
        {
            return $"发送失败：{MapNetworkException(ex)}";
        }
    }

    private async Task<string?> AuthenticateAsync(
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
            return "请输入登录邮箱和密码。";
        }

        if (path.EndsWith("register", StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrWhiteSpace(authEmail) ||
             string.IsNullOrWhiteSpace(verificationCode) ||
             string.IsNullOrWhiteSpace(callsign)))
        {
            return "注册需要登录邮箱、呼号和验证码。";
        }

        try
        {
            var request = new AuthRequest(email.Trim(), password, _localPlayer, authEmail?.Trim(), verificationCode?.Trim(), callsign?.Trim());
            var response = await _networkClient.PostAsJsonAsync(BuildNetworkUri(path), request);
            if (!response.IsSuccessStatusCode)
            {
                var serverError = await ReadResponseErrorAsync(response);
                return MapAuthenticationError(response.StatusCode, serverError, path.EndsWith("register", StringComparison.OrdinalIgnoreCase));
            }

            var auth = await response.Content.ReadFromJsonAsync<AuthResponse>();
            if (auth is null || string.IsNullOrWhiteSpace(auth.Token))
            {
                return $"{actionName}失败：服务器响应无效。";
            }

            ApplyAuthResponse(auth);
            LoginStatusText.Text = $"{actionName}成功：{_accountName}";
            NetworkStatusText.Text = "已登录并连接服务器";
            SaveCurrentConfig();
            RefreshAccountPanel();
            NetworkAutoSyncCheck.IsChecked = true;
            _networkSyncTimer.Start();
            await PullNetworkFleetsAsync(silent: true);
            await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
            return null;
        }
        catch (TaskCanceledException)
        {
            return "连接服务器超时，请检查网络或稍后再试。";
        }
        catch (Exception ex)
        {
            var message = MapNetworkException(ex);
            LoginStatusText.Text = $"{actionName}失败：{message}";
            NetworkStatusText.Text = $"{actionName}失败";
            return $"{actionName}失败：{message}";
        }
    }

    private static string MapVerificationError(string serverError)
    {
        var normalized = serverError.ToLowerInvariant();
        if (normalized.Contains("rate") || normalized.Contains("60"))
        {
            return "验证码发送过于频繁，请稍后再试。";
        }

        if (normalized.Contains("email service") || normalized.Contains("smtp") || normalized.Contains("not configured"))
        {
            return "服务器邮件服务未配置或暂时不可用。";
        }

        if (normalized.Contains("email is required"))
        {
            return "请输入邮箱地址。";
        }

        return NormalizeServerError(serverError, "发送验证码");
    }

    private static string MapAuthenticationError(HttpStatusCode statusCode, string serverError, bool isRegister)
    {
        var actionName = isRegister ? "注册" : "登录";
        var cleanedServerError = NormalizeServerError(serverError, actionName);
        var normalized = serverError.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(cleanedServerError) &&
            ContainsUserFacingError(cleanedServerError))
        {
            return FormatActionFailure(actionName, cleanedServerError);
        }

        if (statusCode == HttpStatusCode.Unauthorized)
        {
            return isRegister
                ? "注册信息未通过验证，请检查验证码后再试。"
                : "邮箱未注册或密码错误。";
        }

        if (statusCode == HttpStatusCode.NotFound)
        {
            return "当前服务器版本缺少登录接口，请联系管理员更新服务器。";
        }

        if (statusCode == HttpStatusCode.Conflict || normalized.Contains("already registered"))
        {
            return "该邮箱已注册，请直接登录。";
        }

        if (normalized.Contains("verification code"))
        {
            return "验证码无效或已过期。";
        }

        if (normalized.Contains("password must"))
        {
            return "密码至少需要 6 位。";
        }

        if (normalized.Contains("callsign"))
        {
            return "呼号过长，请缩短后再试。";
        }

        if (normalized.Contains("email") && normalized.Contains("required"))
        {
            return "请输入登录邮箱和密码。";
        }

        return FormatActionFailure(actionName, cleanedServerError);
    }

    private static string FormatActionFailure(string actionName, string? reason)
    {
        var cleaned = NormalizeServerError(reason, actionName);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "服务器没有返回详细原因。";
        }

        return $"{actionName}失败：{cleaned}";
    }

    private static string NormalizeServerError(string? serverError, string actionName)
    {
        var cleaned = (serverError ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return "";
        }

        var prefixes = new[]
        {
            $"{actionName}失败：",
            $"{actionName}失败:",
            "发送失败：",
            "发送失败:"
        };

        foreach (var prefix in prefixes)
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return cleaned[prefix.Length..].Trim();
            }
        }

        return cleaned;
    }

    private static bool ContainsUserFacingError(string message)
    {
        return message.Contains('：') ||
               message.Contains("验证码", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("邮箱", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("邮件", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("密码", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("呼号", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SMTP", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("未配置", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("过期", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapNetworkException(Exception exception)
    {
        return exception switch
        {
            TaskCanceledException => "连接服务器超时，请检查网络或稍后再试。",
            HttpRequestException => "无法连接服务器，请检查网络、服务器地址或 HTTPS 配置。",
            _ => exception.Message
        };
    }

    private async Task UpdateProfileAsync()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/auth/profile",
                new ProfileUpdateRequest(_callsign, _allowEmailNotifications));
            if (HandleAuthorizationFailure(response.StatusCode, "个人资料同步", silent: true))
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex) when (HandleAuthorizationFailure(ex, "个人资料同步", silent: true))
        {
            // The handler clears stale authenticated state.
        }
        catch
        {
            // Ignore transient relay errors and keep local profile changes.
        }
    }

    private async Task<bool> TestNetworkAsync(bool silent = false)
    {
        try
        {
            var response = await _networkClient.GetAsync(BuildNetworkUri("health"));
            response.EnsureSuccessStatusCode();
            NetworkStatusText.Text = "连接成功";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | connected={NetworkServerUrlBox.Text.Trim()}");
            }
            await PullNetworkFleetsAsync(silent: true);
            return true;
        }
        catch (TaskCanceledException)
        {
            NetworkStatusText.Text = "连接失败：服务器响应超时";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput("NETWORK | connect failed=timeout");
            }

            return false;
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"连接失败：{MapNetworkException(ex)}";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | connect failed={ex.Message}");
            }

            return false;
        }
    }

    private async Task<bool> PushLocalSnapshotAsync(bool silent = false, bool pushFleetDirectory = false)
    {
        if (!IsLoggedIn)
        {
            if (!silent)
            {
                NetworkStatusText.Text = "浏览模式：登录后才能上传状态";
            }

            return false;
        }

        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            if (!silent)
            {
                NetworkStatusText.Text = "需要先从日志识别玩家名";
            }

            return false;
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
            local?.RawLocation ?? "Unknown",
            local?.LocationConfidence ?? "None",
            DateTimeOffset.UtcNow,
            BuildAvatarImageData(),
            BuildOwnedShipSnapshots());

        try
        {
            var response = await PostNetworkJsonAsync("api/players", snapshot);
            response.EnsureSuccessStatusCode();
            if (pushFleetDirectory)
            {
                await PushFleetDirectoryAsync(silent: true);
            }
            NetworkStatusText.Text = $"已上传：{snapshot.Name}";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | pushed={snapshot.Name}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (HandleAuthorizationFailure(ex, "上传状态", silent))
            {
                return false;
            }

            NetworkStatusText.Text = $"上传失败：{ex.Message}";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | push failed={ex.Message}");
            }

            return false;
        }
    }

    private async Task PushOfflineSnapshotOnShutdownAsync()
    {
        if (!IsLoggedIn || string.IsNullOrWhiteSpace(_localPlayer))
        {
            return;
        }

        _fleetState.Apply(new FleetEvent(FleetEventType.PlayerOffline, _localPlayer));

        var snapshot = new NetworkPlayerSnapshot(
            _localPlayer,
            _callsign,
            _hasFleet ? _fleetName : "No Fleet",
            _joinedSquad?.Name ?? "Unassigned",
            false,
            "Unknown",
            "None",
            "Unknown",
            "None",
            DateTimeOffset.UtcNow,
            BuildAvatarImageData(),
            BuildOwnedShipSnapshots());

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildNetworkUri("api/players"))
            {
                Content = JsonContent.Create(snapshot)
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

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var response = await _networkClient.SendAsync(request, timeout.Token);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // The app must still close even if the relay is unreachable.
        }
    }

    private async Task<bool> PullNetworkSnapshotsAsync(bool silent = false)
    {
        try
        {
            var snapshots = await _relayClient.GetFromJsonAsync<NetworkPlayerSnapshot[]>("api/players") ?? [];
            var snapshotNames = snapshots
                .Select(snapshot => snapshot.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in _networkSnapshots.Keys.Where(name => !snapshotNames.Contains(name)).ToArray())
            {
                _networkSnapshots.Remove(name);
            }

            var retainedPlayerNames = snapshotNames.ToList();
            if (!string.IsNullOrWhiteSpace(_localPlayer))
            {
                retainedPlayerNames.Add(_localPlayer);
            }

            _fleetState.RemovePlayersExcept(retainedPlayerNames);
            foreach (var snapshot in snapshots)
            {
                if (IsLocalNetworkSnapshot(snapshot))
                {
                    ApplyLocalNetworkSnapshot(snapshot);
                    continue;
                }

                ApplyNetworkSnapshot(snapshot);
            }

            RenderState();
            NetworkStatusText.Text = $"已拉取：{snapshots.Length} 名玩家";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | pulled players={snapshots.Length}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (HandleAuthorizationFailure(ex, "拉取玩家", silent))
            {
                return false;
            }

            NetworkStatusText.Text = $"拉取失败：{ex.Message}";
            RefreshHeaderStatusBar();
            if (!silent)
            {
                AppendOutput($"NETWORK | pull failed={ex.Message}");
            }

            return false;
        }
    }

    private async Task<bool> PushFleetDirectoryAsync(bool silent = false)
    {
        if (!IsLoggedIn)
        {
            if (!silent)
            {
                NetworkStatusText.Text = "浏览模式：登录后才能发布舰队";
            }

            return false;
        }

        if (!_hasFleet)
        {
            return false;
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

            return true;
        }
        catch (Exception ex)
        {
            if (HandleAuthorizationFailure(ex, "发布舰队", silent))
            {
                return false;
            }

            if (!silent)
            {
                NetworkStatusText.Text = $"发布舰队失败：{ex.Message}";
                AppendOutput($"NETWORK | push fleet failed={ex.Message}");
            }

            return false;
        }
    }

    private async Task<bool> PushFleetMutationAsync<T>(
        string path,
        T payload,
        string successMessage,
        string failurePrefix,
        bool silent = true)
    {
        if (!IsLoggedIn || !_hasFleet || string.IsNullOrWhiteSpace(_fleetCode))
        {
            return false;
        }

        try
        {
            var response = await PostNetworkJsonAsync(path, payload);
            if (!response.IsSuccessStatusCode)
            {
                if (HandleAuthorizationFailure(response.StatusCode, failurePrefix, silent))
                {
                    return false;
                }

                var error = await ReadResponseErrorAsync(response);
                if (!silent)
                {
                    NetworkStatusText.Text = $"{failurePrefix}：{error}";
                    AppendOutput($"NETWORK | {failurePrefix}={error}");
                }

                return false;
            }

            var snapshot = await response.Content.ReadFromJsonAsync<NetworkFleetSnapshot>();
            if (snapshot is not null)
            {
                MergeNetworkFleetState(snapshot);
            }

            if (!silent)
            {
                NetworkStatusText.Text = successMessage;
                AppendOutput($"NETWORK | {successMessage}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (HandleAuthorizationFailure(ex, failurePrefix, silent))
            {
                return false;
            }

            if (!silent)
            {
                NetworkStatusText.Text = $"{failurePrefix}：{ex.Message}";
                AppendOutput($"NETWORK | {failurePrefix}={ex.Message}");
            }

            return false;
        }
    }

    private Task<bool> PushFleetNoticeAsync(bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/notice",
            new FleetNoticeUpdateRequest(
                _fleetCode,
                _fleetNoticeTitle,
                _fleetNoticeContent,
                BuildFleetEventLogSnapshots()),
            "舰队公告已同步",
            "公告同步失败",
            silent);
    }

    private Task<bool> PushFleetTaskAsync(bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/task",
            new FleetTaskUpdateRequest(
                _fleetCode,
                _fleetCurrentTaskTitle,
                _fleetCurrentTaskBrief,
                _fleetCurrentTaskParticipants,
                _fleetCurrentTaskRally,
                _fleetCurrentTaskShip,
                _fleetCurrentTaskTime,
                _fleetCurrentTaskNoticeRevision,
                BuildFleetTaskHistorySnapshots(),
                BuildFleetEventLogSnapshots()),
            "舰队任务已同步",
            "任务同步失败",
            silent);
    }

    private Task<bool> PushFleetActionPlansAsync(bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/action-plans",
            new FleetActionPlansUpdateRequest(
                _fleetCode,
                BuildActionPlanSnapshots(),
                BuildFleetEventLogSnapshots()),
            "行动计划已同步",
            "行动计划同步失败",
            silent);
    }

    private Task<bool> PushFleetActionPlanJoinAsync(
        FleetActionPlanRow plan,
        ActionPlanParticipantRow participant,
        bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/action-plans/join",
            new FleetActionPlanJoinRequest(
                _fleetCode,
                plan.Id,
                BuildActionPlanParticipantSnapshot(participant)),
            "行动预约已同步",
            "行动预约同步失败",
            silent);
    }

    private Task<bool> PushFleetActionPlanLeaveAsync(
        string planId,
        bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/action-plans/leave",
            new FleetActionPlanLeaveRequest(_fleetCode, planId),
            "行动预约已取消",
            "取消行动预约失败",
            silent);
    }

    private Task<bool> PushFleetMemberPermissionAsync(
        FleetMemberManagementRow row,
        bool silent = true)
    {
        var permission = new NetworkFleetMemberPermissionSnapshot(
            row.GameName,
            row.Callsign,
            NormalizeRoleTitle(row.RoleTitle),
            row.PermissionEnabled,
            row.CanRemoveMembers,
            row.CanPublishTasks,
            row.CanPublishPlans,
            row.CanManageFleetInfo,
            DateTimeOffset.UtcNow);

        return PushFleetMutationAsync(
            "api/fleets/permissions",
            new FleetMemberPermissionUpdateRequest(
                _fleetCode,
                permission,
                BuildFleetEventLogSnapshots()),
            "成员权限已同步",
            "成员权限同步失败",
            silent);
    }

    private Task<bool> PushFleetInfoAsync(bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/info",
            new FleetInfoUpdateRequest(
                _fleetCode,
                _fleetDescription,
                _fleetType,
                _fleetActiveTime,
                _fleetJoinPolicy,
                string.IsNullOrWhiteSpace(_fleetCode) ? "LOGO" : _fleetCode,
                BuildFleetLogoImageData(),
                BuildFleetEventLogSnapshots()),
            "舰队资料已同步",
            "舰队资料同步失败",
            silent);
    }

    private Task<bool> PushFleetSquadsAsync(bool silent = true)
    {
        return PushFleetMutationAsync(
            "api/fleets/squads",
            new FleetSquadsUpdateRequest(
                _fleetCode,
                BuildSquadSnapshots(),
                BuildFleetEventLogSnapshots()),
            "小队信息已同步",
            "小队信息同步失败",
            silent);
    }

    private async Task<bool> PullNetworkFleetsAsync(bool silent = false)
    {
        try
        {
            var snapshots = await _relayClient.GetFromJsonAsync<NetworkFleetSnapshot[]>("api/fleets") ?? [];
            _allNetworkFleets.Clear();
            var currentFleetExistsOnRelay = false;
            var clearedMissingFleet = false;
            foreach (var snapshot in snapshots)
            {
                if (IsSameFleet(snapshot.Name) || IsSameFleet(snapshot.Code))
                {
                    currentFleetExistsOnRelay = true;
                    MergeNetworkFleetState(snapshot);
                }

                _allNetworkFleets.Add(NetworkFleetCard.FromSnapshot(snapshot, _fleetName, _fleetCode, _hasFleet));
            }

            if (_hasFleet && !currentFleetExistsOnRelay)
            {
                clearedMissingFleet = true;
                ClearFleetState();
                _networkSnapshots.Clear();
                var retainedPlayerNames = string.IsNullOrWhiteSpace(_localPlayer)
                    ? Array.Empty<string?>()
                    : new string?[] { _localPlayer };
                _fleetState.RemovePlayersExcept(retainedPlayerNames);
                SaveCurrentConfig();
                if (!silent)
                {
                    NetworkStatusText.Text = "服务器中未找到当前舰队，本地舰队缓存已清理";
                    AppendOutput("NETWORK | current fleet missing on relay; local fleet cache cleared");
                }
            }

            ApplyFleetSearchFilter();

            if (!silent)
            {
                NetworkStatusText.Text = clearedMissingFleet
                    ? "服务器中未找到当前舰队，本地舰队缓存已清理"
                    : $"已拉取：{snapshots.Length} 个舰队";
                AppendOutput($"NETWORK | pulled fleets={snapshots.Length}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (HandleAuthorizationFailure(ex, "拉取舰队", silent))
            {
                return false;
            }

            if (!silent)
            {
                NetworkStatusText.Text = $"拉取舰队失败：{ex.Message}";
                AppendOutput($"NETWORK | pull fleets failed={ex.Message}");
            }

            return false;
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

        if (FindFleetEmptyText is not null)
        {
            FindFleetEmptyText.Visibility = _networkFleets.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            FindFleetEmptyText.Text = string.IsNullOrWhiteSpace(query)
                ? "暂无公开舰队。你可以创建舰队，或稍后刷新目录。"
                : "没有找到匹配的舰队。请尝试舰队全名或识别码。";
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
        ApplyNetworkFleetLogo(snapshot);
        MergeFleetMemberPermissions(snapshot.MemberPermissions);
        MergeFleetMembers(snapshot.Members);
        MergeFleetEventLogs(snapshot.EventLog);
        MergeFleetTaskHistory(snapshot.TaskHistory);
        _fleetApplicationSnapshots = snapshot.Applications ?? [];

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
        var remoteTaskRevision = Math.Max(0, snapshot.CurrentTaskNoticeRevision);
        var remoteTaskIsNewer = remoteTaskRevision > _fleetCurrentTaskNoticeRevision;
        if (remoteTaskIsNewer || remoteHasTask || !localHasTask || !isCommander)
        {
            _fleetCurrentTaskTitle = snapshot.CurrentTaskTitle ?? "";
            _fleetCurrentTaskBrief = snapshot.CurrentTaskBrief ?? "";
            _fleetCurrentTaskParticipants = snapshot.CurrentTaskParticipants ?? "";
            _fleetCurrentTaskRally = snapshot.CurrentTaskRally ?? "";
            _fleetCurrentTaskShip = snapshot.CurrentTaskShip ?? "";
            _fleetCurrentTaskTime = snapshot.CurrentTaskTime;
            _fleetCurrentTaskNoticeRevision = Math.Max(_fleetCurrentTaskNoticeRevision, remoteTaskRevision);
        }

        MergeFleetShips(snapshot.Ships);

        _fleetActionPlans.Clear();
        foreach (var actionPlan in snapshot.ActionPlans ?? [])
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
                    participant.AvatarImageData ?? participant.AvatarPath,
                    participant.Initials));
            }

            row.RefreshParticipantSummary();
            _fleetActionPlans.Add(row);
        }

        RebuildJoinedActionPlanIdsFromParticipants();
        SaveCurrentConfig();
        RefreshOverlayWindow();
        RenderState();
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        RefreshFleetMemberManagement();
        RefreshFleetApplications();
        RefreshSquadActionButtons();
        SelectFeaturedActionPlan();
    }

    private void RebuildJoinedActionPlanIdsFromParticipants()
    {
        var identities = EnumerateLocalIdentities()
            .Where(identity => !string.IsNullOrWhiteSpace(identity))
            .Select(identity => identity.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identities.Count == 0)
        {
            return;
        }

        _joinedActionPlanIds.Clear();
        foreach (var plan in _fleetActionPlans)
        {
            var joined = plan.Participants.Any(participant =>
                IsLocalPlayerIdentity(participant.GameName, participant.Callsign) ||
                (!string.IsNullOrWhiteSpace(participant.GameName) && identities.Contains(participant.GameName)) ||
                (!string.IsNullOrWhiteSpace(participant.Callsign) && identities.Contains(participant.Callsign)));

            if (joined)
            {
                _joinedActionPlanIds.Add(plan.Id);
            }
        }
    }

    private void MergeFleetMemberPermissions(NetworkFleetMemberPermissionSnapshot[]? permissions)
    {
        _fleetMemberPermissions.Clear();

        foreach (var permission in permissions ?? [])
        {
            if (string.IsNullOrWhiteSpace(permission.GameName))
            {
                continue;
            }

            _fleetMemberPermissions[permission.GameName.Trim()] = new LocalFleetMemberPermission(
                permission.GameName.Trim(),
                permission.Callsign,
                NormalizeRoleTitle(permission.RoleTitle),
                permission.PermissionEnabled,
                permission.CanRemoveMembers,
                permission.CanPublishTasks,
                permission.CanPublishPlans,
                permission.CanManageFleetInfo,
                permission.UpdatedAt);
        }
    }

    private void MergeFleetMembers(NetworkFleetMemberSnapshot[]? members)
    {
        var changedPlayers = false;
        foreach (var member in members ?? [])
        {
            if (string.IsNullOrWhiteSpace(member.GameName))
            {
                continue;
            }

            var gameName = member.GameName.Trim();
            var memberSnapshot = new NetworkPlayerSnapshot(
                gameName,
                member.Callsign,
                _fleetName,
                string.IsNullOrWhiteSpace(member.SquadName) ? "Unassigned" : member.SquadName,
                member.Online,
                string.IsNullOrWhiteSpace(member.Ship) ? "Unknown" : member.Ship,
                string.IsNullOrWhiteSpace(member.Ship) ||
                member.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                    ? "None"
                    : "Low",
                string.IsNullOrWhiteSpace(member.Location) ? "Unknown" : member.Location,
                string.IsNullOrWhiteSpace(member.LocationConfidence) ? "Low" : member.LocationConfidence,
                member.LastUpdated == default ? DateTimeOffset.UtcNow : member.LastUpdated,
                member.AvatarImageData,
                null);
            _networkSnapshots[gameName] = memberSnapshot;
            ApplyFleetMemberSnapshotToState(memberSnapshot);
            changedPlayers = true;

            if (!_fleetMemberPermissions.ContainsKey(gameName))
            {
                _fleetMemberPermissions[gameName] = new LocalFleetMemberPermission(
                    gameName,
                    member.Callsign,
                    NormalizeRoleTitle(member.RoleTitle),
                    false,
                    false,
                    false,
                    false,
                    false,
                    member.LastUpdated == default ? DateTimeOffset.UtcNow : member.LastUpdated);
            }
        }

        if (changedPlayers)
        {
            RenderState();
        }
    }

    private void ApplyFleetMemberSnapshotToState(NetworkPlayerSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Name) ||
            snapshot.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var timestamp = snapshot.LastUpdated == default ? DateTimeOffset.UtcNow : snapshot.LastUpdated;
        _fleetState.Apply(new FleetEvent(
            snapshot.Online ? FleetEventType.PlayerOnline : FleetEventType.PlayerOffline,
            snapshot.Name,
            Timestamp: timestamp));

        if (!snapshot.Online)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Ship) &&
            !snapshot.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _fleetState.Apply(new FleetEvent(
                FleetEventType.PlayerShipControlSignal,
                snapshot.Name,
                Ship: snapshot.Ship,
                Timestamp: timestamp));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Location) &&
            !snapshot.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _fleetState.Apply(new FleetEvent(
                FleetEventType.PlayerLocationChanged,
                snapshot.Name,
                Location: snapshot.Location,
                LocationEvidenceScore: LocationEvidenceScoreFromConfidence(snapshot.LocationConfidence),
                LocationEvidence: "Fleet member sync",
                Timestamp: timestamp));
        }
    }

    private void MergeFleetEventLogs(NetworkFleetEventLogSnapshot[]? eventLogs)
    {
        var knownIds = new HashSet<string>(
            _allFleetEventLogs.Select(row => row.Id),
            StringComparer.OrdinalIgnoreCase);
        var changed = false;

        foreach (var item in eventLogs ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Id) || knownIds.Contains(item.Id))
            {
                continue;
            }

            _allFleetEventLogs.Add(new FleetEventLogRow(
                item.Id.Trim(),
                item.Timestamp == default ? DateTimeOffset.UtcNow : item.Timestamp,
                string.IsNullOrWhiteSpace(item.Type) ? "舰队" : item.Type,
                item.Title ?? "",
                item.Detail ?? ""));
            knownIds.Add(item.Id.Trim());
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        var ordered = _allFleetEventLogs
            .OrderByDescending(row => row.Timestamp)
            .Take(500)
            .ToArray();
        _allFleetEventLogs.Clear();
        foreach (var row in ordered)
        {
            _allFleetEventLogs.Add(row);
        }

        ApplyFleetEventLogFilter();
    }

    private void MergeFleetTaskHistory(NetworkFleetTaskHistorySnapshot[]? taskHistory)
    {
        var changed = false;
        foreach (var item in taskHistory ?? [])
        {
            if (string.IsNullOrWhiteSpace(item.Key) || string.IsNullOrWhiteSpace(item.Title))
            {
                continue;
            }

            var row = new FleetTaskHistoryRow(
                item.Key.Trim(),
                item.Title.Trim(),
                string.IsNullOrWhiteSpace(item.Brief) ? "未指定" : item.Brief.Trim(),
                string.IsNullOrWhiteSpace(item.Status) ? "进行中" : item.Status.Trim(),
                string.IsNullOrWhiteSpace(item.Participants) ? "参与范围 / 未指定" : item.Participants.Trim(),
                string.IsNullOrWhiteSpace(item.Rally) ? "集结点 / 未发布" : item.Rally.Trim(),
                string.IsNullOrWhiteSpace(item.RequiredShip) ? "指定舰船 / 无" : item.RequiredShip.Trim(),
                string.IsNullOrWhiteSpace(item.PublishedAtText) ? "" : item.PublishedAtText.Trim());
            var existingIndex = _fleetTaskHistory
                .Select((task, index) => new { task, index })
                .FirstOrDefault(entry => entry.task.Key.Equals(row.Key, StringComparison.OrdinalIgnoreCase))
                ?.index;

            if (existingIndex is int index)
            {
                if (!_fleetTaskHistory[index].Equals(row))
                {
                    _fleetTaskHistory[index] = row;
                    changed = true;
                }
            }
            else
            {
                _fleetTaskHistory.Add(row);
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        var ordered = _fleetTaskHistory
            .Take(200)
            .ToArray();
        _fleetTaskHistory.Clear();
        foreach (var row in ordered)
        {
            _fleetTaskHistory.Add(row);
        }

        RefreshTaskManagementPanel();
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
        if (isInFleet)
        {
            MergeFleetShips(BuildFleetShipsFromPlayerSnapshot(snapshot));
        }

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

        if (!string.IsNullOrWhiteSpace(snapshot.Ship) &&
            !snapshot.Ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _fleetState.Apply(new FleetEvent(
                FleetEventType.PlayerShipControlSignal,
                snapshot.Name,
                Ship: snapshot.Ship,
                Timestamp: snapshot.LastUpdated));
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Location) &&
            !snapshot.Location.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            _fleetState.Apply(new FleetEvent(
                FleetEventType.PlayerLocationChanged,
                snapshot.Name,
                Location: snapshot.Location,
                LocationEvidenceScore: LocationEvidenceScoreFromConfidence(snapshot.LocationConfidence),
                LocationEvidence: "Network relay",
                Timestamp: snapshot.LastUpdated));
        }
    }

    private bool IsLocalNetworkSnapshot(NetworkPlayerSnapshot snapshot)
    {
        return !string.IsNullOrWhiteSpace(_localPlayer) &&
               snapshot.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyLocalNetworkSnapshot(NetworkPlayerSnapshot snapshot)
    {
        if (!IsLocalNetworkSnapshot(snapshot))
        {
            return;
        }

        var changed = false;
        if (string.IsNullOrWhiteSpace(_callsign) && !string.IsNullOrWhiteSpace(snapshot.Callsign))
        {
            _callsign = snapshot.Callsign!;
            CallsignBox.Text = _callsign;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Fleet) &&
            !snapshot.Fleet.Equals("No Fleet", StringComparison.OrdinalIgnoreCase) &&
            !IsSameFleet(snapshot.Fleet))
        {
            var fleetCard = _allNetworkFleets.FirstOrDefault(card =>
                snapshot.Fleet.Equals(card.Snapshot.Name, StringComparison.OrdinalIgnoreCase) ||
                snapshot.Fleet.Equals(card.Snapshot.Code, StringComparison.OrdinalIgnoreCase));
            if (fleetCard is not null)
            {
                JoinNetworkFleet(fleetCard.Snapshot);
                changed = true;
            }
        }

        if (_hasFleet &&
            !string.IsNullOrWhiteSpace(snapshot.Squad) &&
            !snapshot.Squad.Equals("Unassigned", StringComparison.OrdinalIgnoreCase))
        {
            var squad = _squads.FirstOrDefault(item =>
                item.Name.Equals(snapshot.Squad, StringComparison.OrdinalIgnoreCase));
            if (squad is not null && !ReferenceEquals(_joinedSquad, squad))
            {
                _joinedSquad = squad;
                _selectedSquad ??= squad;
                SquadSelectionList.SelectedItem = _selectedSquad;
                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        SaveCurrentConfig();
        RenderState();
        RefreshFleetInfoPanel();
        RefreshFleetMemberManagement();
        RefreshOverlayWindow();
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
        var remoteSquadSnapshots = snapshot.Squads ?? [];
        var remoteSquadNames = remoteSquadSnapshots
            .Where(squad => !string.IsNullOrWhiteSpace(squad.Name))
            .Select(squad => squad.Name.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (snapshot.Squads is not null)
        {
            var removedSquadNames = _squads
                .Where(squad => !remoteSquadNames.Contains(squad.Name) &&
                                !HasRecentLocalSquadEdit(squad.Name))
                .Select(squad => squad.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (removedSquadNames.Count > 0)
            {
                for (var index = _squads.Count - 1; index >= 0; index--)
                {
                    if (removedSquadNames.Contains(_squads[index].Name))
                    {
                        _squads.RemoveAt(index);
                        changed = true;
                    }
                }

                if (_joinedSquad is not null && removedSquadNames.Contains(_joinedSquad.Name))
                {
                    _joinedSquad = null;
                }

                if (_selectedSquad is not null && removedSquadNames.Contains(_selectedSquad.Name))
                {
                    _selectedSquad = _joinedSquad ?? _squads.FirstOrDefault();
                    SquadSelectionList.SelectedItem = _selectedSquad;
                }
            }
        }

        foreach (var squadSnapshot in remoteSquadSnapshots)
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
                    Mission = string.IsNullOrWhiteSpace(squadSnapshot.Mission) ? "Standby" : squadSnapshot.Mission!,
                    RallyPoint = string.IsNullOrWhiteSpace(squadSnapshot.RallyPoint) ? "Use Global" : squadSnapshot.RallyPoint!,
                    Type = string.IsNullOrWhiteSpace(squadSnapshot.Type) ? "Assault" : squadSnapshot.Type!,
                    Description = string.IsNullOrWhiteSpace(squadSnapshot.Description) ? "No squad briefing yet." : squadSnapshot.Description!,
                    EmblemPath = SaveNetworkSquadEmblem(snapshot, squadSnapshot),
                    UpdatedAt = squadSnapshot.UpdatedAt
                });
                changed = true;
                continue;
            }

            if (ShouldPreserveLocalSquad(existing, squadSnapshot))
            {
                continue;
            }

            var nextCommander = string.IsNullOrWhiteSpace(squadSnapshot.Commander) ? existing.Commander : squadSnapshot.Commander!;
            var nextMission = string.IsNullOrWhiteSpace(squadSnapshot.Mission) ? existing.Mission : squadSnapshot.Mission!;
            var nextRallyPoint = string.IsNullOrWhiteSpace(squadSnapshot.RallyPoint) ? existing.RallyPoint : squadSnapshot.RallyPoint!;
            var nextType = string.IsNullOrWhiteSpace(squadSnapshot.Type) ? existing.Type : squadSnapshot.Type!;
            var nextDescription = string.IsNullOrWhiteSpace(squadSnapshot.Description) ? existing.Description : squadSnapshot.Description!;
            var remoteHasTimestamp = squadSnapshot.UpdatedAt != default;
            var nextUpdatedAt = remoteHasTimestamp
                ? squadSnapshot.UpdatedAt
                : existing.UpdatedAt;
            if (remoteHasTimestamp && existing.UpdatedAt != default && nextUpdatedAt < existing.UpdatedAt)
            {
                continue;
            }

            var nextEmblemPath = SaveNetworkSquadEmblem(snapshot, squadSnapshot);
            if (nextEmblemPath is null && !string.IsNullOrWhiteSpace(squadSnapshot.EmblemImageData))
            {
                nextEmblemPath = existing.EmblemPath;
            }
            if (existing.Commander != nextCommander ||
                existing.Mission != nextMission ||
                existing.RallyPoint != nextRallyPoint ||
                existing.Type != nextType ||
                existing.Description != nextDescription ||
                existing.EmblemPath != nextEmblemPath ||
                existing.UpdatedAt != nextUpdatedAt)
            {
                existing.Commander = nextCommander;
                existing.Mission = nextMission;
                existing.RallyPoint = nextRallyPoint;
                existing.Type = nextType;
                existing.Description = nextDescription;
                existing.EmblemPath = nextEmblemPath;
                existing.UpdatedAt = nextUpdatedAt;
                existing.RefreshComputed();
                changed = true;
            }
        }

        if (changed)
        {
            RenderSquads();
            RenderMySquad();
            RefreshOverlayWindow();
            SaveCurrentConfig();
        }
    }

    private void MarkLocalSquadEdit(SquadRow squad)
    {
        if (string.IsNullOrWhiteSpace(squad.Name))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        squad.UpdatedAt = now;
        _localSquadEditTimes[squad.Name.Trim()] = now;
    }

    private bool ShouldPreserveLocalSquad(SquadRow existing, NetworkSquadSnapshot remote)
    {
        if (!HasRecentLocalSquadEdit(existing.Name))
        {
            return false;
        }

        if (remote.UpdatedAt == default || existing.UpdatedAt >= remote.UpdatedAt)
        {
            return true;
        }

        _localSquadEditTimes.Remove(existing.Name.Trim());
        return false;
    }

    private bool HasRecentLocalSquadEdit(string? squadName)
    {
        if (string.IsNullOrWhiteSpace(squadName) ||
            !_localSquadEditTimes.TryGetValue(squadName.Trim(), out var editedAt))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - editedAt <= LocalSquadEditProtectionWindow)
        {
            return true;
        }

        _localSquadEditTimes.Remove(squadName.Trim());
        return false;
    }

    private void MarkLocalFleetLogoEdit()
    {
        _localFleetLogoEditTime = DateTimeOffset.UtcNow;
    }

    private bool ShouldPreserveLocalFleetLogo(NetworkFleetSnapshot snapshot)
    {
        if (_localFleetLogoEditTime == default)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - _localFleetLogoEditTime > LocalSquadEditProtectionWindow)
        {
            _localFleetLogoEditTime = default;
            return false;
        }

        if (snapshot.LastUpdated == default || snapshot.LastUpdated <= _localFleetLogoEditTime)
        {
            return true;
        }

        _localFleetLogoEditTime = default;
        return false;
    }

    private void ApplyNetworkFleetLogo(NetworkFleetSnapshot snapshot)
    {
        if (ShouldPreserveLocalFleetLogo(snapshot))
        {
            return;
        }

        _fleetLogoPath = SaveNetworkFleetLogo(snapshot);
    }

    private Uri BuildNetworkUri(string path)
    {
        return _relayClient.BuildUri(path);
    }

    private static string NormalizeNetworkServerUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultRelayUrl;
        }

        var trimmed = value.Trim().TrimEnd('/');
        if (trimmed.Equals("http://198.13.49.128:5058", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("http://api.scstarbridge.com", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("https://198.13.49.128:5058", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRelayUrl;
        }

        return trimmed;
    }

    private Task<HttpResponseMessage> PostNetworkJsonAsync<T>(string path, T payload)
    {
        return _relayClient.PostJsonAsync(path, payload);
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
                        ? "server notify endpoint is not available; deploy the latest StarBridge relay"
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
                try
                {
                    using var document = JsonDocument.Parse(body);
                    if (document.RootElement.ValueKind == JsonValueKind.Object &&
                        document.RootElement.TryGetProperty("error", out var errorElement) &&
                        errorElement.ValueKind == JsonValueKind.String)
                    {
                        return errorElement.GetString() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                    }

                    if (document.RootElement.ValueKind == JsonValueKind.String)
                    {
                        return document.RootElement.GetString() ?? $"{(int)response.StatusCode} {response.ReasonPhrase}";
                    }
                }
                catch (JsonException)
                {
                    // The relay may return plain text for proxy or platform errors.
                }

                return body.Trim();
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
        var squads = BuildSquadSnapshots();
        var actionPlans = BuildActionPlanSnapshots();

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
            _accountName,
            BuildFleetMemberPermissionSnapshots(),
            BuildFleetMemberSnapshots(),
            BuildFleetEventLogSnapshots(),
            _fleetCurrentTaskNoticeRevision,
            BuildFleetShipSnapshots(),
            BuildFleetTaskHistorySnapshots(),
            _fleetApplicationSnapshots);
    }

    private NetworkSquadSnapshot[] BuildSquadSnapshots()
    {
        RepairLocalSquadLifecycle();

        return _squads
            .Select(squad => new NetworkSquadSnapshot(
                squad.Name,
                squad.Commander,
                squad.Type,
                squad.Description,
                squad.Mission,
                squad.RallyPoint,
                BuildImageDataFromPath(squad.EmblemPath, 512 * 1024),
                squad.UpdatedAt))
            .ToArray();
    }

    private NetworkActionPlanSnapshot[] BuildActionPlanSnapshots()
    {
        return _fleetActionPlans
            .Select(BuildActionPlanSnapshot)
            .ToArray();
    }

    private NetworkActionPlanSnapshot BuildActionPlanSnapshot(FleetActionPlanRow plan)
    {
        return new NetworkActionPlanSnapshot(
            plan.Id,
            plan.Title,
            plan.Content,
            plan.StartTime,
            plan.NotifyMembers,
            plan.Participants
                .Select(BuildActionPlanParticipantSnapshot)
                .ToArray());
    }

    private NetworkActionPlanParticipantSnapshot BuildActionPlanParticipantSnapshot(ActionPlanParticipantRow participant)
    {
        return new NetworkActionPlanParticipantSnapshot(
            participant.Callsign,
            participant.GameName,
            participant.AvatarPath,
            participant.Initials,
            ResolveParticipantAvatarImageData(participant));
    }

    private NetworkFleetMemberPermissionSnapshot[] BuildFleetMemberPermissionSnapshots()
    {
        var rows = _fleetMemberPermissions.Values
            .Where(item => !string.IsNullOrWhiteSpace(item.GameName))
            .Select(item => new NetworkFleetMemberPermissionSnapshot(
                item.GameName.Trim(),
                item.Callsign,
                NormalizeRoleTitle(item.RoleTitle),
                item.PermissionEnabled,
                item.CanRemoveMembers,
                item.CanPublishTasks,
                item.CanPublishPlans,
                item.CanManageFleetInfo,
                item.UpdatedAt))
            .ToList();

        var commanderName = GetGameNameFromDisplayName(_fleetChiefCommander);
        if (!string.IsNullOrWhiteSpace(commanderName))
        {
            var commanderPermission = new NetworkFleetMemberPermissionSnapshot(
                commanderName,
                GetCallsignFromDisplayName(_fleetChiefCommander),
                "舰队指挥官",
                true,
                true,
                true,
                true,
                true,
                DateTimeOffset.UtcNow);
            var commanderIndex = rows.FindIndex(item =>
                item.GameName.Equals(commanderName, StringComparison.OrdinalIgnoreCase));
            if (commanderIndex >= 0)
            {
                rows[commanderIndex] = commanderPermission;
            }
            else
            {
                rows.Add(commanderPermission);
            }
        }

        if (!string.IsNullOrWhiteSpace(_localPlayer) &&
            rows.All(item => !item.GameName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase)))
        {
            var localPermission = GetFleetPermission(_localPlayer);
            rows.Add(new NetworkFleetMemberPermissionSnapshot(
                _localPlayer.Trim(),
                string.IsNullOrWhiteSpace(_callsign) ? localPermission?.Callsign : _callsign,
                IsFleetCommander(_localPlayer, _callsign)
                    ? "舰队指挥官"
                    : NormalizeRoleTitle(localPermission?.RoleTitle ?? "成员"),
                localPermission?.PermissionEnabled ?? false,
                localPermission?.CanRemoveMembers ?? false,
                localPermission?.CanPublishTasks ?? false,
                localPermission?.CanPublishPlans ?? false,
                localPermission?.CanManageFleetInfo ?? false,
                localPermission?.UpdatedAt ?? DateTimeOffset.UtcNow));
        }

        return rows.ToArray();
    }

    private NetworkFleetMemberSnapshot[] BuildFleetMemberSnapshots()
    {
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            return [];
        }

        var local = _players.FirstOrDefault(player =>
            player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase));
        var permission = GetFleetPermission(_localPlayer);
        var role = IsFleetCommander(_localPlayer, _callsign)
            ? "舰队指挥官"
            : NormalizeRoleTitle(permission?.RoleTitle ?? local?.Role ?? "成员");

        return
        [
            new NetworkFleetMemberSnapshot(
                _localPlayer.Trim(),
                string.IsNullOrWhiteSpace(_callsign) ? local?.Callsign : _callsign,
                role,
                _joinedSquad?.Name ?? local?.SquadName ?? "Unassigned",
                local?.Status.Equals("Online", StringComparison.OrdinalIgnoreCase) == true,
                string.IsNullOrWhiteSpace(local?.RawShip) ? local?.Ship ?? "Unknown" : local.RawShip,
                string.IsNullOrWhiteSpace(local?.RawLocation) ? "Unknown" : local.RawLocation,
                DateTimeOffset.UtcNow,
                BuildAvatarImageData(),
                local?.LocationConfidence ?? "None")
        ];
    }

    private NetworkFleetEventLogSnapshot[] BuildFleetEventLogSnapshots()
    {
        return _allFleetEventLogs
            .OrderByDescending(row => row.Timestamp)
            .Take(500)
            .Select(row => new NetworkFleetEventLogSnapshot(
                row.Id,
                row.Timestamp,
                row.Type,
                row.Title,
                row.Detail))
            .ToArray();
    }

    private NetworkFleetTaskHistorySnapshot[] BuildFleetTaskHistorySnapshots()
    {
        return _fleetTaskHistory
            .Take(200)
            .Select(row => new NetworkFleetTaskHistorySnapshot(
                row.Key,
                row.Title,
                row.Brief,
                row.Status,
                row.Participants,
                row.Rally,
                row.RequiredShip,
                row.PublishedAtText))
            .ToArray();
    }

    private NetworkOwnedShipSnapshot[] BuildOwnedShipSnapshots()
    {
        return _ownedShips
            .Select(ship => new NetworkOwnedShipSnapshot(
                ship.Code,
                ship.DisplayName,
                ship.Source,
                ship.ImportedAt))
            .ToArray();
    }

    private NetworkFleetShipSnapshot[] BuildFleetShipSnapshots()
    {
        var ownerName = string.IsNullOrWhiteSpace(_localPlayer)
            ? _accountName ?? "Unknown"
            : _localPlayer;
        var ownerSquad = _joinedSquad?.Name ?? "未加入小队";
        var avatarImageData = BuildAvatarImageData();

        return _ownedShips
            .Select(ship => new NetworkFleetShipSnapshot(
                ship.Code,
                ship.DisplayName,
                ownerName,
                _callsign,
                ownerSquad,
                avatarImageData,
                ship.ImportedAt))
            .ToArray();
    }

    private static NetworkFleetShipSnapshot[] BuildFleetShipsFromPlayerSnapshot(NetworkPlayerSnapshot snapshot)
    {
        return (snapshot.OwnedShips ?? [])
            .Where(ship => !string.IsNullOrWhiteSpace(ship.Code))
            .Select(ship => new NetworkFleetShipSnapshot(
                ship.Code,
                ship.DisplayName,
                snapshot.Name,
                snapshot.Callsign,
                snapshot.Squad,
                snapshot.AvatarImageData,
                ship.ImportedAt))
            .ToArray();
    }

    private string? ResolveParticipantAvatarImageData(ActionPlanParticipantRow participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.GameName) &&
            IsLocalPlayer(participant.GameName))
        {
            return BuildAvatarImageData();
        }

        if (IsImageDataValue(participant.AvatarPath))
        {
            return participant.AvatarPath;
        }

        if (!string.IsNullOrWhiteSpace(participant.GameName) &&
            _networkSnapshots.TryGetValue(participant.GameName, out var snapshot))
        {
            return snapshot.AvatarImageData;
        }

        return null;
    }

    private string? GetAvatarImageDataForPlayer(PlayerRow player)
    {
        if (IsLocalPlayer(player.Name))
        {
            return BuildAvatarImageData();
        }

        if (_networkSnapshots.TryGetValue(player.Name, out var snapshot))
        {
            return snapshot.AvatarImageData;
        }

        return IsImageDataValue(player.AvatarPath) ? player.AvatarPath : null;
    }

    private static bool IsImageDataValue(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
    }

    private string? BuildAvatarImageData()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_avatarPath) || !File.Exists(_avatarPath))
            {
                return null;
            }

            var writeTime = File.GetLastWriteTimeUtc(_avatarPath);
            if (_cachedAvatarImagePath == _avatarPath &&
                _cachedAvatarImageWriteTimeUtc == writeTime)
            {
                return _cachedAvatarImageData;
            }

            var imageData = BuildImageDataFromPath(_avatarPath, 512 * 1024);
            if (string.IsNullOrWhiteSpace(imageData))
            {
                return null;
            }

            _cachedAvatarImagePath = _avatarPath;
            _cachedAvatarImageWriteTimeUtc = writeTime;
            _cachedAvatarImageData = imageData;
            return _cachedAvatarImageData;
        }
        catch
        {
            return null;
        }
    }

    private string? BuildFleetLogoImageData()
    {
        return BuildImageDataFromPath(_fleetLogoPath, 768 * 1024);
    }

    private static string? BuildImageDataFromPath(string? path, int maxBytes)
    {
        if (IsImageDataValue(path))
        {
            return path;
        }

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > maxBytes)
            {
                return null;
            }

            var extension = Path.GetExtension(path).ToLowerInvariant();
            var mimeType = extension is ".jpg" or ".jpeg" ? "image/jpeg" : "image/png";
            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }

    private string? SaveNetworkFleetLogo(NetworkFleetSnapshot snapshot)
    {
        var prefix = BuildNetworkFleetLogoPrefix(snapshot.Code);
        if (!TryDecodeImageData(snapshot.LogoImageData, 768 * 1024, out var bytes))
        {
            CleanupImageVariants(prefix, null);
            return null;
        }

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var path = BuildImagePath(prefix, hash);
            WriteImageFileIfChanged(path, bytes);
            CleanupImageVariants(prefix, path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private string? SaveNetworkSquadEmblem(NetworkFleetSnapshot fleetSnapshot, NetworkSquadSnapshot squadSnapshot)
    {
        var prefix = BuildNetworkSquadEmblemPrefix(fleetSnapshot.Code, squadSnapshot.Name);
        if (!TryDecodeImageData(squadSnapshot.EmblemImageData, 512 * 1024, out var bytes))
        {
            CleanupImageVariants(prefix, null);
            return null;
        }

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var path = BuildImagePath(prefix, hash);
            WriteImageFileIfChanged(path, bytes);
            CleanupImageVariants(prefix, path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryDecodeImageData(string? imageData, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return false;
        }

        try
        {
            var payload = imageData;
            var commaIndex = payload.IndexOf(',');
            if (payload.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) && commaIndex >= 0)
            {
                payload = payload[(commaIndex + 1)..];
            }

            bytes = Convert.FromBase64String(payload);
            return bytes.Length > 0 && bytes.Length <= maxBytes;
        }
        catch
        {
            bytes = [];
            return false;
        }
    }

    private static string BuildNetworkFleetLogoPrefix(string? fleetCode)
    {
        return $"fleet-{BuildSafeImageToken(fleetCode, "fleet")}-logo";
    }

    private static string BuildNetworkSquadEmblemPrefix(string? fleetCode, string? squadName)
    {
        return $"squad-{BuildSafeImageToken(fleetCode, "fleet")}-{BuildSafeImageToken(squadName, "squad")}-emblem";
    }

    private static string BuildSafeImageToken(string? value, string fallback)
    {
        var safeValue = new string((value ?? fallback).Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrWhiteSpace(safeValue) ? fallback : safeValue;
    }

    private static string BuildImagePath(string prefix, string? contentHash = null)
    {
        var directory = Path.Combine(DesktopAppConfig.ConfigDirectory, "Images");
        Directory.CreateDirectory(directory);
        var suffix = string.IsNullOrWhiteSpace(contentHash) ? "" : $"-{contentHash}";
        return Path.Combine(directory, $"{prefix}{suffix}.png");
    }

    private static void CleanupImageVariants(string prefix, string? keepPath)
    {
        try
        {
            var directory = Path.Combine(DesktopAppConfig.ConfigDirectory, "Images");
            if (!Directory.Exists(directory))
            {
                return;
            }

            var fullKeepPath = string.IsNullOrWhiteSpace(keepPath) ? null : Path.GetFullPath(keepPath);
            foreach (var file in Directory.EnumerateFiles(directory, $"{prefix}*.png"))
            {
                try
                {
                    if (fullKeepPath is not null &&
                        string.Equals(Path.GetFullPath(file), fullKeepPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    File.Delete(file);
                }
                catch
                {
                    // Cache cleanup is best-effort. A locked image should not break sync.
                }
            }
        }
        catch
        {
            // Cache cleanup is best-effort. A locked image should not break sync.
        }
    }

    private static void WriteImageFileIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = File.ReadAllBytes(path);
                if (existing.SequenceEqual(bytes))
                {
                    return;
                }
            }
            catch
            {
                // Rewrite the image if the old file cannot be read.
            }
        }

        File.WriteAllBytes(path, bytes);
    }

    private async void RefreshFleetDirectory_Click(object sender, RoutedEventArgs e)
    {
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
        await PullNetworkFleetsAsync();
    }

    private async void JoinNetworkFleet_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("加入舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!EnsureIdentityInitialized("加入舰队"))
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

        if (card.RequiresApplication)
        {
            var applyResult = System.Windows.MessageBox.Show(
                this,
                $"该舰队需要审核。是否向 {card.Name} 提交加入申请？",
                "提交加入申请",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (applyResult != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var response = await PostNetworkJsonAsync(
                    "api/fleets/apply",
                    new FleetJoinApplicationRequest(card.Snapshot.Code, ""));
                if (!response.IsSuccessStatusCode)
                {
                    NetworkStatusText.Text = $"申请失败：{await ReadResponseErrorAsync(response)}";
                    return;
                }

                var snapshot = await response.Content.ReadFromJsonAsync<NetworkFleetSnapshot>();
                if (snapshot is not null)
                {
                    var existingIndex = _allNetworkFleets.IndexOf(card);
                    if (existingIndex >= 0)
                    {
                        _allNetworkFleets[existingIndex] = NetworkFleetCard.FromSnapshot(
                            snapshot,
                            _fleetName,
                            _fleetCode,
                            _hasFleet);
                    }
                }

                await PullNetworkFleetsAsync(silent: true);
                NetworkStatusText.Text = $"已提交加入申请：{card.Name}";
            }
            catch (Exception ex)
            {
                NetworkStatusText.Text = $"申请失败：{ex.Message}";
            }

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

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/fleets/apply",
                new FleetJoinApplicationRequest(card.Snapshot.Code, ""));
            if (!response.IsSuccessStatusCode)
            {
                NetworkStatusText.Text = $"加入失败：{await ReadResponseErrorAsync(response)}";
                return;
            }

            var snapshot = await response.Content.ReadFromJsonAsync<NetworkFleetSnapshot>() ?? card.Snapshot;
            JoinNetworkFleet(snapshot);
            AddFleetLog("成员", "加入舰队", $"{FormatCommanderName(_callsign, _localPlayer)} 加入 {card.Name}");
            await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
            await PullNetworkFleetsAsync(silent: true);
            await PullNetworkSnapshotsAsync(silent: true);
            NetworkStatusText.Text = $"已加入舰队：{card.Name}";
            NavigateToMyFleet();
            ShowOneTimeGuideHint(
                "fleet-joined-member",
                "舰队成员引导",
                "你已经加入舰队。下一步可以进入“我的小队”选择或创建小队，并在“个人”页面完善呼号、头像和舰船数据库。");
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"加入失败：{ex.Message}";
        }
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
                Mission = string.IsNullOrWhiteSpace(squad.Mission) ? "Standby" : squad.Mission!,
                RallyPoint = string.IsNullOrWhiteSpace(squad.RallyPoint) ? "Use Global" : squad.RallyPoint!,
                Description = string.IsNullOrWhiteSpace(squad.Description) ? "No squad briefing yet." : squad.Description!,
                EmblemPath = SaveNetworkSquadEmblem(snapshot, squad),
                UpdatedAt = squad.UpdatedAt
            });
        }

        _selectedSquad = _squads.FirstOrDefault();
        _joinedSquad = null;
        SquadSelectionList.SelectedItem = _selectedSquad;
        MergeNetworkFleetState(snapshot);
        UpdateFleetEntryPanels();
        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        SaveCurrentConfig();
        RefreshSquadActionButtons();
        RefreshOverlayWindow();
    }

    private async void LeaveFleetButton_Click(object sender, RoutedEventArgs e)
    {
        await LeaveCurrentFleetAsync();
    }

    private async Task LeaveCurrentFleetAsync()
    {
        if (!EnsureLoggedIn("离开舰队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!_hasFleet || string.IsNullOrWhiteSpace(_fleetCode))
        {
            NetworkStatusText.Text = "当前没有可离开的舰队。";
            return;
        }

        if (IsCurrentUserFleetCommander())
        {
            NetworkStatusText.Text = "舰队指挥官需要先转移指挥权或解散舰队。";
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                $"确认离开舰队 {_fleetName}？",
                "离开舰队",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/fleets/leave",
                new FleetLeaveRequest(_fleetCode));
            if (!response.IsSuccessStatusCode)
            {
                NetworkStatusText.Text = $"离开舰队失败：{await ReadResponseErrorAsync(response)}";
                return;
            }

            ClearFleetState();
            SaveCurrentConfig();
            await PullNetworkFleetsAsync(silent: true);
            await PullNetworkSnapshotsAsync(silent: true);
            NetworkStatusText.Text = "已离开舰队。";
            NavigateToMyFleet();
        }
        catch (Exception ex)
        {
            NetworkStatusText.Text = $"离开舰队失败：{ex.Message}";
        }
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
        _createFleetLogoPath = null;
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
        _fleetApplicationSnapshots = [];
        _fleetApplications.Clear();
        _fleetMemberPermissions.Clear();
        _fleetMemberRows.Clear();
        _mySquadMembers.Clear();
        _remoteFleetShips.Clear();
        _fleetShipInventory.Clear();
        _joinedSquad = null;
        _selectedSquad = null;
        LocalFleetText.Text = "未加入舰队";
        LeaveFleetButton.Visibility = Visibility.Collapsed;
        RefreshFleetHeader();
        UpdateFleetEntryPanels();
        RenderSquads();
        RenderMySquad();
        RefreshFleetApplications();
        RefreshSquadActionButtons();
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
        RefreshFleetNotificationCenter();
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
        var isLoggedIn = IsLoggedIn;
        var overlaySettings = _overlaySettings.Serialize();
        var overlayLayout = SerializeOverlayLayout();
        var fleetStateJson = isLoggedIn ? SerializeFleetState() : null;
        DesktopAppConfig.Save(new DesktopAppConfig(
            _logPath,
            _localPlayer,
            _localPlayerId,
            isLoggedIn ? _avatarPath : null,
            OverlayHotkeyBox.Text,
            overlayLayout,
            isLoggedIn ? _callsign : null,
            overlaySettings,
            _language,
            NormalizeNetworkServerUrl(NetworkServerUrlBox.Text),
            NetworkServerKeyBox.Password,
            isLoggedIn ? _accountName : null,
            isLoggedIn ? _authToken : null,
            fleetStateJson,
            isLoggedIn && _allowEmailNotifications));
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
                squad.EmblemPath,
                squad.UpdatedAt)).ToArray(),
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
                row.Detail)).ToArray(),
            _fleetMemberPermissions.Values.ToArray());
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
            _fleetLogoPath = _hasFleet ? cache.FleetLogoPath : null;
            _createFleetLogoPath = null;
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
                    EmblemPath = squad.EmblemPath,
                    UpdatedAt = squad.UpdatedAt
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

            _fleetMemberPermissions.Clear();
            foreach (var item in cache.MemberPermissions ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item.GameName))
                {
                    _fleetMemberPermissions[item.GameName.Trim()] = item;
                }
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

    private void MergeFleetShips(NetworkFleetShipSnapshot[]? ships)
    {
        var changed = false;
        foreach (var ship in ships ?? [])
        {
            if (string.IsNullOrWhiteSpace(ship.Code) || string.IsNullOrWhiteSpace(ship.OwnerGameName))
            {
                continue;
            }

            var key = BuildFleetShipKey(ship.OwnerGameName, ship.Code);
            if (_remoteFleetShips.TryGetValue(key, out var existing) &&
                existing.ImportedAt >= ship.ImportedAt)
            {
                continue;
            }

            _remoteFleetShips[key] = ship;
            changed = true;
        }

        if (changed)
        {
            RefreshFleetShipInventory();
        }
    }

    private static string BuildFleetShipKey(string? ownerGameName, string? shipCode)
    {
        return $"{NormalizeLocalKey(ownerGameName)}::{NormalizeLocalKey(shipCode)}";
    }

    private static string NormalizeLocalKey(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
    }

    private void RefreshFleetShipInventory()
    {
        if (FleetShipInventoryCountText is null)
        {
            return;
        }

        _fleetShipInventory.Clear();

        var ownerName = string.IsNullOrWhiteSpace(_localPlayer) ? "Unknown" : _localPlayer;
        var ownerCallsign = string.IsNullOrWhiteSpace(_callsign) ? ownerName : _callsign!;
        var ownerSquad = _joinedSquad?.Name ?? "未加入小队";
        var localShips = _ownedShips.Select(ship => new NetworkFleetShipSnapshot(
            ship.Code,
            ship.DisplayName,
            ownerName,
            ownerCallsign,
            ownerSquad,
            BuildAvatarImageData(),
            ship.ImportedAt));
        var rows = new Dictionary<string, NetworkFleetShipSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var ship in _remoteFleetShips.Values.Concat(localShips))
        {
            if (string.IsNullOrWhiteSpace(ship.Code) || string.IsNullOrWhiteSpace(ship.OwnerGameName))
            {
                continue;
            }

            rows[BuildFleetShipKey(ship.OwnerGameName, ship.Code)] = ship;
        }

        var shipRows = new List<FleetShipInventoryRow>();
        foreach (var ship in rows.Values
                     .OrderBy(ship => ship.ImportedAt)
                     .ThenBy(ship => ship.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var catalog = ShipCatalog.Find(ship.Code, ship.DisplayName);
            var shipDisplayName = catalog?.DisplayName(_language) ?? ship.DisplayName;
            var shipSpec = string.IsNullOrWhiteSpace(catalog?.Spec) ? "待分类" : catalog!.Spec;
            var shipRole = catalog?.RoleDisplay(_language) ?? "";
            var shipStatus = string.IsNullOrWhiteSpace(catalog?.Status) ? "概念" : catalog!.Status;
            var shipPrice = catalog?.PriceDisplay ?? "未公布";
            var ownerDisplay = FormatCommanderName(
                ship.OwnerCallsign,
                ship.OwnerGameName,
                ship.OwnerGameName);
            shipRows.Add(new FleetShipInventoryRow(
                0,
                shipDisplayName,
                ship.Code,
                ownerDisplay,
                string.IsNullOrWhiteSpace(ship.OwnerCallsign) ? ship.OwnerGameName : ship.OwnerCallsign!,
                ship.OwnerGameName,
                string.IsNullOrWhiteSpace(ship.OwnerSquad) ? "未加入小队" : ship.OwnerSquad!,
                ship.OwnerAvatarImageData,
                GetInitials(ship.OwnerCallsign ?? ship.OwnerGameName),
                ship.ImportedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                shipSpec,
                shipRole,
                shipStatus,
                shipPrice));
        }

        var sortedRows = SortFleetShipRows(shipRows).ToArray();
        for (var index = 0; index < sortedRows.Length; index++)
        {
            _fleetShipInventory.Add(sortedRows[index] with { Number = index + 1 });
        }

        if (FleetShipCountText is not null)
        {
            FleetShipCountText.Text = _fleetShipInventory.Count.ToString();
        }

        FleetShipInventoryCountText.Text = $"已上传舰船 / {_fleetShipInventory.Count}";
        FleetShipInventoryEmptyText.Visibility = _fleetShipInventory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        FleetShipDatabaseCountText.Text = _fleetShipInventory.Count.ToString();
        FleetShipDatabaseCapitalText.Text = CountFleetShipSpec("旗舰级").ToString();
        FleetShipDatabaseLargeText.Text = CountFleetShipSpec("大型").ToString();
        FleetShipDatabaseMediumText.Text = CountFleetShipSpec("中型").ToString();
        FleetShipDatabaseSmallText.Text = CountFleetShipSpec("小型").ToString();
        var pricedShips = _fleetShipInventory
            .Where(ship => ship.ShipPriceValue.HasValue)
            .ToArray();
        FleetShipDatabaseTotalValueText.Text = pricedShips.Length == 0
            ? "未公布"
            : FormatFleetShipValue(pricedShips.Sum(ship => ship.ShipPriceValue!.Value));
        FleetShipDatabaseTopOwnerText.Text = _fleetShipInventory.Count == 0
            ? "-"
            : _fleetShipInventory
                .GroupBy(ship => ship.OwnerGameId)
                .OrderByDescending(group => group.Count())
                .Select(group => group.First().OwnerDisplay)
                .FirstOrDefault() ?? "-";
        FleetShipDatabaseAceText.Text = pricedShips
            .OrderByDescending(ship => ship.ShipPriceValue!.Value)
            .Select(ship => ship.ShipName)
            .FirstOrDefault() ?? "待评定";
        FleetShipDatabaseEmptyText.Visibility = _fleetShipInventory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private IEnumerable<FleetShipInventoryRow> SortFleetShipRows(IEnumerable<FleetShipInventoryRow> rows)
    {
        var sorted = _fleetShipSortColumn switch
        {
            FleetShipSortColumn.Name => OrderFleetShipRowsByText(rows, ship => ship.ShipName, _fleetShipSortDescending),
            FleetShipSortColumn.Status => rows.OrderBy(ship => GetFleetShipStatusSortRank(ship.ShipStatus, conceptFirst: _fleetShipSortDescending)),
            FleetShipSortColumn.Price => _fleetShipSortDescending
                ? rows.OrderByDescending(ship => ship.ShipPriceValue ?? -1m)
                : rows.OrderBy(ship => ship.ShipPriceValue ?? decimal.MaxValue),
            FleetShipSortColumn.Role => OrderFleetShipRowsByText(rows, ship => ship.ShipRole, _fleetShipSortDescending),
            FleetShipSortColumn.Owner => OrderFleetShipRowsByText(rows, ship => ship.OwnerDisplay, _fleetShipSortDescending),
            FleetShipSortColumn.Squad => OrderFleetShipRowsByText(rows, ship => ship.OwnerSquad, _fleetShipSortDescending),
            _ => rows.OrderBy(ship => GetFleetShipSpecSortRank(ship.ShipSpec, largeFirst: _fleetShipSortDescending))
        };

        return sorted.ThenBy(ship => ship.ShipName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(ship => ship.ShipCode, StringComparer.CurrentCultureIgnoreCase);
    }

    private static IOrderedEnumerable<FleetShipInventoryRow> OrderFleetShipRowsByText(
        IEnumerable<FleetShipInventoryRow> rows,
        Func<FleetShipInventoryRow, string> selector,
        bool descending)
    {
        return descending
            ? rows.OrderBy(ship => string.IsNullOrWhiteSpace(selector(ship)))
                .ThenByDescending(selector, StringComparer.CurrentCultureIgnoreCase)
            : rows.OrderBy(ship => string.IsNullOrWhiteSpace(selector(ship)))
                .ThenBy(selector, StringComparer.CurrentCultureIgnoreCase);
    }

    private static int GetFleetShipSpecSortRank(string spec, bool largeFirst)
    {
        var rank = spec.Trim() switch
        {
            "旗舰级" => 0,
            "大型" => 1,
            "中型" => 2,
            "小型" => 3,
            _ => 4
        };

        if (largeFirst)
        {
            return rank;
        }

        return rank switch
        {
            3 => 0,
            2 => 1,
            1 => 2,
            0 => 3,
            _ => 4
        };
    }

    private static int GetFleetShipStatusSortRank(string status, bool conceptFirst)
    {
        var normalized = status.Trim();
        var isConcept = normalized.Equals("概念", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Concept", StringComparison.OrdinalIgnoreCase);
        var isFlyable = normalized.Equals("可飞", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Flyable", StringComparison.OrdinalIgnoreCase);

        if (isConcept)
        {
            return conceptFirst ? 0 : 1;
        }

        if (isFlyable)
        {
            return conceptFirst ? 1 : 0;
        }

        return 2;
    }

    private void FleetShipDatabaseSortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag } ||
            !TryParseFleetShipSortColumn(tag, out var column))
        {
            return;
        }

        if (_fleetShipSortColumn == column)
        {
            _fleetShipSortDescending = !_fleetShipSortDescending;
        }
        else
        {
            _fleetShipSortColumn = column;
            _fleetShipSortDescending = GetDefaultDescendingForFleetShipSortColumn(column);
        }

        UpdateFleetShipSortHeaderIndicators();
        if (FleetShipInventoryCountText is not null)
        {
            RefreshFleetShipInventory();
        }
    }

    private static bool TryParseFleetShipSortColumn(string tag, out FleetShipSortColumn column)
    {
        column = tag switch
        {
            "name" => FleetShipSortColumn.Name,
            "spec" => FleetShipSortColumn.Spec,
            "status" => FleetShipSortColumn.Status,
            "price" => FleetShipSortColumn.Price,
            "role" => FleetShipSortColumn.Role,
            "owner" => FleetShipSortColumn.Owner,
            "squad" => FleetShipSortColumn.Squad,
            _ => FleetShipSortColumn.Spec
        };

        return tag is "name" or "spec" or "status" or "price" or "role" or "owner" or "squad";
    }

    private static bool GetDefaultDescendingForFleetShipSortColumn(FleetShipSortColumn column)
    {
        return column is FleetShipSortColumn.Spec or FleetShipSortColumn.Status or FleetShipSortColumn.Price;
    }

    private void UpdateFleetShipSortHeaderIndicators()
    {
        SetFleetShipSortArrow(FleetShipSortNameArrow, FleetShipSortColumn.Name);
        SetFleetShipSortArrow(FleetShipSortSpecArrow, FleetShipSortColumn.Spec);
        SetFleetShipSortArrow(FleetShipSortStatusArrow, FleetShipSortColumn.Status);
        SetFleetShipSortArrow(FleetShipSortPriceArrow, FleetShipSortColumn.Price);
        SetFleetShipSortArrow(FleetShipSortRoleArrow, FleetShipSortColumn.Role);
        SetFleetShipSortArrow(FleetShipSortOwnerArrow, FleetShipSortColumn.Owner);
        SetFleetShipSortArrow(FleetShipSortSquadArrow, FleetShipSortColumn.Squad);
    }

    private void SetFleetShipSortArrow(TextBlock arrowText, FleetShipSortColumn column)
    {
        arrowText.Text = _fleetShipSortColumn == column
            ? _fleetShipSortDescending ? "↓" : "↑"
            : string.Empty;
    }

    private int CountFleetShipSpec(string spec)
    {
        return _fleetShipInventory.Count(ship => ship.ShipSpec.Equals(spec, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatFleetShipValue(decimal value)
    {
        return value <= 0
            ? "未公布"
            : string.Format(CultureInfo.InvariantCulture, "${0:N0}", value);
    }

    private async void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentConfig();
        if (!_isClosingAfterOfflineUpload && IsLoggedIn && !string.IsNullOrWhiteSpace(_localPlayer))
        {
            e.Cancel = true;
            _isClosingAfterOfflineUpload = true;
            _gameProcessTimer.Stop();
            _networkSyncTimer.Stop();
            await PushOfflineSnapshotOnShutdownAsync();
            Close();
            return;
        }

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

        _overlayWindow = new OverlayWindow(_squads, GetOverlayPlayers(), _overlayLayout, GetEffectiveOverlaySettings(), _language, _hasFleet, BuildOverlayCommandState());
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
        _overlayWindow.Refresh(_squads, GetOverlayPlayers(), GetEffectiveOverlaySettings(), _language, _hasFleet, BuildOverlayCommandState());
    }

    private OverlayDisplaySettings GetEffectiveOverlaySettings()
    {
        if (!_overlaySettings.AutoThemeByShip)
        {
            return _overlaySettings;
        }

        var localShip = _players.FirstOrDefault(player =>
            player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase))?.RawShip;
        var shipTheme = GetOverlayThemeForShip(localShip);
        return _overlaySettings with { Theme = shipTheme };
    }

    private static OverlayVisualTheme GetOverlayThemeForShip(string? shipCode)
    {
        var code = ShipNameLocalizer.NormalizeCode(shipCode);
        if (string.IsNullOrWhiteSpace(code) ||
            code.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return OverlayVisualTheme.Default;
        }

        var normalizedCode = code.ToUpperInvariant();
        var manufacturerCode = normalizedCode.Split('_', 2)[0];

        return manufacturerCode switch
        {
            "ANVL" => OverlayVisualTheme.Anvil,
            "DRAK" => OverlayVisualTheme.Drake,
            "ARGO" => OverlayVisualTheme.Argo,
            "MRAI" or "MIRAI" => OverlayVisualTheme.Mirai,
            "MISC" when normalizedCode.Contains("RAZOR", StringComparison.OrdinalIgnoreCase) => OverlayVisualTheme.Mirai,
            "MISC" => OverlayVisualTheme.Musashi,
            "CRUS" => OverlayVisualTheme.Crusader,
            "AEGS" => OverlayVisualTheme.Aegis,
            "RSI" => OverlayVisualTheme.Rsi,
            "ORIG" => OverlayVisualTheme.Origin,
            "GAMA" => OverlayVisualTheme.Gatac,
            "XIAN" when normalizedCode.Contains("RAILEN", StringComparison.OrdinalIgnoreCase) => OverlayVisualTheme.Gatac,
            "XIAN" or "AOPOA" or "AOPA" => OverlayVisualTheme.Aopoa,
            "ESPR" => OverlayVisualTheme.Esperia,
            _ => OverlayVisualTheme.Default
        };
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
            AutoThemeByShipCheck is null ||
            ShowCrosshairCheck is null ||
            SimpleCrosshairRadio is null ||
            TechCrosshairRadio is null ||
            CrosshairThemeColorCheck is null ||
            CrosshairSizeSlider is null ||
            CrosshairThicknessSlider is null ||
            CrosshairOpacitySlider is null ||
            CrosshairColorBox is null ||
            CrosshairColorPickerButton is null ||
            OverlayThemeBox is null)
        {
            return;
        }

        var mode = ShowCallsignOnlyRadio.IsChecked == true
            ? OverlayMemberNameMode.CallsignOnly
            : ShowGameNameOnlyRadio.IsChecked == true
                ? OverlayMemberNameMode.GameNameOnly
                : OverlayMemberNameMode.CallsignAndGameName;
        var crosshairMode = TechCrosshairRadio.IsChecked == true
            ? OverlayCrosshairMode.Tech
            : OverlayCrosshairMode.Simple;

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
                3 => OverlayVisualTheme.Argo,
                4 => OverlayVisualTheme.Musashi,
                5 => OverlayVisualTheme.Mirai,
                6 => OverlayVisualTheme.Crusader,
                7 => OverlayVisualTheme.Aegis,
                8 => OverlayVisualTheme.Rsi,
                9 => OverlayVisualTheme.Origin,
                10 => OverlayVisualTheme.Aopoa,
                11 => OverlayVisualTheme.Esperia,
                12 => OverlayVisualTheme.Gatac,
                _ => OverlayVisualTheme.Default
            },
            AutoThemeByShipCheck.IsChecked == true,
            ShowCrosshairCheck.IsChecked == true,
            crosshairMode,
            CrosshairThemeColorCheck.IsChecked == true,
            OverlayDisplaySettings.NormalizeCrosshairColor(CrosshairColorBox.Text),
            Math.Clamp(CrosshairSizeSlider.Value, 48, 240),
            Math.Clamp(CrosshairThicknessSlider.Value, 1, 8),
            Math.Clamp(CrosshairOpacitySlider.Value / 100.0, 0.2, 1.0));

        RefreshCrosshairSettingLabels();
        SaveCurrentConfig();
        RenderOverlayEditor();
        RefreshOverlayWindow();
    }

    private void CrosshairSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        RefreshCrosshairSettingLabels();

        if (_isLoadingSettings)
        {
            return;
        }

        OverlaySetting_Changed(sender, new RoutedEventArgs());
    }

    private void CrosshairColorBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshCrosshairSettingLabels();

        if (_isLoadingSettings)
        {
            return;
        }

        OverlaySetting_Changed(sender, new RoutedEventArgs());
    }

    private void CrosshairColorPreview_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        CrosshairColorPickerButton_Click(sender, e);
    }

    private void CrosshairColorPickerButton_Click(object sender, RoutedEventArgs e)
    {
        if (CrosshairColorBox is null ||
            CrosshairThemeColorCheck is null)
        {
            return;
        }

        var currentColor = TryParseHexColor(CrosshairColorBox.Text, out var parsedColor)
            ? parsedColor
            : Color.FromRgb(235, 247, 255);

        using var dialog = new WinForms.ColorDialog
        {
            AnyColor = true,
            FullOpen = true,
            SolidColorOnly = false,
            Color = DrawingColor.FromArgb(currentColor.R, currentColor.G, currentColor.B)
        };

        var owner = new DialogOwner(new WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != WinForms.DialogResult.OK)
        {
            return;
        }

        var selectedColor = $"#{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        var wasLoading = _isLoadingSettings;
        _isLoadingSettings = true;
        CrosshairThemeColorCheck.IsChecked = false;
        CrosshairColorBox.Text = selectedColor;
        _isLoadingSettings = wasLoading;

        RefreshCrosshairSettingLabels();

        if (!wasLoading)
        {
            OverlaySetting_Changed(sender, new RoutedEventArgs());
        }
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
        StartProfileSyncDebounce();
    }

    private void StartProfileSyncDebounce()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        _profileSyncDebounceTimer.Stop();
        _profileSyncDebounceTimer.Start();
    }

    private async Task FlushProfileSyncDebouncedAsync()
    {
        if (!IsLoggedIn)
        {
            return;
        }

        await UpdateProfileAsync();
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
    }

    private void EmailNotificationsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoadingSettings || _isRefreshingAccountPanel)
        {
            return;
        }

        _allowEmailNotifications = EmailNotificationsCheck.IsChecked == true;
        SaveCurrentConfig();
        if (IsLoggedIn)
        {
            _ = UpdateProfileAsync();
        }
    }

    private void RefreshFleetMemberManagement()
    {
        if (FleetMemberManagementList is null)
        {
            return;
        }

        _fleetMemberRows.Clear();
        if (!_hasFleet)
        {
            if (FleetMemberManagementEmptyText is not null)
            {
                FleetMemberManagementEmptyText.Visibility = Visibility.Visible;
            }
            return;
        }

        var canEditPermissions = IsCurrentUserFleetCommander();
        var canTransferCommand = IsCurrentUserFleetCommander();

        foreach (var player in _players
                     .OrderByDescending(player => IsFleetCommander(player.Name, player.Callsign))
                     .ThenBy(player => player.SquadName)
                     .ThenBy(player => player.Callsign ?? player.Name))
        {
            var isCommander = IsFleetCommander(player.Name, player.Callsign);
            var permission = GetFleetPermission(player.Name);
            var permissionEnabled = isCommander || permission?.PermissionEnabled == true;
            _fleetMemberRows.Add(new FleetMemberManagementRow
            {
                GameName = player.Name,
                Callsign = player.Callsign ?? "",
                DisplayName = string.IsNullOrWhiteSpace(player.Callsign)
                    ? player.Name
                    : $"{player.Callsign} ({player.Name})",
                Initials = player.Initials,
                AvatarPath = player.AvatarPath,
                SquadName = string.IsNullOrWhiteSpace(player.SquadName) ? "Unassigned" : player.SquadName,
                OnlineStatus = player.Status,
                RoleTitle = isCommander
                    ? "舰队指挥官"
                    : NormalizeRoleTitle(permission?.RoleTitle),
                PermissionEnabled = permissionEnabled,
                CanRemoveMembers = isCommander || permission?.CanRemoveMembers == true,
                CanPublishTasks = isCommander || permission?.CanPublishTasks == true,
                CanPublishPlans = isCommander || permission?.CanPublishPlans == true,
                CanManageFleetInfo = isCommander || permission?.CanManageFleetInfo == true,
                IsSelf = IsLocalPlayer(player.Name),
                IsCommander = isCommander,
                CanCurrentUserEditPermissions = canEditPermissions,
                CanCurrentUserRemove = CanCurrentUserRemoveFleetMember(player.Name, player.Callsign, isCommander),
                CanCurrentUserTransferCommand = canTransferCommand,
                RoleBrush = GetFleetRoleBrush(player.Name, player.Callsign)
            });
        }

        var knownPlayers = new HashSet<string>(
            _fleetMemberRows.Select(row => row.GameName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var permission in _fleetMemberPermissions.Values
                     .Where(item => !string.IsNullOrWhiteSpace(item.GameName) &&
                                    !knownPlayers.Contains(item.GameName))
                     .OrderBy(item => item.Callsign ?? item.GameName))
        {
            var isCommander = IsFleetCommander(permission.GameName, permission.Callsign);
            var displayName = string.IsNullOrWhiteSpace(permission.Callsign)
                ? permission.GameName
                : $"{permission.Callsign} ({permission.GameName})";

            _fleetMemberRows.Add(new FleetMemberManagementRow
            {
                GameName = permission.GameName,
                Callsign = permission.Callsign ?? "",
                DisplayName = displayName,
                Initials = GetInitials(permission.Callsign ?? permission.GameName),
                AvatarPath = "",
                SquadName = "Unassigned",
                OnlineStatus = "Offline",
                RoleTitle = isCommander
                    ? "舰队指挥官"
                    : NormalizeRoleTitle(permission.RoleTitle),
                PermissionEnabled = isCommander || permission.PermissionEnabled,
                CanRemoveMembers = isCommander || permission.CanRemoveMembers,
                CanPublishTasks = isCommander || permission.CanPublishTasks,
                CanPublishPlans = isCommander || permission.CanPublishPlans,
                CanManageFleetInfo = isCommander || permission.CanManageFleetInfo,
                IsSelf = IsLocalPlayer(permission.GameName),
                IsCommander = isCommander,
                CanCurrentUserEditPermissions = canEditPermissions,
                CanCurrentUserRemove = CanCurrentUserRemoveFleetMember(permission.GameName, permission.Callsign, isCommander),
                CanCurrentUserTransferCommand = canTransferCommand,
                RoleBrush = GetFleetRoleBrush(permission.GameName, permission.Callsign)
            });
        }

        if (FleetMemberManagementEmptyText is not null)
        {
            FleetMemberManagementEmptyText.Visibility = _fleetMemberRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void RefreshFleetApplications()
    {
        if (FleetApplicationsTab is null || FleetApplicationEmptyText is null)
        {
            return;
        }

        _fleetApplications.Clear();

        var canReviewApplications = _hasFleet && CanCurrentUserManageFleetInfo();
        FleetApplicationsTab.Visibility = canReviewApplications
            ? Visibility.Visible
            : Visibility.Collapsed;

        var pendingApplications = (_fleetApplicationSnapshots ?? [])
            .Where(application =>
                string.IsNullOrWhiteSpace(application.Status) ||
                application.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(application => application.CreatedAt)
            .ToArray();

        FleetApplicationsTab.Header = $"申请 {pendingApplications.Length}";

        if (!canReviewApplications)
        {
            FleetApplicationEmptyText.Visibility = Visibility.Visible;
            FleetApplicationStatusText.Text = "";
            return;
        }

        foreach (var application in pendingApplications)
        {
            var callsign = NormalizeOptionalField(application.ApplicantCallsign);
            var gameName = NormalizeOptionalField(application.ApplicantGameName);
            var displayName = string.IsNullOrWhiteSpace(callsign) ||
                              callsign.Equals(gameName, StringComparison.OrdinalIgnoreCase)
                ? gameName
                : $"{callsign} ({gameName})";

            _fleetApplications.Add(new FleetApplicationRow(
                application.Id,
                displayName,
                gameName,
                callsign,
                application.ApplicantAccount,
                string.IsNullOrWhiteSpace(application.Message) ? "无申请说明" : application.Message,
                string.IsNullOrWhiteSpace(application.Status) ? "Pending" : application.Status,
                application.CreatedAt == default
                    ? "-"
                    : application.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                GetInitials(string.IsNullOrWhiteSpace(callsign) ? gameName : callsign),
                application.AvatarImageData));
        }

        FleetApplicationEmptyText.Visibility = _fleetApplications.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshFleetNotificationCenter();
    }

    private async void ApproveFleetApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FleetApplicationRow row)
        {
            await DecideFleetApplicationAsync(row, approve: true);
        }
    }

    private async void RejectFleetApplication_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FleetApplicationRow row)
        {
            await DecideFleetApplicationAsync(row, approve: false);
        }
    }

    private async Task DecideFleetApplicationAsync(FleetApplicationRow row, bool approve)
    {
        if (!EnsureLoggedIn("审核加入申请需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!CanCurrentUserManageFleetInfo())
        {
            FleetApplicationStatusText.Text = "当前账号没有审核加入申请的权限。";
            return;
        }

        var actionText = approve ? "批准" : "拒绝";
        if (System.Windows.MessageBox.Show(
                this,
                $"确认{actionText} {row.DisplayName} 的加入申请？",
                "审核加入申请",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/fleets/applications/decide",
                new FleetApplicationDecisionRequest(_fleetCode, row.Id, approve));
            if (!response.IsSuccessStatusCode)
            {
                FleetApplicationStatusText.Text = $"{actionText}失败：{await ReadResponseErrorAsync(response)}";
                return;
            }

            var snapshot = await response.Content.ReadFromJsonAsync<NetworkFleetSnapshot>();
            if (snapshot is not null)
            {
                MergeNetworkFleetState(snapshot);
                SaveCurrentConfig();
            }

            await PullNetworkSnapshotsAsync(silent: true);
            await PullNetworkFleetsAsync(silent: true);
            FleetApplicationStatusText.Text = $"已{actionText} {row.DisplayName}。";
        }
        catch (Exception ex)
        {
            FleetApplicationStatusText.Text = $"{actionText}失败：{ex.Message}";
        }
    }

    private bool CanCurrentUserRemoveFleetMember(string? gameName, string? callsign, bool isCommander)
    {
        if (!_hasFleet || isCommander)
        {
            return false;
        }

        var identity = NormalizeOptionalField(gameName);
        if (IsLocalPlayer(identity) || IsLocalPlayer(callsign ?? ""))
        {
            return false;
        }

        if (IsCurrentUserFleetCommander())
        {
            return true;
        }

        if (!CanCurrentUserRemoveMembers())
        {
            return false;
        }

        var targetPermission = GetFleetPermission(gameName, callsign);
        return targetPermission is null || !targetPermission.PermissionEnabled;
    }

    private async void SaveFleetMemberPermission_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FleetMemberManagementRow row)
        {
            return;
        }

        if (row.IsCommander)
        {
            FleetMemberManagementStatusText.Text = "舰队指挥官默认拥有所有权限。";
            return;
        }

        if (!row.CanEditPermissions)
        {
            FleetMemberManagementStatusText.Text = "只有舰队指挥官可以分配权限。";
            return;
        }

        var role = NormalizeRoleTitle(row.RoleTitle);
        row.RoleTitle = role;
        _fleetMemberPermissions[row.GameName] = new LocalFleetMemberPermission(
            row.GameName,
            row.Callsign,
            role,
            row.PermissionEnabled,
            row.CanRemoveMembers,
            row.CanPublishTasks,
            row.CanPublishPlans,
            row.CanManageFleetInfo,
            DateTimeOffset.UtcNow);

        AddFleetLog("权限", "成员权限更新", $"{row.DisplayName} -> {role}");
        SaveCurrentConfig();
        var synced = await PushFleetMemberPermissionAsync(row, silent: true);
        RefreshFleetMemberManagement();
        FleetMemberManagementStatusText.Text = synced
            ? $"已保存并同步 {row.DisplayName} 的权限。"
            : $"已保存 {row.DisplayName} 的权限，但服务器同步失败。";
    }

    private async void RemoveFleetMember_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FleetMemberManagementRow row)
        {
            return;
        }

        if (!row.CanRemoveFromFleet)
        {
            FleetMemberManagementStatusText.Text = "当前账号没有移除此成员的权限。";
            return;
        }

        if (System.Windows.MessageBox.Show($"确认将 {row.DisplayName} 移出舰队？", "移除成员", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync("api/fleets/members/remove",
                new FleetMemberMutationRequest(_fleetCode, row.GameName));
            response.EnsureSuccessStatusCode();
            _fleetMemberPermissions.Remove(row.GameName);
            AddFleetLog("成员", "移除成员", $"{row.DisplayName} 被移出舰队");
            await PullNetworkSnapshotsAsync(silent: true);
            await PullNetworkFleetsAsync(silent: true);
            RefreshFleetMemberManagement();
            FleetMemberManagementStatusText.Text = $"已移除 {row.DisplayName}。";
        }
        catch (Exception ex)
        {
            FleetMemberManagementStatusText.Text = $"移除失败：{ex.Message}";
        }
    }

    private async void TransferFleetCommander_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FleetMemberManagementRow row)
        {
            return;
        }

        if (!row.CanTransferCommander)
        {
            FleetMemberManagementStatusText.Text = "只有舰队指挥官可以转移指挥权。";
            return;
        }

        if (System.Windows.MessageBox.Show($"确认将舰队指挥官转移给 {row.DisplayName}？", "转移舰队指挥官", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var response = await PostNetworkJsonAsync("api/fleets/transfer-commander",
                new FleetCommanderTransferRequest(_fleetCode, row.GameName));
            response.EnsureSuccessStatusCode();
            var snapshot = await response.Content.ReadFromJsonAsync<NetworkFleetSnapshot>();
            if (snapshot is not null)
            {
                MergeNetworkFleetState(snapshot);
            }

            AddFleetLog("权限", "转移舰队指挥官", $"{row.DisplayName} 成为新的舰队指挥官");
            SaveCurrentConfig();
            await PullNetworkFleetsAsync();
            FleetMemberManagementStatusText.Text = "舰队指挥官已转移。";
        }
        catch (Exception ex)
        {
            FleetMemberManagementStatusText.Text = $"转移失败：{ex.Message}";
        }
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
        MarkLocalSquadEdit(squad);
        squad.RefreshComputed();
        RefreshOverlayWindow();
        _ = PushFleetSquadsAsync(silent: true);
    }

    private void CreateSquad_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!EnsureIdentityInitialized("创建小队"))
        {
            return;
        }

        if (!_hasFleet)
        {
            JoinSquadHintText.Text = "请先加入舰队";
            return;
        }

        RepairLocalSquadLifecycle();
        if (CurrentUserCommandsAnySquad())
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

    private async void CreateSquadConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("创建小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!EnsureIdentityInitialized("创建小队"))
        {
            return;
        }

        if (!_hasFleet)
        {
            CreateSquadValidationText.Text = "请先加入舰队。";
            return;
        }

        RepairLocalSquadLifecycle();
        if (CurrentUserCommandsAnySquad())
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
        MarkLocalSquadEdit(squad);

        _squads.Add(squad);
        _selectedSquad = squad;
        _joinedSquad = squad;
        SquadSelectionList.SelectedItem = squad;
        CreateSquadPanel.Visibility = Visibility.Collapsed;
        JoinSquadHintText.Text = $"已创建并加入 {squad.Name}";
        AddFleetLog("成员", "创建小队", $"{FormatCommanderName(_callsign, _localPlayer)} 创建 {squad.Name}");
        RenderSquads();
        RenderMySquad();
        RefreshSquadActionButtons();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
        await PushFleetSquadsAsync(silent: true);
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

    private async void JoinSquad_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("加入小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!EnsureIdentityInitialized("加入小队"))
        {
            return;
        }

        if (!_hasFleet)
        {
            JoinSquadHintText.Text = "请先加入舰队";
            RefreshSquadActionButtons();
            return;
        }

        if (_selectedSquad is null)
        {
            JoinSquadHintText.Text = "请选择一个小队";
            return;
        }

        if (_joinedSquad is not null &&
            _joinedSquad.Name.Equals(_selectedSquad.Name, StringComparison.OrdinalIgnoreCase))
        {
            JoinSquadHintText.Text = $"已经在 {_joinedSquad.Name}";
            RefreshSquadActionButtons();
            return;
        }

        _joinedSquad = _selectedSquad;
        JoinSquadHintText.Text = $"已加入 {_joinedSquad.Name}";
        AddFleetLog("成员", "加入小队", $"{FormatCommanderName(_callsign, _localPlayer)} 加入 {_joinedSquad.Name}");
        RenderState();
        RefreshSquadActionButtons();
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
        await PushFleetSquadsAsync(silent: true);
    }

    private void SquadSelectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedSquad = SquadSelectionList.SelectedItem as SquadRow;
        RefreshSquadActionButtons();
        RenderMySquad();
    }

    private sealed class SquadSuccessorOption
    {
        public SquadSuccessorOption(PlayerRow player)
        {
            Player = player;
            DisplayName = FormatCommanderName(player.Callsign, player.Name);
            var status = player.Status.Equals("Online", StringComparison.OrdinalIgnoreCase) ? "在线" : "离线";
            DisplayText = $"{DisplayName} / {status}";
        }

        public PlayerRow Player { get; }
        public string DisplayName { get; }
        public string DisplayText { get; }
    }

    private bool IsCurrentUserSquadCommander(SquadRow squad)
    {
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            return false;
        }

        var commanderGameName = GetGameNameFromDisplayName(squad.Commander);
        var commanderCallsign = GetCallsignFromDisplayName(squad.Commander);
        return commanderGameName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase) ||
               (!string.IsNullOrWhiteSpace(_callsign) &&
                commanderCallsign.Equals(_callsign, StringComparison.OrdinalIgnoreCase));
    }

    private bool CurrentUserCommandsAnySquad()
    {
        return _squads.Any(IsCurrentUserSquadCommander);
    }

    private static PlayerRow? PickRecommendedSquadSuccessor(IEnumerable<PlayerRow> members)
    {
        return members
            .OrderByDescending(member => member.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
            .ThenBy(member => member.Callsign ?? member.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private SquadSuccessorOption? PromptSquadSuccessor(
        IReadOnlyList<PlayerRow> candidates,
        PlayerRow recommended,
        string squadName)
    {
        var options = candidates
            .OrderByDescending(member => member.Name.Equals(recommended.Name, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(member => member.Status.Equals("Online", StringComparison.OrdinalIgnoreCase))
            .ThenBy(member => member.Callsign ?? member.Name, StringComparer.OrdinalIgnoreCase)
            .Select(member => new SquadSuccessorOption(member))
            .ToList();
        var selected = options.FirstOrDefault(option =>
                           option.Player.Name.Equals(recommended.Name, StringComparison.OrdinalIgnoreCase)) ??
                       options.FirstOrDefault();
        if (selected is null)
        {
            return null;
        }

        var dialog = new Window
        {
            Owner = this,
            Title = "移交小队指挥权",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = FindBrush("PanelBackgroundBrush", new SolidColorBrush(Color.FromRgb(3, 12, 20))),
            Foreground = Brushes.White
        };

        var comboBox = new System.Windows.Controls.ComboBox
        {
            ItemsSource = options,
            SelectedItem = selected,
            DisplayMemberPath = nameof(SquadSuccessorOption.DisplayText),
            Margin = new Thickness(0, 12, 0, 0),
            MinHeight = 34
        };

        var confirmButton = new System.Windows.Controls.Button
        {
            Content = "确认移交并离开",
            Width = 150,
            MinHeight = 34,
            Margin = new Thickness(8, 0, 0, 0)
        };
        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 100,
            MinHeight = 34
        };

        SquadSuccessorOption? result = null;
        confirmButton.Click += (_, _) =>
        {
            result = comboBox.SelectedItem as SquadSuccessorOption;
            dialog.DialogResult = result is not null;
            dialog.Close();
        };
        cancelButton.Click += (_, _) =>
        {
            dialog.DialogResult = false;
            dialog.Close();
        };

        var buttonRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(confirmButton);

        var panel = new StackPanel
        {
            Margin = new Thickness(24)
        };
        panel.Children.Add(new TextBlock
        {
            Text = "离开前需要移交小队指挥权",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = FindBrush("AccentBrush", Brushes.DeepSkyBlue)
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"小队 {squadName} 仍有其他成员。请选择新的小队指挥官，默认已选择推荐接任者。",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            Foreground = FindBrush("MutedTextBrush", Brushes.LightSteelBlue)
        });
        panel.Children.Add(comboBox);
        panel.Children.Add(buttonRow);
        dialog.Content = panel;

        return dialog.ShowDialog() == true ? result : null;
    }

    private async void LeaveSquad_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("离开小队需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (_joinedSquad is null)
        {
            JoinSquadHintText.Text = "当前没有加入小队";
            RefreshSquadActionButtons();
            return;
        }

        var squad = _joinedSquad;
        var squadName = squad.Name;
        var isCommander = IsCurrentUserSquadCommander(squad);
        var squadMembers = _players
            .Where(player => player.SquadName.Equals(squadName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var remainingMembers = squadMembers
            .Where(player => !IsLocalPlayerIdentity(player.Name, player.Callsign))
            .ToList();
        PlayerRow? successor = null;

        if (!isCommander)
        {
            if (System.Windows.MessageBox.Show(
                    this,
                    $"确认离开小队 {squadName}？",
                    "离开小队",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else if (remainingMembers.Count == 0)
        {
            if (System.Windows.MessageBox.Show(
                    this,
                    $"离开后小队 {squadName} 将解散。确认解散并离开？",
                    "解散小队",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
        }
        else
        {
            var recommended = PickRecommendedSquadSuccessor(remainingMembers);
            if (recommended is null)
            {
                JoinSquadHintText.Text = "没有可移交的小队成员";
                return;
            }

            var selection = PromptSquadSuccessor(remainingMembers, recommended, squadName);
            if (selection is null)
            {
                return;
            }

            successor = selection.Player;
        }

        try
        {
            var response = await PostNetworkJsonAsync(
                "api/fleets/squads/leave",
                new FleetSquadLeaveRequest(
                    _fleetCode,
                    squadName,
                    successor?.Name,
                    successor?.Callsign));
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadResponseErrorAsync(response);
                JoinSquadHintText.Text = $"离开失败：{error}";
                return;
            }
        }
        catch (Exception ex)
        {
            JoinSquadHintText.Text = $"离开失败：{ex.Message}";
            return;
        }

        for (var i = 0; i < _players.Count; i++)
        {
            var player = _players[i];
            if (IsLocalPlayerIdentity(player.Name, player.Callsign) &&
                player.SquadName.Equals(squadName, StringComparison.OrdinalIgnoreCase))
            {
                _players[i] = player with { SquadName = "Unassigned" };
            }
        }

        if (isCommander && remainingMembers.Count == 0)
        {
            _squads.Remove(squad);
            if (_selectedSquad is not null &&
                _selectedSquad.Name.Equals(squadName, StringComparison.OrdinalIgnoreCase))
            {
                _selectedSquad = null;
                SquadSelectionList.SelectedItem = null;
            }
        }
        else if (successor is not null)
        {
            squad.Commander = FormatCommanderName(successor.Callsign, successor.Name);
            squad.RefreshComputed();
        }

        _joinedSquad = null;
        JoinSquadHintText.Text = isCommander && remainingMembers.Count == 0
            ? $"已解散 {squadName}"
            : $"已离开 {squadName}";
        await PullNetworkFleetsAsync(silent: true);
        await PullNetworkSnapshotsAsync(silent: true);
        RenderState();
        RenderMySquad();
        RefreshSquadActionButtons();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        await PushLocalSnapshotAsync(silent: true, pushFleetDirectory: false);
    }

    private async void RemoveSquadMember_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SquadMemberStatusRow row } ||
            _selectedSquad is null ||
            !row.CanRemoveFromSquad)
        {
            return;
        }

        if (System.Windows.MessageBox.Show(
                this,
                $"确认将 {row.Callsign} ({row.GameId}) 移出小队 {_selectedSquad.Name}？",
                "移除小队成员",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var request = new FleetSquadMemberMutationRequest(
                _fleetCode,
                _selectedSquad.Name,
                row.GameId,
                row.Callsign == "-" ? null : row.Callsign);
            var response = await PostNetworkJsonAsync("api/fleets/squads/members/remove", request);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadResponseErrorAsync(response);
                JoinSquadHintText.Text = $"移除失败：{error}";
                return;
            }

            for (var i = 0; i < _players.Count; i++)
            {
                var player = _players[i];
                var sameSquad = player.SquadName.Equals(_selectedSquad.Name, StringComparison.OrdinalIgnoreCase);
                var samePlayer = player.Name.Equals(row.GameId, StringComparison.OrdinalIgnoreCase) ||
                                 (!string.IsNullOrWhiteSpace(player.Callsign) &&
                                  player.Callsign.Equals(row.Callsign, StringComparison.OrdinalIgnoreCase));
                if (sameSquad && samePlayer)
                {
                    _players[i] = player with { SquadName = "Unassigned" };
                }
            }

            AddFleetLog("成员", "移除小队成员", $"{row.Callsign} ({row.GameId}) 被移出 {_selectedSquad.Name}");
            await PullNetworkFleetsAsync(silent: true);
            await PullNetworkSnapshotsAsync(silent: true);
            RenderState();
            RenderMySquad();
            RefreshSquadActionButtons();
            RefreshOverlayWindow();
            JoinSquadHintText.Text = $"已移除 {row.Callsign}";
        }
        catch (Exception ex)
        {
            JoinSquadHintText.Text = $"移除失败：{ex.Message}";
        }
    }

    private void RefreshSquadActionButtons()
    {
        if (JoinSquadButton is null || LeaveSquadButton is null || JoinSquadHintText is null)
        {
            return;
        }

        LeaveSquadButton.Visibility = _joinedSquad is null ? Visibility.Collapsed : Visibility.Visible;
        if (!_hasFleet)
        {
            JoinSquadButton.IsEnabled = false;
            JoinSquadButton.Content = "加入小队";
            JoinSquadHintText.Text = "请先加入舰队";
            return;
        }

        if (_selectedSquad is null)
        {
            JoinSquadButton.IsEnabled = false;
            JoinSquadButton.Content = "加入小队";
            JoinSquadHintText.Text = "请选择一个小队";
            return;
        }

        var sameSquad = _joinedSquad is not null &&
                        _joinedSquad.Name.Equals(_selectedSquad.Name, StringComparison.OrdinalIgnoreCase);
        JoinSquadButton.IsEnabled = !sameSquad;
        JoinSquadButton.Content = sameSquad
            ? "已加入"
            : _joinedSquad is null
                ? "加入小队"
                : "切换小队";
        JoinSquadHintText.Text = sameSquad
            ? $"已加入 {_joinedSquad!.Name}"
            : _joinedSquad is null
                ? "可以加入所选小队"
                : $"将从 {_joinedSquad.Name} 切换到 {_selectedSquad.Name}";
    }

    private void OpenPublishTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布任务需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!CanCurrentUserPublishTasks())
        {
            PublishTaskValidationText.Text = "当前账号没有发布任务的权限。";
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

        if (!CanCurrentUserPublishTasks())
        {
            PublishTaskValidationText.Text = "当前账号没有发布集结点的权限。";
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

    private async void PublishFleetTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布任务需要先登录星海舰桥账号。"))
        {
            return;
        }

        if (!CanCurrentUserPublishTasks())
        {
            PublishTaskValidationText.Text = "当前账号没有发布任务的权限。";
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
        await PushFleetTaskAsync(silent: true);
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

        if (!CanCurrentUserPublishTasks())
        {
            AppendOutput("Current account cannot edit fleet tasks.");
            return;
        }

        if (string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            return;
        }

        OpenPublishTaskPanel(editCurrent: true);
    }

    private async void CompleteCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("完成任务需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserPublishTasks())
        {
            AppendOutput("Current account cannot complete fleet tasks.");
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
        await PushFleetTaskAsync(silent: true);
    }

    private async void DeleteCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("删除任务需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserPublishTasks())
        {
            AppendOutput("Current account cannot delete fleet tasks.");
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
        await PushFleetTaskAsync(silent: true);
    }

    private async void RenotifyCurrentTaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("再次通知需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserPublishTasks())
        {
            AppendOutput("Current account cannot re-notify fleet tasks.");
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
        await PushFleetTaskAsync(silent: true);
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

        if (!CanCurrentUserManageFleetInfo())
        {
            FleetNoticeValidationText.Text = "当前账号没有发布舰队公告的权限。";
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

        if (!CanCurrentUserPublishPlans())
        {
            ActionPlanValidationText.Text = "当前账号没有发布行动计划的权限。";
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

    private static string NormalizeOptionalField(string? value)
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
            : "0.3.14";
    }

    private void FleetActionPlanCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_selectedFleetInfoPanel == FleetInfoPanelKind.ActionPlan)
        {
            if (!CanCurrentUserPublishPlans())
            {
                return;
            }

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
        else if (_selectedFleetInfoPanel == FleetInfoPanelKind.Notice && CanCurrentUserManageFleetInfo())
        {
            OpenFleetNoticeEditor();
        }
    }

    private void FleetNotificationCenterAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FleetNotificationCenterItemRow row ||
            string.IsNullOrWhiteSpace(row.ActionKey))
        {
            return;
        }

        switch (row.ActionKey)
        {
            case "find-fleet":
                MainTabs.SelectedItem = FindFleetTab;
                SetActiveNav(FindFleetNavButton);
                _ = PullNetworkFleetsAsync(silent: true);
                break;
            case "applications":
                OpenManageFleetSection(FleetApplicationsTab);
                break;
            case "task-detail":
                _selectedFleetInfoPanel = FleetInfoPanelKind.CurrentTask;
                RefreshFleetInfoPanel();
                OpenCurrentTaskDetail();
                break;
            case "task-manage":
                OpenManageFleetSection(ManageFleetTaskTab);
                break;
            case "plan-detail":
                _selectedFleetInfoPanel = FleetInfoPanelKind.ActionPlan;
                RefreshFleetInfoPanel();
                if (CanCurrentUserPublishPlans())
                {
                    OpenManageFleetSection(ManageFleetPlanTab);
                }
                break;
            case "plan-manage":
                OpenManageFleetSection(ManageFleetPlanTab);
                break;
            case "notice-detail":
                _selectedFleetInfoPanel = FleetInfoPanelKind.Notice;
                RefreshFleetInfoPanel();
                if (CanCurrentUserManageFleetInfo())
                {
                    OpenFleetNoticeEditor();
                }
                break;
            case "notice-edit":
                OpenManageFleetSection(ManageFleetNoticeTab);
                OpenFleetNoticeEditor();
                break;
            case "logs":
                OpenManageFleetSection(ManageFleetLogTab);
                break;
        }
    }

    private void OpenManageFleetSection(TabItem? manageTab)
    {
        if (!_hasFleet || manageTab is null)
        {
            return;
        }

        RefreshFleetManagementPermissions();
        MainTabs.SelectedItem = FleetTab;
        FleetSubTabs.SelectedItem = ManageFleetTab;
        if (ManageFleetTab.Visibility == Visibility.Visible &&
            manageTab.Visibility == Visibility.Visible)
        {
            ManageFleetTabs.SelectedItem = manageTab;
        }

        SetActiveNav(MyFleetNavButton);
    }

    private void OpenFleetNoticeEditor()
    {
        if (!CanCurrentUserManageFleetInfo())
        {
            return;
        }

        FleetNoticeTitleBox.Text = _fleetNoticeTitle;
        FleetNoticeContentBox.Text = _fleetNoticeContent;
        FleetNoticeValidationText.Text = "";
        FleetNoticeEditorPanel.Visibility = Visibility.Visible;
    }

    private void CancelFleetNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        FleetNoticeEditorPanel.Visibility = Visibility.Collapsed;
    }

    private async void PublishFleetNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布舰队公告需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserManageFleetInfo())
        {
            FleetNoticeValidationText.Text = "当前账号没有发布舰队公告的权限。";
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
        await PushFleetNoticeAsync(silent: true);
    }

    private async void SaveFleetDescriptionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("修改舰队介绍需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserManageFleetInfo())
        {
            FleetDescriptionStatusText.Text = "当前账号没有修改舰队介绍的权限。";
            return;
        }

        _fleetDescription = NormalizeOptionalField(FleetDescriptionEditBox.Text);
        FleetDescriptionStatusText.Text = "舰队介绍已保存。";
        AddFleetLog("舰队", "舰队介绍更新", _fleetDescription);
        RefreshFleetHeader();
        RefreshTaskManagementPanel();
        SaveCurrentConfig();
        await PushFleetInfoAsync(silent: true);
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

        if (!CanCurrentUserPublishPlans())
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

    private async void PublishActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("发布行动计划需要先登录。"))
        {
            return;
        }

        if (!CanCurrentUserPublishPlans())
        {
            ActionPlanValidationText.Text = "当前账号没有发布行动计划的权限。";
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
        SaveCurrentConfig();
        await PushFleetActionPlansAsync(silent: true);
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

    private async void JoinFleetActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("预约行动计划需要先登录。"))
        {
            return;
        }

        e.Handled = true;
        if (_joinedActionPlanIds.Contains(_selectedActionPlanId))
        {
            await LeaveSelectedActionPlanAsync();
            RefreshFleetInfoPanel();
            RefreshTaskManagementPanel();
            AppendOutput("Action reservation canceled.");
            return;
        }

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

    private async void ConfirmJoinActionPlanButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureLoggedIn("预约行动计划需要先登录。"))
        {
            return;
        }

        _joinActionNotifyMe = JoinActionNotifyCheck.IsChecked == true;
        await JoinSelectedActionPlanAsync();
        JoinActionPlanPanel.Visibility = Visibility.Collapsed;
        RefreshFleetInfoPanel();
        RefreshTaskManagementPanel();
        AppendOutput(_joinActionNotifyMe
            ? "Action joined. Email reminder requested for 5 minutes before start."
            : "Action joined.");
    }

    private async Task JoinSelectedActionPlanAsync()
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
        SaveCurrentConfig();
        await PushFleetActionPlanJoinAsync(plan, plan.Participants.Last(), silent: true);
    }

    private async Task LeaveSelectedActionPlanAsync()
    {
        if (string.IsNullOrWhiteSpace(_selectedActionPlanId))
        {
            return;
        }

        var plan = _fleetActionPlans.FirstOrDefault(plan =>
            plan.Id.Equals(_selectedActionPlanId, StringComparison.OrdinalIgnoreCase));
        if (plan is null || !_joinedActionPlanIds.Remove(plan.Id))
        {
            return;
        }

        var identities = EnumerateLocalIdentities().ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = plan.Participants.Count - 1; index >= 0; index--)
        {
            var participant = plan.Participants[index];
            if (identities.Contains(participant.GameName) ||
                identities.Contains(participant.Callsign) ||
                IsLocalPlayer(participant.GameName))
            {
                plan.Participants.RemoveAt(index);
            }
        }

        plan.RefreshParticipantSummary();
        SaveCurrentConfig();
        await PushFleetActionPlanLeaveAsync(plan.Id, silent: true);
        RefreshOverlayWindow();
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        var croppedPath = ChooseAndCropImage("Choose player avatar", "player-avatar.png");
        if (croppedPath is null)
        {
            return;
        }

        _avatarPath = croppedPath;
        _cachedAvatarImagePath = null;
        _cachedAvatarImageData = null;
        SaveCurrentConfig();
        LoadAvatarPreview();
        RenderState();
        _ = PushLocalSnapshotAsync(silent: true);
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

    private async void ChooseFleetLogo_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasFleet && !_isCreatingFleet)
        {
            AppendOutput("Open fleet creation before choosing a fleet logo.");
            return;
        }

        if (_hasFleet && !CanCurrentUserManageFleetInfo())
        {
            AppendOutput("Current account cannot update the fleet logo.");
            return;
        }

        var croppedPath = ChooseAndCropImage("Choose fleet logo", "fleet-logo.png");
        if (croppedPath is null)
        {
            return;
        }

        if (!_hasFleet && _isCreatingFleet)
        {
            _createFleetLogoPath = croppedPath;
            LoadCreateFleetLogoPreview();
            AppendOutput("Fleet logo selected for new fleet.");
            return;
        }

        _fleetLogoPath = croppedPath;
        MarkLocalFleetLogoEdit();
        LoadCreateFleetLogoPreview();
        LoadFleetHeaderLogoPreview();
        SaveCurrentConfig();
        if (_hasFleet)
        {
            await PushFleetInfoAsync(silent: true);
        }

        AppendOutput("Fleet logo updated.");
    }

    private void FleetHeaderLogo_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hasFleet || !CanCurrentUserManageFleetInfo())
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

    private async void ChooseSquadEmblem(SquadRow? squad)
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
        MarkLocalSquadEdit(squad);
        squad.RefreshComputed();
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();
        SaveCurrentConfig();
        await PushFleetSquadsAsync(silent: true);
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

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropWindow.CroppedImage));

        using var memoryStream = new MemoryStream();
        encoder.Save(memoryStream);
        var bytes = memoryStream.ToArray();
        var prefix = BuildLocalImagePrefix(fileName);
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var outputPath = BuildImagePath(prefix, hash);
        WriteImageFileIfChanged(outputPath, bytes);
        CleanupImageVariants(prefix, outputPath);
        return outputPath;
    }

    private static string BuildLocalImagePrefix(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var safeName = new string((name ?? "image").Where(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "image" : safeName;
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

        AddOverlayEditorCrosshair();
    }

    private void AddOverlayEditorCrosshair()
    {
        if (!_overlaySettings.ShowCrosshair ||
            OverlayEditorCanvas.ActualWidth <= 0 ||
            OverlayEditorCanvas.ActualHeight <= 0)
        {
            return;
        }

        var accent = GetCrosshairPreviewBrush(_overlaySettings);
        var crosshair = _overlaySettings.CrosshairMode == OverlayCrosshairMode.Tech
            ? CreateTechCrosshairPreview(accent, _overlaySettings.CrosshairSize, _overlaySettings.CrosshairThickness, _overlaySettings.CrosshairOpacity)
            : CreateSimpleCrosshairPreview(accent, _overlaySettings.CrosshairSize, _overlaySettings.CrosshairThickness, _overlaySettings.CrosshairOpacity);

        Canvas.SetLeft(crosshair, (OverlayEditorCanvas.ActualWidth - crosshair.Width) / 2.0);
        Canvas.SetTop(crosshair, (OverlayEditorCanvas.ActualHeight - crosshair.Height) / 2.0);
        OverlayEditorCanvas.Children.Add(crosshair);
    }

    private static Canvas CreateSimpleCrosshairPreview(System.Windows.Media.Brush brush, double size, double thickness, double opacity)
    {
        size = Math.Clamp(size, 48, 240);
        thickness = Math.Clamp(thickness, 1, 8);
        var center = size / 2.0;
        var gap = Math.Max(8, size * 0.145);
        var arm = Math.Max(14, size * 0.25);

        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            Opacity = Math.Clamp(opacity, 0.2, 1.0),
            IsHitTestVisible = false
        };

        AddLine(canvas, center, center - gap - arm, center, center - gap, brush, thickness);
        AddLine(canvas, center, center + gap, center, center + gap + arm, brush, thickness);
        AddLine(canvas, center - gap - arm, center, center - gap, center, brush, thickness);
        AddLine(canvas, center + gap, center, center + gap + arm, center, brush, thickness);

        var dotSize = Math.Max(3, thickness * 2);
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = dotSize,
            Height = dotSize,
            Fill = brush
        };
        Canvas.SetLeft(dot, center - dotSize / 2.0);
        Canvas.SetTop(dot, center - dotSize / 2.0);
        canvas.Children.Add(dot);
        return canvas;
    }

    private static Canvas CreateTechCrosshairPreview(System.Windows.Media.Brush accent, double size, double thickness, double opacity)
    {
        size = Math.Clamp(size, 48, 240);
        thickness = Math.Clamp(thickness, 1, 8);
        var center = size / 2.0;
        var soft = accent.Clone();
        soft.Opacity = 0.42;

        var canvas = new Canvas
        {
            Width = size,
            Height = size,
            Opacity = Math.Clamp(opacity, 0.2, 1.0),
            IsHitTestVisible = false
        };

        var outerSize = size * 0.324;
        var outerRing = new System.Windows.Shapes.Ellipse
        {
            Width = outerSize,
            Height = outerSize,
            Stroke = soft,
            StrokeThickness = Math.Max(1, thickness * 0.55)
        };
        Canvas.SetLeft(outerRing, center - outerSize / 2.0);
        Canvas.SetTop(outerRing, center - outerSize / 2.0);
        canvas.Children.Add(outerRing);

        var centerSize = Math.Max(8, size * 0.085);
        var centerRing = new System.Windows.Shapes.Ellipse
        {
            Width = centerSize,
            Height = centerSize,
            Stroke = accent,
            StrokeThickness = Math.Max(1.1, thickness * 0.7)
        };
        Canvas.SetLeft(centerRing, center - centerSize / 2.0);
        Canvas.SetTop(centerRing, center - centerSize / 2.0);
        canvas.Children.Add(centerRing);

        var majorInner = size * 0.338;
        var majorOuter = size * 0.873;
        var majorStart = size * 0.127;
        var majorEnd = size * 0.662;
        AddLine(canvas, center, majorStart, center, majorInner, accent, thickness);
        AddLine(canvas, center, majorEnd, center, majorOuter, accent, thickness);
        AddLine(canvas, majorStart, center, majorInner, center, accent, thickness);
        AddLine(canvas, majorEnd, center, majorOuter, center, accent, thickness);

        var tickInner = size * 0.38;
        var tickOuter = size * 0.465;
        var tickInner2 = size * 0.535;
        var tickOuter2 = size * 0.62;
        var thin = Math.Max(1, thickness * 0.75);
        AddLine(canvas, tickInner, center, tickOuter, center, soft, thin);
        AddLine(canvas, tickInner2, center, tickOuter2, center, soft, thin);
        AddLine(canvas, center, tickInner, center, tickOuter, soft, thin);
        AddLine(canvas, center, tickInner2, center, tickOuter2, soft, thin);
        return canvas;
    }

    private static System.Windows.Media.Brush GetCrosshairPreviewBrush(OverlayDisplaySettings settings)
    {
        if (!settings.CrosshairUseThemeColor && TryParseHexColor(settings.CrosshairColor, out var customColor))
        {
            return new SolidColorBrush(customColor);
        }

        return GetOverlayThemeAccent(settings.Theme);
    }

    private static void AddLine(Canvas canvas, double x1, double y1, double x2, double y2, System.Windows.Media.Brush brush, double thickness)
    {
        canvas.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Square,
            StrokeEndLineCap = PenLineCap.Square
        });
    }

    private static System.Windows.Media.Brush GetOverlayThemeAccent(OverlayVisualTheme theme)
    {
        return theme switch
        {
            OverlayVisualTheme.Anvil => new SolidColorBrush(Color.FromRgb(78, 255, 171)),
            OverlayVisualTheme.Drake => new SolidColorBrush(Color.FromRgb(255, 178, 48)),
            OverlayVisualTheme.Argo => new SolidColorBrush(Color.FromRgb(255, 132, 73)),
            OverlayVisualTheme.Musashi => new SolidColorBrush(Color.FromRgb(255, 228, 128)),
            OverlayVisualTheme.Mirai => new SolidColorBrush(Color.FromRgb(134, 225, 255)),
            OverlayVisualTheme.Crusader => new SolidColorBrush(Color.FromRgb(110, 205, 255)),
            OverlayVisualTheme.Aegis => new SolidColorBrush(Color.FromRgb(84, 245, 232)),
            OverlayVisualTheme.Rsi => new SolidColorBrush(Color.FromRgb(214, 201, 255)),
            OverlayVisualTheme.Origin => new SolidColorBrush(Color.FromRgb(176, 219, 255)),
            OverlayVisualTheme.Aopoa => new SolidColorBrush(Color.FromRgb(126, 255, 237)),
            OverlayVisualTheme.Esperia => new SolidColorBrush(Color.FromRgb(255, 92, 112)),
            OverlayVisualTheme.Gatac => new SolidColorBrush(Color.FromRgb(255, 205, 230)),
            _ => new SolidColorBrush(Color.FromRgb(83, 190, 255))
        };
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

    private sealed class DialogOwner(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
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
        AutoThemeByShipCheck.IsChecked = _overlaySettings.AutoThemeByShip;
        ShowCrosshairCheck.IsChecked = _overlaySettings.ShowCrosshair;
        SimpleCrosshairRadio.IsChecked = _overlaySettings.CrosshairMode == OverlayCrosshairMode.Simple;
        TechCrosshairRadio.IsChecked = _overlaySettings.CrosshairMode == OverlayCrosshairMode.Tech;
        CrosshairThemeColorCheck.IsChecked = _overlaySettings.CrosshairUseThemeColor;
        CrosshairSizeSlider.Value = Math.Clamp(_overlaySettings.CrosshairSize, 48, 240);
        CrosshairThicknessSlider.Value = Math.Clamp(_overlaySettings.CrosshairThickness, 1, 8);
        CrosshairOpacitySlider.Value = Math.Clamp(_overlaySettings.CrosshairOpacity, 0.2, 1.0) * 100.0;
        CrosshairColorBox.Text = OverlayDisplaySettings.NormalizeCrosshairColor(_overlaySettings.CrosshairColor);
        OverlayThemeBox.SelectedIndex = _overlaySettings.Theme switch
        {
            OverlayVisualTheme.Anvil => 1,
            OverlayVisualTheme.Drake => 2,
            OverlayVisualTheme.Argo => 3,
            OverlayVisualTheme.Musashi => 4,
            OverlayVisualTheme.Mirai => 5,
            OverlayVisualTheme.Crusader => 6,
            OverlayVisualTheme.Aegis => 7,
            OverlayVisualTheme.Rsi => 8,
            OverlayVisualTheme.Origin => 9,
            OverlayVisualTheme.Aopoa => 10,
            OverlayVisualTheme.Esperia => 11,
            OverlayVisualTheme.Gatac => 12,
            _ => 0
        };
        OverlayOpacitySlider.Value = Math.Clamp(_overlaySettings.Opacity, 0.15, 1.0) * 100.0;
        OverlayOpacityValueText.Text = $"{Math.Round(OverlayOpacitySlider.Value)}%";
        ShowCallsignOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignOnly;
        ShowGameNameOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.GameNameOnly;
        ShowCallsignAndNameRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignAndGameName;
        RefreshCrosshairSettingLabels();
    }

    private void RefreshCrosshairSettingLabels()
    {
        if (CrosshairSizeSlider is null ||
            CrosshairThicknessSlider is null ||
            CrosshairOpacitySlider is null ||
            CrosshairSizeValueText is null ||
            CrosshairThicknessValueText is null ||
            CrosshairOpacityValueText is null ||
            CrosshairColorBox is null ||
            CrosshairColorPreview is null ||
            CrosshairThemeColorCheck is null ||
            CrosshairColorPickerButton is null)
        {
            return;
        }

        CrosshairSizeValueText.Text = $"{Math.Round(CrosshairSizeSlider.Value)}px";
        CrosshairThicknessValueText.Text = $"{CrosshairThicknessSlider.Value:0.##}px";
        CrosshairOpacityValueText.Text = $"{Math.Round(CrosshairOpacitySlider.Value)}%";
        var usesThemeColor = CrosshairThemeColorCheck.IsChecked == true;
        CrosshairColorBox.IsEnabled = !usesThemeColor;
        CrosshairColorBox.Opacity = usesThemeColor ? 0.6 : 1.0;

        CrosshairColorPreview.Background = usesThemeColor
            ? GetOverlayThemeAccent(_overlaySettings.Theme)
            : new SolidColorBrush(TryParseHexColor(CrosshairColorBox.Text, out var parsed)
                ? parsed
                : Color.FromRgb(235, 247, 255));
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
            ? "搜索公开舰队，确认加入方式后即可进入同一舰队同步。已加入的舰队会显示为当前舰队。"
            : "Search public fleets, confirm the join policy, then join the same fleet for synchronization.";
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
        AutoThemeByShipCheck.Content = zh ? "自动切换至当前飞船厂商风格" : "Auto switch to current ship manufacturer style";
        if (OverlayThemeBox.Items.Count >= 13)
        {
            ((ComboBoxItem)OverlayThemeBox.Items[0]).Content = zh ? "默认" : "Default";
            ((ComboBoxItem)OverlayThemeBox.Items[1]).Content = zh ? "铁砧" : "Anvil";
            ((ComboBoxItem)OverlayThemeBox.Items[2]).Content = zh ? "德雷克" : "Drake";
            ((ComboBoxItem)OverlayThemeBox.Items[3]).Content = zh ? "南船座" : "Argo";
            ((ComboBoxItem)OverlayThemeBox.Items[4]).Content = zh ? "武藏" : "MISC";
            ((ComboBoxItem)OverlayThemeBox.Items[5]).Content = zh ? "未来" : "Mirai";
            ((ComboBoxItem)OverlayThemeBox.Items[6]).Content = zh ? "十字军" : "Crusader";
            ((ComboBoxItem)OverlayThemeBox.Items[7]).Content = zh ? "圣盾" : "Aegis";
            ((ComboBoxItem)OverlayThemeBox.Items[8]).Content = "RSI";
            ((ComboBoxItem)OverlayThemeBox.Items[9]).Content = zh ? "起源" : "Origin";
            ((ComboBoxItem)OverlayThemeBox.Items[10]).Content = zh ? "奥波亚" : "Aopoa";
            ((ComboBoxItem)OverlayThemeBox.Items[11]).Content = zh ? "埃斯佩里亚" : "Esperia";
            ((ComboBoxItem)OverlayThemeBox.Items[12]).Content = zh ? "盖塔克" : "Gatac";
        }
        CrosshairLabel.Text = zh ? "虚拟准星" : "VIRTUAL CROSSHAIR";
        ShowCrosshairCheck.Content = zh ? "显示虚拟准星" : "Show virtual crosshair";
        SimpleCrosshairRadio.Content = zh ? "简洁" : "Simple";
        TechCrosshairRadio.Content = zh ? "科技" : "Tech";
        CrosshairThemeColorCheck.Content = zh ? "跟随当前风格颜色" : "Use current theme color";
        CrosshairSizeLabel.Text = zh ? "大小" : "SIZE";
        CrosshairThicknessLabel.Text = zh ? "粗细" : "THICKNESS";
        CrosshairOpacityLabel.Text = zh ? "准星透明度" : "CROSSHAIR OPACITY";
        CrosshairColorPickerButton.Content = zh ? "选择颜色" : "Pick color";
        CrosshairColorPreview.ToolTip = zh ? "选择颜色" : "Pick color";
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
        EmailNotificationsCheck.Content = zh ? "允许接收舰队邮件通知" : "Allow fleet email notifications";
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

        if (string.IsNullOrWhiteSpace(_createFleetLogoPath) || !File.Exists(_createFleetLogoPath))
        {
            CreateFleetLogoImage.Source = null;
            CreateFleetLogoText.Visibility = Visibility.Visible;
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(_createFleetLogoPath);
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
        RefreshFleetManagementPermissions();
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

        RefreshFleetNotificationCenter();
    }

    private void RefreshFleetNotificationCenter()
    {
        if (FleetNotificationCenterSummaryText is null)
        {
            return;
        }

        _fleetNotificationCenterItems.Clear();

        if (!_hasFleet)
        {
            FleetNotificationCenterSummaryText.Text = "加入或创建舰队后显示任务、计划和舰队动态。";
            _fleetNotificationCenterItems.Add(new FleetNotificationCenterItemRow(
                "入门",
                "尚未加入舰队",
                "寻找已有舰队，或创建自己的舰队。",
                "",
                "前往",
                "find-fleet",
                Brushes.DeepSkyBlue));
            return;
        }

        var added = 0;
        void AddItem(
            string kind,
            string title,
            string detail,
            string timeText,
            string actionText,
            string actionKey,
            System.Windows.Media.Brush accentBrush)
        {
            if (added >= 5)
            {
                return;
            }

            _fleetNotificationCenterItems.Add(new FleetNotificationCenterItemRow(
                kind,
                TruncateNotificationText(title, 34),
                TruncateNotificationText(detail, 52),
                timeText,
                actionText,
                actionKey,
                accentBrush));
            added++;
        }

        var pendingApplications = CountPendingFleetApplications();
        if (pendingApplications > 0 && CanCurrentUserManageFleetInfo())
        {
            AddItem(
                "待处理",
                $"{pendingApplications} 个加入申请",
                "前往管理舰队审核新成员。",
                "",
                "处理",
                "applications",
                Brushes.Orange);
        }

        if (!string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle))
        {
            AddItem(
                "当前任务",
                _fleetCurrentTaskTitle,
                BuildCurrentTaskNotificationDetail(),
                _fleetCurrentTaskTime is null ? "" : _fleetCurrentTaskTime.Value.ToString("MM-dd HH:mm"),
                "详情",
                "task-detail",
                Brushes.DeepSkyBlue);
        }
        else if (CanCurrentUserPublishTasks())
        {
            AddItem(
                "当前任务",
                "暂无当前任务",
                "前往管理舰队发布任务或集结点。",
                "",
                "发布",
                "task-manage",
                Brushes.DeepSkyBlue);
        }

        var nextPlan = GetVisibleActionPlans()
            .Where(plan => plan.StartTime >= DateTime.Now)
            .OrderBy(plan => plan.StartTime)
            .FirstOrDefault();
        if (nextPlan is not null)
        {
            AddItem(
                "行动计划",
                nextPlan.Title,
                $"开始时间 {nextPlan.StartTime:MM-dd HH:mm}，{nextPlan.ParticipantCountText}",
                nextPlan.StartTime.ToString("MM-dd HH:mm"),
                _joinedActionPlanIds.Contains(nextPlan.Id) ? "已预约" : "查看",
                "plan-detail",
                Brushes.MediumSpringGreen);
        }
        else if (CanCurrentUserPublishPlans())
        {
            AddItem(
                "行动计划",
                "未来 7 天暂无计划",
                "前往管理舰队创建行动计划。",
                "",
                "创建",
                "plan-manage",
                Brushes.MediumSpringGreen);
        }

        if (!string.IsNullOrWhiteSpace(_fleetNoticeTitle))
        {
            AddItem(
                "舰队公告",
                _fleetNoticeTitle,
                string.IsNullOrWhiteSpace(_fleetNoticeContent) ? "点击查看公告。" : _fleetNoticeContent,
                "",
                CanCurrentUserManageFleetInfo() ? "编辑" : "查看",
                "notice-detail",
                Brushes.Cyan);
        }
        else if (CanCurrentUserManageFleetInfo())
        {
            AddItem(
                "舰队公告",
                "暂无舰队公告",
                "发布公告后会同步给舰队成员与 Overlay。",
                "",
                "发布",
                "notice-edit",
                Brushes.Cyan);
        }

        var canOpenManagement = CanCurrentUserOpenFleetManagement();
        foreach (var log in _allFleetEventLogs
                     .OrderByDescending(log => log.Timestamp)
                     .Take(2))
        {
            AddItem(
                $"日志 / {log.Type}",
                log.Title,
                log.Detail,
                log.Timestamp.ToLocalTime().ToString("MM-dd HH:mm"),
                canOpenManagement ? "查看" : "",
                canOpenManagement ? "logs" : "",
                Brushes.LightSkyBlue);
        }

        if (_fleetNotificationCenterItems.Count == 0)
        {
            AddItem(
                "舰队状态",
                "暂无新的舰队动态",
                "任务、公告、计划、申请和日志会在这里聚合。",
                "",
                "",
                "",
                Brushes.DeepSkyBlue);
        }

        var activeCount = _fleetNotificationCenterItems.Count;
        FleetNotificationCenterSummaryText.Text = activeCount == 0
            ? "暂无待处理事项。"
            : $"{activeCount} 条舰队动态，点击卡片可跳转处理。";
    }

    private int CountPendingFleetApplications()
    {
        return (_fleetApplicationSnapshots ?? [])
            .Count(application =>
                string.IsNullOrWhiteSpace(application.Status) ||
                application.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase));
    }

    private string BuildCurrentTaskNotificationDetail()
    {
        var detail = string.IsNullOrWhiteSpace(_fleetCurrentTaskBrief)
            ? _fleetCurrentTaskParticipants
            : _fleetCurrentTaskBrief;
        if (!string.IsNullOrWhiteSpace(_fleetCurrentTaskRally))
        {
            detail = string.IsNullOrWhiteSpace(detail)
                ? $"集结点：{_fleetCurrentTaskRally}"
                : $"{detail} / 集结点：{_fleetCurrentTaskRally}";
        }

        return string.IsNullOrWhiteSpace(detail) ? "点击查看任务详情。" : detail;
    }

    private static string TruncateNotificationText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= maxLength
            ? normalized
            : normalized[..Math.Max(0, maxLength - 1)] + "…";
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
        FleetActionPlanTimeText.Text = CanCurrentUserManageFleetInfo()
            ? "点击编辑公告"
            : "";
        JoinFleetActionButton.Visibility = Visibility.Collapsed;
    }

    private void RefreshCurrentTaskPanel()
    {
        var hasTask = !string.IsNullOrWhiteSpace(_fleetCurrentTaskTitle);
        if (!hasTask)
        {
            FleetActionPlanTitleText.Text = CanCurrentUserPublishTasks()
                ? "暂无当前任务"
                : "当前无任务";
            FleetActionPlanSummaryText.Text = CanCurrentUserPublishTasks()
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
        JoinFleetActionButton.Content = joined ? "取消预约" : "参与";
        JoinFleetActionButton.IsEnabled = true;
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

        RefreshFleetManagementPermissions();

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

        var canPublishTasks = CanCurrentUserPublishTasks();
        OpenPublishTaskButton.IsEnabled = canPublishTasks;
        EditCurrentTaskButton.IsEnabled = hasTask && canPublishTasks;
        CompleteCurrentTaskButton.IsEnabled = hasTask && canPublishTasks;
        DeleteCurrentTaskButton.IsEnabled = hasTask && canPublishTasks;
        RenotifyCurrentTaskButton.IsEnabled = hasTask && canPublishTasks;
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

    private bool IsLocalPlayerIdentity(string? gameName, string? callsign)
    {
        return (!string.IsNullOrWhiteSpace(gameName) &&
                !string.IsNullOrWhiteSpace(_localPlayer) &&
                gameName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(callsign) &&
                !string.IsNullOrWhiteSpace(_callsign) &&
                callsign.Equals(_callsign, StringComparison.OrdinalIgnoreCase));
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
        RepairLocalSquadLifecycle();

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

    private void RepairLocalSquadLifecycle()
    {
        var now = DateTimeOffset.UtcNow;
        for (var index = _squads.Count - 1; index >= 0; index--)
        {
            var squad = _squads[index];
            var members = _players
                .Where(player => player.SquadName.Equals(squad.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (members.Count == 0)
            {
                if (squad.UpdatedAt != default &&
                    now - squad.UpdatedAt <= TimeSpan.FromMinutes(2))
                {
                    continue;
                }

                var removedName = squad.Name;
                _squads.RemoveAt(index);
                _localSquadEditTimes.Remove(removedName.Trim());

                if (_joinedSquad is not null &&
                    _joinedSquad.Name.Equals(removedName, StringComparison.OrdinalIgnoreCase))
                {
                    _joinedSquad = null;
                }

                if (_selectedSquad is not null &&
                    _selectedSquad.Name.Equals(removedName, StringComparison.OrdinalIgnoreCase))
                {
                    _selectedSquad = _joinedSquad ?? _squads.FirstOrDefault();
                }

                continue;
            }

            if (members.Any(member => IsSquadCommander(squad, member)))
            {
                continue;
            }

            var successor = PickRecommendedSquadSuccessor(members);
            if (successor is null)
            {
                continue;
            }

            squad.Commander = FormatCommanderName(successor.Callsign, successor.Name);
            squad.UpdatedAt = now;
            squad.RefreshComputed();
        }

        if (SquadSelectionList is not null &&
            !ReferenceEquals(SquadSelectionList.SelectedItem, _selectedSquad))
        {
            SquadSelectionList.SelectedItem = _selectedSquad;
        }
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
            GetSquadNameBrush(squad, player),
            CanCurrentUserRemoveSquadMember(squad, player));
    }

    private bool CanCurrentUserManageSquad(SquadRow squad)
    {
        if (!_hasFleet)
        {
            return false;
        }

        if (IsCurrentUserFleetCommander())
        {
            return true;
        }

        var commanderGameName = GetGameNameFromDisplayName(squad.Commander);
        var commanderCallsign = GetCallsignFromDisplayName(squad.Commander);
        return (!string.IsNullOrWhiteSpace(_localPlayer) &&
                commanderGameName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(_callsign) &&
                commanderCallsign.Equals(_callsign, StringComparison.OrdinalIgnoreCase));
    }

    private bool CanCurrentUserRemoveSquadMember(SquadRow squad, PlayerRow player)
    {
        if (!CanCurrentUserManageSquad(squad))
        {
            return false;
        }

        if (!player.SquadName.Equals(squad.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsSquadCommander(squad, player))
        {
            return false;
        }

        return !IsLocalPlayerIdentity(player.Name, player.Callsign);
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
        foreach (var player in _players.Where(player =>
                     player.SquadName.Equals(squad.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _mySquadMembers.Add(CreateSquadStatusRow(squad, player));
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

