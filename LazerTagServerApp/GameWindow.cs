using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using LazerTagHostLibrary;

namespace LazerTagServerApp
{
    /// <summary>
    /// Fullscreen game window.
    /// Lobby → Countdown → Playing → Debriefing → Scores
    /// </summary>
    public class GameWindow : Form
    {
        // ── Phases ────────────────────────────────────────────────────────────
        private enum Phase { Lobby, Countdown, Playing, Debriefing, Scores }
        private Phase _phase = Phase.Lobby;

        // ── Colour palette ────────────────────────────────────────────────────
        private static readonly Color BgDark      = Color.FromArgb(12, 14, 26);
        private static readonly Color BgCard      = Color.FromArgb(24, 27, 48);
        private static readonly Color BgCardDim   = Color.FromArgb(18, 20, 36);
        private static readonly Color TextPrimary = Color.FromArgb(230, 235, 255);
        private static readonly Color TextMuted   = Color.FromArgb(110, 115, 150);
        private static readonly Color AccentBlue  = Color.FromArgb(90, 160, 255);
        private static readonly Color AccentGold  = Color.FromArgb(255, 210, 60);
        private static readonly Color AccentGreen = Color.FromArgb(55, 205, 95);
        private static readonly Color AccentAmber = Color.FromArgb(255, 165, 35);

        private static readonly Color[] TeamColors =
        {
            Color.FromArgb(220, 70,  70),
            Color.FromArgb(60,  140, 230),
            Color.FromArgb(50,  190, 80),
        };

        // ── References ────────────────────────────────────────────────────────
        private readonly HostGun      _hostGun;
        private readonly List<string> _plannedNames;
        private readonly Timer        _clockTimer;

        // ── Lobby ─────────────────────────────────────────────────────────────
        private Panel           _lobbyPanel;
        private FlowLayoutPanel _playerTilesPanel;
        private Label           _waitingLabel;
        private Label           _nextPlayerLabel;
        private Button          _startGameBtn;

        // ── Playing ───────────────────────────────────────────────────────────
        private Panel  _playingPanel;
        private Label  _clockLabel;
        private Label  _stateLabel;
        private Label  _gameNameLabel;
        private Label  _relayWarningLabel;
        private Button _endGameBtn;

        // ── Debrief ───────────────────────────────────────────────────────────
        private Panel           _debriefPanel;
        private FlowLayoutPanel _debriefTilesPanel;
        private Label           _debriefStatusLabel;

        // ── Scores ────────────────────────────────────────────────────────────
        private Panel    _scoresPanel;
        private Label    _scoresTitleLabel;
        private Label    _teamScoreLabel;
        private ListView _rankView;
        private Button   _closeBtn;

        // ── Constructor ───────────────────────────────────────────────────────
        public GameWindow(HostGun hostGun, List<string> plannedNames)
        {
            _hostGun      = hostGun;
            _plannedNames = plannedNames ?? new List<string>();

            Text            = "LazerTag  " + AppVersion.Version;
            WindowState     = FormWindowState.Maximized;
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition   = FormStartPosition.CenterScreen;
            BackColor       = BgDark;
            Font            = new Font("Segoe UI", 12f);

            BuildLobbyPanel();
            BuildPlayingPanel();
            BuildDebriefPanel();
            BuildScoresPanel();

            Controls.Add(_scoresPanel);
            Controls.Add(_debriefPanel);
            Controls.Add(_playingPanel);
            Controls.Add(_lobbyPanel);

            // Version — top-right, floats above all panels
            var ver = new Label
            {
                Text      = AppVersion.Version,
                AutoSize  = true,
                Font      = new Font("Segoe UI", 10f),
                ForeColor = Color.FromArgb(50, 55, 85),
                BackColor = Color.Transparent,
            };
            Controls.Add(ver);
            ver.BringToFront();
            Resize += (s, e) => ver.Location = new Point(ClientSize.Width - ver.Width - 12, 8);

            _clockTimer       = new Timer { Interval = 100 };
            _clockTimer.Tick += ClockTimer_Tick;

            _hostGun.PlayerListChanged   += HostGun_PlayerListChanged;
            _hostGun.HostingStateChanged += HostGun_HostingStateChanged;

            ShowPhase(Phase.Lobby);
            RefreshLobby();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _clockTimer.Stop();
            _hostGun.PlayerListChanged   -= HostGun_PlayerListChanged;
            _hostGun.HostingStateChanged -= HostGun_HostingStateChanged;
            base.OnFormClosed(e);
        }

        // ── Build Lobby ───────────────────────────────────────────────────────
        private void BuildLobbyPanel()
        {
            _lobbyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            var header = new Label
            {
                Text = "WAITING FOR PLAYERS", Dock = DockStyle.Top, Height = 72,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                ForeColor = AccentBlue, BackColor = Color.Transparent,
            };
            var sep = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Color.FromArgb(30, 34, 62) };

            _nextPlayerLabel = new Label
            {
                Dock = DockStyle.Top, Height = 88, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 40f, FontStyle.Bold),
                ForeColor = AccentGold, BackColor = Color.Transparent, Text = "",
            };
            _waitingLabel = new Label
            {
                Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13f),
                ForeColor = TextMuted, BackColor = Color.Transparent, Text = "",
            };
            _playerTilesPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true, Padding = new Padding(24, 14, 24, 14),
                AutoScroll = true, BackColor = Color.Transparent,
            };

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = Color.Transparent };
            _startGameBtn = new Button
            {
                Text = "▶   Start Game", Width = 240, Height = 56, Top = 14,
                Font = new Font("Segoe UI", 16f, FontStyle.Bold),
                BackColor = Color.FromArgb(28, 110, 52), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Enabled = false,
            };
            _startGameBtn.FlatAppearance.BorderSize = 0;
            _startGameBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(36, 150, 68);
            _startGameBtn.Click += StartGameBtn_Click;
            btnPanel.Controls.Add(_startGameBtn);
            btnPanel.Resize += (s, e) => _startGameBtn.Left = (btnPanel.Width - _startGameBtn.Width) / 2;

            _lobbyPanel.Controls.Add(_playerTilesPanel);
            _lobbyPanel.Controls.Add(_waitingLabel);
            _lobbyPanel.Controls.Add(_nextPlayerLabel);
            _lobbyPanel.Controls.Add(sep);
            _lobbyPanel.Controls.Add(header);
            _lobbyPanel.Controls.Add(btnPanel);
        }

        // ── Build Playing ─────────────────────────────────────────────────────
        private void BuildPlayingPanel()
        {
            _playingPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            _gameNameLabel = new Label
            {
                Dock = DockStyle.Top, Height = 44, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13f), ForeColor = TextMuted, BackColor = Color.Transparent,
            };
            _stateLabel = new Label
            {
                Dock = DockStyle.Top, Height = 58, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 24f, FontStyle.Bold),
                ForeColor = AccentAmber, BackColor = Color.Transparent, Text = "GET READY",
            };
            _relayWarningLabel = new Label
            {
                Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 14f, FontStyle.Bold),
                ForeColor = Color.FromArgb(255, 80, 80),
                BackColor = Color.FromArgb(60, 20, 20),
                Text = "⚠  Relay disconnected — IR packets not being sent or received",
                Visible = false,
            };
            var sep = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Color.FromArgb(30, 34, 62) };

            _clockLabel = new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 130f, FontStyle.Bold),
                ForeColor = Color.White, BackColor = Color.Transparent,
                Text = "--:--", AutoSize = false,
            };
            // Scale clock font so it doesn't clip on small screens
            _playingPanel.Resize += (s, e) =>
            {
                int size = Math.Max(40, Math.Min(130, _playingPanel.Height / 6));
                _clockLabel.Font = new Font("Segoe UI", size, FontStyle.Bold);
            };

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 84, BackColor = Color.Transparent };
            _endGameBtn = new Button
            {
                Text = "■   End Game", Width = 210, Height = 56, Top = 14,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                BackColor = Color.FromArgb(155, 32, 32), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
            };
            _endGameBtn.FlatAppearance.BorderSize = 0;
            _endGameBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(195, 50, 50);
            _endGameBtn.Click += EndGameBtn_Click;
            btnPanel.Controls.Add(_endGameBtn);
            btnPanel.Resize += (s, e) => _endGameBtn.Left = (btnPanel.Width - _endGameBtn.Width) / 2;

            _playingPanel.Controls.Add(_clockLabel);
            _playingPanel.Controls.Add(sep);
            _playingPanel.Controls.Add(_stateLabel);
            _playingPanel.Controls.Add(_relayWarningLabel);
            _playingPanel.Controls.Add(_gameNameLabel);
            _playingPanel.Controls.Add(btnPanel);
        }

        // ── Build Debrief ─────────────────────────────────────────────────────
        private void BuildDebriefPanel()
        {
            _debriefPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            var header = new Label
            {
                Text = "UPLOADING SCORES", Dock = DockStyle.Top, Height = 72,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 26f, FontStyle.Bold),
                ForeColor = Color.FromArgb(130, 140, 225), BackColor = Color.Transparent,
            };
            var sep = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Color.FromArgb(30, 34, 62) };
            _debriefStatusLabel = new Label
            {
                Dock = DockStyle.Top, Height = 40, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13f),
                ForeColor = TextMuted, BackColor = Color.Transparent,
                Text = "Point your tagger at the relay to upload",
            };
            _debriefTilesPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true, Padding = new Padding(24, 14, 24, 14),
                AutoScroll = true, BackColor = Color.Transparent,
            };

            _debriefPanel.Controls.Add(_debriefTilesPanel);
            _debriefPanel.Controls.Add(_debriefStatusLabel);
            _debriefPanel.Controls.Add(sep);
            _debriefPanel.Controls.Add(header);
        }

        // ── Build Scores ──────────────────────────────────────────────────────
        private void BuildScoresPanel()
        {
            _scoresPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

            _scoresTitleLabel = new Label
            {
                Text = "GAME OVER", Dock = DockStyle.Top, Height = 86,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 36f, FontStyle.Bold),
                ForeColor = AccentGold, BackColor = Color.Transparent,
            };
            var sep = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Color.FromArgb(55, 48, 10) };
            _teamScoreLabel = new Label
            {
                Dock = DockStyle.Top, Height = 36, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 13f),
                ForeColor = Color.FromArgb(170, 195, 230), BackColor = Color.Transparent, Text = "",
            };

            // Custom dark column header row
            var colHeader = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(26, 28, 50) };
            string[] cols   = { "  Rank", "  Name", "  Team", "  Score", "  Hits ▲", "  Hits ▼", "  Survived", "  Time" };
            int[]    widths = { 80, 200, 100, 80, 95, 95, 95, 80 };
            int x = 0;
            for (int i = 0; i < cols.Length; i++)
            {
                colHeader.Controls.Add(new Label
                {
                    Text = cols[i], Left = x, Top = 0, Width = widths[i], Height = 34,
                    TextAlign = ContentAlignment.MiddleLeft,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    ForeColor = TextMuted, BackColor = Color.Transparent,
                });
                x += widths[i];
            }

            var rowSep = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(40, 44, 72) };

            _rankView = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                GridLines = false, HeaderStyle = ColumnHeaderStyle.None,
                Font = new Font("Segoe UI", 14f),
                BackColor = BgDark, ForeColor = TextPrimary, BorderStyle = BorderStyle.None,
            };
            _rankView.Columns.Add("Rank",      80);
            _rankView.Columns.Add("Name",      200);
            _rankView.Columns.Add("Team",      100);
            _rankView.Columns.Add("Score",     80);
            _rankView.Columns.Add("Hits ▲",  95);
            _rankView.Columns.Add("Hits ▼",  95);
            _rankView.Columns.Add("Survived",  95);
            _rankView.Columns.Add("Time",      80);

            var btnPanel = new Panel { Dock = DockStyle.Bottom, Height = 88, BackColor = Color.Transparent };

            // Buttons: Save CSV + Close
            var saveBtn = new Button
            {
                Text = "⬇   Save Scores CSV",
                Width = 220, Height = 56, Top = 16, Left = 20,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                BackColor = Color.FromArgb(24, 60, 100),
                ForeColor = Color.FromArgb(140, 200, 255),
                FlatStyle = FlatStyle.Flat,
            };
            saveBtn.FlatAppearance.BorderColor = Color.FromArgb(40, 80, 130);
            saveBtn.FlatAppearance.BorderSize  = 1;
            saveBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(32, 80, 130);
            saveBtn.Click += SaveScoresCsv_Click;

            _closeBtn = new Button
            {
                Text = "✕   Close  —  Setup New Game",
                Width = 300, Height = 56, Top = 16,
                Font = new Font("Segoe UI", 13f, FontStyle.Bold),
                BackColor = Color.FromArgb(36, 40, 64),
                ForeColor = Color.FromArgb(150, 155, 200),
                FlatStyle = FlatStyle.Flat,
            };
            _closeBtn.FlatAppearance.BorderColor = Color.FromArgb(55, 60, 95);
            _closeBtn.FlatAppearance.BorderSize  = 1;
            _closeBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(48, 54, 86);
            _closeBtn.Click += (s, e) => Close();

            btnPanel.Controls.Add(saveBtn);
            btnPanel.Controls.Add(_closeBtn);
            btnPanel.Resize += (s, e) =>
            {
                int total = saveBtn.Width + 20 + _closeBtn.Width;
                int startX = (btnPanel.Width - total) / 2;
                saveBtn.Left  = startX;
                _closeBtn.Left = startX + saveBtn.Width + 20;
            };

            _scoresPanel.Controls.Add(_rankView);
            _scoresPanel.Controls.Add(rowSep);
            _scoresPanel.Controls.Add(colHeader);
            _scoresPanel.Controls.Add(_teamScoreLabel);
            _scoresPanel.Controls.Add(sep);
            _scoresPanel.Controls.Add(_scoresTitleLabel);
            _scoresPanel.Controls.Add(btnPanel);
        }

        // ── Phase switching ───────────────────────────────────────────────────
        private void ShowPhase(Phase phase)
        {
            _phase = phase;
            _lobbyPanel.Visible   = (phase == Phase.Lobby);
            _playingPanel.Visible = (phase == Phase.Countdown || phase == Phase.Playing);
            _debriefPanel.Visible = (phase == Phase.Debriefing);
            _scoresPanel.Visible  = (phase == Phase.Scores);

            switch (phase)
            {
                case Phase.Countdown:
                    _gameNameLabel.Text   = _hostGun.GameDefinition.GameType.ToString()
                        .Replace("CustomLazerTag", "Custom LazerTag");
                    _stateLabel.Text      = "GET READY";
                    _stateLabel.ForeColor = AccentAmber;
                    _clockLabel.ForeColor = AccentAmber;
                    _clockTimer.Start();
                    break;
                case Phase.Playing:
                    _stateLabel.Text      = "GAME IN PROGRESS";
                    _stateLabel.ForeColor = AccentBlue;
                    _clockLabel.ForeColor = Color.White;
                    _clockTimer.Start();
                    break;
                case Phase.Debriefing:
                    _clockTimer.Stop();
                    RefreshDebrief();
                    break;
                case Phase.Scores:
                    _clockTimer.Stop();
                    PopulateScores();
                    break;
                default:
                    _clockTimer.Stop();
                    break;
            }
        }

        // ── Lobby refresh ─────────────────────────────────────────────────────
        private void RefreshLobby()
        {
            var players       = _hostGun.Players.ToList();
            var expectedCount = _plannedNames.Count > 0 ? _plannedNames.Count : -1;
            var isTeam        = _hostGun.GameDefinition.IsTeamGame;
            var teamCount     = _hostGun.GameDefinition.TeamCount;

            _playerTilesPanel.SuspendLayout();
            _playerTilesPanel.Controls.Clear();

            if (isTeam && teamCount > 1)
            {
                // Group by team — show a coloured header label then tiles for each team
                // Distribute expected slots evenly across teams
                int slotsPerTeam = expectedCount > 0 ? (int)Math.Ceiling((double)expectedCount / teamCount) : 0;

                for (int t = 1; t <= teamCount; t++)
                {
                    var tc = TeamColor(t);
                    string[] teamNames = { "Red Team", "Blue Team", "Green Team" };
                    string   teamName  = t <= teamNames.Length ? teamNames[t - 1] : "Team " + t;

                    // Team header — full width strip
                    var header = new Label
                    {
                        Width     = _playerTilesPanel.ClientSize.Width > 0
                            ? _playerTilesPanel.ClientSize.Width - 48 : 600,
                        Height    = 32,
                        Margin    = new Padding(0, t == 1 ? 4 : 16, 0, 4),
                        TextAlign = ContentAlignment.MiddleLeft,
                        Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                        ForeColor = tc,
                        BackColor = Color.FromArgb(
                            Math.Max(0, tc.R / 6),
                            Math.Max(0, tc.G / 6),
                            Math.Max(0, tc.B / 6)),
                        Text      = "  " + teamName,
                        Tag       = "header-" + t, // mark for resize
                    };
                    // Left border bar in team colour
                    var capturedTc = tc; // capture for lambda
                    header.Paint += (s, e) =>
                        e.Graphics.FillRectangle(new System.Drawing.SolidBrush(capturedTc),
                            0, 0, 4, ((Label)s).Height);
                    _playerTilesPanel.Controls.Add(header);

                    // Tiles for connected players on this team
                    foreach (var p in players.Where(p => p.TeamPlayerId.TeamNumber == t)
                                             .OrderBy(p => p.TeamPlayerId.TeamPlayerNumber))
                        _playerTilesPanel.Controls.Add(MakePlayerTile(p, isTeam));

                    // Empty slots for this team
                    int connectedOnTeam = players.Count(p => p.TeamPlayerId.TeamNumber == t);
                    int emptyForTeam    = slotsPerTeam - connectedOnTeam;
                    // Distribute planned names to teams in order
                    int nameBase = (t - 1) * slotsPerTeam;
                    for (int i = 0; i < emptyForTeam; i++)
                    {
                        int nameIdx = nameBase + connectedOnTeam + i;
                        string slotName = (nameIdx < _plannedNames.Count)
                            ? _plannedNames[nameIdx] : "Player " + (nameIdx + 1);
                        _playerTilesPanel.Controls.Add(MakeEmptyTile(slotName, t));
                    }
                }
            }
            else
            {
                // Solo / free-for-all — flat list as before
                foreach (var p in players.OrderBy(p => p.TeamPlayerId.PlayerNumber))
                    _playerTilesPanel.Controls.Add(MakePlayerTile(p, isTeam));
                if (expectedCount > 0)
                    for (int i = players.Count; i < expectedCount; i++)
                        _playerTilesPanel.Controls.Add(MakeEmptyTile(
                            i < _plannedNames.Count ? _plannedNames[i] : "Player " + (i + 1)));
            }

            // Keep team headers full-width on resize
            _playerTilesPanel.Resize += (s, e) =>
            {
                int w = _playerTilesPanel.ClientSize.Width - 48;
                foreach (Control c in _playerTilesPanel.Controls)
                    if (c is Label lbl && lbl.Tag?.ToString()?.StartsWith("header-") == true)
                        lbl.Width = Math.Max(200, w);
            };

            _playerTilesPanel.ResumeLayout();

            if (expectedCount > 0)
            {
                int rem = expectedCount - players.Count;
                if (rem > 0)
                {
                    string next = players.Count < _plannedNames.Count
                        ? _plannedNames[players.Count] : "Player " + (players.Count + 1);
                    _nextPlayerLabel.Text      = next + "  —  connect now";
                    _nextPlayerLabel.ForeColor = AccentGold;
                    _waitingLabel.Text         = players.Count + " of " + expectedCount + " connected";
                }
                else
                {
                    _nextPlayerLabel.Text      = "All players connected!";
                    _nextPlayerLabel.ForeColor = AccentGreen;
                    _waitingLabel.Text         = "Press Start Game when ready";
                }
            }
            else
            {
                _nextPlayerLabel.Text      = players.Count == 0 ? "Connect your first tagger" : "Connect next tagger";
                _nextPlayerLabel.ForeColor = AccentGold;
                _waitingLabel.Text         = players.Count == 0 ? "Point your tagger at the relay"
                    : players.Count + " player" + (players.Count == 1 ? "" : "s") + " connected";
            }

            bool allIn   = expectedCount > 0 ? players.Count >= expectedCount : players.Count >= 2;
            bool allConf = players.Count > 0 && players.All(p => p.Confirmed);
            _startGameBtn.Enabled   = allIn && allConf;
            _startGameBtn.BackColor = allIn && allConf ? Color.FromArgb(28, 110, 52) : Color.FromArgb(28, 38, 30);
            _startGameBtn.ForeColor = allIn && allConf ? Color.White : Color.FromArgb(70, 90, 70);
        }

        // ── Debrief refresh ───────────────────────────────────────────────────
        private void RefreshDebrief()
        {
            var players = _hostGun.Players.ToList();
            var isTeam  = _hostGun.GameDefinition.IsTeamGame;
            int done    = players.Count(p => p.AllTagReportsReceived());
            int total   = players.Count;

            _debriefTilesPanel.SuspendLayout();
            _debriefTilesPanel.Controls.Clear();
            foreach (var p in players.OrderBy(p => p.TeamPlayerId.PlayerNumber))
                _debriefTilesPanel.Controls.Add(MakeDebriefTile(p, isTeam));
            _debriefTilesPanel.ResumeLayout();

            _debriefStatusLabel.Text      = done < total
                ? done + " of " + total + " uploaded — point remaining taggers at the relay"
                : "All scores uploaded!";
            _debriefStatusLabel.ForeColor = done == total ? AccentGreen : TextMuted;
        }

        // ── Tile builders ─────────────────────────────────────────────────────
        private Panel MakePlayerTile(Player player, bool isTeam)
        {
            var  tc   = TeamColor(player.TeamPlayerId.TeamNumber);
            bool conf = player.Confirmed;
            var  tile = MakeTileBase(conf ? BgCard : BgCardDim, tc);

            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 19f, FontStyle.Bold),
                ForeColor = conf ? TextPrimary : Color.FromArgb(110, 115, 150),
                BackColor = Color.Transparent, Text = player.DisplayName,
            });
            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 10f, FontStyle.Bold),
                ForeColor = conf ? AccentGreen : AccentAmber,
                BackColor = Color.Transparent,
                Text = conf ? "✓  Ready" : "⏳  Joining",
            });
            // Show team name prominently — especially important in non-team mode
            // so players know which slot they were assigned
            if (!isTeam)
                tile.Controls.Add(new Label
                {
                    Dock = DockStyle.Top, Height = 20, TextAlign = ContentAlignment.MiddleRight,
                    Font = new Font("Segoe UI", 8.5f),
                    ForeColor = TextMuted, BackColor = Color.Transparent,
                    Text = "Slot " + player.TeamPlayerId.PlayerNumber + "   ",
                });
            return tile;
        }

        private Panel MakeEmptyTile(string name, int teamHint = 0)
        {
            var barColor = teamHint > 0 ? Color.FromArgb(
                TeamColor(teamHint).R / 3,
                TeamColor(teamHint).G / 3,
                TeamColor(teamHint).B / 3)
                : Color.FromArgb(38, 42, 66);

            var tile = MakeTileBase(Color.FromArgb(15, 17, 30), barColor);
            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 17f),
                ForeColor = Color.FromArgb(65, 68, 100), BackColor = Color.Transparent, Text = name,
            });
            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Bottom, Height = 28, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(48, 52, 78), BackColor = Color.Transparent, Text = "waiting…",
            });
            return tile;
        }

        private Panel MakeDebriefTile(Player player, bool isTeam)
        {
            bool done = player.AllTagReportsReceived();
            var  tc   = TeamColor(player.TeamPlayerId.TeamNumber);
            var  tile = MakeTileBase(
                done ? Color.FromArgb(14, 30, 18) : BgCard,
                done ? AccentGreen : tc);

            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 19f, FontStyle.Bold),
                ForeColor = done ? Color.FromArgb(120, 240, 150) : TextPrimary,
                BackColor = Color.Transparent, Text = player.DisplayName,
            });
            tile.Controls.Add(new Label
            {
                Dock = DockStyle.Bottom, Height = 30, TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = done ? AccentGreen : AccentAmber,
                BackColor = Color.Transparent,
                Text = done ? "✓  Uploaded" : "⏳  Waiting…",
            });
            return tile;
        }

        private static Panel MakeTileBase(Color bg, Color bar)
        {
            var tile = new Panel { Width = 230, Height = 148, Margin = new Padding(10), BackColor = bg };
            tile.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 5, BackColor = bar });
            return tile;
        }

        private static Color TeamColor(int t) =>
            (t >= 1 && t <= TeamColors.Length) ? TeamColors[t - 1] : Color.FromArgb(90, 95, 130);

        // ── Clock tick ────────────────────────────────────────────────────────
        private void ClockTimer_Tick(object sender, EventArgs e)
        {
            if (_hostGun == null) return;

            // Update clock
            var t = _hostGun.GetTimeRemaining();
            if (!string.IsNullOrEmpty(t))
            {
                _clockLabel.Text = t;
                if (_phase == Phase.Playing)
                    _clockLabel.ForeColor = (t.Length <= 4 && string.Compare(t, "1:00") < 0)
                        ? AccentAmber : Color.White;
            }

            // Relay health check — warn visibly if connection drops during a game
            bool connected = _hostGun.Protocol != null && _hostGun.Protocol.IsConnected;
            if (_relayWarningLabel.Visible == connected) // XOR: only update when state changes
            {
                _relayWarningLabel.Visible = !connected;
                _relayWarningLabel.Height  = !connected ? 44 : 0;
            }
        }

        // ── Button handlers ───────────────────────────────────────────────────
        private void StartGameBtn_Click(object sender, EventArgs e)
        {
            if (!_hostGun.StartCountdown())
                DarkDialog("Not Ready", "Countdown could not start.\nCheck player count.", "OK", null);
        }

        private void EndGameBtn_Click(object sender, EventArgs e)
        {
            if (DarkDialog("End Game", "End the game now?", "End Game", "Cancel"))
                _hostGun.EndGame();
        }

        // ── Dark-themed dialog (replaces jarring white MessageBox) ────────────
        private bool DarkDialog(string title, string message, string ok, string cancel)
        {
            bool result = false;
            using (var dlg = new Form
            {
                Text = title, Width = 420, Height = 200,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition   = FormStartPosition.CenterParent,
                BackColor       = Color.FromArgb(20, 22, 40),
                MaximizeBox     = false, MinimizeBox = false,
            })
            {
                dlg.Controls.Add(new Label
                {
                    Text = message, Left = 24, Top = 28, Width = 370, Height = 64,
                    Font = new Font("Segoe UI", 12f), ForeColor = TextPrimary,
                    BackColor = Color.Transparent,
                });

                if (cancel != null)
                {
                    var btnCancel = new Button
                    {
                        Text = cancel, Left = 232, Top = 118, Width = 148, Height = 40,
                        FlatStyle = FlatStyle.Flat,
                        BackColor = Color.FromArgb(36, 40, 64),
                        ForeColor = Color.FromArgb(150, 155, 200),
                        Font = new Font("Segoe UI", 11f),
                    };
                    btnCancel.FlatAppearance.BorderColor = Color.FromArgb(55, 60, 90);
                    btnCancel.Click += (s2, e2) => dlg.Close();
                    dlg.Controls.Add(btnCancel);
                }

                var btnOk = new Button
                {
                    Text = ok,
                    Left = cancel != null ? 58 : 130, Top = 118, Width = 150, Height = 40,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = cancel != null ? Color.FromArgb(155, 32, 32) : Color.FromArgb(28, 110, 52),
                    ForeColor = Color.White,
                    Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                };
                btnOk.FlatAppearance.BorderSize = 0;
                btnOk.Click += (s2, e2) => { result = true; dlg.Close(); };
                dlg.Controls.Add(btnOk);

                dlg.ShowDialog(this);
            }
            return result;
        }

        // ── HostGun event handlers ────────────────────────────────────────────
        private void HostGun_PlayerListChanged(object sender, HostGun.PlayerListChangedEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action<object, HostGun.PlayerListChangedEventArgs>(HostGun_PlayerListChanged), sender, e); return; }
            if (_phase == Phase.Lobby)      RefreshLobby();
            if (_phase == Phase.Debriefing) RefreshDebrief();
        }

        private void HostGun_HostingStateChanged(object sender, HostGun.HostingStateChangedEventArgs e)
        {
            if (InvokeRequired) { BeginInvoke(new Action<object, HostGun.HostingStateChangedEventArgs>(HostGun_HostingStateChanged), sender, e); return; }
            switch (e.State)
            {
                case HostGun.HostingStates.Countdown: ShowPhase(Phase.Countdown);  break;
                case HostGun.HostingStates.Playing:   ShowPhase(Phase.Playing);    break;
                case HostGun.HostingStates.Summary:   ShowPhase(Phase.Debriefing); break;
                case HostGun.HostingStates.GameOver:  ShowPhase(Phase.Scores);     break;
                case HostGun.HostingStates.Idle:      Close();                     break;
            }
        }

        // ── Populate scores ───────────────────────────────────────────────────
        private void PopulateScores()
        {
            var players   = _hostGun.Players.ToList();
            var isTeam    = _hostGun.GameDefinition.IsTeamGame;
            var teamCount = _hostGun.GameDefinition.TeamCount;

            _scoresTitleLabel.Text = "GAME OVER";

            if (isTeam && teamCount > 1)
            {
                var parts = new List<string>();
                for (int t = 1; t <= teamCount; t++)
                {
                    var team = _hostGun.Teams.Team(t);
                    if (team != null)
                        parts.Add("Team " + t + ": " + team.Score + " pts  (rank " + team.Rank + ")");
                }
                _teamScoreLabel.Text = string.Join("          ", parts);
            }
            else _teamScoreLabel.Text = "";

            string[] medals = { "🥇", "🥈", "🥉" };

            _rankView.BeginUpdate();
            _rankView.Items.Clear();
            foreach (var player in players.OrderBy(p => p.Rank).ThenBy(p => p.Name))
            {
                int hs = TotalEffectiveHits(player, players);

                string rankText = (player.Rank >= 1 && player.Rank <= 3)
                    ? medals[player.Rank - 1]
                    : (player.Rank > 0 ? player.Rank.ToString() : "—");

                var item = new ListViewItem(rankText);
                item.SubItems.Add(player.DisplayName);
                item.SubItems.Add(isTeam ? "Team " + player.TeamPlayerId.TeamNumber : "");
                item.SubItems.Add(player.Score.ToString());
                item.SubItems.Add(hs.ToString());
                item.SubItems.Add(player.TagsTaken.ToString());
                item.SubItems.Add(player.Survived ? "Yes" : "No");
                item.SubItems.Add(player.SurviveTime.TotalSeconds > 0
                    ? string.Format("{0}:{1:D2}", (int)player.SurviveTime.TotalMinutes, player.SurviveTime.Seconds)
                    : "—");

                switch (player.Rank)
                {
                    case 1: item.BackColor = Color.FromArgb(52, 44, 6);  item.ForeColor = Color.FromArgb(255, 222, 70);  break;
                    case 2: item.BackColor = Color.FromArgb(32, 36, 52); item.ForeColor = Color.FromArgb(190, 205, 228); break;
                    case 3: item.BackColor = Color.FromArgb(46, 30, 12); item.ForeColor = Color.FromArgb(210, 155, 82);  break;
                    default: item.BackColor = BgDark; item.ForeColor = Color.FromArgb(160, 165, 205); break;
                }

                if (isTeam)
                    item.SubItems[2].ForeColor = TeamColor(player.TeamPlayerId.TeamNumber);

                _rankView.Items.Add(item);
            }
            _rankView.EndUpdate();
        }

        // ── Save scores to CSV ────────────────────────────────────────────────
        private void SaveScoresCsv_Click(object sender, EventArgs e)
        {
            try
            {
                var players   = _hostGun.Players.ToList();
                var isTeam    = _hostGun.GameDefinition.IsTeamGame;
                var teamCount = _hostGun.GameDefinition.TeamCount;
                var sb        = new System.Text.StringBuilder();

                // Header
                sb.AppendLine("Rank,Name,Team,Score,Hits Scored,Hits Taken,Survived,Survive Time");

                foreach (var player in players.OrderBy(p => p.Rank).ThenBy(p => p.Name))
                {
                    int hs = TotalEffectiveHits(player, players);

                    string surviveTime = player.SurviveTime.TotalSeconds > 0
                        ? string.Format("{0}:{1:D2}", (int)player.SurviveTime.TotalMinutes, player.SurviveTime.Seconds)
                        : "";

                    sb.AppendLine(string.Format("{0},{1},{2},{3},{4},{5},{6},{7}",
                        player.Rank,
                        CsvEscape(player.DisplayName),
                        isTeam ? "Team " + player.TeamPlayerId.TeamNumber : "",
                        player.Score,
                        hs,
                        player.TagsTaken,
                        player.Survived ? "Yes" : "No",
                        surviveTime));
                }

                // Hit matrix
                sb.AppendLine();
                sb.AppendLine("Hit Matrix (rows = scorer, columns = target)");
                var header = "," + string.Join(",", players.OrderBy(p => p.TeamPlayerId.PlayerNumber)
                    .Select(p => CsvEscape(p.DisplayName)));
                sb.AppendLine(header);

                foreach (var scorer in players.OrderBy(p => p.TeamPlayerId.PlayerNumber))
                {
                    var row = new System.Text.StringBuilder(CsvEscape(scorer.DisplayName));
                    foreach (var target in players.OrderBy(p => p.TeamPlayerId.PlayerNumber))
                    {
                        int hits = scorer == target ? 0 : EffectiveHits(scorer, target);
                        row.Append("," + hits);
                    }
                    sb.AppendLine(row.ToString());
                }

                string folder = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "LazerTagLogs");
                System.IO.Directory.CreateDirectory(folder);
                string path = System.IO.Path.Combine(folder,
                    "scores_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".csv");
                System.IO.File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);

                DarkDialog("Saved", "Scores saved to:\n" + path, "OK", null);
            }
            catch (Exception ex)
            {
                DarkDialog("Error", "Could not save scores:\n" + ex.Message, "OK", null);
            }
        }

        // Returns hits scored by scorer against target, capped at target's HP.
        private int EffectiveHits(Player scorer, Player target)
        {
            if (scorer == null || target == null) return 0;
            int raw = scorer.TaggedPlayerCounts[target.TeamPlayerId.PlayerNumber - 1];
            int hp  = target.TagsOverride.HasValue
                ? target.TagsOverride.Value
                : _hostGun.GameDefinition.Tags;
            return Math.Min(raw, hp);
        }

        // Total effective hits scored by a player across all opponents.
        private int TotalEffectiveHits(Player scorer, List<Player> allPlayers)
        {
            int total = 0;
            foreach (var target in allPlayers)
                if (target != scorer)
                    total += EffectiveHits(scorer, target);
            return total;
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n"))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }
    }
}
