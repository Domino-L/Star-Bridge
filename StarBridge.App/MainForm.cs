using StarBridge.Core.Events;
using StarBridge.Core.LogWatching;
using StarBridge.Core.Parsing;
using StarBridge.Core.State;
using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace StarBridge.App;

public sealed class MainForm : Form
{
    private static readonly TimeSpan ControlSignalFreshness = TimeSpan.FromSeconds(30);
    private static readonly Color WindowBackColor = Theme.WindowBackColor;
    private static readonly Color PanelBackColor = Theme.PanelBackColor;
    private static readonly Color FieldBackColor = Theme.FieldBackColor;
    private static readonly Color PrimaryTextColor = Theme.PrimaryTextColor;
    private static readonly Color MutedTextColor = Theme.MutedTextColor;
    private static readonly Color AccentColor = Theme.AccentColor;
    private static readonly Color AccentDimColor = Theme.AccentDimColor;
    private static readonly Color BorderColor = Theme.BorderColor;
    private static readonly Color CyanGlowColor = Theme.CyanGlowColor;

    private readonly RegexLogEventParser _parser = new();
    private readonly TextBox _pathTextBox = new();
    private readonly Button _selectButton = new();
    private readonly TextBox _outputTextBox = new();
    private readonly ListBox _playerList = new();
    private FleetState _fleetState = new();
    private readonly LocalShipState _shipState = new();
    private GameLogWatcher? _watcher;
    private string? _localPlayer;
    private readonly System.Windows.Forms.Timer _processTimer = new();
    private readonly Label _shipLabel = new();
    private readonly TextBox _reportedShipTextBox = new();
    private readonly ComboBox _reportedStationComboBox = new();
    private readonly Button _submitReportButton = new();
    private readonly Label _reportLabel = new();
    private readonly List<FleetSquad> _squads = new();
    private readonly FlowLayoutPanel _squadFlowPanel = new();
    private readonly FlowLayoutPanel _fleetPlayersFlowPanel = new();
    private readonly Label _totalMembersValueLabel = new();
    private readonly Label _onlineMembersValueLabel = new();
    private readonly Label _activeTasksValueLabel = new();
    private readonly Label _squadCountValueLabel = new();
    private readonly TextBox _globalMissionTextBox = new();
    private readonly TextBox _rallyPointTextBox = new();
    private readonly Label _publishedMissionLabel = new();
    private readonly Label _publishedRallyPointLabel = new();
    private readonly PictureBox _avatarPictureBox = new();
    private readonly Label _profileGameNameLabel = new();
    private readonly Label _profilePlayerIdLabel = new();
    private readonly Label _profileStatusLabel = new();
    private readonly Label _profileFleetLabel = new();
    private readonly Label _profileSquadLabel = new();
    private readonly TextBox _displayNameTextBox = new();
    private readonly Button _saveProfileButton = new();
    private readonly Button _chooseAvatarButton = new();
    private string _globalMission = "Standby";
    private string _rallyPoint = "Unassigned";
    private string? _displayName;
    private string? _avatarPath;
    private string? _localPlayerId;




    public MainForm()
    {
        Text = "SC Fleet Command";
        MinimumSize = new Size(1080, 680);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10F);
        BackColor = WindowBackColor;
        ForeColor = PrimaryTextColor;

        BuildLayout();

        var config = AppConfig.Load();
        _localPlayer = config.LastPlayerName;
        _localPlayerId = config.LastPlayerId;
        _displayName = config.DisplayName;
        _avatarPath = config.AvatarPath;
        _displayNameTextBox.Text = _displayName ?? "";
        LoadAvatarPreview();
        if (!string.IsNullOrWhiteSpace(_localPlayer))
        {
            _fleetState.Apply(new FleetEvent(FleetEventType.PlayerOffline, _localPlayer));
            SyncLocalPlayerToDefaultSquad();
            RenderFleetManagement();
        }

        RenderProfile();

        if (!string.IsNullOrWhiteSpace(config.LogPath) && File.Exists(config.LogPath))
        {
            _pathTextBox.Text = config.LogPath;
            StartWatching(config.LogPath);
        }
        else
        {
            AppendOutput("Select Star Citizen Game.log to start realtime output.");
            if (!string.IsNullOrWhiteSpace(_localPlayer))
            {
                AppendOutput($"Cached player identity: {_localPlayer} / ID {_localPlayerId ?? "Unknown"}");
            }
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _watcher?.Dispose();
        base.OnFormClosed(e);
    }

    private void BuildLayout()
    {
        _processTimer.Interval = 5000;

        _processTimer.Tick += (_, _) => CheckGameProcess();

        _processTimer.Start();

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(16),
            BackColor = WindowBackColor
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var topBar = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = WindowBackColor
        };
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 168));
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));

        var appTitle = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Fleet Command",
            Font = new Font("Segoe UI Semibold", 13F),
            ForeColor = PrimaryTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        };

        _pathTextBox.Dock = DockStyle.Fill;
        _pathTextBox.ReadOnly = true;
        _pathTextBox.PlaceholderText = "No Game.log selected";
        _pathTextBox.BackColor = FieldBackColor;
        _pathTextBox.ForeColor = PrimaryTextColor;
        _pathTextBox.BorderStyle = BorderStyle.FixedSingle;

        _selectButton.Dock = DockStyle.Fill;
        _selectButton.Text = "Select Log";
        StylePrimaryButton(_selectButton);
        _selectButton.Click += (_, _) => SelectLog();

        _outputTextBox.Dock = DockStyle.Fill;
        _outputTextBox.Multiline = true;
        _outputTextBox.ReadOnly = true;
        _outputTextBox.ScrollBars = ScrollBars.Vertical;
        _outputTextBox.Font = new Font("Cascadia Mono", 9.5F);
        _outputTextBox.BackColor = FieldBackColor;
        _outputTextBox.ForeColor = PrimaryTextColor;
        _outputTextBox.BorderStyle = BorderStyle.FixedSingle;



        topBar.Controls.Add(appTitle, 0, 0);
        topBar.Controls.Add(_pathTextBox, 1, 0);
        topBar.Controls.Add(_selectButton, 2, 0);
        root.Controls.Add(topBar, 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 320,
            BackColor = WindowBackColor
        };

        _playerList.Dock = DockStyle.Fill;
        _playerList.Font = new Font("Segoe UI", 10F);
        _playerList.BackColor = FieldBackColor;
        _playerList.ForeColor = PrimaryTextColor;
        _playerList.BorderStyle = BorderStyle.None;

        _shipLabel.Dock = DockStyle.Fill;
        _shipLabel.Padding = new Padding(10);
        _shipLabel.BackColor = FieldBackColor;
        _shipLabel.ForeColor = PrimaryTextColor;
        _shipLabel.Text =
            "Current Ship: Unknown" +
            Environment.NewLine +
            "Driving Ship: None" +
            Environment.NewLine +
            "Control Signal: None" +
            Environment.NewLine +
            "Station Type: Unknown" +
            Environment.NewLine +
            "Last Driven: Unknown" +
            Environment.NewLine +
            "In Ship: No";

        var leftPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = WindowBackColor
        };
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));
        leftPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 178));

        var reportPanel = BuildReportPanel();

        leftPanel.Controls.Add(CreateSection("Fleet Members", _playerList), 0, 0);
        leftPanel.Controls.Add(CreateSection("Manual Ship Report", reportPanel), 0, 1);
        leftPanel.Controls.Add(CreateSection("Local Ship Status", _shipLabel), 0, 2);

        split.Panel1.Controls.Add(leftPanel);
        split.Panel2.Controls.Add(CreateSection("Realtime Events", _outputTextBox));


        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(148, 34),
            SizeMode = TabSizeMode.Fixed
        };
        tabs.DrawItem += DrawMainTab;

        var fleetPage = new TabPage("Fleet Management")
        {
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Padding = new Padding(0, 8, 0, 0)
        };
        fleetPage.Controls.Add(BuildFleetManagementPage());

        var profilePage = new TabPage("Personal")
        {
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Padding = new Padding(0, 8, 0, 0)
        };
        profilePage.Controls.Add(BuildProfilePage());

        var localPage = new TabPage("Local Monitor")
        {
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Padding = new Padding(0, 8, 0, 0)
        };
        localPage.Controls.Add(split);

        tabs.Controls.Add(fleetPage);
        tabs.Controls.Add(profilePage);
        tabs.Controls.Add(localPage);

        root.Controls.Add(tabs, 0, 1);

        Controls.Add(root);
        SeedDefaultSquad();
        RenderFleetManagement();
    }

    private Control BuildReportPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(10),
            BackColor = FieldBackColor
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _reportedShipTextBox.Dock = DockStyle.Fill;
        _reportedShipTextBox.PlaceholderText = "Ship name";
        _reportedShipTextBox.BackColor = PanelBackColor;
        _reportedShipTextBox.ForeColor = PrimaryTextColor;
        _reportedShipTextBox.BorderStyle = BorderStyle.FixedSingle;

        _reportedStationComboBox.Dock = DockStyle.Fill;
        _reportedStationComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _reportedStationComboBox.BackColor = PanelBackColor;
        _reportedStationComboBox.ForeColor = PrimaryTextColor;
        _reportedStationComboBox.Items.AddRange(
        [
            "Pilot",
            "Turret",
            "Crew",
            "Passenger",
            "Unknown"
        ]);
        _reportedStationComboBox.SelectedItem = "Unknown";

        _submitReportButton.Dock = DockStyle.Fill;
        _submitReportButton.Text = "Submit Report";
        StylePrimaryButton(_submitReportButton);
        _submitReportButton.Click += (_, _) => SubmitManualReport();

        var useCurrentButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Use Current"
        };
        StyleSecondaryButton(useCurrentButton);
        useCurrentButton.Click += (_, _) =>
        {
            if (!IsUnknownShip(_shipState.CurrentShipName))
            {
                _reportedShipTextBox.Text = _shipState.CurrentShipName;
            }
        };

        _reportLabel.Dock = DockStyle.Fill;
        _reportLabel.ForeColor = MutedTextColor;
        _reportLabel.Text = "Verification: Not Reported";

        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = FieldBackColor
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.Controls.Add(_submitReportButton, 0, 0);
        buttonRow.Controls.Add(useCurrentButton, 1, 0);

        panel.Controls.Add(CreateFieldLabel("Ship"), 0, 0);
        panel.Controls.Add(_reportedShipTextBox, 1, 0);
        panel.Controls.Add(CreateFieldLabel("Station"), 0, 1);
        panel.Controls.Add(_reportedStationComboBox, 1, 1);
        panel.SetColumnSpan(buttonRow, 2);
        panel.Controls.Add(buttonRow, 0, 2);
        panel.SetColumnSpan(_reportLabel, 2);
        panel.Controls.Add(_reportLabel, 0, 3);

        return panel;
    }

    private Control BuildProfilePage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowBackColor
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 360));
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var identityPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = PanelBackColor,
            Padding = new Padding(18),
            Margin = new Padding(0, 0, 14, 0)
        };
        identityPanel.Paint += (_, e) => PaintHolographicPanel(e.Graphics, identityPanel.ClientRectangle, true);
        identityPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
        identityPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        identityPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        identityPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _avatarPictureBox.Dock = DockStyle.Fill;
        _avatarPictureBox.BackColor = FieldBackColor;
        _avatarPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        _avatarPictureBox.Margin = new Padding(32, 12, 32, 18);

        _chooseAvatarButton.Dock = DockStyle.Fill;
        _chooseAvatarButton.Text = "Choose Avatar";
        StyleSecondaryButton(_chooseAvatarButton);
        _chooseAvatarButton.Click += (_, _) => ChooseAvatar();

        _saveProfileButton.Dock = DockStyle.Fill;
        _saveProfileButton.Text = "Save Profile";
        StylePrimaryButton(_saveProfileButton);
        _saveProfileButton.Click += (_, _) => SaveProfile();

        var identityHint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Game name is read from Game.log only. Custom callsign is display-only.",
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.TopLeft
        };

        identityPanel.Controls.Add(_avatarPictureBox, 0, 0);
        identityPanel.Controls.Add(_chooseAvatarButton, 0, 1);
        identityPanel.Controls.Add(_saveProfileButton, 0, 2);
        identityPanel.Controls.Add(identityHint, 0, 3);

        var detailsPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            BackColor = PanelBackColor,
            Padding = new Padding(18)
        };
        detailsPanel.Paint += (_, e) => PaintHolographicPanel(e.Graphics, detailsPanel.ClientRectangle, true);
        detailsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        detailsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++)
        {
            detailsPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        }
        detailsPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _displayNameTextBox.Dock = DockStyle.Fill;
        StyleCommandTextBox(_displayNameTextBox);
        _displayNameTextBox.PlaceholderText = "Custom callsign";

        ConfigureProfileValueLabel(_profileGameNameLabel);
        ConfigureProfileValueLabel(_profilePlayerIdLabel);
        ConfigureProfileValueLabel(_profileStatusLabel);
        ConfigureProfileValueLabel(_profileFleetLabel);
        ConfigureProfileValueLabel(_profileSquadLabel);

        detailsPanel.Controls.Add(CreateFieldLabel("Game Name"), 0, 0);
        detailsPanel.Controls.Add(_profileGameNameLabel, 1, 0);
        detailsPanel.Controls.Add(CreateFieldLabel("Player ID"), 0, 1);
        detailsPanel.Controls.Add(_profilePlayerIdLabel, 1, 1);
        detailsPanel.Controls.Add(CreateFieldLabel("Callsign"), 0, 2);
        detailsPanel.Controls.Add(_displayNameTextBox, 1, 2);
        detailsPanel.Controls.Add(CreateFieldLabel("Fleet"), 0, 3);
        detailsPanel.Controls.Add(_profileFleetLabel, 1, 3);
        detailsPanel.Controls.Add(CreateFieldLabel("Squad"), 0, 4);
        detailsPanel.Controls.Add(_profileSquadLabel, 1, 4);
        detailsPanel.Controls.Add(CreateFieldLabel("Status"), 0, 5);
        detailsPanel.Controls.Add(_profileStatusLabel, 1, 5);

        var lockNotice = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Identity lock: start Star Citizen and keep Game.log selected until the player id is detected.",
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.TopLeft
        };
        detailsPanel.SetColumnSpan(lockNotice, 2);
        detailsPanel.Controls.Add(lockNotice, 0, 6);

        page.Controls.Add(identityPanel, 0, 0);
        page.Controls.Add(detailsPanel, 1, 0);

        return page;
    }

    private static void ConfigureProfileValueLabel(Label label)
    {
        label.Dock = DockStyle.Fill;
        label.ForeColor = PrimaryTextColor;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = new Font("Segoe UI Semibold", 10F);
        label.AutoEllipsis = true;
    }

    private Control BuildFleetManagementPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = WindowBackColor
        };
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var summary = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = WindowBackColor,
            Padding = new Padding(0, 0, 0, 10)
        };
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        summary.Controls.Add(CreateMetricCard("Total Members", _totalMembersValueLabel), 0, 0);
        summary.Controls.Add(CreateMetricCard("Online", _onlineMembersValueLabel), 1, 0);
        summary.Controls.Add(CreateMetricCard("Active Tasks", _activeTasksValueLabel), 2, 0);
        summary.Controls.Add(CreateMetricCard("Squads", _squadCountValueLabel), 3, 0);

        page.Controls.Add(summary, 0, 0);
        page.Controls.Add(BuildGlobalCommandPanel(), 0, 1);
        page.Controls.Add(BuildFleetBoardTabs(), 0, 2);

        return page;
    }

    private Control BuildFleetBoardTabs()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.FlatButtons,
            DrawMode = TabDrawMode.OwnerDrawFixed,
            ItemSize = new Size(152, 32),
            SizeMode = TabSizeMode.Fixed,
            Padding = new Point(12, 4)
        };
        tabs.DrawItem += DrawMainTab;

        var playersPage = new TabPage("All Players")
        {
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Padding = new Padding(0, 10, 0, 0)
        };
        playersPage.Controls.Add(BuildFleetPlayersPanel());

        var squadsPage = new TabPage("Squads")
        {
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Padding = new Padding(0, 10, 0, 0)
        };
        squadsPage.Controls.Add(BuildSquadBoardPanel());

        tabs.Controls.Add(playersPage);
        tabs.Controls.Add(squadsPage);

        return tabs;
    }

    private Control BuildSquadBoardPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = WindowBackColor
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var commandRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = WindowBackColor,
            Padding = new Padding(0, 0, 0, 10)
        };
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));
        commandRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 148));

        commandRow.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Squad Board",
            Font = new Font("Segoe UI Semibold", 12F),
            ForeColor = PrimaryTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var refreshButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Refresh"
        };
        StyleSecondaryButton(refreshButton);
        refreshButton.Click += (_, _) => RenderFleetManagement();

        var createSquadButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Create Squad"
        };
        StylePrimaryButton(createSquadButton);
        createSquadButton.Click += (_, _) => ShowCreateSquadDialog();

        commandRow.Controls.Add(refreshButton, 1, 0);
        commandRow.Controls.Add(createSquadButton, 2, 0);

        _squadFlowPanel.Dock = DockStyle.Fill;
        _squadFlowPanel.AutoScroll = true;
        _squadFlowPanel.FlowDirection = FlowDirection.TopDown;
        _squadFlowPanel.WrapContents = false;
        _squadFlowPanel.BackColor = WindowBackColor;
        _squadFlowPanel.Padding = new Padding(0, 0, 12, 0);
        _squadFlowPanel.Resize += (_, _) => RenderFleetManagement();

        panel.Controls.Add(commandRow, 0, 0);
        panel.Controls.Add(_squadFlowPanel, 0, 1);

        return panel;
    }

    private Control BuildFleetPlayersPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = PanelBackColor,
            Padding = new Padding(14, 10, 14, 12),
            Margin = new Padding(0, 0, 10, 10)
        };
        panel.Paint += (_, e) => PaintHolographicPanel(e.Graphics, panel.ClientRectangle, true);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "FLEET PLAYERS",
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI Semibold", 10F),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        _fleetPlayersFlowPanel.Dock = DockStyle.Fill;
        _fleetPlayersFlowPanel.AutoScroll = true;
        _fleetPlayersFlowPanel.FlowDirection = FlowDirection.LeftToRight;
        _fleetPlayersFlowPanel.WrapContents = false;
        _fleetPlayersFlowPanel.BackColor = Color.Transparent;
        panel.Controls.Add(_fleetPlayersFlowPanel, 0, 1);

        return panel;
    }

    private Control BuildGlobalCommandPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 2,
            BackColor = PanelBackColor,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 10, 10)
        };
        panel.Paint += (_, e) => PaintHolographicPanel(e.Graphics, panel.ClientRectangle, true);
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 132));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _globalMissionTextBox.Dock = DockStyle.Fill;
        _globalMissionTextBox.Text = _globalMission;
        StyleCommandTextBox(_globalMissionTextBox);

        _rallyPointTextBox.Dock = DockStyle.Fill;
        _rallyPointTextBox.Text = _rallyPoint;
        StyleCommandTextBox(_rallyPointTextBox);

        var publishButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Publish"
        };
        StylePrimaryButton(publishButton);
        publishButton.Click += (_, _) => PublishGlobalCommand();

        panel.Controls.Add(CreateFieldLabel("Global Task"), 0, 0);
        panel.Controls.Add(_globalMissionTextBox, 1, 0);
        panel.Controls.Add(CreateFieldLabel("Rally"), 2, 0);
        panel.Controls.Add(_rallyPointTextBox, 3, 0);
        panel.Controls.Add(publishButton, 4, 0);

        _publishedMissionLabel.Dock = DockStyle.Fill;
        _publishedMissionLabel.ForeColor = MutedTextColor;
        _publishedMissionLabel.Text = $"Current Task: {_globalMission}";
        _publishedRallyPointLabel.Dock = DockStyle.Fill;
        _publishedRallyPointLabel.ForeColor = MutedTextColor;
        _publishedRallyPointLabel.Text = $"Rally Point: {_rallyPoint}";

        panel.SetColumnSpan(_publishedMissionLabel, 2);
        panel.Controls.Add(_publishedMissionLabel, 0, 1);
        panel.SetColumnSpan(_publishedRallyPointLabel, 3);
        panel.Controls.Add(_publishedRallyPointLabel, 2, 1);

        return panel;
    }

    private static void StyleCommandTextBox(TextBox textBox)
    {
        textBox.BackColor = FieldBackColor;
        textBox.ForeColor = PrimaryTextColor;
        textBox.BorderStyle = BorderStyle.FixedSingle;
    }

    private static Control CreateMetricCard(string title, Label valueLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = PanelBackColor,
            Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 10, 0)
        };
        panel.Paint += (_, e) => PaintHolographicPanel(e.Graphics, panel.ClientRectangle, false);
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var titleLabel = new Label
        {
            Dock = DockStyle.Fill,
            Text = title,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        };

        valueLabel.Dock = DockStyle.Fill;
        valueLabel.Text = "0";
        valueLabel.Font = new Font("Segoe UI Semibold", 20F);
        valueLabel.ForeColor = PrimaryTextColor;
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;

        panel.Controls.Add(titleLabel, 0, 0);
        panel.Controls.Add(valueLabel, 0, 1);

        return panel;
    }

    private static Label CreateFieldLabel(string text)
    {
        return new Label
        {
            Dock = DockStyle.Fill,
            Text = text,
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.MiddleLeft
        };
    }

    private static GroupBox CreateSection(string title, Control content)
    {
        var group = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = title,
            ForeColor = MutedTextColor,
            BackColor = WindowBackColor,
            Padding = new Padding(10, 22, 10, 10),
            Margin = new Padding(0, 0, 10, 12)
        };

        content.Margin = Padding.Empty;
        group.Controls.Add(content);
        return group;
    }

    private static void StylePrimaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = AccentColor;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI Semibold", 9.5F);
    }

    private static void StyleSecondaryButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = BorderColor;
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = PanelBackColor;
        button.ForeColor = PrimaryTextColor;
        button.Font = new Font("Segoe UI Semibold", 9.5F);
    }

    private static void StyleDangerButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Color.FromArgb(151, 76, 82);
        button.FlatAppearance.BorderSize = 1;
        button.BackColor = Color.FromArgb(62, 27, 34);
        button.ForeColor = Color.FromArgb(255, 198, 203);
        button.Font = new Font("Segoe UI Semibold", 8.5F);
    }

    private void PublishGlobalCommand()
    {
        _globalMission = string.IsNullOrWhiteSpace(_globalMissionTextBox.Text)
            ? "Standby"
            : _globalMissionTextBox.Text.Trim();
        _rallyPoint = string.IsNullOrWhiteSpace(_rallyPointTextBox.Text)
            ? "Unassigned"
            : _rallyPointTextBox.Text.Trim();

        _publishedMissionLabel.Text = $"Current Task: {_globalMission}";
        _publishedRallyPointLabel.Text = $"Rally Point: {_rallyPoint}";

        AppendOutput($"COMMAND | global_task={_globalMission} | rally_point={_rallyPoint}");
        AppendOutput(string.Empty);
        RenderFleetManagement();
    }

    private void DrawMainTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs)
        {
            return;
        }

        var selected = e.Index == tabs.SelectedIndex;
        var bounds = e.Bounds;
        var backColor = selected ? PanelBackColor : WindowBackColor;
        var textColor = selected ? PrimaryTextColor : MutedTextColor;

        using var backBrush = new SolidBrush(backColor);
        using var textBrush = new SolidBrush(textColor);
        using var accentBrush = new SolidBrush(AccentColor);
        using var borderPen = new Pen(selected ? AccentColor : BorderColor);

        e.Graphics.FillRectangle(backBrush, bounds);
        e.Graphics.DrawRectangle(borderPen, bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);

        if (selected)
        {
            e.Graphics.FillRectangle(accentBrush, bounds.X, bounds.Bottom - 3, bounds.Width, 3);
        }

        var text = tabs.TabPages[e.Index].Text;
        var textBounds = new Rectangle(bounds.X + 12, bounds.Y, bounds.Width - 24, bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            new Font("Segoe UI Semibold", 9F),
            textBounds,
            textColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    private void SeedDefaultSquad()
    {
        if (_squads.Count > 0)
        {
            return;
        }

        _squads.Add(new FleetSquad
        {
            Name = "Alpha",
            Icon = "A",
            Commander = _localPlayer ?? "Unassigned",
            Mission = "Standby",
            Members = []
        });
    }

    private void RenderFleetManagement()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RenderFleetManagement);
            return;
        }

        SyncLocalPlayerToDefaultSquad();

        var knownPlayers = _fleetState.Players.ToArray();
        var squadMemberNames = _squads
            .SelectMany(squad => squad.Members)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var totalMembers = squadMemberNames.Length;
        var onlineMembers = squadMemberNames.Count(IsMemberOnline);
        var activeTasks = _squads.Count(squad =>
            !string.IsNullOrWhiteSpace(squad.Mission) &&
            !squad.Mission.Equals("Standby", StringComparison.OrdinalIgnoreCase));
        if (!_globalMission.Equals("Standby", StringComparison.OrdinalIgnoreCase))
        {
            activeTasks++;
        }

        _totalMembersValueLabel.Text = totalMembers.ToString();
        _onlineMembersValueLabel.Text = onlineMembers.ToString();
        _activeTasksValueLabel.Text = activeTasks.ToString();
        _squadCountValueLabel.Text = _squads.Count.ToString();

        _squadFlowPanel.SuspendLayout();
        _squadFlowPanel.Controls.Clear();

        foreach (var squad in _squads)
        {
            _squadFlowPanel.Controls.Add(CreateSquadCard(squad, knownPlayers));
        }

        _squadFlowPanel.ResumeLayout();
        RenderFleetPlayers(knownPlayers);
    }

    private void SyncLocalPlayerToDefaultSquad()
    {
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            return;
        }

        var squad = _squads.FirstOrDefault();
        if (squad is null)
        {
            return;
        }

        if (!squad.Members.Contains(_localPlayer, StringComparer.OrdinalIgnoreCase))
        {
            squad.Members.Add(_localPlayer);
        }

        if (squad.Commander == "Unassigned")
        {
            squad.Commander = _localPlayer;
        }
    }

    private Control CreateSquadCard(FleetSquad squad, IReadOnlyCollection<FleetPlayer> knownPlayers)
    {
        var height = squad.Expanded
            ? Math.Max(232, 134 + Math.Max(1, squad.Members.Count) * 44)
            : 112;

        var card = new Panel
        {
            Width = Math.Max(760, _squadFlowPanel.ClientSize.Width - 36),
            Height = height,
            BackColor = PanelBackColor,
            Margin = new Padding(0, 0, 0, 16),
            Padding = new Padding(14),
            Cursor = Cursors.Hand
        };
        card.Paint += (_, e) => PaintGeometricCard(e.Graphics, card.ClientRectangle);
        card.Click += (_, _) => ToggleSquad(squad);

        var accent = new Panel
        {
            BackColor = AccentColor,
            Bounds = new Rectangle(0, 0, 4, card.Height)
        };
        card.Controls.Add(accent);

        var icon = new Label
        {
            Bounds = new Rectangle(24, 24, 56, 56),
            Text = string.IsNullOrWhiteSpace(squad.Icon) ? squad.Name[..1] : squad.Icon,
            Font = new Font("Segoe UI Semibold", 18F),
            ForeColor = Color.White,
            BackColor = AccentDimColor,
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        icon.Click += (_, _) => ToggleSquad(squad);
        card.Controls.Add(icon);

        var x = 104;
        AddSquadValue(card, "Squad", squad.Name, x, 22, 190);
        AddSquadValue(card, "Commander", squad.Commander, x + 206, 22, 170);
        AddSquadValue(card, "Mission", squad.Mission, x + 390, 22, 170);
        AddSquadValue(card, "Members", squad.Members.Count.ToString(), x + 572, 22, 84);
        AddSquadValue(card, "Online", squad.Members.Count(IsMemberOnline).ToString(), x + 664, 22, 84);

        var rallyText = squad.RallyPoint == "Use Global"
            ? $"Rally: {_rallyPoint}"
            : $"Rally: {squad.RallyPoint}";
        var rallyLabel = new Label
        {
            Bounds = new Rectangle(x, 74, 480, 20),
            Text = rallyText,
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        card.Controls.Add(rallyLabel);

        var avatarStrip = CreateSquadAvatarStrip(squad);
        avatarStrip.Bounds = new Rectangle(x + 494, 70, 180, 34);
        avatarStrip.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        card.Controls.Add(avatarStrip);

        var taskButton = new Button
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Bounds = new Rectangle(card.Width - 278, 36, 72, 30),
            Text = "TASK"
        };
        StyleSecondaryButton(taskButton);
        taskButton.Click += (_, _) => ShowSquadTaskDialog(squad);
        card.Controls.Add(taskButton);

        var deleteButton = new Button
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Bounds = new Rectangle(card.Width - 198, 36, 78, 30),
            Text = "DELETE"
        };
        StyleDangerButton(deleteButton);
        deleteButton.Click += (_, _) => DeleteSquad(squad);
        card.Controls.Add(deleteButton);

        var viewLabel = new Label
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Bounds = new Rectangle(card.Width - 112, 36, 86, 30),
            Text = squad.Expanded ? "COLLAPSE" : "EXPAND",
            ForeColor = AccentColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 8.5F),
            TextAlign = ContentAlignment.MiddleCenter,
            Cursor = Cursors.Hand
        };
        viewLabel.Click += (_, _) => ToggleSquad(squad);
        card.Controls.Add(viewLabel);

        if (squad.Expanded)
        {
            var members = CreateSquadMembersPanel(squad, knownPlayers);
            members.Bounds = new Rectangle(24, 104, card.Width - 48, card.Height - 124);
            members.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
            card.Controls.Add(members);
        }

        return card;
    }

    private Control CreateSquadAvatarStrip(FleetSquad squad)
    {
        var strip = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        foreach (var member in squad.Members.Take(5))
        {
            var avatar = CreateAvatarPicture(member, 30);
            avatar.Margin = new Padding(0, 0, 6, 0);
            strip.Controls.Add(avatar);
        }

        if (squad.Members.Count > 5)
        {
            strip.Controls.Add(new Label
            {
                Width = 38,
                Height = 30,
                Text = $"+{squad.Members.Count - 5}",
                ForeColor = MutedTextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI Semibold", 8F)
            });
        }

        return strip;
    }

    private static void PaintGeometricCard(Graphics graphics, Rectangle bounds)
    {
        PaintHolographicPanel(graphics, bounds, true);

        var outer = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
        using var accentPen = new Pen(CyanGlowColor, 2);
        using var dimPen = new Pen(AccentDimColor);
        graphics.DrawLine(accentPen, outer.Left + 16, outer.Top, outer.Left + 86, outer.Top);
        graphics.DrawLine(accentPen, outer.Right - 86, outer.Bottom, outer.Right - 16, outer.Bottom);
        graphics.DrawLine(dimPen, outer.Left + 88, outer.Top + 18, outer.Right - 16, outer.Top + 18);
    }

    private static void PaintHolographicPanel(Graphics graphics, Rectangle bounds, bool strong)
    {
        if (bounds.Width <= 4 || bounds.Height <= 4)
        {
            return;
        }

        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(bounds.X + 1, bounds.Y + 1, bounds.Width - 3, bounds.Height - 3);
        using var path = CreateRoundRectPath(rect, strong ? 18 : 12);

        using (var fill = new LinearGradientBrush(
                   rect,
                   Color.FromArgb(strong ? 238 : 224, PanelBackColor),
                   Color.FromArgb(strong ? 210 : 190, FieldBackColor),
                   LinearGradientMode.ForwardDiagonal))
        {
            graphics.FillPath(fill, path);
        }

        using var outerGlow = new Pen(Color.FromArgb(strong ? 210 : 145, AccentColor), strong ? 2F : 1.4F);
        using var innerLine = new Pen(Color.FromArgb(100, CyanGlowColor), 1F);
        using var innerPath = CreateRoundRectPath(
            new Rectangle(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 8),
            strong ? 14 : 9);
        graphics.DrawPath(outerGlow, path);
        graphics.DrawPath(innerLine, innerPath);

        using var dotBrush = new SolidBrush(Color.FromArgb(strong ? 90 : 55, AccentColor));
        var startX = rect.Right - Math.Min(170, rect.Width / 3);
        var endX = rect.Right - 20;
        var endY = rect.Top + Math.Min(38, rect.Height - 16);
        for (var y = rect.Top + 12; y < endY; y += 6)
        {
            for (var x = startX; x < endX; x += 6)
            {
                if ((x + y) % 18 == 0)
                {
                    graphics.FillEllipse(dotBrush, x, y, 1.7F, 1.7F);
                }
            }
        }

        using var cornerPen = new Pen(CyanGlowColor, 2F);
        graphics.DrawLine(cornerPen, rect.Left + 16, rect.Top, rect.Left + 82, rect.Top);
        graphics.DrawLine(cornerPen, rect.Left, rect.Top + 16, rect.Left, rect.Top + 48);
        graphics.DrawLine(cornerPen, rect.Right - 82, rect.Bottom, rect.Right - 16, rect.Bottom);
        graphics.DrawLine(cornerPen, rect.Right, rect.Bottom - 48, rect.Right, rect.Bottom - 16);
    }

    private static GraphicsPath CreateRoundRectPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void AddSquadValue(Control parent, string caption, string value, int x, int y, int width)
    {
        var valueLabel = new Label
        {
            Bounds = new Rectangle(x, y, width, 28),
            Text = value,
            ForeColor = PrimaryTextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 10.5F),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };

        var captionLabel = new Label
        {
            Bounds = new Rectangle(x, y + 30, width, 22),
            Text = caption.ToUpperInvariant(),
            ForeColor = MutedTextColor,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8F),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };

        parent.Controls.Add(valueLabel);
        parent.Controls.Add(captionLabel);
    }

    private static Control CreateSquadHeaderLabel(string value, string caption)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = PanelBackColor,
            Cursor = Cursors.Hand
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = value,
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI Semibold", 10F),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        }, 0, 0);

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = caption,
            ForeColor = MutedTextColor,
            Font = new Font("Segoe UI", 8.5F),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        }, 0, 1);

        return panel;
    }

    private Control CreateSquadMembersPanel(FleetSquad squad, IReadOnlyCollection<FleetPlayer> knownPlayers)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = Math.Max(1, squad.Members.Count),
            BackColor = FieldBackColor,
            Padding = new Padding(10)
        };

        if (squad.Members.Count == 0)
        {
            panel.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "No members assigned.",
                ForeColor = MutedTextColor,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            return panel;
        }

        foreach (var memberName in squad.Members)
        {
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var player = knownPlayers.FirstOrDefault(p =>
                p.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            panel.Controls.Add(CreateSquadMemberRow(memberName, player, squad));
        }

        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        return panel;
    }

    private Control CreateSquadMemberRow(string memberName, FleetPlayer? player, FleetSquad squad)
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 5,
            RowCount = 1,
            BackColor = FieldBackColor,
            Padding = new Padding(4, 2, 4, 2),
            Margin = new Padding(0, 0, 0, 4)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 42));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        var status = player?.Online == true ? "ONLINE" : "OFFLINE";
        var ship = player?.Ship ?? "Unknown";
        var task = squad.Mission == "Standby" && _globalMission != "Standby"
            ? _globalMission
            : squad.Mission;
        var commanderMark = memberName.Equals(squad.Commander, StringComparison.OrdinalIgnoreCase)
            ? "COMMAND"
            : "MEMBER";

        row.Controls.Add(CreateAvatarPicture(memberName, 34), 0, 0);
        row.Controls.Add(CreateCompactValue(memberName, commanderMark, player?.Online == true), 1, 0);
        row.Controls.Add(CreateCompactValue(status, "STATUS", player?.Online == true), 2, 0);
        row.Controls.Add(CreateCompactValue(ship, "SHIP", player?.Online == true), 3, 0);
        row.Controls.Add(CreateCompactValue(task, "TASK", player?.Online == true), 4, 0);

        return row;
    }

    private void RenderFleetPlayers(IReadOnlyCollection<FleetPlayer> knownPlayers)
    {
        _fleetPlayersFlowPanel.SuspendLayout();
        _fleetPlayersFlowPanel.Controls.Clear();

        var names = _squads
            .SelectMany(squad => squad.Members)
            .Concat(knownPlayers.Select(player => player.Name))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (names.Length == 0)
        {
            _fleetPlayersFlowPanel.Controls.Add(new Label
            {
                Width = 420,
                Height = 54,
                Text = "No fleet players detected yet.",
                ForeColor = MutedTextColor,
                BackColor = Color.Transparent,
                TextAlign = ContentAlignment.MiddleLeft
            });
        }

        foreach (var name in names)
        {
            var player = knownPlayers.FirstOrDefault(p =>
                p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _fleetPlayersFlowPanel.Controls.Add(CreateFleetPlayerChip(name, player));
        }

        _fleetPlayersFlowPanel.ResumeLayout();
    }

    private Control CreateFleetPlayerChip(string name, FleetPlayer? player)
    {
        var chip = new TableLayoutPanel
        {
            Width = 248,
            Height = 58,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = FieldBackColor,
            Padding = new Padding(8),
            Margin = new Padding(0, 0, 10, 0)
        };
        chip.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
        chip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var squad = GetPlayerSquad(name);
        var status = player?.Online == true ? "ONLINE" : "OFFLINE";
        var detail = $"{status} / {squad?.Name ?? "No Squad"} / {player?.Ship ?? "Unknown"}";

        chip.Controls.Add(CreateAvatarPicture(name, 40), 0, 0);
        chip.Controls.Add(CreateCompactValue(name, detail, player?.Online == true), 1, 0);
        return chip;
    }

    private Control CreateCompactValue(string value, string caption, bool online)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            BackColor = Color.Transparent
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = value,
            ForeColor = online ? PrimaryTextColor : MutedTextColor,
            Font = new Font("Segoe UI Semibold", 9F),
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true
        }, 0, 0);

        panel.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = caption,
            ForeColor = online ? AccentColor : MutedTextColor,
            Font = new Font("Segoe UI", 7.5F),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true
        }, 0, 1);

        return panel;
    }

    private PictureBox CreateAvatarPicture(string playerName, int size)
    {
        return new PictureBox
        {
            Width = size,
            Height = size,
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = CreateAvatarImage(playerName, size)
        };
    }

    private Bitmap CreateAvatarImage(string playerName, int size)
    {
        if (!string.IsNullOrWhiteSpace(_localPlayer) &&
            playerName.Equals(_localPlayer, StringComparison.OrdinalIgnoreCase) &&
            _avatarPictureBox.Image is not null)
        {
            return new Bitmap(_avatarPictureBox.Image, new Size(size, size));
        }

        var bitmap = new Bitmap(size, size);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var background = new LinearGradientBrush(
            new Rectangle(0, 0, size, size),
            AccentDimColor,
            Color.FromArgb(18, 45, 76),
            LinearGradientMode.ForwardDiagonal);
        using var border = new Pen(CyanGlowColor, Math.Max(1F, size / 18F));
        graphics.FillEllipse(background, 1, 1, size - 2, size - 2);
        graphics.DrawEllipse(border, 1, 1, size - 3, size - 3);

        var initials = GetPlayerInitials(playerName);
        using var font = new Font("Segoe UI Semibold", Math.Max(8F, size * 0.38F));
        TextRenderer.DrawText(
            graphics,
            initials,
            font,
            new Rectangle(0, 0, size, size),
            PrimaryTextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        return bitmap;
    }

    private static string GetPlayerInitials(string playerName)
    {
        var cleaned = string.IsNullOrWhiteSpace(playerName)
            ? "?"
            : playerName.Trim();

        var parts = cleaned
            .Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[1][0]).ToUpperInvariant();
        }

        return cleaned.Length >= 2
            ? cleaned[..2].ToUpperInvariant()
            : cleaned[..1].ToUpperInvariant();
    }

    private void ToggleSquad(FleetSquad squad)
    {
        squad.Expanded = !squad.Expanded;
        RenderFleetManagement();
    }

    private void ShowSquadTaskDialog(FleetSquad squad)
    {
        using var dialog = new Form
        {
            Text = $"Set Task - {squad.Name}",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 172),
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI", 10F)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(16),
            BackColor = WindowBackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var missionBox = CreateDialogTextBox(squad.Mission);
        var rallyBox = CreateDialogTextBox(squad.RallyPoint);

        layout.Controls.Add(CreateFieldLabel("Task"), 0, 0);
        layout.Controls.Add(missionBox, 1, 0);
        layout.Controls.Add(CreateFieldLabel("Rally"), 0, 1);
        layout.Controls.Add(rallyBox, 1, 1);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "This overrides the global order for this squad.",
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.TopLeft
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 2);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowBackColor
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var saveButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Save Task"
        };
        StylePrimaryButton(saveButton);
        saveButton.Click += (_, _) =>
        {
            squad.Mission = string.IsNullOrWhiteSpace(missionBox.Text)
                ? "Standby"
                : missionBox.Text.Trim();
            squad.RallyPoint = string.IsNullOrWhiteSpace(rallyBox.Text)
                ? "Use Global"
                : rallyBox.Text.Trim();

            AppendOutput($"COMMAND | squad={squad.Name} | task={squad.Mission} | rally_point={squad.RallyPoint}");
            AppendOutput(string.Empty);

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        var cancelButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Cancel"
        };
        StyleSecondaryButton(cancelButton);
        cancelButton.Click += (_, _) => dialog.Close();

        buttons.Controls.Add(saveButton, 0, 0);
        buttons.Controls.Add(cancelButton, 1, 0);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(buttons, 0, 3);
        dialog.Controls.Add(layout);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            RenderFleetManagement();
        }
    }

    private void DeleteSquad(FleetSquad squad)
    {
        var result = MessageBox.Show(
            this,
            $"Delete squad '{squad.Name}'?",
            "Delete Squad",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);

        if (result != DialogResult.Yes)
        {
            return;
        }

        _squads.Remove(squad);
        AppendOutput($"COMMAND | deleted_squad={squad.Name}");
        AppendOutput(string.Empty);
        RenderFleetManagement();
    }

    private bool IsMemberOnline(string memberName)
    {
        return _fleetState.Players.Any(player =>
            player.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase) &&
            player.Online);
    }

    private void ShowCreateSquadDialog()
    {
        using var dialog = new Form
        {
            Text = "Create Squad",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 294),
            BackColor = WindowBackColor,
            ForeColor = PrimaryTextColor,
            Font = new Font("Segoe UI", 10F)
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 7,
            Padding = new Padding(16),
            BackColor = WindowBackColor
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 5; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        }
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var nameBox = CreateDialogTextBox("Alpha");
        var iconBox = CreateDialogTextBox("A");
        var commanderBox = CreateDialogTextBox(_localPlayer ?? string.Empty);
        var missionBox = CreateDialogTextBox("Standby");
        var membersBox = CreateDialogTextBox(_localPlayer ?? string.Empty);

        layout.Controls.Add(CreateFieldLabel("Name"), 0, 0);
        layout.Controls.Add(nameBox, 1, 0);
        layout.Controls.Add(CreateFieldLabel("Icon"), 0, 1);
        layout.Controls.Add(iconBox, 1, 1);
        layout.Controls.Add(CreateFieldLabel("Commander"), 0, 2);
        layout.Controls.Add(commanderBox, 1, 2);
        layout.Controls.Add(CreateFieldLabel("Mission"), 0, 3);
        layout.Controls.Add(missionBox, 1, 3);
        layout.Controls.Add(CreateFieldLabel("Members"), 0, 4);
        layout.Controls.Add(membersBox, 1, 4);

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Separate member names with commas.",
            ForeColor = MutedTextColor,
            TextAlign = ContentAlignment.TopLeft
        };
        layout.SetColumnSpan(hint, 2);
        layout.Controls.Add(hint, 0, 5);

        var buttonRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = WindowBackColor
        };
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        buttonRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var createButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Create"
        };
        StylePrimaryButton(createButton);
        createButton.Click += (_, _) =>
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _squads.Add(new FleetSquad
            {
                Name = name,
                Icon = string.IsNullOrWhiteSpace(iconBox.Text) ? name[..1] : iconBox.Text.Trim(),
                Commander = string.IsNullOrWhiteSpace(commanderBox.Text) ? "Unassigned" : commanderBox.Text.Trim(),
                Mission = string.IsNullOrWhiteSpace(missionBox.Text) ? "Standby" : missionBox.Text.Trim(),
                Members = membersBox.Text
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            });

            dialog.DialogResult = DialogResult.OK;
            dialog.Close();
        };

        var cancelButton = new Button
        {
            Dock = DockStyle.Fill,
            Text = "Cancel"
        };
        StyleSecondaryButton(cancelButton);
        cancelButton.Click += (_, _) => dialog.Close();

        buttonRow.Controls.Add(createButton, 0, 0);
        buttonRow.Controls.Add(cancelButton, 1, 0);
        layout.SetColumnSpan(buttonRow, 2);
        layout.Controls.Add(buttonRow, 0, 6);

        dialog.Controls.Add(layout);

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            RenderFleetManagement();
        }
    }

    private static TextBox CreateDialogTextBox(string value)
    {
        return new TextBox
        {
            Dock = DockStyle.Fill,
            Text = value,
            BackColor = FieldBackColor,
            ForeColor = PrimaryTextColor,
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private static bool IsStarCitizenRunning()
    {
        return Process.GetProcessesByName("StarCitizen").Length > 0;
    }

    private void SelectLog()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select Star Citizen Game.log",
            Filter = "Star Citizen Game.log|Game.log|Log files (*.log)|*.log|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_pathTextBox.Text))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_pathTextBox.Text);
            dialog.FileName = Path.GetFileName(_pathTextBox.Text);
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _pathTextBox.Text = dialog.FileName;
        SaveCurrentConfig();
        StartWatching(dialog.FileName);
    }

    private void StartWatching(string logPath)
    {
        _watcher?.Dispose();
        _fleetState = new FleetState();
        _outputTextBox.Clear();

        AppendOutput($"Watching: {logPath}");

        var detectedPlayer = TryFindPlayerNameInLog(logPath);
        if (detectedPlayer is not null)
        {
            SetLocalPlayerFromLog(detectedPlayer.PlayerName, detectedPlayer.PlayerId);
            AppendOutput($"Verified game identity from log: {_localPlayer} / ID {_localPlayerId ?? "Unknown"}");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_localPlayer))
            {
                _localPlayer = null;
            }

            RenderProfile();
            AppendOutput(string.IsNullOrWhiteSpace(_localPlayer)
                ? "Waiting for Star Citizen player identity in Game.log."
                : $"Using cached player identity until Game.log confirms it: {_localPlayer} / ID {_localPlayerId ?? "Unknown"}");
        }

        if (!string.IsNullOrWhiteSpace(_localPlayer))
        {
            _fleetState.Apply(new FleetEvent(IsStarCitizenRunning() ? FleetEventType.PlayerOnline : FleetEventType.PlayerOffline, _localPlayer));

            RenderPlayers();
            RenderFleetManagement();
            
            AppendOutput($"Recovered local player: {_localPlayer}");

        }

        AppendOutput("Realtime output started.");
        AppendOutput(string.Empty);

        _watcher = new GameLogWatcher(logPath, replayExistingLines: false, OnLogLine);
        _watcher.Start();


    }
    private void RenderPlayers()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RenderPlayers);
            return;
        }

        _playerList.Items.Clear();

        foreach (var player in _fleetState.Players)
        {
            var status =
                player.Online
                    ? "Online"
                    : "Offline";

            _playerList.Items.Add(
                $"{player.Name} [{status}] - {player.Ship}");
        }
    }

    private void OnLogLine(string line)
    {
        var fleetEvent = _parser.TryParse(line);
        if (fleetEvent is not null)
        {
            fleetEvent = ResolveLocalPlayer(fleetEvent);
        }

        if (fleetEvent?.Type == FleetEventType.PlayerOnline)
        {
            SetLocalPlayerFromLog(fleetEvent.Player, fleetEvent.PlayerId);
        }

        if (fleetEvent?.Type == FleetEventType.PlayerEnteredShip && fleetEvent.Ship is not null)
        {
            _shipState.CurrentShipName = fleetEvent.Ship;
            _shipState.InShip = true;
            UpdateManualReportVerification();

            RenderShipState();
        }

        if (fleetEvent?.Type == FleetEventType.PlayerControllingShip)
        {
            if (ShouldAcceptShipEvent(fleetEvent))
            {
                if (_shipState.Driving &&
                    ShipNamesMatch(_shipState.CurrentDrivingShipName, fleetEvent.Ship ?? _shipState.CurrentShipName))
                {
                    return;
                }

                var shipName = fleetEvent.Ship is null
                    ? _shipState.CurrentShipName
                    : GetBestShipDisplayName(fleetEvent.Ship);

                _shipState.InShip = true;
                _shipState.CurrentShipName = shipName;
                _shipState.Driving = true;
                _shipState.CurrentDrivingShipName = shipName;
                _shipState.CurrentControlShipName = shipName;
                _shipState.ControlSignalStatus = "Explicit";
                _shipState.ControlStationType = "Pilot/Control Token";
                _shipState.LastControlSignalAt = DateTimeOffset.Now;
                UpdateManualReportVerification();
            }
            else
            {
                return;
            }

        }

        if (fleetEvent?.Type == FleetEventType.PlayerShipControlSignal)
        {
            if (ShouldAcceptShipEvent(fleetEvent))
            {
                ApplyControlSignal(fleetEvent);
                UpdateManualReportVerification();
            }
            else
            {
                return;
            }
        }

        if (fleetEvent?.Type ==
            FleetEventType.PlayerStoppedDrivingShip)
        {
            _shipState.Driving = false;
            _shipState.LastDrivenShipName = fleetEvent.Ship ?? _shipState.CurrentDrivingShipName;
            _shipState.CurrentDrivingShipName = "None";
            _shipState.CurrentControlShipName = "None";
            _shipState.ControlSignalStatus = "Released";
            _shipState.ControlStationType = "Unknown";
            _shipState.LastControlSignalAt = DateTimeOffset.Now;
            UpdateManualReportVerification();
        }

        if (fleetEvent?.Type == FleetEventType.PlayerExitedShip)
        {
            _shipState.InShip = false;
            _shipState.Driving = false;
            _shipState.CurrentShipName = "Unknown";
            _shipState.CurrentDrivingShipName = "None";
            _shipState.CurrentControlShipName = "None";
            _shipState.ControlSignalStatus = "None";
            _shipState.ControlStationType = "Unknown";
            _shipState.LastControlSignalAt = null;
            UpdateManualReportVerification();
        }

        if (fleetEvent is null)
        {
            return;
        }

        _fleetState.Apply(fleetEvent);
        RenderPlayers();
        RenderFleetManagement();
        RenderShipState(); 
        RenderProfile();
        AppendOutput(FormatEvent(fleetEvent));
        AppendOutput(FormatFleetSummary());
        AppendOutput(string.Empty);
    }

    private FleetEvent ResolveLocalPlayer(FleetEvent fleetEvent)
    {
        if (!string.Equals(fleetEvent.Player, "LocalPlayer", StringComparison.OrdinalIgnoreCase))
        {
            return fleetEvent;
        }

        return !string.IsNullOrWhiteSpace(_localPlayer)
            ? fleetEvent with { Player = _localPlayer }
            : fleetEvent;
    }

    private void SetLocalPlayerFromLog(string playerName, string? playerId = null)
    {
        if (string.IsNullOrWhiteSpace(playerName) ||
            playerName.Equals("LocalPlayer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _localPlayer = playerName.Trim();
        if (!string.IsNullOrWhiteSpace(playerId))
        {
            _localPlayerId = playerId.Trim();
        }

        SyncLocalPlayerToDefaultSquad();
        SaveCurrentConfig();
        RenderProfile();
        RenderFleetManagement();
    }

    private LocalIdentity? TryFindPlayerNameInLog(string logPath)
    {
        try
        {
            LocalIdentity? identity = null;
            foreach (var line in ReadSharedLines(logPath))
            {
                var fleetEvent = _parser.TryParse(line);
                if (fleetEvent?.Type == FleetEventType.PlayerOnline &&
                    !fleetEvent.Player.Equals("LocalPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    identity = new LocalIdentity(fleetEvent.Player, fleetEvent.PlayerId);
                }
            }

            return identity;
        }
        catch
        {
            return null;
        }
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

    private void SaveProfile()
    {
        _displayName = _displayNameTextBox.Text.Trim();
        SaveCurrentConfig();
        RenderProfile();

        AppendOutput($"PROFILE | game_name={_localPlayer ?? "Unverified"} | callsign={_displayName}");
        AppendOutput(string.Empty);
    }

    private void ChooseAvatar()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose avatar image",
            Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        using var cropDialog = new AvatarCropDialog(dialog.FileName);
        if (cropDialog.ShowDialog(this) != DialogResult.OK || cropDialog.CroppedAvatar is null)
        {
            return;
        }

        Directory.CreateDirectory(AppConfig.ConfigDirectory);
        var avatarPath = Path.Combine(AppConfig.ConfigDirectory, "avatar.png");
        cropDialog.CroppedAvatar.Save(avatarPath, System.Drawing.Imaging.ImageFormat.Png);
        _avatarPath = avatarPath;
        SaveCurrentConfig();
        LoadAvatarPreview();
    }

    private void LoadAvatarPreview()
    {
        if (string.IsNullOrWhiteSpace(_avatarPath) || !File.Exists(_avatarPath))
        {
            _avatarPictureBox.Image?.Dispose();
            _avatarPictureBox.Image = null;
            return;
        }

        using var image = Image.FromFile(_avatarPath);
        _avatarPictureBox.Image?.Dispose();
        _avatarPictureBox.Image = new Bitmap(image);
    }

    private void RenderProfile()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RenderProfile);
            return;
        }

        _profileGameNameLabel.Text = string.IsNullOrWhiteSpace(_localPlayer)
            ? "Waiting for Game.log identity"
            : _localPlayer;
        _profilePlayerIdLabel.Text = string.IsNullOrWhiteSpace(_localPlayerId)
            ? "Waiting for playerGEID"
            : _localPlayerId;

        var squad = GetLocalPlayerSquad();
        _profileFleetLabel.Text = "Local Fleet";
        _profileSquadLabel.Text = squad?.Name ?? "Unassigned";
        _profileStatusLabel.Text = string.IsNullOrWhiteSpace(_localPlayer)
            ? "Identity Required"
            : $"{(IsStarCitizenRunning() ? "Online" : "Offline")} / {_shipState.CurrentShipName}";
    }

    private FleetSquad? GetLocalPlayerSquad()
    {
        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            return null;
        }

        return GetPlayerSquad(_localPlayer);
    }

    private FleetSquad? GetPlayerSquad(string playerName)
    {
        return _squads.FirstOrDefault(squad =>
            squad.Members.Contains(playerName, StringComparer.OrdinalIgnoreCase));
    }

    private void SaveCurrentConfig()
    {
        AppConfig.Save(new AppConfig(
            _pathTextBox.Text,
            _localPlayer,
            _displayName,
            _avatarPath,
            _localPlayerId));
    }

    private void SubmitManualReport()
    {
        var reportedShip = _reportedShipTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(reportedShip))
        {
            reportedShip = _shipState.CurrentShipName;
        }

        _shipState.ReportedShipName = string.IsNullOrWhiteSpace(reportedShip)
            ? "Unknown"
            : reportedShip;
        _shipState.ReportedStationType = _reportedStationComboBox.SelectedItem?.ToString() ?? "Unknown";

        UpdateManualReportVerification();
        RenderShipState();

        AppendOutput(
            $"REPORT | ship={_shipState.ReportedShipName} | station={_shipState.ReportedStationType} | verification={_shipState.ReportVerification}");
        AppendOutput(string.Empty);
    }

    private void UpdateManualReportVerification()
    {
        if (IsUnknownShip(_shipState.ReportedShipName))
        {
            _shipState.ReportVerification = "Not Reported";
            RenderReportVerification();
            return;
        }

        if (_shipState.Driving &&
            ShipNamesMatch(_shipState.ReportedShipName, _shipState.CurrentDrivingShipName))
        {
            _shipState.ReportVerification = "Verified";
            RenderReportVerification();
            return;
        }

        if (_shipState.InShip &&
            ShipNamesMatch(_shipState.ReportedShipName, _shipState.CurrentShipName))
        {
            _shipState.ReportVerification = "Verified On Ship";
            RenderReportVerification();
            return;
        }

        if (_shipState.ControlSignalStatus == "Recent" &&
            ShipNamesMatch(_shipState.ReportedShipName, _shipState.CurrentControlShipName))
        {
            _shipState.ReportVerification = "Weak Evidence";
            RenderReportVerification();
            return;
        }

        _shipState.ReportVerification = "Unverified";
        RenderReportVerification();
    }

    private void RenderReportVerification()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RenderReportVerification);
            return;
        }

        _reportLabel.Text = $"Verification: {_shipState.ReportVerification}";
    }

    private void ApplyControlSignal(FleetEvent fleetEvent)
    {
        var shipName = fleetEvent.Ship is null
            ? _shipState.CurrentShipName
            : GetBestShipDisplayName(fleetEvent.Ship);

        _shipState.InShip = true;
        _shipState.CurrentShipName = shipName;
        _shipState.CurrentControlShipName = shipName;
        _shipState.ControlSignalStatus = "Recent";
        _shipState.ControlStationType = "Unknown";
        _shipState.LastControlSignalAt = DateTimeOffset.Now;
    }

    private bool ShouldAcceptShipEvent(FleetEvent fleetEvent)
    {
        if (fleetEvent.Type != FleetEventType.PlayerControllingShip &&
            fleetEvent.Type != FleetEventType.PlayerShipControlSignal)
        {
            return false;
        }

        if (fleetEvent.Ship is null)
        {
            return _shipState.InShip;
        }

        if (!_shipState.InShip || IsUnknownShip(_shipState.CurrentShipName))
        {
            return true;
        }

        return ShipNamesMatch(_shipState.CurrentShipName, fleetEvent.Ship);
    }

    private string GetBestShipDisplayName(string eventShip)
    {
        return ShipNamesMatch(_shipState.CurrentShipName, eventShip)
            ? _shipState.CurrentShipName
            : ToFriendlyShipName(eventShip);
    }

    private static bool IsUnknownShip(string value)
    {
        return string.IsNullOrWhiteSpace(value) ||
               value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShipNamesMatch(string left, string right)
    {
        return NormalizeShipKey(left) == NormalizeShipKey(right);
    }

    private static string NormalizeShipKey(string value)
    {
        var normalized = value
            .Replace("_", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .ToLowerInvariant();

        return normalized
            .Replace("anvl", "anvil")
            .Replace("aegs", "aegis")
            .Replace("crus", "crusader")
            .Replace("drak", "drake")
            .Replace("orig", "origin")
            .Replace("mrai", "mirai")
            .Replace("cnou", "consolidatedoutland")
            .Replace("rsi", "rsi")
            .Replace("misc", "misc")
            .Replace("argo", "argo");
    }

    private static string ToFriendlyShipName(string value)
    {
        var parts = value
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return value;
        }

        parts[0] = parts[0].ToLowerInvariant() switch
        {
            "anvl" => "Anvil",
            "aegs" => "Aegis",
            "crus" => "Crusader",
            "drak" => "Drake",
            "orig" => "Origin",
            "mrai" => "Mirai",
            "cnou" => "Consolidated Outland",
            "rsi" => "RSI",
            "misc" => "MISC",
            "argo" => "ARGO",
            _ => parts[0]
        };

        return string.Join(" ", parts);
    }


    private string FormatEvent(FleetEvent fleetEvent)
    {
        var details = new[]
        {
            fleetEvent.Ship is null ? null : $"ship={fleetEvent.Ship}",
            fleetEvent.Location is null ? null : $"location={fleetEvent.Location}",
            fleetEvent.CombatState is null ? null : $"combat={fleetEvent.CombatState}",
            fleetEvent.NetworkState is null ? null : $"network={fleetEvent.NetworkState}"
        }.Where(value => value is not null);

        var suffix = string.Join(" | ", details);
        return string.IsNullOrWhiteSpace(suffix)
            ? $"EVENT | {fleetEvent.Type} | player={fleetEvent.Player}"
            : $"EVENT | {fleetEvent.Type} | player={fleetEvent.Player} | {suffix}";
    }

    private string FormatFleetSummary()
    {
        var summary = _fleetState.GetSummary();
        var players = string.Join(", ", _fleetState.Players.Select(player =>
            $"{player.Name}:{(player.Online ? "Online" : "Offline")}:{player.Ship}:{player.Location}"));

        return $"STATE | {summary.OnlinePlayers}/{summary.TotalPlayers} online | {players}";
    }

    private void AppendOutput(string message)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendOutput(message));
            return;
        }

        _outputTextBox.AppendText(message + Environment.NewLine);
    }

    private void CheckGameProcess()
    {
        ExpireStaleControlSignal();

        if (string.IsNullOrWhiteSpace(_localPlayer))
        {
            return;
        }

        var player = _fleetState.Players
            .FirstOrDefault(p =>
                p.Name.Equals(
                    _localPlayer,
                    StringComparison.OrdinalIgnoreCase));

        if (player is null)
        {
            return;
        }

        var running = IsStarCitizenRunning();

        if (player.Online != running)
        {
            player.Online = running;

            RenderPlayers();
            RenderProfile();

            AppendOutput(
                running
                    ? "Star Citizen detected."
                    : "Star Citizen process exited.");
        }
    }

    private void ExpireStaleControlSignal()
    {
        if (_shipState.ControlSignalStatus != "Recent" ||
            _shipState.LastControlSignalAt is null)
        {
            return;
        }

        if (DateTimeOffset.Now - _shipState.LastControlSignalAt.Value <= ControlSignalFreshness)
        {
            return;
        }

        _shipState.CurrentControlShipName = "None";
        _shipState.ControlSignalStatus = "Stale";
        _shipState.ControlStationType = "Unknown";
        UpdateManualReportVerification();
        RenderShipState();
        RenderProfile();
    }

    private void RenderShipState()
    {
        if (InvokeRequired)
        {
            BeginInvoke(RenderShipState);
            return;
        }

        _shipLabel.Text =
            $"Current Ship: {_shipState.CurrentShipName}" +
            Environment.NewLine +
            $"Driving Ship: {_shipState.CurrentDrivingShipName}" +
            Environment.NewLine +
            $"Control Signal: {_shipState.ControlSignalStatus} ({_shipState.CurrentControlShipName})" +
            Environment.NewLine +
            $"Station Type: {_shipState.ControlStationType}" +
            Environment.NewLine +
            $"Reported: {_shipState.ReportedShipName} / {_shipState.ReportedStationType}" +
            Environment.NewLine +
            $"Last Driven: {_shipState.LastDrivenShipName}" +
            Environment.NewLine +
            $"In Ship: {(_shipState.InShip ? "Yes" : "No")} / Driving: {(_shipState.Driving ? "Yes" : "No")}";

        RenderProfile();
    }
}

internal sealed class FleetSquad
{
    public string Name { get; set; } = "Unnamed";

    public string Icon { get; set; } = "?";

    public string Commander { get; set; } = "Unassigned";

    public string Mission { get; set; } = "Standby";

    public string RallyPoint { get; set; } = "Use Global";

    public List<string> Members { get; set; } = [];

    public bool Expanded { get; set; }
}

internal sealed record LocalIdentity(string PlayerName, string? PlayerId);
