using Microsoft.Win32;
using SCFleetCommand.Core.Events;
using SCFleetCommand.Core.LogWatching;
using SCFleetCommand.Core.Parsing;
using SCFleetCommand.Core.State;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

namespace SCFleetCommand.Desktop;

public partial class MainWindow : Window
{
    private readonly RegexLogEventParser _parser = new();
    private readonly FleetState _fleetState = new();
    private readonly ObservableCollection<PlayerRow> _players = [];
    private readonly ObservableCollection<SquadRow> _squads = [];
    private readonly List<OverlayLayoutItem> _overlayLayout = [];
    private readonly DispatcherTimer _gameProcessTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private GameLogWatcher? _watcher;
    private string? _logPath;
    private string? _localPlayer;
    private string? _localPlayerId;
    private string? _avatarPath;
    private string? _callsign;
    private OverlayWindow? _overlayWindow;
    private OverlayLayoutItem? _activeOverlayItem;
    private FrameworkElement? _activeOverlayEditorElement;
    private bool _isOverlayResize;
    private System.Windows.Point _lastOverlayEditorPoint;
    private OverlayDisplaySettings _overlaySettings = OverlayDisplaySettings.Default;
    private string _language = "en";
    private bool _isGameProcessRunning;
    private bool _isLoadingSettings;

    public MainWindow()
    {
        InitializeComponent();
        NavigateToMyFleet();
        PlayersList.ItemsSource = _players;
        SquadsList.ItemsSource = _squads;

        _isLoadingSettings = true;
        var config = DesktopAppConfig.Load();
        _logPath = config.LogPath;
        _localPlayer = config.PlayerName;
        _localPlayerId = config.PlayerId;
        _avatarPath = config.AvatarPath;
        _callsign = config.Callsign;
        _language = NormalizeLanguage(config.Language);
        _overlaySettings = OverlayDisplaySettings.Parse(config.OverlaySettings);
        LoadOverlayLayout(config.OverlayLayout);
        ApplyOverlaySettingsToControls();
        ApplyLanguageToControls();
        CallsignBox.Text = _callsign ?? "";
        RenderCachedIdentity();
        LoadAvatarPreview();
        _isLoadingSettings = false;

        SeedSquads();
        Loaded += (_, _) =>
        {
            RenderOverlayEditor();
            if (!string.IsNullOrWhiteSpace(_logPath) && File.Exists(_logPath))
            {
                StartWatching(_logPath);
            }
        };
        _gameProcessTimer.Tick += (_, _) => UpdateLocalOnlineStateFromGameProcess();
        _gameProcessTimer.Start();
        AppendOutput("Designer WPF shell ready. Select Game.log to start.");
    }

    private void FindFleetNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = FindFleetTab;
        SetActiveNav(FindFleetNavButton);
    }

    private void MyFleetNav_Click(object sender, RoutedEventArgs e)
    {
        NavigateToMyFleet();
    }

    private void MySquadNav_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = FleetTab;
        FleetSubTabs.SelectedItem = SquadsTab;
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

    private void NavigateToMyFleet()
    {
        MainTabs.SelectedItem = FleetTab;
        FleetSubTabs.SelectedItem = AllPlayersTab;
        SetActiveNav(MyFleetNavButton);
    }

    private void SetActiveNav(System.Windows.Controls.Button activeButton)
    {
        FindFleetNavButton.Tag = null;
        MyFleetNavButton.Tag = null;
        MySquadNavButton.Tag = null;
        OverlayNavButton.Tag = null;
        PersonalNavButton.Tag = null;
        activeButton.Tag = "Active";
    }

    protected override void OnClosed(EventArgs e)
    {
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

        foreach (var line in File.ReadLines(logPath))
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
            GameNameText.Text = _localPlayer;
            PlayerIdText.Text = string.IsNullOrWhiteSpace(_localPlayerId)
                ? "Unknown"
                : _localPlayerId;
            ProfileStatusText.Text = "Online";
            SaveCurrentConfig();
        }

        _fleetState.Apply(fleetEvent);
        RenderState();

        if (output)
        {
            AppendOutput($"{fleetEvent.Type} | {fleetEvent.Player} | {fleetEvent.Ship ?? ""}");
        }
    }

    private void RenderState()
    {
        _isGameProcessRunning = IsStarCitizenRunning();
        _players.Clear();

        foreach (var player in _fleetState.Players)
        {
            var isLocalPlayer = !string.IsNullOrWhiteSpace(_localPlayer) &&
                                player.Name.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase);
            var online = player.Online && (!isLocalPlayer || _isGameProcessRunning);
            _players.Add(new PlayerRow(
                player.Name,
                online ? "Online" : "Offline",
                player.Ship,
                player.Location,
                IsLocalPlayer(player.Name) ? _callsign : null));
        }

        TotalMembersText.Text = _players.Count.ToString();
        OnlineMembersText.Text = _players.Count(player => player.Status == "Online").ToString();
        SquadCountText.Text = _squads.Count.ToString();
        RenderSquads();

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

    private void AppendOutput(string message)
    {
        OutputBox.AppendText(message + Environment.NewLine);
        OutputBox.ScrollToEnd();
    }

    private void PlayersList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {

    }

    private void RenderCachedIdentity()
    {
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
        DesktopAppConfig.Save(new DesktopAppConfig(
            _logPath,
            _localPlayer,
            _localPlayerId,
            _avatarPath,
            OverlayHotkeyBox.Text,
            SerializeOverlayLayout(),
            _callsign,
            _overlaySettings.Serialize(),
            _language));
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is ComboBoxItem { Tag: string language })
        {
            _language = NormalizeLanguage(language);
            ApplyLanguageToControls();
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

        OverlayHotkeyBox.Text = key.ToString();
        if (!_isLoadingSettings)
        {
            SaveCurrentConfig();
        }
    }

    private void OpenOverlay_Click(object sender, RoutedEventArgs e)
    {
        RenderSquads();
        if (_overlayWindow is { IsVisible: true })
        {
            _overlayWindow.Close();
            return;
        }

        _overlayWindow = new OverlayWindow(_squads, _players, _overlayLayout, _overlaySettings, _language);
        _overlayWindow.Closed += (_, _) => _overlayWindow = null;
        _overlayWindow.Show();
    }

    private void SaveOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();
        AppendOutput("Overlay layout saved.");
    }

    private void ResetOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        _overlayLayout.Clear();
        _overlayLayout.AddRange(CreateDefaultOverlayLayout());
        RenderOverlayEditor();
        SaveCurrentConfig();
        AppendOutput("Overlay layout reset.");
    }

    private void OverlayEditorCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderOverlayEditor();
    }

    private void OverlaySetting_Changed(object sender, RoutedEventArgs e)
    {
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
            TrayModeCheck.IsChecked == true);

        if (!_isLoadingSettings)
        {
            SaveCurrentConfig();
        }
    }

    private void CallsignBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings)
        {
            return;
        }

        _callsign = string.IsNullOrWhiteSpace(CallsignBox.Text)
            ? null
            : CallsignBox.Text.Trim();
        SaveCurrentConfig();
        RenderState();
    }

    private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
    {
        AppendOutput("Avatar selection is not available in this shell.");
    }

    private void ChooseSquadEmblem_Click(object sender, RoutedEventArgs e)
    {
        AppendOutput("Squad emblem selection is not available in this shell.");
    }

    private void RenderOverlayEditor()
    {
        if (OverlayEditorCanvas is null)
        {
            return;
        }

        OverlayEditorCanvas.Children.Clear();

        foreach (var item in _overlayLayout)
        {
            var panel = CreateOverlayEditorPanel(item);
            Canvas.SetLeft(panel, item.X * OverlayEditorCanvas.Width);
            Canvas.SetTop(panel, item.Y * OverlayEditorCanvas.Height);
            panel.Width = item.Width * OverlayEditorCanvas.Width;
            panel.Height = item.Height * OverlayEditorCanvas.Height;
            OverlayEditorCanvas.Children.Add(panel);
        }
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

    private void LoadOverlayLayout(string? serialized)
    {
        _overlayLayout.Clear();
        var parsed = OverlayLayoutItem.ParseMany(serialized).ToArray();
        _overlayLayout.AddRange(parsed.Length == 0 ? CreateDefaultOverlayLayout() : parsed);
    }

    private string SerializeOverlayLayout()
    {
        return string.Join(";", _overlayLayout.Select(item => item.Serialize()));
    }

    private void ApplyOverlaySettingsToControls()
    {
        HideMissionWhenIdleCheck.IsChecked = _overlaySettings.HideMissionWhenIdle;
        HideOfflineMembersCheck.IsChecked = _overlaySettings.HideOfflineMembers;
        HideSquadIconsCheck.IsChecked = _overlaySettings.HideSquadIcons;
        TrayModeCheck.IsChecked = _overlaySettings.EnableTrayMode;
        ShowCallsignOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignOnly;
        ShowGameNameOnlyRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.GameNameOnly;
        ShowCallsignAndNameRadio.IsChecked = _overlaySettings.MemberNameMode == OverlayMemberNameMode.CallsignAndGameName;
    }

    private void ApplyLanguageToControls()
    {
        var zh = _language == "zh";
        Title = zh ? "SC 舰队指挥设计器" : "SC Fleet Command Designer";

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
            ? "舰队发现原型区域。之后这里可以显示公开舰队、搜索、筛选和申请加入。"
            : "Fleet discovery prototype area. Later this can show public fleets, search, filters, and join requests.";
        SelectLogButton.Content = zh ? "选择日志" : "Select Log";
        ToggleOverlayButton.Content = zh ? "切换 Overlay" : "Toggle Overlay";
        FleetTab.Header = zh ? "舰队" : "Fleet";
        AllPlayersTab.Header = zh ? "全成员" : "All Players";
        SquadsTab.Header = zh ? "小队" : "Squads";
        OverlayEditTab.Header = zh ? "Overlay 编辑" : "Overlay Edit";
        PersonalTab.Header = zh ? "个人" : "Personal";
        MonitorTab.Header = zh ? "监控" : "Monitor";
        TotalMembersLabel.Text = zh ? "总人数" : "TOTAL MEMBERS";
        OnlineLabel.Text = zh ? "在线" : "ONLINE";
        ActiveTasksLabel.Text = zh ? "执行中任务" : "ACTIVE TASKS";
        SquadsMetricLabel.Text = zh ? "小队" : "SQUADS";
        PlayerNameColumn.Header = zh ? "游戏名" : "Name";
        PlayerStatusColumn.Header = zh ? "状态" : "Status";
        PlayerShipColumn.Header = zh ? "飞船" : "Ship";
        PlayerLocationColumn.Header = zh ? "位置" : "Location";
        OverlayEditHintText.Text = zh
            ? "拖拽面板移动位置，拖拽右下角手柄缩放。保存后的布局会应用到全屏 Overlay。"
            : "Drag panels to move. Drag the lower-right handle to resize. Saved layout is applied to fullscreen Overlay.";
        SaveLayoutButton.Content = zh ? "保存布局" : "Save Layout";
        ResetLayoutButton.Content = zh ? "重置" : "Reset";
        OverlayOptionsLabel.Text = zh ? "OVERLAY 选项" : "OVERLAY OPTIONS";
        HideMissionWhenIdleCheck.Content = zh ? "无任务时隐藏右侧任务 Overlay" : "No task: hide right mission overlay";
        HideOfflineMembersCheck.Content = zh ? "隐藏离线小队成员" : "Hide offline squad members";
        HideSquadIconsCheck.Content = zh ? "左侧不显示小队图标" : "Left panel: hide squad icons";
        MemberNameDisplayLabel.Text = zh ? "成员名字显示" : "MEMBER NAME DISPLAY";
        ShowCallsignAndNameRadio.Content = zh ? "显示呼号和游戏名" : "Show callsign and game name";
        ShowCallsignOnlyRadio.Content = zh ? "只显示呼号" : "Only callsign";
        ShowGameNameOnlyRadio.Content = zh ? "只显示游戏名" : "Only game name";
        BackgroundModeLabel.Text = zh ? "后台模式" : "BACKGROUND";
        TrayModeCheck.Content = zh ? "托盘 / 后台模式" : "Tray / background mode";
        TrayModeHintText.Text = zh
            ? "启用后，关闭或最小化窗口会隐藏到托盘，但热键仍然保持可用。"
            : "When enabled, closing or minimizing hides this window but keeps the hotkey alive.";
        AvatarPlaceholderText.Text = zh ? "头像" : "AVATAR";
        ChooseAvatarButton.Content = zh ? "选择头像" : "Choose Avatar";
        PlayerNameLabel.Text = zh ? "游戏名" : "Player Name";
        PlayerIdLabel.Text = zh ? "玩家 ID" : "Player ID";
        CallsignLabel.Text = zh ? "呼号" : "Callsign";
        FleetLabel.Text = zh ? "舰队" : "Fleet";
        LocalFleetText.Text = zh ? "本地舰队" : "Local Fleet";
        StatusLabel.Text = zh ? "状态" : "Status";
        RenderOverlayEditor();
    }

    private static string NormalizeLanguage(string? language)
    {
        return string.Equals(language, "zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en";
    }

    private void LoadAvatarPreview()
    {
        if (string.IsNullOrWhiteSpace(_avatarPath) || !File.Exists(_avatarPath))
        {
            AvatarImage.Source = null;
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

    private bool IsLocalPlayer(string playerName)
    {
        return !string.IsNullOrWhiteSpace(_localPlayer) &&
               playerName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase);
    }

    private void SeedSquads()
    {
        if (_squads.Count > 0)
        {
            return;
        }

        _squads.Add(new SquadRow { Name = "Alpha", Icon = "A", Commander = "Unassigned" });
    }

    private void RenderSquads()
    {
        foreach (var squad in _squads)
        {
            squad.Members.Clear();
            foreach (var player in _players)
            {
                squad.Members.Add(new MemberAvatarRow(player.Name, GetInitials(player.Name), player.Status));
            }

            squad.RefreshComputed();
        }
    }

    private static string GetInitials(string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? "?"
            : name.Length >= 2 ? name[..2].ToUpperInvariant() : name[..1].ToUpperInvariant();
    }

    private static IEnumerable<OverlayLayoutItem> CreateDefaultOverlayLayout()
    {
        yield return new OverlayLayoutItem("Notice", "FLEET NOTICE", 0.305, 0.01, 0.39, 0.07, Brushes.Yellow);
        yield return new OverlayLayoutItem("Squads", "FLEET / SQUADS", 0.01, 0.32, 0.16, 0.36, Brushes.DeepSkyBlue);
        yield return new OverlayLayoutItem("Mission", "MISSION PACKAGE", 0.835, 0.26, 0.16, 0.14, Brushes.Red);
        yield return new OverlayLayoutItem("Members", "SQUAD MEMBERS", 0.82, 0.56, 0.18, 0.22, Brushes.Gray);
    }
}

public sealed record PlayerRow(string Name, string Status, string Ship, string Location, string? Callsign = null);
