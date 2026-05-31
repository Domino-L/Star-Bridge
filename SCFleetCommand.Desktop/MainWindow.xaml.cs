using Microsoft.Win32;
using SCFleetCommand.Core.Events;
using SCFleetCommand.Core.LogWatching;
using SCFleetCommand.Core.Parsing;
using SCFleetCommand.Core.State;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    private const int OverlayHotkeyId = 0x5343;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly RegexLogEventParser _parser = new();
    private readonly FleetState _fleetState = new();
    private readonly ObservableCollection<PlayerRow> _players = [];
    private readonly ObservableCollection<SquadRow> _squads = [];
    private readonly ObservableCollection<SquadMemberStatusRow> _mySquadMembers = [];
    private readonly GridViewColumn PlayerNameColumn = new();
    private readonly GridViewColumn PlayerStatusColumn = new();
    private readonly GridViewColumn PlayerShipColumn = new();
    private readonly GridViewColumn PlayerLocationColumn = new();
    private readonly List<OverlayLayoutItem> _overlayLayout = [];
    private readonly DispatcherTimer _gameProcessTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private GameLogWatcher? _watcher;
    private string? _logPath;
    private string? _localPlayer;
    private string? _localPlayerId;
    private string? _avatarPath;
    private string? _fleetLogoPath;
    private string _fleetName = "No Fleet";
    private string _fleetCode = "N/A";
    private string _fleetChiefCommander = "Unassigned";
    private string _fleetDeputyCommander = "Unassigned";
    private string _fleetActionTitle = "";
    private string _fleetActionContent = "";
    private DateTime? _fleetActionStartTime;
    private bool _fleetActionNotifyMembers;
    private bool _joinActionNotifyMe;
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
    private HwndSource? _hotkeySource;
    private bool _hotkeyRegistered;
    private bool _hasFleet;
    private bool _isCreatingFleet;

    public MainWindow()
    {
        InitializeComponent();
        NavigateToMyFleet();
        PlayersList.ItemsSource = _players;
        SquadsList.ItemsSource = _squads;
        MySquadMembersList.ItemsSource = _mySquadMembers;

        _isLoadingSettings = true;
        var config = DesktopAppConfig.Load();
        _logPath = config.LogPath;
        _localPlayer = config.PlayerName;
        _localPlayerId = config.PlayerId;
        _avatarPath = config.AvatarPath;
        _callsign = config.Callsign;
        _language = NormalizeLanguage(config.Language);
        _overlaySettings = OverlayDisplaySettings.Parse(DesktopAppConfig.LoadOverlaySettings() ?? config.OverlaySettings);
        LoadOverlayLayout(DesktopAppConfig.LoadOverlayLayout() ?? config.OverlayLayout);
        ApplyOverlaySettingsToControls();
        ApplyLanguageToControls();
        OverlayHotkeyBox.Text = string.IsNullOrWhiteSpace(config.OverlayHotkey)
            ? "Ctrl+Shift+O"
            : config.OverlayHotkey;
        CallsignBox.Text = _callsign ?? "";
        RenderCachedIdentity();
        LoadAvatarPreview();
        _isLoadingSettings = false;

        SeedSquads();
        UpdateFleetEntryPanels();
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
        LocalFleetText.Text = $"{CreateFleetNameBox.Text.Trim()} [{CreateFleetCodeBox.Text.Trim()}]";
        RefreshFleetHeader();
        UpdateFleetEntryPanels();
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
            AppendOutput($"{fleetEvent.Type} | {fleetEvent.Player} | {fleetEvent.Ship ?? ""} | {fleetEvent.Location ?? ""}");
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
            var primarySquad = _squads.FirstOrDefault();
            _players.Add(new PlayerRow(
                player.Name,
                online ? "Online" : "Offline",
                player.Ship,
                player.Location,
                IsLocalPlayer(player.Name) ? _callsign : null,
                IsLocalPlayer(player.Name) ? _avatarPath : null,
                GetInitials(player.Name),
                primarySquad?.Name ?? "Unassigned",
                "Member"));
        }

        TotalMembersText.Text = _players.Count.ToString();
        OnlineMembersText.Text = _players.Count(player => player.Status == "Online").ToString();
        FleetShipCountText.Text = _players
            .Select(player => player.Ship)
            .Where(ship => !string.IsNullOrWhiteSpace(ship) &&
                           !ship.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count()
            .ToString();
        RefreshFleetHeader();
        RenderSquads();
        RenderMySquad();
        RefreshOverlayWindow();

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
        var overlaySettings = _overlaySettings.Serialize();
        var overlayLayout = SerializeOverlayLayout();
        DesktopAppConfig.Save(new DesktopAppConfig(
            _logPath,
            _localPlayer,
            _localPlayerId,
            _avatarPath,
            OverlayHotkeyBox.Text,
            overlayLayout,
            _callsign,
            overlaySettings,
            _language));
        DesktopAppConfig.SaveOverlaySettings(overlaySettings);
        DesktopAppConfig.SaveOverlayLayout(overlayLayout);
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentConfig();
        CloseOverlayWindow();
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

        _overlayWindow = new OverlayWindow(_squads, _players, _overlayLayout, _overlaySettings, _language, _hasFleet);
        _overlayWindow.Owner = this;
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
        _overlayWindow.Refresh(_squads, _players, _overlaySettings, _language, _hasFleet);
    }

    private void SaveOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentConfig();
        RefreshOverlayWindow();
        AppendOutput("Overlay layout saved.");
    }

    private void ResetOverlayLayout_Click(object sender, RoutedEventArgs e)
    {
        _overlayLayout.Clear();
        _overlayLayout.AddRange(CreateDefaultOverlayLayout());
        RenderOverlayEditor();
        SaveCurrentConfig();
        RefreshOverlayWindow();
        AppendOutput("Overlay layout reset.");
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
            ShowMembersPanelCheck is null)
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
            ShowMembersPanelCheck.IsChecked == true);

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

    private void MySquadDescriptionBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoadingSettings || MySquadDescriptionBox is null)
        {
            return;
        }

        var squad = _squads.FirstOrDefault();
        if (squad is null)
        {
            return;
        }

        squad.Description = MySquadDescriptionBox.Text.Trim();
        squad.RefreshComputed();
        RefreshOverlayWindow();
    }

    private void FleetActionPlanCard_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        OpenActionPlanEditor();
    }

    private void OpenActionPlanEditor()
    {
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

        _fleetActionTitle = title;
        _fleetActionContent = content;
        _fleetActionStartTime = startTime;
        _fleetActionNotifyMembers = ActionPlanNotifyFleetCheck.IsChecked == true;
        ActionPlanEditorPanel.Visibility = Visibility.Collapsed;
        RefreshActionPlanCard();
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
        _joinActionNotifyMe = JoinActionNotifyCheck.IsChecked == true;
        JoinActionPlanPanel.Visibility = Visibility.Collapsed;
        AppendOutput(_joinActionNotifyMe
            ? "Action joined. Email reminder requested for 5 minutes before start."
            : "Action joined.");
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

    private void ChooseFleetLogo_Click(object sender, RoutedEventArgs e)
    {
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

    private void ChooseSquadEmblem_Click(object sender, RoutedEventArgs e)
    {
        var squad = (sender as FrameworkElement)?.Tag as SquadRow ?? _squads.FirstOrDefault();
        ChooseSquadEmblem(squad);
    }

    private void MySquadEmblem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        ChooseSquadEmblem(_squads.FirstOrDefault());
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
            "SCFleetCommand",
            "Images");
        Directory.CreateDirectory(directory);

        var outputPath = Path.Combine(directory, fileName);
        using var stream = File.Create(outputPath);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(cropWindow.CroppedImage));
        encoder.Save(stream);
        return outputPath;
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
        ShowNoticePanelCheck.IsChecked = _overlaySettings.ShowNotice;
        ShowSquadsPanelCheck.IsChecked = _overlaySettings.ShowSquads;
        ShowMissionPanelCheck.IsChecked = _overlaySettings.ShowMission;
        ShowMembersPanelCheck.IsChecked = _overlaySettings.ShowMembers;
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
            ? "舰队发现原型区域。之后这里可以显示公开舰队、搜索、筛选和申请加入。"
            : "Fleet discovery prototype area. Later this can show public fleets, search, filters, and join requests.";
        SelectLogButton.Content = zh ? "选择日志" : "Select Log";
        ToggleOverlayButton.Content = zh ? "切换 Overlay" : "Toggle Overlay";
        FleetTab.Header = zh ? "舰队" : "Fleet";
        MySquadTab.Header = zh ? "我的小队" : "My Squad";
        AllPlayersTab.Header = zh ? "全成员" : "All Players";
        SquadsTab.Header = zh ? "小队" : "Squads";
        OverlayEditTab.Header = zh ? "Overlay 编辑" : "Overlay Edit";
        PersonalTab.Header = zh ? "个人" : "Personal";
        MonitorTab.Header = zh ? "监控" : "Monitor";
        TotalMembersLabel.Text = zh ? "总人数" : "TOTAL MEMBERS";
        OnlineLabel.Text = zh ? "在线" : "ONLINE";
        FleetShipsLabel.Text = zh ? "舰船数量" : "SHIPS";
        FleetActionPlanLabel.Text = zh ? "行动计划" : "ACTION PLAN";
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
            ? $"活动时间 / {CreateFleetOnlineFromBox.Text.Trim()} - {CreateFleetOnlineToBox.Text.Trim()} UTC+8"
            : "活动时间 / 未设置";
        RefreshActionPlanCard();

        LoadFleetHeaderLogoPreview();
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

    private void RefreshActionPlanCard()
    {
        if (FleetActionPlanTitleText is null)
        {
            return;
        }

        var hasAction = !string.IsNullOrWhiteSpace(_fleetActionTitle);
        if (!hasAction)
        {
            FleetActionPlanTitleText.Text = "暂无行动计划，等待下一步指挥";
            FleetActionPlanSummaryText.Text = "";
            FleetActionPlanTimeText.Text = "";
            JoinFleetActionButton.Visibility = Visibility.Collapsed;
            return;
        }

        FleetActionPlanTitleText.Text = _fleetActionTitle;
        FleetActionPlanSummaryText.Text = _fleetActionContent;
        FleetActionPlanTimeText.Text = _fleetActionStartTime is null
            ? ""
            : $"开始时间 / {_fleetActionStartTime:yyyy-MM-dd HH:mm}";
        JoinFleetActionButton.Visibility = Visibility.Visible;
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

    private void RenderMySquad()
    {
        var squad = _squads.FirstOrDefault();
        if (squad is null)
        {
            MySquadNameText.Text = "No Squad";
            MySquadCommanderText.Text = "Commander / Unassigned";
            if (!MySquadDescriptionBox.IsKeyboardFocusWithin)
            {
                MySquadDescriptionBox.Text = "Create or join a squad to see squad details here.";
            }
            MySquadIconText.Text = "?";
            MySquadIconText.Visibility = Visibility.Visible;
            MySquadEmblemImage.Source = null;
            MySquadEmblemHintText.Visibility = Visibility.Visible;
            _mySquadMembers.Clear();
            return;
        }

        MySquadNameText.Text = squad.Name;
        MySquadCommanderText.Text = $"Commander / {squad.Commander}";
        if (!MySquadDescriptionBox.IsKeyboardFocusWithin)
        {
            MySquadDescriptionBox.Text = squad.Description;
        }
        MySquadIconText.Text = squad.Icon;
        LoadMySquadEmblem(squad.EmblemPath);

        _mySquadMembers.Clear();
        foreach (var player in _players)
        {
            _mySquadMembers.Add(new SquadMemberStatusRow(
                GetInitials(player.Name),
                player.AvatarPath,
                player.Role,
                player.Callsign ?? "-",
                player.Name,
                player.Status,
                player.Ship,
                player.Location));
        }
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
        yield return new OverlayLayoutItem("Notice", "FLEET NOTICE", 0.305, 0.01, 0.39, 0.07, Brushes.Yellow);
        yield return new OverlayLayoutItem("Squads", "FLEET / SQUADS", 0.01, 0.32, 0.16, 0.36, Brushes.DeepSkyBlue);
        yield return new OverlayLayoutItem("Mission", "MISSION PACKAGE", 0.835, 0.26, 0.16, 0.14, Brushes.Red);
        yield return new OverlayLayoutItem("Members", "SQUAD MEMBERS", 0.82, 0.56, 0.18, 0.22, Brushes.Gray);
    }
}

public sealed record PlayerRow(
    string Name,
    string Status,
    string Ship,
    string Location,
    string? Callsign = null,
    string? AvatarPath = null,
    string Initials = "?",
    string SquadName = "Unassigned",
    string Role = "Member");

public sealed record SquadMemberStatusRow(
    string Avatar,
    string? AvatarPath,
    string Role,
    string Callsign,
    string GameId,
    string OnlineStatus,
    string ShipStatus,
    string Location);

