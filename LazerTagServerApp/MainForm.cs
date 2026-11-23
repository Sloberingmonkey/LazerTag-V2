using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Text;
using System.Windows.Forms;
using LazerTagHostLibrary;

namespace LazerTagServerApp
{
    public class MainForm : Form
    {
        // ESP32 AP mode always assigns this address — no discovery needed.
        private const string Esp32ApAddress = "192.168.4.1:8266";

        private ComboBox _comPortsCombo;
        private Button _refreshPortsButton;
        private Button _connectButton;
        private ComboBox _gameTypeCombo;
        private NumericUpDown _minutesBox;
        private NumericUpDown _countdownBox;
        private NumericUpDown _globalTagsBox;
        private NumericUpDown _globalShieldsBox;
        private NumericUpDown _globalReloadsBox;
        private NumericUpDown _globalMegaBox;
        private NumericUpDown _globalStartingAmmoBox;
        private NumericUpDown _globalSleepTimeoutBox;
        private CheckBox _slowTagsCheck;
        private CheckBox _teamTagsCheck;
        private CheckBox _medicModeCheck;
        private CheckBox _ltarDisableIffCheck;
        private CheckBox _ltarDisableHitRegisterCheck;
        private CheckBox _autoReloadCheck;
        private CheckBox _fullAutoCheck;
        private Button _startHostButton;
        private Button _startCountdownButton;
        private Button _endGameButton;
        private Button _nextButton;
        private Button _finishDebriefButton;
        private Button _saveLogButton;
        private Label _statusLabel;
        private Label _relayStatusLabel;
        private ListView _playersView;
        private TextBox _logBox;
        private Timer _updateTimer;

        private NumericUpDown _plannedPlayersBox;
        private Button _buildPlayerSlotsButton;
        private DataGridView _plannedPlayersGrid;
        private Label _guidedJoinLabel;
        private Label _telemetryLabel;
        private FlowLayoutPanel _teamDropPanel;
        private readonly List<Label> _teamDropTargets = new List<Label>();

        private HostGun _hostGun;
        private LazerTagSerial _preConnectSerial; // relay connection held before a game starts
        private int _sessionGameCount = 0;        // increments each time Start Hosting is clicked
        private bool _pauseLogAutoScroll;
        private readonly List<string> _plannedNames = new List<string>();
        private readonly List<int?> _plannedTeamPreferences = new List<int?>();
        private readonly List<int?> _plannedHpOverrides = new List<int?>();
        private readonly List<int?> _plannedShieldsOverrides = new List<int?>();
        private readonly List<int?> _plannedReloadsOverrides = new List<int?>();
        private readonly List<bool?> _plannedAutoReloadOverrides = new List<bool?>();
        private readonly List<bool?> _plannedFullAutoOverrides = new List<bool?>();
        private readonly List<int?> _plannedStartingAmmoOverrides = new List<int?>();
        private readonly List<int?> _plannedSleepTimeoutOverrides = new List<int?>();

        private int _telemetryRelayConnectAttempts;
        private int _telemetryRelayConnectedCount;
        private int _telemetryRelayTimeoutCount;
        private int _telemetryRelayConnectFailureCount;
        private int _telemetryRelayIoErrorCount;
        private int _telemetryUnexpectedPacketCount;
        private int _telemetryStateTimeoutCount;
        private int _telemetryGamesStartedCount;
        private int _telemetryGamesCompletedCount;

        public MainForm()
        {
            Text = "Dan's IR Laser Tag Server  " + AppVersion.Version;
            Width = 1240;
            Height = 840;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(14, 16, 28);

            BuildUi();
            ApplyDarkTheme();
            RefreshPorts();
            RefreshGameTypes();
            BuildPlayerSlots();
            LoadPlayerSettings();
            UpdateTelemetry();

            _updateTimer = new Timer();
            _updateTimer.Interval = 100;
            _updateTimer.Tick += UpdateTimer_Tick;

            Log.MinimumSeverity = Log.Severity.Information; // suppress Debug allocations on every IR byte
            Log.EntryAdded += Log_EntryAdded;
            FormClosing += MainForm_FormClosing;
        }

        private void BuildUi()
        {
            // ── Controls panel (auto-sizes to fit content) ───────────────────────
            var topPanel = new TableLayoutPanel();
            topPanel.Dock = DockStyle.Top;
            topPanel.AutoSize = true;
            topPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            topPanel.ColumnCount = 2;
            topPanel.RowCount = 4;
            topPanel.Padding = new Padding(8, 4, 8, 2);
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));   // 0 label
            topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // 1 rest
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            topPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            // ── Create all controls ───────────────────────────────────────────────
            _comPortsCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, Width = 160 };
            _refreshPortsButton = new Button { Text = "Refresh", Height = 24 };
            _refreshPortsButton.Click += delegate { RefreshPorts(); };

            _connectButton = new Button { Text = "Connect", Height = 24, Width = 72 };
            _connectButton.Click += ConnectButton_Click;

            _gameTypeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            _gameTypeCombo.SelectedIndexChanged += GameTypeCombo_SelectedIndexChanged;

            _statusLabel = new Label { Text = "Status: Idle", AutoSize = true,
                Font = new Font(Font.FontFamily, 10.0f, FontStyle.Bold),
                Padding = new Padding(8, 5, 0, 0) };

            _relayStatusLabel = new Label
            {
                Text    = "⬤  Relay: not connected",
                AutoSize = true,
                Font    = new Font(Font.FontFamily, 9.0f),
                Padding = new Padding(16, 6, 0, 0),
            };

            _minutesBox   = new NumericUpDown { Minimum = 1,  Maximum = 99,  Value = 10, Width = 65 };
            _countdownBox = new NumericUpDown { Minimum = 5,  Maximum = 60,  Value = 30, Width = 65 };
            _globalTagsBox    = new NumericUpDown { Minimum = 1,   Maximum = 254, Value = 10, Width = 65 };
            _globalShieldsBox = new NumericUpDown { Minimum = 0,   Maximum = 254, Value = 0,  Width = 65 };
            _globalReloadsBox = new NumericUpDown { Minimum = 0,   Maximum = 99,  Value = 0,  Width = 60 };
            _globalMegaBox    = new NumericUpDown { Minimum = 0,   Maximum = 99,  Value = 0,  Width = 60 };
            _globalStartingAmmoBox  = new NumericUpDown { Minimum = 0, Maximum = 99, Value = 10, Width = 60 };
            _globalSleepTimeoutBox  = new NumericUpDown { Minimum = 0, Maximum = 99, Value = 0, Width = 60 };

            _slowTagsCheck            = new CheckBox { Text = "Hit Cooldown (1s)",    AutoSize = true };
            _teamTagsCheck            = new CheckBox { Text = "Team Tags",           AutoSize = true };
            _medicModeCheck           = new CheckBox { Text = "Medic Mode (non-LTAR)", AutoSize = true };
            _autoReloadCheck          = new CheckBox { Text = "Auto Reload",         AutoSize = true };
            _fullAutoCheck            = new CheckBox { Text = "Full Auto",           AutoSize = true };
            _ltarDisableIffCheck      = new CheckBox { Text = "Disable IFF Beacons",  AutoSize = true, Checked = true };
            _ltarDisableHitRegisterCheck = new CheckBox { Text = "Disable Hit Register", AutoSize = true, Checked = true };

            _plannedPlayersBox = new NumericUpDown { Minimum = 1, Maximum = 24, Value = 3, Width = 55 };
            _buildPlayerSlotsButton = new Button { Text = "Create Slots", Height = 24 };
            _buildPlayerSlotsButton.Click += delegate { BuildPlayerSlots(); };

            _guidedJoinLabel = new Label
            {
                AutoSize = true,
                Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
                Padding = new Padding(12, 5, 0, 0)
            };

            _telemetryLabel = new Label
            {
                AutoSize = false,
                Height = 42,
                Dock = DockStyle.Top,
                Font = new Font(Font.FontFamily, 8.75f, FontStyle.Regular),
                Padding = new Padding(10, 6, 0, 0),
                Text = "Telemetry: Relay 0/0, Timeouts 0, IO 0 | Players 0/0/0 | State Idle | Games 0/0"
            };

            // ── Row 0: COM Port | game type | status ─────────────────────────────
            var row0 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false };
            row0.Controls.Add(MakeLabel("Connection:", 0));
            row0.Controls.Add(_comPortsCombo);
            row0.Controls.Add(_refreshPortsButton);
            row0.Controls.Add(_connectButton);
            row0.Controls.Add(MakeLabel("Game Type:", 12));
            row0.Controls.Add(_gameTypeCombo);
            row0.Controls.Add(_statusLabel);
            row0.Controls.Add(_relayStatusLabel);
            AddCell(topPanel, row0, 0, 0, 2);

            // ── Row 1: timing + HP + shields + reloads ───────────────────────────
            var row1 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false };
            row1.Controls.Add(MakeLabel("Minutes:", 0));
            row1.Controls.Add(_minutesBox);
            row1.Controls.Add(MakeLabel("Countdown:", 10));
            row1.Controls.Add(_countdownBox);
            row1.Controls.Add(MakeLabel("HP (tags):", 10));
            row1.Controls.Add(_globalTagsBox);
            row1.Controls.Add(MakeLabel("Shields (s toggle):", 10));
            row1.Controls.Add(_globalShieldsBox);
            row1.Controls.Add(MakeLabel("Reloads (0=∞):", 10));
            row1.Controls.Add(_globalReloadsBox);
            row1.Controls.Add(MakeLabel("Mega:", 10));
            row1.Controls.Add(_globalMegaBox);
            row1.Controls.Add(_slowTagsCheck);
            AddCell(topPanel, row1, 0, 1, 2);

            // ── Row 2: ammo + sleep + checkboxes ─────────────────────────────────
            var row2 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false };
            row2.Controls.Add(MakeLabel("Ammo:", 0));
            row2.Controls.Add(_globalStartingAmmoBox);
            row2.Controls.Add(MakeLabel("(0=∞)", 2));
            row2.Controls.Add(MakeLabel("Sleep (m):", 8));
            row2.Controls.Add(_globalSleepTimeoutBox);
            row2.Controls.Add(MakeLabel("(0=off)", 2));
            row2.Controls.Add(_autoReloadCheck);
            row2.Controls.Add(_fullAutoCheck);
            row2.Controls.Add(_teamTagsCheck);
            row2.Controls.Add(_medicModeCheck);
            row2.Controls.Add(_ltarDisableIffCheck);
            row2.Controls.Add(_ltarDisableHitRegisterCheck);
            AddCell(topPanel, row2, 0, 2, 2);

            // ── Row 3: players count + create slots + guided join label ──────────
            var row3 = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false, WrapContents = false };
            row3.Controls.Add(MakeLabel("Players:", 0));
            row3.Controls.Add(_plannedPlayersBox);
            row3.Controls.Add(_buildPlayerSlotsButton);
            row3.Controls.Add(_guidedJoinLabel);
            AddCell(topPanel, row3, 0, 3, 2);

            // ── Buttons panel ────────────────────────────────────────────────────
            var buttonsPanel = new FlowLayoutPanel();
            buttonsPanel.Dock = DockStyle.Top;
            buttonsPanel.Height = 44;
            buttonsPanel.Padding = new Padding(8, 4, 8, 4);
            buttonsPanel.AutoSize = false;

            _startHostButton = new Button { Text = "Start Hosting", Height = 34, Width = 120 };
            _startHostButton.Click += StartHostButton_Click;
            _startCountdownButton = new Button { Text = "Start Countdown", Height = 34, Width = 130 };
            _startCountdownButton.Click += StartCountdownButton_Click;
            _endGameButton = new Button { Text = "End Game", Height = 34, Width = 100 };
            _endGameButton.Click += EndGameButton_Click;
            _nextButton = new Button { Text = "Next / Summary", Height = 34, Width = 120 };
            _nextButton.Click += NextButton_Click;
            _finishDebriefButton = new Button { Text = "Finish with missing reports", Height = 34, Width = 190, Visible = false };
            _finishDebriefButton.Click += (s, e) => GameWindow.ConfirmFinishDebrief(this, _hostGun);
            _saveLogButton = new Button { Text = "Save Log", Height = 34, Width = 90 };
            _saveLogButton.Click += SaveLogButton_Click;

            buttonsPanel.Controls.Add(_startHostButton);
            buttonsPanel.Controls.Add(_startCountdownButton);
            buttonsPanel.Controls.Add(_endGameButton);
            buttonsPanel.Controls.Add(_nextButton);
            buttonsPanel.Controls.Add(_saveLogButton);
            buttonsPanel.Controls.Add(_finishDebriefButton);

            // ── Player setup grid (fixed height, outside topPanel for full width) ─
            _plannedPlayersGrid = new DataGridView();
            _plannedPlayersGrid.Dock = DockStyle.Top;
            _plannedPlayersGrid.Height = 220;
            _plannedPlayersGrid.AllowUserToAddRows = false;
            _plannedPlayersGrid.AllowUserToDeleteRows = false;
            _plannedPlayersGrid.RowHeadersVisible = false;
            _plannedPlayersGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _plannedPlayersGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            _plannedPlayersGrid.RowTemplate.Height = 26;
            _plannedPlayersGrid.Columns.Add("Slot", "#");
            _plannedPlayersGrid.Columns[0].ReadOnly = true;
            _plannedPlayersGrid.Columns[0].FillWeight = 5;
            _plannedPlayersGrid.Columns.Add("Name", "Player Name");
            _plannedPlayersGrid.Columns[1].FillWeight = 42;

            var teamPrefCol = new DataGridViewComboBoxColumn
            {
                Name = "TeamPref",
                HeaderText = "Team",
                FillWeight = 10,
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat
            };
            teamPrefCol.Items.AddRange("Auto", "Team 1", "Team 2", "Team 3");
            _plannedPlayersGrid.Columns.Add(teamPrefCol);

            _plannedPlayersGrid.Columns.Add("HP", "HP");
            _plannedPlayersGrid.Columns[3].FillWeight = 10;
            _plannedPlayersGrid.Columns.Add("Shields", "Shields (s toggle)");
            _plannedPlayersGrid.Columns[4].FillWeight = 12;
            _plannedPlayersGrid.Columns.Add("Reloads", "Reloads (0=∞)");
            _plannedPlayersGrid.Columns[5].FillWeight = 10;
            var autoReloadCol = new DataGridViewCheckBoxColumn { Name = "AutoReload", HeaderText = "Auto Reload", FillWeight = 12, ThreeState = false };
            _plannedPlayersGrid.Columns.Add(autoReloadCol);
            var fullAutoCol = new DataGridViewCheckBoxColumn { Name = "FullAuto", HeaderText = "Full Auto", FillWeight = 11, ThreeState = false };
            _plannedPlayersGrid.Columns.Add(fullAutoCol);
            _plannedPlayersGrid.Columns.Add("Ammo", "Ammo (0=∞)");
            _plannedPlayersGrid.Columns[8].FillWeight = 10;
            _plannedPlayersGrid.Columns.Add("Sleep", "Sleep m (0=off)");
            _plannedPlayersGrid.Columns[9].FillWeight = 10;
            _plannedPlayersGrid.CellValidating += PlannedPlayersGrid_CellValidating;
            UpdatePlannedTeamColumnVisibility();

            // ── Players list + log split ─────────────────────────────────────────
            _playersView = new ListView();
            _playersView.Dock = DockStyle.Fill;
            _playersView.View = View.Details;
            _playersView.FullRowSelect = true;
            _playersView.GridLines = true;
            _playersView.Columns.Add("Player", 90);
            _playersView.Columns.Add("Team", 55);
            _playersView.Columns.Add("Slot", 50);
            _playersView.Columns.Add("Name", 160);
            _playersView.Columns.Add("Confirmed", 80);
            _playersView.Columns.Add("Hits Scored", 90);
            _playersView.Columns.Add("Hits Taken", 85);
            _playersView.Columns.Add("Tagger ID", 90);
            _playersView.ItemDrag += PlayersView_ItemDrag;

            _teamDropPanel = new FlowLayoutPanel();
            _teamDropPanel.Dock = DockStyle.Top;
            _teamDropPanel.Height = 44;
            _teamDropPanel.Padding = new Padding(8, 6, 8, 6);
            _teamDropPanel.WrapContents = false;
            _teamDropPanel.FlowDirection = FlowDirection.LeftToRight;
            _teamDropPanel.AutoScroll = true;

            _logBox = new TextBox();
            _logBox.Dock = DockStyle.Fill;
            _logBox.Multiline = true;
            _logBox.ReadOnly = true;
            _logBox.ScrollBars = ScrollBars.Vertical;
            _logBox.Font = new Font("Consolas", 9.0f);
            _logBox.MouseDown += LogBox_MouseDown;
            _logBox.Leave += LogBox_Leave;

            var split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.Orientation = Orientation.Horizontal;
            split.SplitterDistance = 400;
            split.SplitterWidth    = 5;
            split.BackColor        = Color.FromArgb(38, 42, 68); // visible splitter bar
            split.Panel1.Controls.Add(_playersView);
            split.Panel2.Controls.Add(_logBox);

            BuildTeamDropTargets();

            // Controls are added bottom-up for DockStyle.Top stacking
            Controls.Add(split);
            Controls.Add(_teamDropPanel);
            Controls.Add(_plannedPlayersGrid);
            Controls.Add(_telemetryLabel);
            Controls.Add(buttonsPanel);
            Controls.Add(topPanel);
        }



        private void BuildTeamDropTargets()
        {
            _teamDropTargets.Clear();
            _teamDropPanel.Controls.Clear();

            for (int teamNumber = 1; teamNumber <= 3; teamNumber++)
            {
                var label = new Label
                {
                    AutoSize = false,
                    Width = 170,
                    Height = 28,
                    Margin = new Padding(4, 0, 4, 0),
                    TextAlign = ContentAlignment.MiddleCenter,
                    BorderStyle = BorderStyle.FixedSingle,
                    AllowDrop = true,
                    Tag = teamNumber,
                    Text = "Drop selected tagger on Team " + teamNumber
                };
                label.DragEnter += TeamDropTarget_DragEnter;
                label.DragDrop += TeamDropTarget_DragDrop;
                _teamDropTargets.Add(label);
                _teamDropPanel.Controls.Add(label);
            }

            UpdateTeamDropTargets();
        }

        private void UpdatePlannedTeamColumnVisibility()
        {
            if (_plannedPlayersGrid == null || _plannedPlayersGrid.Columns.Count < 3)
                return;

            var isTeamGame = IsCurrentUiTeamGame();
            _plannedPlayersGrid.Columns["TeamPref"].Visible = isTeamGame;
        }

        private int GetCurrentUiTeamCount()
        {
            if (_hostGun != null && _hostGun.GameDefinition != null)
                return _hostGun.TeamCount;

            var choice = _gameTypeCombo.SelectedItem as GameTypeChoice;
            if (choice == null)
                return 1;

            return GameTypes.GetInfo(choice.Type).TeamCount;
        }

        private bool IsCurrentUiTeamGame()
        {
            return GetCurrentUiTeamCount() > 1;
        }

        private void UpdateTeamDropTargets()
        {
            // Drag-and-drop team reassignment is hidden in the dropdown-team version of the UI.
            foreach (var label in _teamDropTargets)
            {
                label.Visible = false;
                label.Enabled = false;
            }

            _teamDropPanel.Visible = false;
            UpdatePlannedTeamColumnVisibility();
        }

        private void PlayersView_ItemDrag(object sender, ItemDragEventArgs e)
        {
            if (_hostGun == null) return;
            if (_hostGun.GameDefinition == null || !_hostGun.GameDefinition.IsTeamGame) return;
            if (_hostGun.HostingState != HostGun.HostingStates.Adding &&
                _hostGun.HostingState != HostGun.HostingStates.AcknowledgePlayerAssignment)
                return;

            var item = e.Item as ListViewItem;
            if (item == null || item.Tag == null) return;

            DoDragDrop(item.Tag, DragDropEffects.Move);
        }

        private void TeamDropTarget_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(typeof(TeamPlayerId))
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void TeamDropTarget_DragDrop(object sender, DragEventArgs e)
        {
            if (_hostGun == null) return;
            if (_hostGun.GameDefinition == null || !_hostGun.GameDefinition.IsTeamGame) return;

            var label = sender as Label;
            if (label == null || label.Tag == null) return;
            if (!e.Data.GetDataPresent(typeof(TeamPlayerId))) return;

            var currentId = (TeamPlayerId)e.Data.GetData(typeof(TeamPlayerId));
            var requestedTeam = (int)label.Tag;

            if (_hostGun.MovePlayerToTeam(currentId, requestedTeam))
            {
                AppendLog("Moved " + currentId.ToString(_hostGun.GameDefinition.IsTeamGame) +
                          " to Team " + requestedTeam + ".");
                RefreshPlayersView();
            }
            else
            {
                AppendLog("Unable to move " + currentId.ToString(_hostGun.GameDefinition.IsTeamGame) +
                          " to Team " + requestedTeam + ".");
            }

            UpdateTeamDropTargets();
        }

        // ── Dark theme ────────────────────────────────────────────────────────
        private static readonly Color DkBg        = Color.FromArgb(14, 16, 28);
        private static readonly Color DkBgPanel   = Color.FromArgb(20, 22, 38);
        private static readonly Color DkBgControl = Color.FromArgb(28, 32, 52);
        private static readonly Color DkBgHover   = Color.FromArgb(36, 40, 64);
        private static readonly Color DkText      = Color.FromArgb(220, 225, 255);
        private static readonly Color DkTextMuted = Color.FromArgb(130, 135, 170);
        private static readonly Color DkBorder    = Color.FromArgb(44, 48, 76);
        private static readonly Color DkAccent    = Color.FromArgb(80, 150, 255);

        private void ApplyDarkTheme()
        {
            BackColor = DkBg;
            ForeColor = DkText;
            ApplyThemeRecursive(this);

            StyleButton(_startHostButton,      Color.FromArgb(25, 100, 48), Color.White);
            StyleButton(_startCountdownButton, Color.FromArgb(20, 80, 140), Color.White);
            StyleButton(_endGameButton,        Color.FromArgb(140, 30, 30), Color.White);
            StyleButton(_nextButton,           DkBgHover, DkTextMuted);
            StyleButton(_finishDebriefButton,  DkBgHover, DkTextMuted);
            StyleButton(_saveLogButton,        DkBgHover, DkTextMuted);
            StyleButton(_refreshPortsButton,   DkBgHover, DkTextMuted);
            StyleButton(_connectButton,        Color.FromArgb(20, 80, 140), Color.White);
            StyleButton(_buildPlayerSlotsButton, DkBgHover, DkTextMuted);

            _statusLabel.ForeColor     = DkAccent;
            _guidedJoinLabel.ForeColor = Color.FromArgb(75, 195, 115);
            _relayStatusLabel.ForeColor = Color.FromArgb(150, 60, 60);
            _telemetryLabel.ForeColor = Color.FromArgb(185, 195, 220);
            _telemetryLabel.BackColor = Color.FromArgb(18, 20, 34);
            // Medic Mode is not supported by LTAR hardware — dim it visually
            _medicModeCheck.ForeColor  = Color.FromArgb(70, 72, 100);
            _medicModeCheck.Enabled    = false; // starts red — not connected

            _plannedPlayersGrid.BackgroundColor      = DkBg;
            _plannedPlayersGrid.GridColor            = DkBorder;
            _plannedPlayersGrid.DefaultCellStyle.BackColor            = DkBgControl;
            _plannedPlayersGrid.DefaultCellStyle.ForeColor            = DkText;
            _plannedPlayersGrid.DefaultCellStyle.SelectionBackColor   = DkBgHover;
            _plannedPlayersGrid.DefaultCellStyle.SelectionForeColor   = Color.White;
            _plannedPlayersGrid.ColumnHeadersDefaultCellStyle.BackColor = DkBgPanel;
            _plannedPlayersGrid.ColumnHeadersDefaultCellStyle.ForeColor = DkTextMuted;
            _plannedPlayersGrid.ColumnHeadersBorderStyle   = DataGridViewHeaderBorderStyle.Single;
            _plannedPlayersGrid.EnableHeadersVisualStyles  = false;
            _plannedPlayersGrid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(22, 24, 42);

            _playersView.BackColor = DkBgControl;
            _playersView.ForeColor = DkText;
            _playersView.GridLines = false;
            _playersView.HeaderStyle = ColumnHeaderStyle.None; // hide system header — use custom below

            _teamDropPanel.BackColor = DkBg;
            foreach (var label in _teamDropTargets)
            {
                label.BackColor = DkBgControl;
                label.ForeColor = DkTextMuted;
            }

            _logBox.BackColor = DkBgPanel;
            _logBox.ForeColor = Color.FromArgb(155, 210, 155);
        }

        private static void StyleButton(Button btn, Color bg, Color fg)
        {
            btn.FlatStyle  = FlatStyle.Flat;
            btn.BackColor  = bg;
            btn.ForeColor  = fg;
            btn.FlatAppearance.BorderColor = Color.FromArgb(
                Math.Min(bg.R + 30, 255), Math.Min(bg.G + 30, 255), Math.Min(bg.B + 30, 255));
            btn.FlatAppearance.BorderSize  = 1;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(
                Math.Min(bg.R + 20, 255), Math.Min(bg.G + 20, 255), Math.Min(bg.B + 20, 255));
        }

        private static void ApplyThemeRecursive(Control parent)
        {
            foreach (Control c in parent.Controls)
            {
                switch (c)
                {
                    case TableLayoutPanel _:
                    case FlowLayoutPanel _:
                    case Panel _:
                    case SplitContainer sc:
                        c.BackColor = DkBg;
                        c.ForeColor = DkText;
                        if (c is SplitContainer s) {
                            s.Panel1.BackColor = DkBg;
                            s.Panel2.BackColor = DkBg;
                        }
                        break;
                    case ComboBox cb:
                        cb.BackColor = DkBgControl;
                        cb.ForeColor = DkText;
                        break;
                    case NumericUpDown nud:
                        nud.BackColor = DkBgControl;
                        nud.ForeColor = DkText;
                        break;
                    case CheckBox chk:
                        chk.BackColor = Color.Transparent;
                        chk.ForeColor = DkTextMuted;
                        break;
                    case Label lbl when lbl.ForeColor == SystemColors.ControlText:
                        lbl.ForeColor = DkTextMuted;
                        break;
                }
                if (c.HasChildren) ApplyThemeRecursive(c);
            }
        }

        private void BuildPlayerSlots()        {
            var count = Convert.ToInt32(_plannedPlayersBox.Value);
            _plannedPlayersGrid.Rows.Clear();
            for (int i = 1; i <= count; i++)
            {
                _plannedPlayersGrid.Rows.Add(i.ToString(), string.Empty, "Auto", string.Empty, string.Empty, string.Empty, false, false, string.Empty, string.Empty);
            }
            UpdatePlannedTeamColumnVisibility();
            UpdateGuidedJoinLabel();
        }

        private void LoadPlayerSettings()
        {
            try
            {
                if (!PlayerSettings.TryLoad(out int count, out var rows)) return;

                count = Math.Max(1, Math.Min(count, (int)_plannedPlayersBox.Maximum));
                _plannedPlayersBox.Value = count;
                BuildPlayerSlots();

                for (int i = 0; i < rows.Count && i < _plannedPlayersGrid.Rows.Count; i++)
                {
                    var row = rows[i];
                    var gr  = _plannedPlayersGrid.Rows[i];
                    if (!string.IsNullOrWhiteSpace(row.Name))         gr.Cells[1].Value = row.Name;
                    gr.Cells[2].Value = string.IsNullOrWhiteSpace(row.TeamPreference) ? "Auto" : row.TeamPreference;
                    if (!string.IsNullOrWhiteSpace(row.Hp))           gr.Cells[3].Value = row.Hp;
                    if (!string.IsNullOrWhiteSpace(row.Shields))      gr.Cells[4].Value = row.Shields;
                    if (!string.IsNullOrWhiteSpace(row.Reloads))      gr.Cells[5].Value = row.Reloads;
                    gr.Cells[6].Value = row.AutoReload;
                    gr.Cells[7].Value = row.FullAuto;
                    if (!string.IsNullOrWhiteSpace(row.Ammo))         gr.Cells[8].Value = row.Ammo;
                    if (!string.IsNullOrWhiteSpace(row.SleepTimeout)) gr.Cells[9].Value = row.SleepTimeout;
                }

                int named = rows.Count(r => !string.IsNullOrWhiteSpace(r.Name));
                UpdatePlannedTeamColumnVisibility();
                UpdateGuidedJoinLabel();
                AppendLog(string.Format("Loaded {0} player slot{1} from last session.",
                    count, count == 1 ? "" : "s") + (named > 0 ? string.Format(" ({0} named)", named) : ""));
            }
            catch { /* non-fatal */ }
        }

        private void CapturePlannedNamesFromGrid()
        {
            _plannedNames.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var value = row.Cells[1].Value == null ? string.Empty : row.Cells[1].Value.ToString();
                _plannedNames.Add(string.IsNullOrWhiteSpace(value) ? "Player " + (row.Index + 1) : value.Trim());
            }
        }

        private void CaptureTeamPreferencesFromGrid()
        {
            _plannedTeamPreferences.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[2].Value == null ? string.Empty : row.Cells[2].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    _plannedTeamPreferences.Add(null);
                }
                else if (raw.EndsWith("1"))
                {
                    _plannedTeamPreferences.Add(1);
                }
                else if (raw.EndsWith("2"))
                {
                    _plannedTeamPreferences.Add(2);
                }
                else if (raw.EndsWith("3"))
                {
                    _plannedTeamPreferences.Add(3);
                }
                else
                {
                    _plannedTeamPreferences.Add(null);
                }
            }
        }

        private void CaptureHpOverridesFromGrid()
        {
            _plannedHpOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[5].Value == null ? string.Empty : row.Cells[5].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    _plannedHpOverrides.Add(null);
                else if (int.TryParse(raw, out int hp) && hp >= 1 && hp <= 254)
                    _plannedHpOverrides.Add(hp);
                else
                    _plannedHpOverrides.Add(null);
            }
        }

        private void CaptureShieldsOverridesFromGrid()
        {
            _plannedShieldsOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[5].Value == null ? string.Empty : row.Cells[5].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    _plannedShieldsOverrides.Add(null);
                else if (int.TryParse(raw, out int s) && s >= 0 && s <= 254)
                    _plannedShieldsOverrides.Add(s);
                else
                    _plannedShieldsOverrides.Add(null);
            }
        }

        private void CaptureReloadsOverridesFromGrid()
        {
            _plannedReloadsOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[5].Value == null ? string.Empty : row.Cells[5].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    _plannedReloadsOverrides.Add(null);
                else if (int.TryParse(raw, out int r) && r >= 0 && r <= 99)
                    _plannedReloadsOverrides.Add(r);
                else
                    _plannedReloadsOverrides.Add(null);
            }
        }

        private void CaptureAutoReloadOverridesFromGrid()
        {
            _plannedAutoReloadOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var val = row.Cells[5].Value;
                _plannedAutoReloadOverrides.Add(val is bool b ? b : false);
            }
        }

        private void CaptureFullAutoOverridesFromGrid()
        {
            _plannedFullAutoOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var val = row.Cells[6].Value;
                _plannedFullAutoOverrides.Add(val is bool b ? b : false);
            }
        }

        private void CaptureStartingAmmoOverridesFromGrid()
        {
            _plannedStartingAmmoOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[9].Value == null ? string.Empty : row.Cells[9].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    _plannedStartingAmmoOverrides.Add(null);
                else if (int.TryParse(raw, out int a) && a >= 0 && a <= 99)
                    _plannedStartingAmmoOverrides.Add(a);
                else
                    _plannedStartingAmmoOverrides.Add(null);
            }
        }

        private void CaptureSleepTimeoutOverridesFromGrid()
        {
            _plannedSleepTimeoutOverrides.Clear();
            foreach (DataGridViewRow row in _plannedPlayersGrid.Rows)
            {
                var raw = row.Cells[9].Value == null ? string.Empty : row.Cells[9].Value.ToString().Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    _plannedSleepTimeoutOverrides.Add(null);
                else if (int.TryParse(raw, out int s) && s >= 0 && s <= 99)
                    _plannedSleepTimeoutOverrides.Add(s);
                else
                    _plannedSleepTimeoutOverrides.Add(null);
            }
        }

        private void PlannedPlayersGrid_CellValidating(object sender, DataGridViewCellValidatingEventArgs e)
        {
            var raw = e.FormattedValue == null ? string.Empty : e.FormattedValue.ToString().Trim();
            if (string.IsNullOrWhiteSpace(raw)) return;

            if (e.ColumnIndex == 3) // HP
            {
                if (!int.TryParse(raw, out int hp) || hp < 1 || hp > 254)
                {
                    e.Cancel = true;
                    MessageBox.Show("HP must be 1–254, or blank to use the global value.",
                        "Invalid HP", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (e.ColumnIndex == 4) // Shields
            {
                if (!int.TryParse(raw, out int s) || s < 0 || s > 254)
                {
                    e.Cancel = true;
                    MessageBox.Show("Shields must be 0–254 seconds, or blank to use the global value.",
                        "Invalid Shields", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (e.ColumnIndex == 5) // Reloads
            {
                if (!int.TryParse(raw, out int r) || r < 0 || r > 99)
                {
                    e.Cancel = true;
                    MessageBox.Show("Reloads must be 0–99 clips (0 = unlimited), or blank to use the global value.",
                        "Invalid Reloads", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (e.ColumnIndex == 8) // Starting Ammo
            {
                if (!int.TryParse(raw, out int a) || a < 0 || a > 99)
                {
                    e.Cancel = true;
                    MessageBox.Show("Starting ammo must be 0–99 rounds (0 = unlimited), or blank to use the global value.",
                        "Invalid Ammo", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else if (e.ColumnIndex == 9) // Sleep Timeout
            {
                if (!int.TryParse(raw, out int s) || s < 0 || s > 99)
                {
                    e.Cancel = true;
                    MessageBox.Show("Sleep timeout must be 0–99 minutes (0 = disabled), or blank to use the global value.",
                        "Invalid Sleep Timeout", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void UpdateGuidedJoinLabel()
        {
            if (_hostGun == null)
            {
                if (_plannedPlayersGrid.Rows.Count == 0)
                {
                    _guidedJoinLabel.Text = "Guided Join: create player slots first.";
                }
                else
                {
                    var nextName = GetPlannedName(0);
                    _guidedJoinLabel.Text = "Guided Join: when hosting starts, turn on " + nextName + "'s gun first.";
                }
                return;
            }

            var nextSlot = _hostGun.NextPlannedSlotIndex;
            if (nextSlot < _plannedNames.Count)
            {
                var nextName = _plannedNames[nextSlot];
                _guidedJoinLabel.Text = "Guided Join: waiting for " + nextName + " to connect next.";
            }
            else if (_plannedNames.Count > 0)
            {
                _guidedJoinLabel.Text = "Guided Join: all planned players connected. Start the countdown when ready.";
            }
            else
            {
                _guidedJoinLabel.Text = "Guided Join: players may join now.";
            }
        }

        private string GetPlannedName(int index)
        {
            if (index < 0 || index >= _plannedPlayersGrid.Rows.Count) return "the next player";
            var value = _plannedPlayersGrid.Rows[index].Cells[1].Value == null ? string.Empty : _plannedPlayersGrid.Rows[index].Cells[1].Value.ToString();
            return string.IsNullOrWhiteSpace(value) ? "Player " + (index + 1) : value.Trim();
        }

        private static Label MakeLabel(string text, int leftPad = 0)
        {
            return new Label
            {
                Text      = text,
                AutoSize  = true,
                Padding   = new Padding(leftPad, 7, 4, 0),
                ForeColor = Color.FromArgb(150, 155, 190),
            };
        }

        private static void AddCell(TableLayoutPanel panel, Control control, int column, int row, int columnSpan)
        {
            panel.Controls.Add(control, column, row);
            panel.SetColumnSpan(control, columnSpan);
            control.Dock = DockStyle.Fill;
        }

        private static void AddCell(TableLayoutPanel panel, Control control, int column, int row)
        {
            AddCell(panel, control, column, row, 1);
        }

        private void RefreshPorts()
        {
            var ports    = LazerTagSerial.GetSerialPorts();
            var selected = _comPortsCombo.Text;
            _comPortsCombo.Items.Clear();

            // ESP32 AP mode fixed address — always first, always correct
            _comPortsCombo.Items.Add(Esp32ApAddress);

            // Serial ports (fallback if using USB cable)
            foreach (var port in ports.OrderBy(p => p))
                _comPortsCombo.Items.Add(port);

            // Restore previous selection if the user had changed it
            if (!string.IsNullOrWhiteSpace(selected))
            {
                var idx = _comPortsCombo.Items.IndexOf(selected);
                if (idx >= 0) _comPortsCombo.SelectedIndex = idx;
                else          _comPortsCombo.Text = selected; // custom value typed by user
            }
            else
            {
                // Default to ESP32 AP address on first launch
                _comPortsCombo.SelectedIndex = 0;
            }
        }

        private void RefreshGameTypes()
        {
            _gameTypeCombo.Items.Clear();
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.CustomLazerTag, "Custom Lazer Tag (Solo)"));
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.CustomLazerTagTwoTeams, "Custom Lazer Tag (2 Teams)"));
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.CustomLazerTagThreeTeams, "Custom Lazer Tag (3 Teams)"));
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.RespawnTwoTeams, "Respawn (2 Teams)"));
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.RespawnThreeTeams, "Respawn (3 Teams)"));
            _gameTypeCombo.Items.Add(new GameTypeChoice(GameType.SurvivalTwoTeams, "Survival (2 Teams)"));
            _gameTypeCombo.SelectedIndex = 1;
        }

        private void GameTypeCombo_SelectedIndexChanged(object sender, EventArgs e)
        {
            var choice = _gameTypeCombo.SelectedItem as GameTypeChoice;
            if (choice == null) return;

            var info = GameTypes.GetInfo(choice.Type);
            _minutesBox.Value = ClampToRange(info.DefaultGameTimeMinutes, _minutesBox.Minimum, _minutesBox.Maximum);
            _globalTagsBox.Value    = ClampToRange(info.DefaultTags, _globalTagsBox.Minimum, _globalTagsBox.Maximum);
            _globalShieldsBox.Value = ClampToRange(info.DefaultShields, _globalShieldsBox.Minimum, _globalShieldsBox.Maximum);
            _globalReloadsBox.Value = ClampToRange(info.DefaultReloads == 0xff ? 0 : info.DefaultReloads, _globalReloadsBox.Minimum, _globalReloadsBox.Maximum);
            _globalMegaBox.Value    = ClampToRange(info.DefaultMega == 0xff ? 0 : info.DefaultMega, _globalMegaBox.Minimum, _globalMegaBox.Maximum);
            _slowTagsCheck.Checked = info.DefaultSlowTags;
            _teamTagsCheck.Checked = false;   // always off by default
            _medicModeCheck.Checked = false;  // always off by default
            UpdateTeamDropTargets();
        }

        private static decimal ClampToRange(int value, decimal min, decimal max)
        {
            var dec = Convert.ToDecimal(value);
            if (dec < min) return min;
            if (dec > max) return max;
            return dec;
        }

        private string BuildGameSetupSummary(GameDefinition definition, GameTypeChoice choice, string port)
        {
            var lines = new List<string>();
            lines.Add("Connection: " + port);
            lines.Add("Game Type: " + (choice?.Text ?? "Unknown"));
            lines.Add("Team Game: " + (definition.IsTeamGame ? "Yes" : "No"));
            if (definition.IsTeamGame)
                lines.Add("Teams: " + _hostGunTeamCountPreview(choice.Type).ToString());

            lines.Add("Minutes: " + _minutesBox.Value);
            lines.Add("Countdown Seconds: " + _countdownBox.Value);
            lines.Add("HP (global): " + _globalTagsBox.Value);
            lines.Add("Shields (global): " + _globalShieldsBox.Value);
            lines.Add("Reloads (global): " + _globalReloadsBox.Value);
            lines.Add("Auto Reload (global): " + (_autoReloadCheck.Checked ? "On" : "Off"));
            lines.Add("Full Auto (global): " + (_fullAutoCheck.Checked ? "On" : "Off"));
            lines.Add("Starting Ammo (global): " + _globalStartingAmmoBox.Value);
            lines.Add("Sleep Timeout (global): " + _globalSleepTimeoutBox.Value);
            lines.Add("Medic Mode: " + (_medicModeCheck.Checked ? "On" : "Off"));
            lines.Add("Team Tags: " + (_teamTagsCheck.Checked ? "On" : "Off"));
            lines.Add("LTAR Enhanced: " + (definition.UseLtarEnhancedProtocol ? "On" : "Off"));

            var playerLines = new List<string>();
            for (int i = 0; i < _plannedPlayersGrid.Rows.Count; i++)
            {
                var row = _plannedPlayersGrid.Rows[i];
                var name = row.Cells[1].Value?.ToString() ?? "";
                var teamPref = row.Cells[2].Value?.ToString() ?? "Auto";
                var hp = row.Cells[3].Value?.ToString() ?? "";
                var shields = row.Cells[4].Value?.ToString() ?? "";
                var reloads = row.Cells[5].Value?.ToString() ?? "";
                var autoReload = row.Cells[6].Value is bool ab && ab;
                var fullAuto = row.Cells[7].Value is bool fb && fb;
                var ammo = row.Cells[8].Value?.ToString() ?? "";
                var sleep = row.Cells[9].Value?.ToString() ?? "";

                if (string.IsNullOrWhiteSpace(name) &&
                    string.Equals(teamPref, "Auto", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(hp) &&
                    string.IsNullOrWhiteSpace(shields) &&
                    string.IsNullOrWhiteSpace(reloads) &&
                    !autoReload &&
                    !fullAuto &&
                    string.IsNullOrWhiteSpace(ammo) &&
                    string.IsNullOrWhiteSpace(sleep))
                {
                    continue;
                }

                var parts = new List<string>();
                parts.Add(string.IsNullOrWhiteSpace(name) ? ("Player " + (i + 1)) : name);

                if (definition.IsTeamGame && !string.Equals(teamPref, "Auto", StringComparison.OrdinalIgnoreCase))
                    parts.Add(teamPref);

                if (!string.IsNullOrWhiteSpace(hp))
                    parts.Add("HP " + hp);
                if (!string.IsNullOrWhiteSpace(shields))
                    parts.Add("Shields " + shields);
                if (!string.IsNullOrWhiteSpace(reloads))
                    parts.Add("Reloads " + reloads);
                if (autoReload)
                    parts.Add("AutoReload On");
                if (fullAuto)
                    parts.Add("FullAuto On");
                if (!string.IsNullOrWhiteSpace(ammo))
                    parts.Add("Ammo " + ammo);
                if (!string.IsNullOrWhiteSpace(sleep))
                    parts.Add("Sleep " + sleep);

                playerLines.Add("• " + string.Join("  |  ", parts));
            }

            if (playerLines.Count > 0)
            {
                lines.Add("");
                lines.Add("Planned Players:");
                lines.AddRange(playerLines);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private int _hostGunTeamCountPreview(GameType gameType)
        {
            return GameTypes.GetInfo(gameType).TeamCount;
        }

        private Control CreateSummarySection(string title, IEnumerable<Tuple<string, string>> rows)
        {
            var section = new Panel
            {
                Width = 820,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 0, 0, 10),
                Margin = new Padding(0, 0, 0, 12),
                BackColor = Color.Transparent
            };

            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = DkBgPanel,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220f));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            int rowIndex = 0;
            foreach (var row in rows)
            {
                body.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var keyLabel = new Label
                {
                    AutoSize = true,
                    Padding = new Padding(10, 9, 10, 9),
                    Margin = new Padding(0),
                    Text = row.Item1,
                    Font = new Font(Font.FontFamily, 9.5f, FontStyle.Bold),
                    ForeColor = DkText
                };

                var valueLabel = new Label
                {
                    AutoSize = true,
                    Padding = new Padding(10, 9, 10, 9),
                    Margin = new Padding(0),
                    MaximumSize = new Size(560, 0),
                    Text = string.IsNullOrWhiteSpace(row.Item2) ? "-" : row.Item2,
                    ForeColor = DkText
                };

                body.Controls.Add(keyLabel, 0, rowIndex);
                body.Controls.Add(valueLabel, 1, rowIndex);
                rowIndex++;
            }

            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = title,
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
                ForeColor = DkAccent,
                Padding = new Padding(2, 0, 0, 4)
            };

            section.Controls.Add(body);
            section.Controls.Add(titleLabel);
            return section;
        }

        private Control CreatePlannedPlayersSection(GameDefinition definition)
        {
            var section = new Panel
            {
                Width = 820,
                Height = 260,
                Padding = new Padding(0, 0, 0, 10),
                Margin = new Padding(0, 0, 0, 12),
                BackColor = Color.Transparent
            };

            var titleLabel = new Label
            {
                Dock = DockStyle.Top,
                Height = 28,
                Text = "Planned Players",
                Font = new Font(Font.FontFamily, 11f, FontStyle.Bold),
                ForeColor = DkAccent,
                Padding = new Padding(2, 0, 0, 4)
            };

            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                MultiSelect = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                BackgroundColor = DkBgPanel,
                GridColor = DkBorder,
                BorderStyle = BorderStyle.FixedSingle,
                EnableHeadersVisualStyles = false
            };

            grid.ColumnHeadersDefaultCellStyle.BackColor = DkBgControl;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = DkText;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font(Font.FontFamily, 9f, FontStyle.Bold);
            grid.DefaultCellStyle.BackColor = DkBgPanel;
            grid.DefaultCellStyle.ForeColor = DkText;
            grid.DefaultCellStyle.SelectionBackColor = DkBgHover;
            grid.DefaultCellStyle.SelectionForeColor = DkText;

            grid.Columns.Add("Player", "Player");
            if (definition.IsTeamGame)
                grid.Columns.Add("Team", "Team");
            grid.Columns.Add("HP", "HP");
            grid.Columns.Add("Shields", "Shields");
            grid.Columns.Add("Reloads", "Reloads");
            grid.Columns.Add("AutoReload", "Auto Reload");
            grid.Columns.Add("FullAuto", "Full Auto");
            grid.Columns.Add("Ammo", "Ammo");
            grid.Columns.Add("Sleep", "Sleep");

            for (int i = 0; i < _plannedPlayersGrid.Rows.Count; i++)
            {
                var row = _plannedPlayersGrid.Rows[i];
                var cells = new List<object>();
                cells.Add(FormatSummaryCell(row.Cells[1]));

                if (definition.IsTeamGame)
                    cells.Add(FormatSummaryCell(row.Cells[2]));

                cells.Add(FormatSummaryCell(row.Cells[3]));
                cells.Add(FormatSummaryCell(row.Cells[4]));
                cells.Add(FormatSummaryCell(row.Cells[5]));
                cells.Add(FormatSummaryCell(row.Cells[6]));
                cells.Add(FormatSummaryCell(row.Cells[7]));
                cells.Add(FormatSummaryCell(row.Cells[8]));
                cells.Add(FormatSummaryCell(row.Cells[9]));

                grid.Rows.Add(cells.ToArray());
            }

            section.Controls.Add(grid);
            section.Controls.Add(titleLabel);
            return section;
        }

        private string FormatSummaryCell(DataGridViewCell cell)
        {
            if (cell == null || cell.Value == null)
                return "-";

            var text = cell.Value.ToString();
            return string.IsNullOrWhiteSpace(text) ? "-" : text;
        }

        private bool ConfirmGameSetup(GameDefinition definition, GameTypeChoice choice, string port)
        {
            using (var dlg = new Form())
            {
                dlg.Text = "Review Game Setup";
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.Size = new Size(900, 700);
                dlg.MinimumSize = new Size(820, 620);
                dlg.FormBorderStyle = FormBorderStyle.SizableToolWindow;
                dlg.BackColor = DkBg;
                dlg.ForeColor = DkText;

                var header = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 74,
                    Padding = new Padding(16, 14, 16, 10),
                    BackColor = DkBgControl
                };

                var titleLabel = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 28,
                    Text = "Game Setup Summary",
                    Font = new Font(Font.FontFamily, 15f, FontStyle.Bold),
                    ForeColor = DkText
                };

                var subtitleLabel = new Label
                {
                    Dock = DockStyle.Top,
                    Height = 22,
                    Text = "Review teams, timing, options, and player overrides before hosting starts.",
                    ForeColor = DkTextMuted
                };

                header.Controls.Add(subtitleLabel);
                header.Controls.Add(titleLabel);

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Bottom,
                    Height = 56,
                    FlowDirection = FlowDirection.RightToLeft,
                    Padding = new Padding(10),
                    BackColor = DkBgControl
                };

                var startButton = new Button
                {
                    Text = "Start Hosting",
                    DialogResult = DialogResult.OK,
                    Width = 130,
                    Height = 32
                };

                var cancelButton = new Button
                {
                    Text = "Cancel",
                    DialogResult = DialogResult.Cancel,
                    Width = 110,
                    Height = 32
                };

                buttons.Controls.Add(startButton);
                buttons.Controls.Add(cancelButton);

                var scrollPanel = new Panel
                {
                    Dock = DockStyle.Fill,
                    AutoScroll = true,
                    Padding = new Padding(14),
                    BackColor = DkBg
                };

                var content = new FlowLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    FlowDirection = FlowDirection.TopDown,
                    WrapContents = false,
                    Width = 840
                };

                content.Controls.Add(CreateSummarySection("Connection", new[]
                {
                    Tuple.Create("Relay", port),
                    Tuple.Create("Game Type", choice != null ? choice.Text : "Unknown"),
                    Tuple.Create("Teams", definition.IsTeamGame ? _hostGunTeamCountPreview(choice.Type).ToString() : "Free-for-all")
                }));

                content.Controls.Add(CreateSummarySection("Timing", new[]
                {
                    Tuple.Create("Minutes", _minutesBox.Value.ToString()),
                    Tuple.Create("Countdown", _countdownBox.Value + " sec")
                }));

                content.Controls.Add(CreateSummarySection("Global Defaults", new[]
                {
                    Tuple.Create("HP", _globalTagsBox.Value.ToString()),
                    Tuple.Create("Shields", _globalShieldsBox.Value.ToString()),
                    Tuple.Create("Reloads", _globalReloadsBox.Value.ToString()),
                    Tuple.Create("Starting Ammo", _globalStartingAmmoBox.Value.ToString()),
                    Tuple.Create("Sleep Timeout", _globalSleepTimeoutBox.Value + " sec")
                }));

                content.Controls.Add(CreateSummarySection("Gameplay Options", new[]
                {
                    Tuple.Create("Auto Reload", _autoReloadCheck.Checked ? "On" : "Off"),
                    Tuple.Create("Full Auto", _fullAutoCheck.Checked ? "On" : "Off"),
                    Tuple.Create("Slow Tags", _slowTagsCheck.Checked ? "On" : "Off"),
                    Tuple.Create("Team Tags", _teamTagsCheck.Checked ? "On" : "Off"),
                    Tuple.Create("Medic Mode", _medicModeCheck.Checked ? "On" : "Off"),
                    Tuple.Create("Enhanced LTAR", definition.UseLtarEnhancedProtocol ? "On" : "Off"),
                    Tuple.Create("Disable IFF", _ltarDisableIffCheck.Checked ? "Yes" : "No"),
                    Tuple.Create("Disable Hit Register", _ltarDisableHitRegisterCheck.Checked ? "Yes" : "No")
                }));

                content.Controls.Add(CreatePlannedPlayersSection(definition));

                scrollPanel.Controls.Add(content);
                dlg.Controls.Add(scrollPanel);
                dlg.Controls.Add(buttons);
                dlg.Controls.Add(header);
                dlg.AcceptButton = startButton;
                dlg.CancelButton = cancelButton;

                return dlg.ShowDialog(this) == DialogResult.OK;
            }
        }

        private void StartHostButton_Click(object sender, EventArgs e)
        {
            try
            {
                if (_hostGun != null)
                {
                    AppendLog("A host is already running. End the current game first.");
                    return;
                }

                var port = _comPortsCombo.Text?.Trim();
                if (string.IsNullOrWhiteSpace(port))
                {
                    MessageBox.Show(this, "No connection selected. Choose the ESP32 WiFi address or a COM port.", "No Connection", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var choice = _gameTypeCombo.SelectedItem as GameTypeChoice;
                if (choice == null)
                {
                    MessageBox.Show(this, "Choose a game type first.", "No Game Type", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                CapturePlannedNamesFromGrid();
                CaptureTeamPreferencesFromGrid();
                CaptureHpOverridesFromGrid();
                CaptureShieldsOverridesFromGrid();
                CaptureReloadsOverridesFromGrid();
                CaptureAutoReloadOverridesFromGrid();
                CaptureFullAutoOverridesFromGrid();
                CaptureStartingAmmoOverridesFromGrid();
                CaptureSleepTimeoutOverridesFromGrid();
                UpdateGuidedJoinLabel();

                // Auto-save previous log then clear for this game
                if (_logBox.TextLength > 10)
                    SaveLogToFile();
                _logBox.Clear();
                _sessionGameCount++;
                _telemetryGamesStartedCount++;
                AppendLog(string.Format("═══ Game {0}  —  {1}  —  {2} ═══",
                    _sessionGameCount,
                    DateTime.Now.ToString("HH:mm"),
                    (choice?.Text ?? "Unknown")));

                // Persist full player grid so it reloads next session
                var rowsToSave = new List<PlayerSettings.PlayerRow>();
                foreach (DataGridViewRow gr in _plannedPlayersGrid.Rows)
                {
                    rowsToSave.Add(new PlayerSettings.PlayerRow
                    {
                        Name           = gr.Cells[1].Value?.ToString() ?? "",
                        TeamPreference = gr.Cells[2].Value?.ToString() ?? "Auto",
                        Hp             = gr.Cells[3].Value?.ToString() ?? "",
                        Shields        = gr.Cells[4].Value?.ToString() ?? "",
                        Reloads        = gr.Cells[5].Value?.ToString() ?? "",
                        AutoReload     = gr.Cells[6].Value is bool ab && ab,
                        FullAuto       = gr.Cells[7].Value is bool fb && fb,
                        Ammo           = gr.Cells[8].Value?.ToString() ?? "",
                        SleepTimeout   = gr.Cells[9].Value?.ToString() ?? "",
                    });
                }
                PlayerSettings.Save(_plannedPlayersGrid.Rows.Count, rowsToSave);

                var definition = BuildGameDefinition(choice.Type);

                if (!ConfirmGameSetup(definition, choice, port))
                {
                    AppendLog("Start hosting canceled from game setup summary.");
                    return;
                }

                // Close any pre-connect test connection before HostGun opens its own
                DisconnectPreConnect();

                _hostGun = new HostGun(port, RecordDiagnostic);
                _hostGun.PlayerListChanged   += HostGun_PlayerListChanged;
                _hostGun.HostingStateChanged += HostGun_HostingStateChanged;
                _hostGun.Protocol.IoError    += Protocol_IoError;
                _hostGun.SetPlannedPlayerNames(_plannedNames);
                _hostGun.SetPlannedTeamPreferences(_plannedTeamPreferences);
                _hostGun.SetPlannedTagsOverrides(_plannedHpOverrides);
                _hostGun.SetPlannedShieldsOverrides(_plannedShieldsOverrides);
                _hostGun.SetPlannedReloadsOverrides(_plannedReloadsOverrides);
                _hostGun.SetPlannedAutoReloadOverrides(_plannedAutoReloadOverrides);
                _hostGun.SetPlannedFullAutoOverrides(_plannedFullAutoOverrides);
                _hostGun.SetPlannedStartingAmmoOverrides(_plannedStartingAmmoOverrides);
                _hostGun.SetPlannedSleepTimeoutOverrides(_plannedSleepTimeoutOverrides);
                _hostGun.StartServer(definition);

                _updateTimer.Start();
                UpdateStatus();
                AppendLog("Host started on " + port + ". Waiting for taggers to join.");
                if (definition.UseLtarEnhancedProtocol)
                {
                    AppendLog("LTAR enhanced hosting enabled. Standard beacons=" + definition.UseStandardLtarBeacons + ", Disable IFF=" + definition.DisableLtarIffProximityBeacons + ", Disable Hit Register=" + definition.DisableLtarTagReceivedBeacons);
                }
                UpdateGuidedJoinLabel();

                // Open fullscreen game window — lobby, countdown, playing, scores
                var gameWindow = new GameWindow(_hostGun, _plannedNames.ToList(), _plannedTeamPreferences.ToList());
                gameWindow.FormClosed += (ws, we) =>
                {
                    // When game window closes (user pressed Close on scores),
                    // clean up so the setup screen is ready for a new game.
                    if (_hostGun != null) CleanupHost();
                };
                gameWindow.Show(this);
            }
            catch (Exception ex)
            {
                CleanupHost();
                MessageBox.Show(this, ex.Message, "Could not start host", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private GameDefinition BuildGameDefinition(GameType gameType)
        {
            var info = GameTypes.GetInfo(gameType);
            var definition = new GameDefinition();
            definition.GameType = gameType;
            definition.GameId = new RandomId();
            definition.GameTimeMinutes = Convert.ToInt32(_minutesBox.Value);
            definition.PlannedPlayerCount = _plannedNames.Count;
            definition.Tags = Convert.ToInt32(_globalTagsBox.Value);
            definition.Shields = Convert.ToInt32(_globalShieldsBox.Value);
            var reloadsVal = Convert.ToInt32(_globalReloadsBox.Value);
            definition.Reloads = reloadsVal == 0 ? 0xff : reloadsVal; // 0 in UI = unlimited = 0xFF in protocol
            definition.Mega = Convert.ToInt32(_globalMegaBox.Value);
            definition.TeamTags = _teamTagsCheck.Checked;
            definition.MedicMode = _medicModeCheck.Checked;
            definition.SlowTags = _slowTagsCheck.Checked;
            definition.AutomaticReload = _autoReloadCheck.Checked;
            definition.AllowFullAuto = _fullAutoCheck.Checked;
            definition.ExtendedTagging = false;
            definition.CountdownTimeSeconds = Convert.ToInt32(_countdownBox.Value);
            definition.StartingAmmo = Convert.ToInt32(_globalStartingAmmoBox.Value); // 0 = unlimited (0xFF sent)
            definition.SleepTimeoutMinutes = Convert.ToInt32(_globalSleepTimeoutBox.Value); // 0 = disabled
            definition.ResendCountdownTimeSeconds = 10;
            definition.UseLtarEnhancedProtocol = _ltarDisableIffCheck.Checked || _ltarDisableHitRegisterCheck.Checked
                || _autoReloadCheck.Checked || _fullAutoCheck.Checked
                || definition.Tags > BinaryCodedDecimal.MaxDecimalValue || definition.Shields > BinaryCodedDecimal.MaxDecimalValue
                || _plannedHpOverrides.Any(hp => hp > BinaryCodedDecimal.MaxDecimalValue)
                || _plannedShieldsOverrides.Any(shields => shields > BinaryCodedDecimal.MaxDecimalValue);
            definition.DisableLtarIffProximityBeacons = _ltarDisableIffCheck.Checked;
            definition.DisableLtarTagReceivedBeacons = _ltarDisableHitRegisterCheck.Checked;
            // Enhanced LTAR options control beacon behavior themselves; do not force standard beacons on.
            definition.UseStandardLtarBeacons = false;
            return definition;
        }

        private void StartCountdownButton_Click(object sender, EventArgs e)
        {
            if (_hostGun == null)
            {
                MessageBox.Show(this, "Start hosting first.", "Host Not Running", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var nextSlot = _hostGun.NextPlannedSlotIndex;
            if (nextSlot < _plannedNames.Count)
            {
                AppendLog("Countdown not started yet. Still waiting for " + _plannedNames[nextSlot] + " to connect.");
                return;
            }

            var started = _hostGun.StartCountdown();
            if (!started)
            {
                AppendLog("Countdown was not started. You usually need at least two players joined first.");
            }
        }

        private void EndGameButton_Click(object sender, EventArgs e)
        {
            if (_hostGun == null) return;
            AppendLog("End game pressed while state was: " + _hostGun.HostingState);
            _hostGun.EndGame();
            AppendLog("End game requested.");
            SaveLogToFile();
        }

        private void NextButton_Click(object sender, EventArgs e)
        {
            if (_hostGun == null) return;
            _hostGun.Next();
            AppendLog("Next step requested.");
        }

        private void SaveLogButton_Click(object sender, EventArgs e)
        {
            SaveLogToFile();
        }

        private void ConnectButton_Click(object sender, EventArgs e)
        {
            var port = _comPortsCombo.Text?.Trim();
            if (string.IsNullOrWhiteSpace(port))
            {
                AppendLog("No connection address selected.");
                return;
            }

            // Disconnect any existing pre-connect
            if (_preConnectSerial != null)
            {
                _preConnectSerial.IoError -= PreConnectSerial_IoError;
                _preConnectSerial.Disconnect();
                _preConnectSerial.Dispose();
                _preConnectSerial = null;
            }

            // Don't attempt if a game is already hosting (it has its own connection)
            if (_hostGun != null)
            {
                AppendLog("Already hosting — relay connection is managed by the active game.");
                return;
            }

            _connectButton.Enabled = false;
            _connectButton.Text    = "Connecting…";
            _telemetryRelayConnectAttempts++;
            SetRelayStatus(false);
            UpdateTelemetry();
            AppendLog("Connecting to relay at " + port + "…");

            // Connect on a background thread so the UI stays responsive during the 3s timeout
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var serial = new LazerTagSerial(RecordDiagnostic);
                serial.IoError += PreConnectSerial_IoError;
                bool ok = serial.Connect(port);

                BeginInvoke(new Action(() =>
                {
                    _connectButton.Enabled = true;
                    _connectButton.Text    = "Connect";

                    if (ok)
                    {
                        _preConnectSerial = serial;
                        _telemetryRelayConnectedCount++;
                        SetRelayStatus(true);
                        UpdateTelemetry();
                        AppendLog("Relay connected. Ready to start hosting.");
                    }
                    else
                    {
                        serial.IoError -= PreConnectSerial_IoError;
                        serial.Dispose();
                        _telemetryRelayConnectFailureCount++;
                        SetRelayStatus(false);
                        UpdateTelemetry();
                        AppendLog("Could not connect to relay. Check the ESP32 is powered and you are on the LazerTag WiFi network.");
                    }
                }));
            });
        }

        private void PreConnectSerial_IoError(object sender, LazerTagSerial.IoErrorEventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<object, LazerTagSerial.IoErrorEventArgs>(PreConnectSerial_IoError), sender, e); return; }
            _telemetryRelayIoErrorCount++;
            SetRelayStatus(false);
            UpdateTelemetry();
            AppendLog("Relay connection lost.");
        }

        private void LogBox_MouseDown(object sender, MouseEventArgs e)
        {
            _pauseLogAutoScroll = true;
        }

        private void LogBox_Leave(object sender, EventArgs e)
        {
            _pauseLogAutoScroll = false;
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            if (_hostGun == null) return;
            try
            {
                _hostGun.Update();
                UpdateStatus();
                UpdateTelemetry();
            }
            catch (Exception ex)
            {
                AppendLog("Update error: " + ex.Message);
            }
        }

        private void HostGun_PlayerListChanged(object sender, HostGun.PlayerListChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, HostGun.PlayerListChangedEventArgs>(HostGun_PlayerListChanged), sender, e);
                return;
            }

            RefreshPlayersView();
            UpdateTelemetry();
        }

        private void RefreshPlayersView()
        {
            if (_hostGun == null) return;

            _playersView.BeginUpdate();
            _playersView.Items.Clear();
            foreach (var player in _hostGun.Players.OrderBy(p => p.TeamPlayerId.PlayerNumber))
            {
                var item = new ListViewItem(player.TeamPlayerId.ToString(_hostGun.GameDefinition.IsTeamGame));
                item.SubItems.Add(player.TeamPlayerId.TeamNumber.ToString());
                item.SubItems.Add(player.TeamPlayerId.TeamPlayerNumber.ToString());
                item.SubItems.Add(string.IsNullOrWhiteSpace(player.Name) ? "" : player.Name);
                item.SubItems.Add(player.Confirmed ? "Yes" : "No");
                var hitsScored = 0;
                for (var i = 0; i < player.TaggedPlayerCounts.Length; i++)
                    hitsScored += player.TaggedPlayerCounts[i];
                item.SubItems.Add(hitsScored > 0 ? hitsScored.ToString() : "");
                item.SubItems.Add(player.TagsTaken > 0 ? player.TagsTaken.ToString() : "");
                item.SubItems.Add(player.TaggerId.ToString());
                item.Tag = player.TeamPlayerId;
                _playersView.Items.Add(item);
            }
            _playersView.EndUpdate();
            UpdateGuidedJoinLabel();
            UpdateTeamDropTargets();
        }

        private void HostGun_HostingStateChanged(object sender, HostGun.HostingStateChangedEventArgs e)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, HostGun.HostingStateChangedEventArgs>(HostGun_HostingStateChanged), sender, e);
                return;
            }

            if (e.State == HostGun.HostingStates.GameOver) _telemetryGamesCompletedCount++;
            UpdateStatus();
            RefreshPlayersView();
            UpdateTelemetry();
            AppendLog("State changed: " + e.PreviousState + " -> " + e.State);
        }

        private void RecordDiagnostic(HostDiagnostic diagnostic)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<HostDiagnostic>(RecordDiagnostic), diagnostic);
                return;
            }

            switch (diagnostic)
            {
                case HostDiagnostic.RelayConnectAttempt:
                    _telemetryRelayConnectAttempts++;
                    break;
                case HostDiagnostic.RelayConnected:
                    _telemetryRelayConnectedCount++;
                    SetRelayStatus(true);
                    break;
                case HostDiagnostic.RelayConnectTimeout:
                case HostDiagnostic.RelayHandshakeTimeout:
                case HostDiagnostic.RelayConnectionRefused:
                case HostDiagnostic.RelayHandshakeFailed:
                case HostDiagnostic.RelayConnectFailed:
                    _telemetryRelayConnectFailureCount++;
                    if (diagnostic == HostDiagnostic.RelayConnectTimeout || diagnostic == HostDiagnostic.RelayHandshakeTimeout)
                        _telemetryRelayTimeoutCount++;
                    SetRelayStatus(false);
                    break;
                case HostDiagnostic.UnexpectedPacket:
                    _telemetryUnexpectedPacketCount++;
                    break;
                case HostDiagnostic.StateTimeout:
                    _telemetryStateTimeoutCount++;
                    break;
            }
            UpdateTelemetry();
        }

        private void Log_EntryAdded(object sender, Log.EntryAddedEventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<object, Log.EntryAddedEventArgs>(Log_EntryAdded), sender, e);
                return;
            }
            AppendLog(string.Format("[{0:HH:mm:ss}] {1}: {2}", e.Entry.Time, e.Entry.Severity, e.Entry.Message));
        }

        private void AppendLog(string message)
        {
            if (_logBox.TextLength > 0)
            {
                _logBox.AppendText(Environment.NewLine);
            }
            _logBox.AppendText(message);
            if (!_pauseLogAutoScroll)
            {
                _logBox.SelectionStart = _logBox.TextLength;
                _logBox.ScrollToCaret();
            }
        }

        private void SaveLogToFile()
        {
            try
            {
                string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LazerTagLogs");
                Directory.CreateDirectory(folder);
                string fileName = "debrief_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt";
                string fullPath = Path.Combine(folder, fileName);
                File.WriteAllText(fullPath, _logBox.Text, Encoding.UTF8);
                AppendLog("Log saved to: " + fullPath);
            }
            catch (Exception ex)
            {
                AppendLog("Failed to save log: " + ex.Message);
            }
        }

        private void UpdateTelemetry()
        {
            if (_telemetryLabel == null) return;

            if (_hostGun == null)
            {
                _telemetryLabel.Text =
                    string.Format(
                        "Telemetry: Relay {0}/{1}, Timeouts {2}, Fail {3}, IO {4} | Players 0/0/0 | State Idle | Games {5}/{6} | Unexpected {7}, StateTimeouts {8}",
                        _telemetryRelayConnectedCount,
                        _telemetryRelayConnectAttempts,
                        _telemetryRelayTimeoutCount,
                        _telemetryRelayConnectFailureCount,
                        _telemetryRelayIoErrorCount,
                        _telemetryGamesCompletedCount,
                        _telemetryGamesStartedCount,
                        _telemetryUnexpectedPacketCount,
                        _telemetryStateTimeoutCount);
                return;
            }

            var players = _hostGun.Players.ToList();
            var joinedCount = players.Count;
            var confirmedCount = players.Count(p => p.Confirmed);
            var debriefedCount = players.Count(p => p.AllTagReportsReceived());

            var stateText = _hostGun.HostingState.ToString();
            try
            {
                stateText += " / " + _hostGun.ExplicitHostingState;
            }
            catch
            {
            }

            _telemetryLabel.Text =
                string.Format(
                    "Telemetry: Relay {0}/{1}, Timeouts {2}, Fail {3}, IO {4} | Players {5}/{6}/{7} | State {8} | Games {9}/{10} | Unexpected {11}, StateTimeouts {12}",
                    _telemetryRelayConnectedCount,
                    _telemetryRelayConnectAttempts,
                    _telemetryRelayTimeoutCount,
                    _telemetryRelayConnectFailureCount,
                    _telemetryRelayIoErrorCount,
                    joinedCount,
                    confirmedCount,
                    debriefedCount,
                    stateText,
                    _telemetryGamesCompletedCount,
                    _telemetryGamesStartedCount,
                    _telemetryUnexpectedPacketCount,
                    _telemetryStateTimeoutCount);
        }

        private void UpdateStatus()
        {
            var state = _hostGun?.HostingState ?? HostGun.HostingStates.Idle;
            _endGameButton.Enabled = state != HostGun.HostingStates.Idle
                && state != HostGun.HostingStates.Summary && state != HostGun.HostingStates.GameOver;
            _nextButton.Enabled = state == HostGun.HostingStates.Adding || state == HostGun.HostingStates.Playing;
            _finishDebriefButton.Visible = state == HostGun.HostingStates.Summary && !_hostGun.AllPlayersDebriefed;
            if (_hostGun == null)
            {
                _statusLabel.Text = "Idle";
                return;
            }
            _statusLabel.Text = _hostGun.GetGameStateText();
        }

        // ── Relay connection indicator ─────────────────────────────────────────
        private void Protocol_IoError(object sender, LazerTagSerial.IoErrorEventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new Action<object, LazerTagSerial.IoErrorEventArgs>(Protocol_IoError), sender, e); return; }
            _telemetryRelayIoErrorCount++;
            SetRelayStatus(false);
            UpdateTelemetry();
        }

        private void SetRelayStatus(bool connected)
        {
            if (connected)
            {
                _relayStatusLabel.Text      = "⬤  Relay: connected";
                _relayStatusLabel.ForeColor = Color.FromArgb(55, 195, 95);
            }
            else
            {
                _relayStatusLabel.Text      = "⬤  Relay: disconnected";
                _relayStatusLabel.ForeColor = Color.FromArgb(190, 55, 55);
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            DisconnectPreConnect();
            CleanupHost();
        }

        private void CleanupHost()
        {
            _updateTimer.Stop();
            if (_hostGun != null)
            {
                try { _hostGun.Protocol.Dispose(); } catch { }
                _hostGun = null;
            }
            UpdateStatus();
            SetRelayStatus(false);
            UpdateGuidedJoinLabel();
            UpdateTelemetry();
        }

        private void DisconnectPreConnect()
        {
            if (_preConnectSerial == null) return;
            _preConnectSerial.IoError -= PreConnectSerial_IoError;
            try { _preConnectSerial.Disconnect(); _preConnectSerial.Dispose(); } catch { }
            _preConnectSerial = null;
        }

        private sealed class GameTypeChoice
        {
            public GameTypeChoice(GameType type, string text)
            {
                Type = type;
                Text = text;
            }

            public GameType Type { get; private set; }
            public string Text { get; private set; }

            public override string ToString()
            {
                return Text;
            }
        }
    }
}
