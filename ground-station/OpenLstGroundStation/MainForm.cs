// MainForm.cs
// OpenLST Ground Station — VS2022 .NET 8 WinForms
// All controls created in code (no designer), easy to extend.
// Global state: serial port, HWID, log panel, file logger.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OpenLstGroundStation
{
    public class MainForm : Form
    {
        // ══════════════════════════════════════════════════════════════════
        //  THEME
        // ══════════════════════════════════════════════════════════════════
        private static class Theme
        {
            public static readonly Color FormBack      = Color.FromArgb(14, 14, 20);
            public static readonly Color PanelBack     = Color.FromArgb(22, 22, 32);
            public static readonly Color TabBack       = Color.FromArgb(18, 18, 26);
            public static readonly Color HeaderBack    = Color.FromArgb(10, 10, 16);
            public static readonly Color BorderColor   = Color.FromArgb(50, 50, 80);
            public static readonly Color LogBack       = Color.FromArgb(8,  8,  12);

            public static readonly Color Green   = Color.LimeGreen;
            public static readonly Color Yellow  = Color.Gold;
            public static readonly Color Red     = Color.OrangeRed;
            public static readonly Color Cyan    = Color.Cyan;
            public static readonly Color White   = Color.WhiteSmoke;
            public static readonly Color Gray    = Color.DimGray;
            public static readonly Color Silver  = Color.Silver;
            public static readonly Color Magenta = Color.MediumOrchid;

            public static readonly Font FontMono     = new Font("Consolas", 9.5f);
            public static readonly Font FontMonoBold = new Font("Consolas", 9.5f, FontStyle.Bold);
            public static readonly Font FontSmall    = new Font("Consolas", 8.5f);
            public static readonly Font FontLarge    = new Font("Consolas", 11f, FontStyle.Bold);
            public static readonly Font FontTitle    = new Font("Consolas", 13f, FontStyle.Bold);
        }

        // ══════════════════════════════════════════════════════════════════
        //  GLOBAL SERIAL STATE
        // ══════════════════════════════════════════════════════════════════
        private SerialPort? _port;
        private ushort      _seqNum = OpenLstProtocol.SEQNUM_MIN;

        private readonly List<byte>                _rxBuf    = new();
        private readonly object                    _rxLock   = new();
        private readonly ConcurrentQueue<RxPacket> _rxQueue  = new();

        private ushort IncSeqNum() => _seqNum = OpenLstProtocol.IncSeqNum(_seqNum);

        private ushort ActiveHwid
        {
            get
            {
                if (ushort.TryParse(txtHwid?.Text?.Trim() ?? "",
                    System.Globalization.NumberStyles.HexNumber, null, out ushort h)) return h;
                return 0;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  LIVE TELEM POLL
        // ══════════════════════════════════════════════════════════════════
        private System.Windows.Forms.Timer? _telemTimer;
        private TelemData? _lastTelem;

        // ══════════════════════════════════════════════════════════════════
        //  LIVE LOG MONITOR
        // ══════════════════════════════════════════════════════════════════
        private System.Windows.Forms.Timer? _liveLogTimer;
        private bool _liveLogEnabled = false;

        // ══════════════════════════════════════════════════════════════════
        //  GLOBAL UI CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private ComboBox    cmbPort     = null!;
        private ComboBox    cmbBaud     = null!;
        private TextBox     txtHwid     = null!;
        private Button      btnConnect  = null!;
        private Label       lblStatus   = null!;
        private RichTextBox rtbLog      = null!;
        private Button      btnClearLog = null!;
        private Button      btnLiveLog  = null!;
        private TabControl  tabMain     = null!;

        // ── Tab pages (built by each Build*Tab method) ────────────────────
        private TabPage tpHome     = null!;
        private TabPage tpCommands = null!;
        private TabPage tpFirmware = null!;
        private TabPage tpTerminal = null!;
        private TabPage tpCustom   = null!;
        private TabPage tpProvision = null!;
        private TabPage tpFiles    = null!;

        // ══════════════════════════════════════════════════════════════════
        //  HOME TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private Label lblUptime         = null!;
        private Label lblRssi           = null!;
        private Label lblLqi            = null!;
        private Label lblPktsGood       = null!;
        private Label lblPktsSent       = null!;
        private Label lblRejCksum       = null!;
        private Label lblRejOther       = null!;
        private Label lblUart0          = null!;
        private Label lblUart1          = null!;
        private Label lblRxMode         = null!;
        private Label lblTxMode         = null!;
        private Label lblTelemAge       = null!;
        private Button btnGetTelemNow   = null!;
        private Button btnTelemAuto     = null!;

        // ══════════════════════════════════════════════════════════════════
        //  COMMANDS TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox txtCallsign     = null!;
        private TextBox txtRawCmd       = null!;

        // ══════════════════════════════════════════════════════════════════
        //  FIRMWARE TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox     txtProjectDir   = null!;
        private TextBox     txtFirmwareFile = null!;
        private TextBox     txtAesKey       = null!;
        private Button      btnBuild        = null!;
        private Button      btnSign         = null!;
        private Button      btnFlash        = null!;
        private Button      btnBuildFlash   = null!;
        private Button      btnBrowseHex    = null!;
        private Button      btnBrowseDir    = null!;
        private ProgressBar pbFlash         = null!;
        private Label       lblFlashStatus  = null!;

        // ══════════════════════════════════════════════════════════════════
        //  TERMINAL TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private RichTextBox rtbTerminal  = null!;
        private TextBox     txtTermInput = null!;
        private Button      btnTermSend  = null!;
        private Button      btnTermClear = null!;

        // ══════════════════════════════════════════════════════════════════
        //  CUSTOM COMMANDS TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private ListBox  lstCustomCmds   = null!;
        private TextBox  txtCmdName      = null!;
        private TextBox  txtCmdOpcode    = null!;
        private ComboBox cmbCmdType      = null!;
        private TextBox  txtCmdPayload   = null!;
        private TextBox  txtCmdNotes     = null!;
        private Button   btnCmdSave      = null!;
        private Button   btnCmdDelete    = null!;
        private Button   btnCmdSend      = null!;
        private List<CustomCommand> _customCommands = new();

        // ══════════════════════════════════════════════════════════════════
        //  FILES TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private ListBox     lstFiles         = null!;
        private Button      btnFilesRefresh  = null!;
        private Button      btnFilesGet      = null!;
        private Button      btnFilesDelete   = null!;
        private ProgressBar pbTransfer       = null!;
        private Label       lblTransferStatus = null!;
        private bool        _transferring    = false;

        // ── Persistent settings (saved to appsettings.json) ───────────────
        private static string SettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        private void SaveSettings()
        {
            try
            {
                var d = new System.Collections.Generic.Dictionary<string, string>
                {
                    ["ProjectDir"]   = txtProjectDir?.Text  ?? "",
                    ["FirmwareFile"] = txtFirmwareFile?.Text ?? "",
                    ["AesKey"]       = txtAesKey?.Text       ?? "",
                    ["LastPort"]     = cmbPort?.SelectedItem?.ToString() ?? "",
                    ["LastBaud"]     = cmbBaud?.SelectedItem?.ToString() ?? "",
                    ["LastHwid"]     = txtHwid?.Text         ?? "",
                };
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsPath,
                    System.Text.Json.JsonSerializer.Serialize(d, opts));
            }
            catch { }
        }

        private System.Collections.Generic.Dictionary<string, string> LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new();
                string json = File.ReadAllText(SettingsPath);
                return System.Text.Json.JsonSerializer
                    .Deserialize<System.Collections.Generic.Dictionary<string, string>>(json)
                    ?? new();
            }
            catch { return new(); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  PROVISION TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox  txtProvHwid   = null!;
        private TextBox  txtProvKey0   = null!;
        private TextBox  txtProvKey1   = null!;
        private TextBox  txtProvKey2   = null!;
        private TextBox  txtProvHexPath = null!;
        private Button   btnProvBrowse  = null!;
        private Button   btnProvFlash   = null!;
        private Button   btnProvDetect  = null!;
        private Label    lblProvStatus  = null!;
        private const string SMARTRF_PATH =
            @"C:\Program Files (x86)\Texas Instruments\SmartRF Tools\Flash Programmer\bin\SmartRFProgConsole.exe";

        // ══════════════════════════════════════════════════════════════════
        //  CONSTRUCTOR
        // ══════════════════════════════════════════════════════════════════
        public MainForm()
        {
            AppLogger.Initialize();
            BuildForm();
            BuildHeader();
            BuildTabControl();
            BuildHomeTab();
            BuildCommandsTab();
            BuildFirmwareTab();
            BuildTerminalTab();
            BuildCustomCommandsTab();
            BuildFilesTab();
            BuildProvisionTab();
            BuildLogPanel();
            WireTimers();

            // Restore persistent settings
            var s = LoadSettings();
            if (s.TryGetValue("ProjectDir",   out string? pd)  && Directory.Exists(pd))
                txtProjectDir.Text = pd;
            if (s.TryGetValue("FirmwareFile", out string? ff)  && File.Exists(ff))
                txtFirmwareFile.Text = ff;
            if (s.TryGetValue("AesKey",       out string? ak)  && ak.Length == 32)
                txtAesKey.Text = ak;
            if (s.TryGetValue("LastPort",     out string? lp)  && cmbPort.Items.Contains(lp))
                cmbPort.SelectedItem = lp;
            if (s.TryGetValue("LastBaud",     out string? lb)  && cmbBaud.Items.Contains(lb))
                cmbBaud.SelectedItem = lb;
            if (s.TryGetValue("LastHwid",     out string? lh)  && !string.IsNullOrEmpty(lh))
                txtHwid.Text = lh;

            SetStatus("Disconnected", Theme.Red);
            Log("OpenLST Ground Station ready.", Theme.Cyan);
            Log($"Log folder: {AppLogger.LogFolder}", Theme.Gray);
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM SHELL
        // ══════════════════════════════════════════════════════════════════
        private void BuildForm()
        {
            Text            = "OpenLST Ground Station";
            Size            = new Size(1280, 820);
            MinimumSize     = new Size(1100, 700);
            BackColor       = Theme.FormBack;
            ForeColor       = Theme.Silver;
            Font            = Theme.FontMono;
            StartPosition   = FormStartPosition.CenterScreen;
            DoubleBuffered  = true;

            // Min/Close buttons are native — no need to recreate them
        }

        // ══════════════════════════════════════════════════════════════════
        //  HEADER BAR  (global controls: port, baud, hwid, connect, status)
        // ══════════════════════════════════════════════════════════════════
        private void BuildHeader()
        {
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 52,
                BackColor = Theme.HeaderBack,
                Padding   = new Padding(8, 0, 8, 0),
            };

            // Title
            var lblTitle = MkLabel("◈ OpenLST Ground Station", 10, 14, 280, Theme.Cyan, Theme.FontTitle);

            // Port label + combo
            var lblPort = MkLabel("Port:", 300, 17, 38, Theme.Gray);
            cmbPort = new ComboBox
            {
                Location      = new Point(342, 13),
                Size          = new Size(90, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                FlatStyle     = FlatStyle.Flat,
                Font          = Theme.FontMono,
            };
            foreach (string p in SerialPort.GetPortNames().OrderBy(x => x))
                cmbPort.Items.Add(p);
            if (cmbPort.Items.Count > 0) cmbPort.SelectedIndex = 0;

            // Baud label + combo
            var lblBaud = MkLabel("Baud:", 440, 17, 42, Theme.Gray);
            cmbBaud = new ComboBox
            {
                Location      = new Point(486, 13),
                Size          = new Size(90, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                FlatStyle     = FlatStyle.Flat,
                Font          = Theme.FontMono,
            };
            foreach (string b in new[] { "9600", "19200", "38400", "57600", "115200", "230400" })
                cmbBaud.Items.Add(b);
            cmbBaud.SelectedItem = "115200";

            // HWID label + textbox
            var lblHwidH = MkLabel("HWID:", 590, 17, 46, Theme.Gray);
            txtHwid = new TextBox
            {
                Location  = new Point(640, 13),
                Size      = new Size(60, 26),
                BackColor = Theme.PanelBack,
                ForeColor = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font      = Theme.FontMonoBold,
                Text      = "0001",
                MaxLength = 4,
            };

            // Refresh ports button
            var btnRefreshPorts = MkButton("↺", 714, 12, 30, Theme.Gray, Theme.PanelBack);
            btnRefreshPorts.Font = new Font("Consolas", 12f, FontStyle.Bold);
            btnRefreshPorts.FlatAppearance.BorderColor = Theme.BorderColor;
            ToolTip tt = new ToolTip();
            tt.SetToolTip(btnRefreshPorts, "Refresh COM port list");
            btnRefreshPorts.Click += (s, e) =>
            {
                string? current = cmbPort.SelectedItem?.ToString();
                cmbPort.Items.Clear();
                foreach (string pt in SerialPort.GetPortNames().OrderBy(x => x))
                    cmbPort.Items.Add(pt);
                if (current != null && cmbPort.Items.Contains(current))
                    cmbPort.SelectedItem = current;
                else if (cmbPort.Items.Count > 0)
                    cmbPort.SelectedIndex = 0;
                Log($"COM ports refreshed. {cmbPort.Items.Count} port(s) found.", Theme.Gray);
            };

            // Connect button
            btnConnect = MkButton("Connect", 752, 12, 100, Theme.Cyan, Theme.PanelBack);
            btnConnect.Click += BtnConnect_Click;

            // Status label
            lblStatus = new Label
            {
                Location  = new Point(864, 17),
                Size      = new Size(400, 22),
                ForeColor = Theme.Red,
                BackColor = Color.Transparent,
                Font      = Theme.FontMono,
                Text      = "● Disconnected",
            };

            header.Controls.AddRange(new Control[]
            {
                lblTitle, lblPort, cmbPort, lblBaud, cmbBaud,
                lblHwidH, txtHwid, btnRefreshPorts, btnConnect, lblStatus
            });

            Controls.Add(header);
        }

        // ══════════════════════════════════════════════════════════════════
        //  TAB CONTROL  (fills remaining space left of log panel)
        // ══════════════════════════════════════════════════════════════════
        private void BuildTabControl()
        {
            tabMain = new TabControl
            {
                Location  = new Point(0, 52),
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Theme.TabBack,
                Font      = Theme.FontMonoBold,
                Padding   = new Point(14, 4),
            };

            // Resize hook to keep tab + log panel correctly proportioned
            SizeChanged += (s, e) => ResizeLayout();

            tpHome      = MkTab("  Home  ");
            tpCommands  = MkTab("  Commands  ");
            tpFirmware  = MkTab("  Firmware  ");
            tpTerminal  = MkTab("  Terminal  ");
            tpCustom    = MkTab("  Custom Commands  ");
            tpFiles     = MkTab("  Files  ");
            tpProvision = MkTab("  Provision  ");

            tabMain.TabPages.AddRange(new[] { tpHome, tpCommands, tpFirmware, tpTerminal, tpCustom, tpFiles, tpProvision });
            Controls.Add(tabMain);
            ResizeLayout();
        }

        private void ResizeLayout()
        {
            int logW   = 380;
            int top    = 52;
            int bottom = ClientSize.Height - top;
            tabMain.SetBounds(0, top, ClientSize.Width - logW - 4, bottom);
        }

        // ══════════════════════════════════════════════════════════════════
        //  LOG PANEL  (right side, full height minus header)
        // ══════════════════════════════════════════════════════════════════
        private void BuildLogPanel()
        {
            int logW = 380;

            var logPanel = new Panel
            {
                BackColor = Theme.LogBack,
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Right,
            };

            SizeChanged += (s, e) =>
            {
                logPanel.SetBounds(ClientSize.Width - logW, 52, logW, ClientSize.Height - 52);
            };

            // Header row
            var lblLogTitle = MkLabel("◈ LOG", 8, 8, 80, Theme.Cyan, Theme.FontMonoBold);

            btnClearLog = MkButton("Clear", logW - 120, 5, 56, Theme.Gray, Theme.LogBack);
            btnClearLog.FlatAppearance.BorderColor = Theme.BorderColor;
            btnClearLog.Click += (s, e) => rtbLog.Clear();

            btnLiveLog = MkButton("Live: OFF", logW - 60, 5, 56, Theme.Gray, Theme.LogBack);
            btnLiveLog.FlatAppearance.BorderColor = Theme.BorderColor;
            btnLiveLog.Click += BtnLiveLog_Click;

            rtbLog = new RichTextBox
            {
                Location    = new Point(0, 32),
                BackColor   = Theme.LogBack,
                ForeColor   = Theme.Silver,
                Font        = new Font("Consolas", 8.5f),
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                WordWrap    = true,
                Anchor      = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            logPanel.Controls.AddRange(new Control[] { lblLogTitle, btnClearLog, btnLiveLog, rtbLog });
            logPanel.SizeChanged += (s, e) =>
            {
                rtbLog.SetBounds(0, 32, logPanel.Width, logPanel.Height - 32);
            };

            Controls.Add(logPanel);
            logPanel.SetBounds(ClientSize.Width - logW, 52, logW, ClientSize.Height - 52);
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOME TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildHomeTab()
        {
            var p = tpHome;

            // Telem control row
            btnGetTelemNow = MkButton("▶ Get Telem", 10, 12, 130, Theme.Cyan, Theme.PanelBack);
            btnGetTelemNow.Click += async (s, e) => await SendGetTelemAsync();

            btnTelemAuto = MkButton("Auto: OFF", 150, 12, 100, Theme.Gray, Theme.PanelBack);
            btnTelemAuto.Click += BtnTelemAuto_Click;

            lblTelemAge = MkLabel("Last update: —", 264, 17, 300, Theme.Gray, Theme.FontSmall);

            p.Controls.AddRange(new Control[] { btnGetTelemNow, btnTelemAuto, lblTelemAge });

            // Telem panels — two columns
            int col1 = 10, col2 = 310, rowStart = 52, rowH = 68;

            p.Controls.Add(BuildTelemPanel("Uptime",         col1, rowStart + rowH * 0, ref lblUptime,   "—"));
            p.Controls.Add(BuildTelemPanel("Last RSSI (dBm)", col2, rowStart + rowH * 0, ref lblRssi,     "—"));
            p.Controls.Add(BuildTelemPanel("Last LQI",        col1, rowStart + rowH * 1, ref lblLqi,      "—"));
            p.Controls.Add(BuildTelemPanel("Last Freq Est",   col2, rowStart + rowH * 1, ref lblRssi,     "—")); // reuse lblRssi slot — fix below
            p.Controls.Add(BuildTelemPanel("Packets Good",    col1, rowStart + rowH * 2, ref lblPktsGood, "—"));
            p.Controls.Add(BuildTelemPanel("Packets Sent",    col2, rowStart + rowH * 2, ref lblPktsSent, "—"));
            p.Controls.Add(BuildTelemPanel("Rej Checksum",    col1, rowStart + rowH * 3, ref lblRejCksum, "—"));
            p.Controls.Add(BuildTelemPanel("Rej Other",       col2, rowStart + rowH * 3, ref lblRejOther, "—"));
            p.Controls.Add(BuildTelemPanel("UART0 RX Count",  col1, rowStart + rowH * 4, ref lblUart0,    "—"));
            p.Controls.Add(BuildTelemPanel("UART1 RX Count",  col2, rowStart + rowH * 4, ref lblUart1,    "—"));
            p.Controls.Add(BuildTelemPanel("RX Mode",         col1, rowStart + rowH * 5, ref lblRxMode,   "—"));
            p.Controls.Add(BuildTelemPanel("TX Mode",         col2, rowStart + rowH * 5, ref lblTxMode,   "—"));
        }

        private Panel BuildTelemPanel(string title, int x, int y, ref Label valueLabel, string defaultVal)
        {
            var panel = new Panel
            {
                Location  = new Point(x, y),
                Size      = new Size(285, 60),
                BackColor = Theme.PanelBack,
            };
            DrawBorder(panel);

            var lbl = MkLabel(title.ToUpper(), 10, 6, 260, Theme.Gray, Theme.FontSmall);
            var val = new Label
            {
                Location  = new Point(10, 26),
                Size      = new Size(260, 28),
                Text      = defaultVal,
                ForeColor = Theme.Green,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 16f, FontStyle.Bold),
            };
            panel.Controls.AddRange(new Control[] { lbl, val });
            valueLabel = val;
            return panel;
        }

        // ══════════════════════════════════════════════════════════════════
        //  COMMANDS TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildCommandsTab()
        {
            var p = tpCommands;
            int col1 = 10, col2 = 210, col3 = 410;
            int row = 12;

            p.Controls.Add(MkSectionLabel("── Board Commands", col1, row));
            row += 28;

            // Standard command buttons
            var cmds = new[]
            {
                ("Get Telem",     "get_telem"),
                ("Get Callsign",  "get_callsign"),
                ("Get Time",      "get_time"),
                ("Reboot",        "reboot"),
            };

            int bx = col1, by = row;
            foreach (var (label, cmd) in cmds)
            {
                var btn = MkButton(label, bx, by, 170, Theme.Cyan, Theme.PanelBack);
                string cmdCopy = cmd;
                btn.Click += async (s, e) => await SendSimpleCommandAsync(cmdCopy);
                p.Controls.Add(btn);
                bx += 184;
                if (bx > 600) { bx = col1; by += 36; }
            }

            row = by + 46;
            p.Controls.Add(MkSectionLabel("── Callsign", col1, row));
            row += 28;

            p.Controls.Add(MkLabel("Callsign:", col1, row + 3, 80, Theme.Gray));
            txtCallsign = new TextBox
            {
                Location  = new Point(col1 + 84, row),
                Size      = new Size(200, 26),
                BackColor = Theme.PanelBack,
                ForeColor = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font      = Theme.FontMono,
                MaxLength = 6,
            };
            var btnSetCallsign = MkButton("Set Callsign", col1 + 294, row, 130, Theme.Cyan, Theme.PanelBack);
            btnSetCallsign.Click += async (s, e) => await SendSetCallsignAsync();
            p.Controls.AddRange(new Control[] { txtCallsign, btnSetCallsign });

            row += 46;
            p.Controls.Add(MkSectionLabel("── Raw Command", col1, row));
            row += 28;

            p.Controls.Add(MkLabel("Command:", col1, row + 3, 80, Theme.Gray));
            txtRawCmd = new TextBox
            {
                Location    = new Point(col1 + 84, row),
                Size        = new Size(340, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                PlaceholderText = "e.g.  lst get_telem",
            };
            var btnRawSend = MkButton("Send", col1 + 434, row, 80, Theme.Yellow, Theme.PanelBack);
            btnRawSend.Click += async (s, e) => await SendRawCommandAsync();
            p.Controls.AddRange(new Control[] { txtRawCmd, btnRawSend });
        }

        // ══════════════════════════════════════════════════════════════════
        //  FIRMWARE TAB  (proven OTA flash logic from OpenLstFlasher)
        // ══════════════════════════════════════════════════════════════════
        private void BuildFirmwareTab()
        {
            var p = tpFirmware;
            int lx = 10, row = 12;

            p.Controls.Add(MkSectionLabel("── Build", lx, row));
            row += 28;

            // Project directory
            p.Controls.Add(MkLabel("Project Dir:", lx, row + 3, 95, Theme.Gray));
            txtProjectDir = new TextBox
            {
                Location    = new Point(lx + 98, row),
                Size        = new Size(440, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                PlaceholderText = "Path to OpenLST project folder",
            };
            btnBrowseDir = MkButton("Browse…", lx + 548, row, 80, Theme.Gray, Theme.PanelBack);
            btnBrowseDir.Click += BtnBrowseDir_Click;
            p.Controls.AddRange(new Control[] { txtProjectDir, btnBrowseDir });
            row += 36;

            btnBuild = MkButton("▶ Build", lx, row, 110, Theme.Yellow, Theme.PanelBack);
            btnBuild.Click += async (s, e) => await RunBuildAsync();

            var btnClean = MkButton("⌧ Clean", lx + 120, row, 90, Theme.Gray, Theme.PanelBack);
            btnClean.Click += (s, e) => RunClean();

            var btnCleanBuild = MkButton("⌧ Clean + Build", lx + 220, row, 140, Theme.Magenta, Theme.PanelBack);
            btnCleanBuild.Click += async (s, e) => { RunClean(); await RunBuildAsync(); };

            p.Controls.AddRange(new Control[] { btnBuild, btnClean, btnCleanBuild });
            row += 42;

            p.Controls.Add(MkSectionLabel("── Sign & Flash", lx, row));
            row += 28;

            // Firmware hex file
            p.Controls.Add(MkLabel("HEX File:", lx, row + 3, 75, Theme.Gray));
            txtFirmwareFile = new TextBox
            {
                Location    = new Point(lx + 78, row),
                Size        = new Size(460, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                PlaceholderText = "openlst_437_radio.hex",
            };
            btnBrowseHex = MkButton("Browse…", lx + 548, row, 80, Theme.Gray, Theme.PanelBack);
            btnBrowseHex.Click += BtnBrowseHex_Click;
            p.Controls.AddRange(new Control[] { txtFirmwareFile, btnBrowseHex });
            row += 36;

            // AES key
            p.Controls.Add(MkLabel("AES Key:", lx, row + 3, 75, Theme.Gray));
            txtAesKey = new TextBox
            {
                Location    = new Point(lx + 78, row),
                Size        = new Size(340, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                PlaceholderText = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                Text        = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            };
            p.Controls.Add(txtAesKey);
            row += 36;

            // Action buttons
            btnSign = MkButton("Sign", lx, row, 100, Theme.Cyan, Theme.PanelBack);
            btnSign.Click += BtnSign_Click;

            btnFlash = MkButton("▶ Flash OTA", lx + 112, row, 130, Theme.Green, Theme.PanelBack);
            btnFlash.BackColor = Color.FromArgb(20, 60, 20);
            btnFlash.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnFlash.Click += async (s, e) => await RunFlashAsync();

            btnBuildFlash = MkButton("▶ Build + Flash", lx + 254, row, 150, Theme.Yellow, Theme.PanelBack);
            btnBuildFlash.Click += async (s, e) => await RunBuildAndFlashAsync();

            p.Controls.AddRange(new Control[] { btnSign, btnFlash, btnBuildFlash });
            row += 46;

            // Progress bar + status
            pbFlash = new ProgressBar
            {
                Location  = new Point(lx, row),
                Size      = new Size(620, 16),
                Style     = ProgressBarStyle.Continuous,
                BackColor = Theme.PanelBack,
                ForeColor = Theme.Green,
            };
            row += 22;
            lblFlashStatus = MkLabel("Ready", lx, row, 620, Theme.Gray, Theme.FontSmall);

            p.Controls.AddRange(new Control[] { pbFlash, lblFlashStatus });
        }

        // ══════════════════════════════════════════════════════════════════
        //  TERMINAL TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildTerminalTab()
        {
            var p = tpTerminal;

            // ── Terminal output ───────────────────────────────────────────
            rtbTerminal = new RichTextBox
            {
                Location    = new Point(8, 8),
                BackColor   = Theme.LogBack,
                ForeColor   = Theme.White,
                Font        = Theme.FontMono,
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                WordWrap    = true,
                Anchor      = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            p.SizeChanged += (s, e) =>
                rtbTerminal.SetBounds(8, 8, p.Width - 16, p.Height - 50);

            var inputRow = new Panel
            {
                BackColor = Theme.PanelBack,
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            p.SizeChanged += (s, e) =>
                inputRow.SetBounds(8, p.Height - 40, p.Width - 16, 32);

            txtTermInput = new TextBox
            {
                Location        = new Point(0, 3),
                Size            = new Size(500, 26),
                BackColor       = Theme.LogBack,
                ForeColor       = Theme.White,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "lst get_telem",
            };
            txtTermInput.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; _ = SendTerminalCommandAsync(); }
            };

            btnTermSend = MkButton("Send", 510, 3, 70, Theme.Cyan, Theme.PanelBack);
            btnTermSend.Click += async (s, e) => await SendTerminalCommandAsync();

            btnTermClear = MkButton("Clear", 590, 3, 70, Theme.Gray, Theme.PanelBack);
            btnTermClear.Click += (s, e) => rtbTerminal.Clear();

            inputRow.Controls.AddRange(new Control[] { txtTermInput, btnTermSend, btnTermClear });
            p.Controls.AddRange(new Control[] { rtbTerminal, inputRow });
        }

        // ══════════════════════════════════════════════════════════════════
        //  CUSTOM COMMANDS TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildCustomCommandsTab()
        {
            var p = tpCustom;
            _customCommands = CustomCommandStore.Load();

            // Left: list
            p.Controls.Add(MkSectionLabel("── Saved Commands", 10, 10));
            lstCustomCmds = new ListBox
            {
                Location  = new Point(10, 36),
                Size      = new Size(220, 360),
                BackColor = Theme.PanelBack,
                ForeColor = Theme.White,
                Font      = Theme.FontMono,
                BorderStyle = BorderStyle.FixedSingle,
            };
            lstCustomCmds.SelectedIndexChanged += LstCustomCmds_SelectedIndexChanged;
            p.Controls.Add(lstCustomCmds);
            RefreshCustomCmdList();

            // Right: editor
            int ex = 248, ey = 10;
            p.Controls.Add(MkSectionLabel("── Command Editor", ex, ey)); ey += 28;

            p.Controls.Add(MkLabel("Name:",    ex, ey + 3, 70, Theme.Gray));
            txtCmdName = MkTextBox(ex + 74, ey, 260); p.Controls.Add(txtCmdName); ey += 34;

            p.Controls.Add(MkLabel("Opcode:",  ex, ey + 3, 70, Theme.Gray));
            txtCmdOpcode = MkTextBox(ex + 74, ey, 80);
            txtCmdOpcode.PlaceholderText = "0x20";
            p.Controls.Add(txtCmdOpcode); ey += 34;

            p.Controls.Add(MkLabel("Type:",    ex, ey + 3, 70, Theme.Gray));
            cmbCmdType = new ComboBox
            {
                Location      = new Point(ex + 74, ey),
                Size          = new Size(120, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                Font          = Theme.FontMono,
            };
            cmbCmdType.Items.AddRange(new object[] { "RawHex", "Structured" });
            cmbCmdType.SelectedIndex = 0;
            p.Controls.Add(cmbCmdType); ey += 34;

            p.Controls.Add(MkLabel("Payload:", ex, ey + 3, 70, Theme.Gray));
            txtCmdPayload = MkTextBox(ex + 74, ey, 260);
            txtCmdPayload.PlaceholderText = "hex bytes e.g. 01 02 03";
            p.Controls.Add(txtCmdPayload); ey += 34;

            p.Controls.Add(MkLabel("Notes:",   ex, ey + 3, 70, Theme.Gray));
            txtCmdNotes = MkTextBox(ex + 74, ey, 260); p.Controls.Add(txtCmdNotes); ey += 44;

            btnCmdSave   = MkButton("Save",   ex,       ey, 80, Theme.Cyan,  Theme.PanelBack);
            btnCmdDelete = MkButton("Delete", ex + 90,  ey, 80, Theme.Red,   Theme.PanelBack);
            btnCmdSend   = MkButton("▶ Send", ex + 180, ey, 100, Theme.Green, Theme.PanelBack);
            btnCmdSend.BackColor = Color.FromArgb(20, 60, 20);

            btnCmdSave.Click   += BtnCmdSave_Click;
            btnCmdDelete.Click += BtnCmdDelete_Click;
            btnCmdSend.Click   += async (s, e) => await SendCustomCommandAsync();

            p.Controls.AddRange(new Control[] { btnCmdSave, btnCmdDelete, btnCmdSend });
        }

        // ══════════════════════════════════════════════════════════════════
        //  TIMERS
        // ══════════════════════════════════════════════════════════════════
        private void WireTimers()
        {
            _telemTimer = new System.Windows.Forms.Timer { Interval = 5000 };
            _telemTimer.Tick += async (s, e) => await SendGetTelemAsync();

            _liveLogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _liveLogTimer.Tick += LiveLogTimer_Tick;
        }

        // ══════════════════════════════════════════════════════════════════
        //  SERIAL CONNECT / DISCONNECT
        // ══════════════════════════════════════════════════════════════════
        private void BtnConnect_Click(object sender, EventArgs e)
        {
            if (_port != null && _port.IsOpen)
            {
                _port.DataReceived -= Port_DataReceived;
                _port.Close();
                _port.Dispose();
                _port = null;
                btnConnect.Text = "Connect";
                SetStatus("● Disconnected", Theme.Red);
                Log("Serial port closed.", Theme.Gray);
                return;
            }

            string portName = cmbPort.SelectedItem?.ToString() ?? "";
            int    baud     = int.TryParse(cmbBaud.SelectedItem?.ToString(), out int b) ? b : 115200;

            if (string.IsNullOrEmpty(portName)) { Log("No port selected.", Theme.Red); return; }

            try
            {
                _port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout     = SerialPort.InfiniteTimeout,
                    WriteTimeout    = 2000,
                    ReadBufferSize  = 65536,
                    WriteBufferSize = 65536,
                };
                _port.DataReceived += Port_DataReceived;
                _port.Open();
                btnConnect.Text = "Disconnect";
                SetStatus($"● Connected  {portName} @ {baud}", Theme.Green);
                Log($"Connected: {portName} @ {baud}", Theme.Green);
            }
            catch (Exception ex)
            {
                Log($"Connect failed: {ex.Message}", Theme.Red);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  SERIAL RECEIVE
        // ══════════════════════════════════════════════════════════════════
        private void Port_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int avail = _port!.BytesToRead;
                if (avail <= 0) return;
                byte[] buf = new byte[avail];
                int    read = _port.Read(buf, 0, avail);
                lock (_rxLock)
                {
                    for (int i = 0; i < read; i++) _rxBuf.Add(buf[i]);
                    var packets = OpenLstProtocol.FramePackets(_rxBuf, msg => LogRx(msg));
                    foreach (var pkt in packets)
                    {
                        _rxQueue.Enqueue(pkt);
                        // Update Home tab telem if it's a telem packet for our HWID
                        if (pkt.OpName == "telem" && pkt.Hwid == ActiveHwid)
                        {
                            var td = OpenLstProtocol.ParseTelem(pkt.RawPayload);
                            if (td != null) BeginInvoke(new Action(() => UpdateTelemDisplay(td)));
                        }
                    }
                }
            }
            catch (Exception ex) { LogRx($"[RX ERROR] {ex.Message}"); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  SEND HELPERS
        // ══════════════════════════════════════════════════════════════════
        private void FlushRxQueue()
        {
            int n = 0;
            while (_rxQueue.TryDequeue(out _)) n++;
            if (n > 0) Log($"  [flush] discarded {n} stale packet(s)", Theme.Gray);
        }

        private async Task<RxPacket?> WaitForReply(ushort hwid, ushort seq, int timeoutMs)
        {
            int elapsed = 0;
            while (elapsed < timeoutMs)
            {
                while (_rxQueue.TryDequeue(out RxPacket? pkt))
                {
                    if (pkt.Hwid == hwid && pkt.SeqNum == seq) return pkt;
                    Log($"  [discard] hwid={pkt.Hwid:X4} seq={pkt.SeqNum} op={pkt.OpName}", Theme.Gray);
                }
                await Task.Delay(10);
                elapsed += 10;
            }
            return null;
        }

        private void WritePacket(byte[] packet)
        {
            if (_port == null || !_port.IsOpen) throw new InvalidOperationException("Not connected");
            _port.Write(packet, 0, packet.Length);
        }

        private bool CheckConnected()
        {
            if (_port != null && _port.IsOpen) return true;
            Log("Not connected.", Theme.Red);
            return false;
        }

        // ══════════════════════════════════════════════════════════════════
        //  COMMAND SENDERS
        // ══════════════════════════════════════════════════════════════════
        private async Task SendSimpleCommandAsync(string commandName)
        {
            if (!CheckConnected()) return;
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, seq, commandName));
            LogTx($"{commandName}  (seq={seq})", hwid);

            var pkt = await WaitForReply(hwid, seq, 3000);
            if (pkt != null) Log($"  ✓ {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
            else             Log($"  ✗ No reply", Theme.Red);
        }

        private async Task SendGetTelemAsync()
        {
            if (!CheckConnected()) return;
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, seq, "get_telem"));
            LogTx($"get_telem  (seq={seq})", hwid);
            var pkt = await WaitForReply(hwid, seq, 3000);
            if (pkt != null)
            {
                Log($"  ✓ telem received  hwid={pkt.Hwid:X4}", Theme.Green);
                var td = OpenLstProtocol.ParseTelem(pkt.RawPayload);
                if (td != null) UpdateTelemDisplay(td);
            }
            else Log("  ✗ No telem reply", Theme.Red);
        }

        private async Task SendSetCallsignAsync()
        {
            if (!CheckConnected()) return;
            string cs = txtCallsign.Text.Trim();
            if (string.IsNullOrEmpty(cs)) { Log("Enter a callsign first.", Theme.Red); return; }
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            byte[] args = System.Text.Encoding.ASCII.GetBytes(cs);
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, OpenLstProtocol.Opcodes["set_callsign"], args));
            LogTx($"set_callsign {cs}  (seq={seq})", hwid);
            var pkt = await WaitForReply(hwid, seq, 3000);
            if (pkt != null) Log($"  ✓ {pkt.OpName}", Theme.Green);
            else             Log("  ✗ No reply", Theme.Red);
        }

        private async Task SendRawCommandAsync()
        {
            if (!CheckConnected()) return;
            string raw = txtRawCmd.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;

            // Parse "lst <commandname> [args...]"
            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens[0].ToLower() != "lst")
            { Log("Format: lst <command> [args]", Theme.Red); return; }

            string cmdName = tokens[1].ToLower();
            if (!OpenLstProtocol.Opcodes.TryGetValue(cmdName, out byte opcode))
            { Log($"Unknown command: {cmdName}", Theme.Red); return; }

            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, opcode));
            LogTx($"{raw}  (seq={seq})", hwid);
            var pkt = await WaitForReply(hwid, seq, 3000);
            if (pkt != null) Log($"  ✓ {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
            else             Log("  ✗ No reply", Theme.Red);
        }

        private async Task SendTerminalCommandAsync()
        {
            string raw = txtTermInput.Text.Trim();
            if (string.IsNullOrEmpty(raw)) return;
            TermLog($"> {raw}", Theme.Cyan);
            txtTermInput.Clear();

            if (!CheckConnected()) { TermLog("Not connected.", Theme.Red); return; }

            var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2 || tokens[0].ToLower() != "lst")
            { TermLog("Format: lst <command> [args]", Theme.Red); return; }

            string cmdName = tokens[1].ToLower();
            if (!OpenLstProtocol.Opcodes.TryGetValue(cmdName, out byte opcode))
            { TermLog($"Unknown command: {cmdName}", Theme.Red); return; }

            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, opcode));
            var pkt = await WaitForReply(hwid, seq, 3000);
            if (pkt != null) TermLog($"< {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
            else             TermLog("< No reply", Theme.Red);
        }

        // ── Pico raw byte sender ───────────────────────────────────────────
        // Sends a single raw byte directly to the serial port — NO OpenLST framing.
        // Used for direct Pico testing before the CC1110 radio pipe is in place.
        // The Pico MicroPython code listens for these raw bytes on its UART0.
        private async Task SendCustomCommandAsync()
        {
            if (lstCustomCmds.SelectedIndex < 0) { Log("Select a command first.", Theme.Red); return; }
            if (!CheckConnected()) return;

            var cmd  = _customCommands[lstCustomCmds.SelectedIndex];
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();

            byte[]? args = null;
            if (!string.IsNullOrWhiteSpace(cmd.Payload))
            {
                try
                {
                    string hex = cmd.Payload.Replace(" ", "").Replace("-", "");
                    args = Enumerable.Range(0, hex.Length / 2)
                        .Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16))
                        .ToArray();
                }
                catch { Log("Invalid payload hex.", Theme.Red); return; }
            }

            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, cmd.Opcode, args));
            LogTx($"[CUSTOM] {cmd.Name}  opcode=0x{cmd.Opcode:X2}  (seq={seq})", hwid);
            var pkt = await WaitForReply(hwid, seq, 15000);  // 15s — allows for camera capture time
            if (pkt != null)
            {
                if (pkt.PicoPayload != null)
                    Log($"  ✓ {pkt.OpName} → {pkt.PicoPayload}", Theme.Green);
                else
                    Log($"  ✓ {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
            }
            else Log("  ✗ No reply", Theme.Red);
        }

        // ══════════════════════════════════════════════════════════════════
        //  FIRMWARE: BUILD / SIGN / FLASH  (from OpenLstFlasher — unchanged)
        // ══════════════════════════════════════════════════════════════════
        private void BtnBrowseDir_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select OpenLST project directory" };
            if (dlg.ShowDialog() == DialogResult.OK) { txtProjectDir.Text = dlg.SelectedPath; SaveSettings(); }
        }

        private void BtnBrowseHex_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog { Filter = "Intel HEX (*.hex)|*.hex|All files (*.*)|*.*" };
            if (dlg.ShowDialog() == DialogResult.OK) { txtFirmwareFile.Text = dlg.FileName; SaveSettings(); }
        }

        private void BtnSign_Click(object sender, EventArgs e)
        {
            string hexPath = txtFirmwareFile.Text.Trim();
            if (!File.Exists(hexPath)) { Log("HEX file not found.", Theme.Red); return; }
            if (!TryGetAesKey(out byte[] aesKey)) return;

            try
            {
                byte[] app = OpenLstProtocol.ParseIntelHex(hexPath);
                byte[] sig = OpenLstProtocol.SignFirmware(app, aesKey);
                Log($"Signature (CBC-MAC): {BitConverter.ToString(sig).Replace("-","").ToLower()}", Theme.Green);
                Log("(Signature computed — will be inserted automatically at flash time)", Theme.Gray);
            }
            catch (Exception ex) { Log($"Sign error: {ex.Message}", Theme.Red); }
        }

        private async Task RunBuildAsync()
        {
            if (string.IsNullOrWhiteSpace(txtProjectDir.Text) || !Directory.Exists(txtProjectDir.Text))
            { Log("Select a valid project directory first.", Theme.Red); return; }

            SetFirmwareButtons(false);
            lblFlashStatus.Text = "Building...";
            pbFlash.Style = ProgressBarStyle.Marquee;
            Log("Starting build: mingw32-make openlst_437_radio", Theme.Cyan);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = "mingw32-make",
                    Arguments              = "openlst_437_radio",
                    WorkingDirectory       = txtProjectDir.Text,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                string cur = Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.EnvironmentVariables["PATH"] = cur + @";C:\Program Files\SDCC\bin";

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());

                foreach (string line in stdout.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Where(l => !l.Contains("Nothing to be done")))  // not an error, just means already built
                    Log(line.Trim(), Theme.White);
                foreach (string line in stderr.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Where(l => !l.Contains("fatal: not a git repository")))  // makefile calls git describe — not our error
                    Log(line.Trim(), Theme.Yellow);

                if (proc.ExitCode == 0)
                {
                    string hex = Path.Combine(txtProjectDir.Text, "openlst_437_radio.hex");
                    if (File.Exists(hex))
                    {
                        txtFirmwareFile.Text = hex;
                        Log($"Build succeeded: {hex}", Theme.Green);
                        lblFlashStatus.Text = "Build complete — ready to flash.";
                    }
                    else { Log("Build OK but .hex not found.", Theme.Red); lblFlashStatus.Text = "Build OK but .hex missing."; }
                }
                else { Log($"Build FAILED (exit {proc.ExitCode})", Theme.Red); lblFlashStatus.Text = "Build failed."; }
            }
            catch (Exception ex) { Log($"Build exception: {ex.Message}", Theme.Red); }
            finally
            {
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                pbFlash.Value = 0;
            }
        }

        private async Task RunFlashAsync()
        {
            if (!CheckConnected()) return;

            string hexPath = txtFirmwareFile.Text.Trim();
            if (!File.Exists(hexPath)) { Log("HEX file not found.", Theme.Red); return; }
            if (!TryGetAesKey(out byte[] aesKey)) return;

            ushort hwid = ActiveHwid;
            SetFirmwareButtons(false);

            try
            {
                Log("═══════════════════════════════════════════════════", Theme.Cyan);
                Log($"  OTA Flash  →  HWID {hwid:X4}", Theme.Cyan);
                Log("═══════════════════════════════════════════════════", Theme.Cyan);

                byte[] app = OpenLstProtocol.ParseIntelHex(hexPath);
                Log($"HEX parsed. Buffer = {app.Length} bytes.", Theme.White);

                byte[] sig = OpenLstProtocol.SignFirmware(app, aesKey);
                Array.Copy(sig, 0, app, OpenLstProtocol.FLASH_SIGNATURE_START, OpenLstProtocol.FLASH_SIGNATURE_LEN);
                Log($"Signature (CBC-MAC): {BitConverter.ToString(sig).Replace("-","").ToLower()}", Theme.Green);

                await FlashApplicationAsync(app, hwid);
            }
            catch (Exception ex) { Log($"FATAL: {ex.Message}", Theme.Red); }
            finally { SetFirmwareButtons(true); }
        }

        private async Task RunBuildAndFlashAsync()
        {
            await RunBuildAsync();
            if (File.Exists(txtFirmwareFile.Text.Trim()))
                await RunFlashAsync();
        }

        private void SetFirmwareButtons(bool enabled)
        {
            btnBuild.Enabled      = enabled;
            btnSign.Enabled       = enabled;
            btnFlash.Enabled      = enabled;
            btnBuildFlash.Enabled = enabled;
        }

        // ── Clean radio build artifacts ────────────────────────────────────
        // Deletes only *.openlst_437.rel (and associated) files — NOT bootloader
        // Bootloader files contain .bl. in the name and are left untouched.
        // This avoids the "No rule to make target flash_trigger" error that
        // occurs when bootloader .rel files are accidentally deleted.
        private void RunClean()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            { Log("Select a valid project directory first.", Theme.Red); return; }

            // Radio-only extensions to clean — never touch .bl. files
            string[] extensions = { ".rel", ".asm", ".lst", ".rst", ".sym" };
            // Only delete files that contain "openlst_437" but NOT ".bl."
            // .bl. = bootloader intermediate — must be preserved
            int deleted = 0;
            int skipped = 0;

            try
            {
                foreach (string ext in extensions)
                {
                    foreach (string file in Directory.GetFiles(dir, $"*openlst_437*{ext}",
                        SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(file);
                        if (name.Contains(".bl."))
                        {
                            skipped++;
                            continue;  // preserve bootloader files
                        }
                        File.Delete(file);
                        deleted++;
                    }
                }

                // Also delete the output hex and map files
                foreach (string file in new[]
                {
                    Path.Combine(dir, "openlst_437_radio.hex"),
                    Path.Combine(dir, "openlst_437_radio.lk"),
                    Path.Combine(dir, "openlst_437_radio.mem"),
                    Path.Combine(dir, "openlst_437_radio.map"),
                })
                {
                    if (File.Exists(file)) { File.Delete(file); deleted++; }
                }

                Log($"Clean complete — {deleted} radio files deleted, {skipped} bootloader files preserved.", Theme.Cyan);
                lblFlashStatus.Text = "Cleaned — ready to build.";
            }
            catch (Exception ex)
            {
                Log($"Clean error: {ex.Message}", Theme.Red);
            }
        }

        private bool TryGetAesKey(out byte[] key)
        {
            key = Array.Empty<byte>();
            string s = txtAesKey.Text.Trim();
            if (s.Length != 32) { Log("AES key must be 32 hex chars.", Theme.Red); return false; }
            try
            {
                key = Enumerable.Range(0, 16).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray();
                return true;
            }
            catch { Log("Invalid AES key hex.", Theme.Red); return false; }
        }

        // ── OTA flash engine (exact copy from OpenLstFlasher — no logic changes) ──
        private async Task FlashApplicationAsync(byte[] appSection, ushort hwid)
        {
            Log("── PHASE 1: Enter Bootloader ──────────────────────────", Theme.Cyan);
            bool inBootloader = false;
            int  bootLoop = 0;

            while (!inBootloader)
            {
                bootLoop++;
                Log($"  Loop {bootLoop}: reboot → bootloader_erase", Theme.Yellow);

                ushort rSeq = IncSeqNum();
                FlushRxQueue();
                WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, rSeq, "reboot"));
                LogTx($"reboot (seq={rSeq})", hwid);
                await Task.Delay(100);
                await Task.Delay(200);

                ushort eSeq = IncSeqNum();
                FlushRxQueue();
                for (int t = 0; t <= 1 && !inBootloader; t++)
                {
                    if (t > 0) FlushRxQueue();
                    WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, eSeq, "bootloader_erase"));
                    LogTx($"bootloader_erase (seq={eSeq}, send {t+1}/2)", hwid);
                    var pkt = await WaitForReply(hwid, eSeq, 2000);
                    if (pkt != null && pkt.OpName == "bootloader_ack" && pkt.AckValue == 1)
                    { inBootloader = true; Log("  ✓ bootloader_ack 1 — flash erased.", Theme.Green); }
                    else if (pkt != null)
                        Log($"  Reply: {pkt.OpName} {pkt.AckValue} — not ready", Theme.Yellow);
                    else
                        Log($"  No reply (send {t+1}/2)", Theme.Yellow);
                }
                await Task.Delay(1500);
            }

            const int MAX_ATTEMPTS = 50;
            Log("── PHASE 2: Writing Pages ─────────────────────────────", Theme.Cyan);

            int totalPages = 0, skipped = 0;
            int firstPage = OpenLstProtocol.FLASH_APP_START / OpenLstProtocol.FLASH_PAGE_SIZE;
            int lastPage  = OpenLstProtocol.FLASH_APP_END   / OpenLstProtocol.FLASH_PAGE_SIZE;
            pbFlash.Minimum = firstPage; pbFlash.Maximum = lastPage; pbFlash.Value = firstPage;

            for (int addr = OpenLstProtocol.FLASH_APP_START; addr <= OpenLstProtocol.FLASH_APP_END; addr += OpenLstProtocol.FLASH_PAGE_SIZE)
            {
                int pageNum = addr / OpenLstProtocol.FLASH_PAGE_SIZE;
                byte[] pageData = new byte[OpenLstProtocol.FLASH_PAGE_SIZE];
                int copyLen = Math.Max(0, Math.Min(OpenLstProtocol.FLASH_PAGE_SIZE, appSection.Length - addr));
                if (copyLen > 0) Array.Copy(appSection, addr, pageData, 0, copyLen);
                for (int f = copyLen; f < OpenLstProtocol.FLASH_PAGE_SIZE; f++) pageData[f] = 0xFF;

                if (pageData.All(b => b == 0xFF))
                { skipped++; lblFlashStatus.Text = $"Page {pageNum} — skipping"; pbFlash.Value = Math.Clamp(pageNum, firstPage, lastPage); continue; }

                string hexData = BitConverter.ToString(pageData).Replace("-", "").ToLower();
                ushort pageSeq = IncSeqNum();
                bool   pageOk  = false;
                int    attempt = 0;

                while (!pageOk && attempt < MAX_ATTEMPTS)
                {
                    attempt++;
                    FlushRxQueue();
                    WritePacket(OpenLstProtocol.BuildBootloaderWritePage(hwid, pageSeq, pageNum, hexData));
                    LogTx($"write_page {pageNum,3} (seq={pageSeq}, attempt {attempt})", hwid);

                    var pkt = await WaitForReply(hwid, pageSeq, 1200);
                    if (pkt != null && pkt.OpName == "bootloader_ack" && pkt.AckValue == pageNum)
                    { pageOk = true; Log($"  Page {pageNum,3}: ✓ attempt={attempt}", Theme.Green); }
                    else if (pkt != null)
                        Log($"  Page {pageNum,3}: ✗ unexpected {pkt.OpName} {pkt.AckValue}", Theme.Red);
                    else
                        Log($"  Page {pageNum,3}: ✗ no reply (attempt {attempt})", Theme.Red);
                }

                if (!pageOk) throw new Exception($"Page {pageNum} failed after {MAX_ATTEMPTS} attempts.");

                totalPages++;
                pbFlash.Value    = Math.Clamp(pageNum, firstPage, lastPage);
                lblFlashStatus.Text = $"Page {pageNum}  ({totalPages} written, {skipped} skipped)";
            }

            Log("── PHASE 3: End Signal ────────────────────────────────", Theme.Cyan);
            ushort endSeq = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildBootloaderWritePage(hwid, endSeq, 255, null));
            LogTx($"write_page 255 (seq={endSeq}) — board will reboot", hwid);
            await Task.Delay(500);

            Log("═══════════════════════════════════════════════════", Theme.Cyan);
            Log($"  FLASH COMPLETE ✓  {totalPages} written, {skipped} skipped.", Theme.Green);
            Log("═══════════════════════════════════════════════════", Theme.Cyan);
            pbFlash.Value    = pbFlash.Maximum;
            lblFlashStatus.Text = $"Done — {totalPages} pages written.";
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOME TAB — TELEM DISPLAY
        // ══════════════════════════════════════════════════════════════════
        private void UpdateTelemDisplay(TelemData td)
        {
            _lastTelem = td;

            TimeSpan up = TimeSpan.FromSeconds(td.Uptime);
            lblUptime.Text   = up.TotalDays >= 1
                ? $"{(int)up.TotalDays}d {up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}"
                : $"{up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
            lblRssi.Text     = $"{td.LastRssi} dBm";
            lblLqi.Text      = td.LastLqi.ToString();
            lblPktsGood.Text = td.PacketsGood.ToString("N0");
            lblPktsSent.Text = td.PacketsSent.ToString("N0");
            lblRejCksum.Text = td.PacketsRejChecksum.ToString("N0");
            lblRejOther.Text = (td.PacketsRejOther + td.PacketsRejReserved).ToString("N0");
            lblUart0.Text    = td.Uart0RxCount.ToString("N0");
            lblUart1.Text    = td.Uart1RxCount.ToString("N0");
            lblRxMode.Text   = td.RxMode.ToString();
            lblTxMode.Text   = td.TxMode.ToString();
            lblTelemAge.Text = $"Last update: {DateTime.Now:HH:mm:ss}";
        }

        private void BtnTelemAuto_Click(object sender, EventArgs e)
        {
            if (_telemTimer!.Enabled)
            { _telemTimer.Stop(); btnTelemAuto.Text = "Auto: OFF"; btnTelemAuto.ForeColor = Theme.Gray; }
            else
            { _telemTimer.Start(); btnTelemAuto.Text = "Auto: ON"; btnTelemAuto.ForeColor = Theme.Green; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  LIVE LOG
        // ══════════════════════════════════════════════════════════════════
        private void BtnLiveLog_Click(object sender, EventArgs e)
        {
            _liveLogEnabled = !_liveLogEnabled;
            if (_liveLogEnabled)
            { _liveLogTimer!.Start(); btnLiveLog.Text = "Live: ON"; btnLiveLog.ForeColor = Theme.Green; }
            else
            { _liveLogTimer!.Stop(); btnLiveLog.Text = "Live: OFF"; btnLiveLog.ForeColor = Theme.Gray; }
        }

        private void LiveLogTimer_Tick(object? sender, EventArgs e)
        {
            // Drain and log anything sitting in the RX queue
            while (_rxQueue.TryDequeue(out RxPacket? pkt))
                Log($"  [live] {pkt.OpName} hwid={pkt.Hwid:X4} seq={pkt.SeqNum}", Theme.Gray);

            // Also refresh telem on the Home tab if connected
            if (_port != null && _port.IsOpen)
                _ = SendGetTelemAsync();
        }

        // ══════════════════════════════════════════════════════════════════
        //  CUSTOM COMMANDS PERSISTENCE
        // ══════════════════════════════════════════════════════════════════
        private void RefreshCustomCmdList()
        {
            lstCustomCmds.Items.Clear();
            foreach (var c in _customCommands)
                lstCustomCmds.Items.Add($"0x{c.Opcode:X2}  {c.Name}");
        }

        private void LstCustomCmds_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (lstCustomCmds.SelectedIndex < 0) return;
            var c = _customCommands[lstCustomCmds.SelectedIndex];
            txtCmdName.Text    = c.Name;
            txtCmdOpcode.Text  = $"0x{c.Opcode:X2}";
            cmbCmdType.SelectedItem = c.Type;
            txtCmdPayload.Text = c.Payload;
            txtCmdNotes.Text   = c.Notes;
        }

        private void BtnCmdSave_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrWhiteSpace(txtCmdName.Text)) { Log("Enter a command name.", Theme.Red); return; }
            string opcStr = txtCmdOpcode.Text.Trim().Replace("0x","").Replace("0X","");
            if (!byte.TryParse(opcStr, System.Globalization.NumberStyles.HexNumber, null, out byte opc))
            { Log("Invalid opcode hex.", Theme.Red); return; }

            var cmd = lstCustomCmds.SelectedIndex >= 0
                ? _customCommands[lstCustomCmds.SelectedIndex]
                : new CustomCommand();

            cmd.Name    = txtCmdName.Text.Trim();
            cmd.Opcode  = opc;
            cmd.Type    = cmbCmdType.SelectedItem?.ToString() ?? "RawHex";
            cmd.Payload = txtCmdPayload.Text.Trim();
            cmd.Notes   = txtCmdNotes.Text.Trim();

            if (lstCustomCmds.SelectedIndex < 0) _customCommands.Add(cmd);
            CustomCommandStore.Save(_customCommands);
            RefreshCustomCmdList();
            Log($"Custom command '{cmd.Name}' saved.", Theme.Green);
        }

        private void BtnCmdDelete_Click(object sender, EventArgs e)
        {
            if (lstCustomCmds.SelectedIndex < 0) return;
            _customCommands.RemoveAt(lstCustomCmds.SelectedIndex);
            CustomCommandStore.Save(_customCommands);
            RefreshCustomCmdList();
        }

        // ══════════════════════════════════════════════════════════════════
        //  LOG HELPERS
        // ══════════════════════════════════════════════════════════════════
        private void Log(string message, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(message, color))); return; }
            AppLogger.Write(message);
            rtbLog.SelectionStart  = rtbLog.TextLength;
            rtbLog.SelectionLength = 0;
            rtbLog.SelectionColor  = color;
            rtbLog.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            rtbLog.SelectionColor  = rtbLog.ForeColor;
            rtbLog.ScrollToCaret();
        }

        private void LogTx(string desc, ushort hwid)
            => Log($"  → TX [{hwid:X4}]  {desc}", Theme.Cyan);

        private void LogRx(string desc)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => LogRx(desc))); return; }
            Log($"  ← RX  {desc}", Theme.White);
        }

        private void TermLog(string message, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => TermLog(message, color))); return; }
            rtbTerminal.SelectionStart  = rtbTerminal.TextLength;
            rtbTerminal.SelectionLength = 0;
            rtbTerminal.SelectionColor  = color;
            rtbTerminal.AppendText($"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            rtbTerminal.SelectionColor  = rtbTerminal.ForeColor;
            rtbTerminal.ScrollToCaret();
        }

        private void SetStatus(string text, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, color))); return; }
            lblStatus.Text      = text;
            lblStatus.ForeColor = color;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CONTROL FACTORY HELPERS  (keeps BuildXxxTab methods clean)
        // ══════════════════════════════════════════════════════════════════
        private static Label MkLabel(string text, int x, int y, int w,
            Color? color = null, Font? font = null)
            => new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(w, 22),
                ForeColor = color ?? Theme.Gray,
                BackColor = Color.Transparent,
                Font      = font ?? Theme.FontMono,
            };

        private static Label MkSectionLabel(string text, int x, int y)
            => MkLabel(text, x, y, 400, Theme.Cyan, Theme.FontMonoBold);

        private static Button MkButton(string text, int x, int y, int w,
            Color fore, Color back)
        {
            var btn = new Button
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(w, 28),
                BackColor = back,
                ForeColor = fore,
                FlatStyle = FlatStyle.Flat,
                Font      = Theme.FontMonoBold,
                Cursor    = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor = Theme.BorderColor;
            return btn;
        }

        private static TextBox MkTextBox(int x, int y, int w)
            => new TextBox
            {
                Location    = new Point(x, y),
                Size        = new Size(w, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
            };

        private static TabPage MkTab(string text)
            => new TabPage(text)
            {
                BackColor = Theme.TabBack,
                ForeColor = Theme.Silver,
                Font      = Theme.FontMono,
            };

        private static void DrawBorder(Panel panel)
        {
            panel.Paint += (s, e) =>
            {
                using var pen = new Pen(Theme.BorderColor, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
            };
        }

        // ══════════════════════════════════════════════════════════════════
        //  FILES TAB  —  List, download and delete files from Pico SD card
        // ══════════════════════════════════════════════════════════════════
        private void BuildFilesTab()
        {
            var p  = tpFiles;
            int lx = 10, row = 12;

            p.Controls.Add(MkSectionLabel("── SD Card Files", lx, row));
            row += 28;

            // File list
            lstFiles = new ListBox
            {
                Location    = new Point(lx, row),
                Size        = new Size(340, 280),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                Font        = Theme.FontMono,
                BorderStyle = BorderStyle.FixedSingle,
            };
            p.Controls.Add(lstFiles);

            // Buttons alongside list
            int bx = lx + 354;
            btnFilesRefresh = MkButton("↺ Refresh List", bx, row, 160, Theme.Cyan, Theme.PanelBack);
            btnFilesRefresh.Click += async (s, e) => await RefreshFileListAsync();

            btnFilesGet = MkButton("▼ Get File", bx, row + 40, 160, Theme.Green, Theme.PanelBack);
            btnFilesGet.BackColor = Color.FromArgb(20, 60, 20);
            btnFilesGet.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnFilesGet.Click += async (s, e) => await GetSelectedFileAsync();

            btnFilesDelete = MkButton("✕ Delete File", bx, row + 80, 160, Theme.Red, Theme.PanelBack);
            btnFilesDelete.BackColor = Color.FromArgb(60, 20, 20);
            btnFilesDelete.FlatAppearance.BorderColor = Color.FromArgb(120, 40, 40);
            btnFilesDelete.Click += async (s, e) => await DeleteSelectedFileAsync();

            p.Controls.AddRange(new Control[] { lstFiles, btnFilesRefresh, btnFilesGet, btnFilesDelete });
            row += 294;

            // Progress bar
            pbTransfer = new ProgressBar
            {
                Location  = new Point(lx, row),
                Size      = new Size(520, 16),
                Style     = ProgressBarStyle.Continuous,
                Minimum   = 0,
                Maximum   = 100,
                Value     = 0,
                BackColor = Theme.PanelBack,
                ForeColor = Theme.Green,
            };
            row += 22;
            lblTransferStatus = MkLabel("Ready", lx, row, 520, Theme.Gray, Theme.FontSmall);

            p.Controls.AddRange(new Control[] { pbTransfer, lblTransferStatus });
        }

        // ── Images folder ──────────────────────────────────────────────────
        private static string ImagesFolderPath
        {
            get
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        // ── Refresh file list ──────────────────────────────────────────────
        private async Task RefreshFileListAsync()
        {
            if (!CheckConnected()) return;
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();

            Log("Files: requesting list from Pico...", Theme.Cyan);
            lblTransferStatus.Text = "Refreshing...";

            // Send LIST command (opcode 0x20, sub-opcode 0x03)
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, new byte[] { 0x03 }));
            var pkt = await WaitForReply(hwid, seq, 10000);

            if (pkt?.PicoPayload == null)
            {
                Log("Files: no response from Pico", Theme.Red);
                lblTransferStatus.Text = "No response.";
                return;
            }

            string response = pkt.PicoPayload;
            lstFiles.Items.Clear();

            if (response.StartsWith("LIST:") && response != "LIST:EMPTY")
            {
                string filesPart = response.Substring(5);
                foreach (string f in filesPart.Split(','))
                {
                    if (!string.IsNullOrWhiteSpace(f))
                        lstFiles.Items.Add(f.Trim());
                }
                Log($"Files: {lstFiles.Items.Count} file(s) on SD card", Theme.Green);
                lblTransferStatus.Text = $"{lstFiles.Items.Count} file(s) found.";
            }
            else
            {
                Log("Files: SD card is empty", Theme.Gray);
                lblTransferStatus.Text = "SD card empty.";
            }
        }

        // ── Get selected file ──────────────────────────────────────────────
        private async Task GetSelectedFileAsync()
        {
            if (!CheckConnected()) return;
            if (lstFiles.SelectedItem == null)
            { Log("Files: select a file first.", Theme.Red); return; }
            if (_transferring)
            { Log("Files: transfer already in progress.", Theme.Red); return; }

            string   filename = lstFiles.SelectedItem.ToString()!;
            ushort   hwid     = ActiveHwid;
            _transferring     = true;
            btnFilesGet.Enabled    = false;
            btnFilesDelete.Enabled = false;

            try
            {
                // ── Step 1: GET_INFO ──────────────────────────────────────
                Log($"Files: requesting info for {filename}...", Theme.Cyan);
                ushort seq = IncSeqNum();
                FlushRxQueue();
                byte[] infoPayload = new byte[] { 0x04 }
                    .Concat(System.Text.Encoding.UTF8.GetBytes(filename)).ToArray();
                WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, infoPayload));
                var infoPkt = await WaitForReply(hwid, seq, 10000);

                if (infoPkt?.PicoPayload == null || !infoPkt.PicoPayload.StartsWith("INFO:"))
                {
                    Log($"Files: failed to get info for {filename}", Theme.Red);
                    lblTransferStatus.Text = "Get info failed.";
                    return;
                }

                // Parse INFO:<filename>:<bytes>:<chunks>
                string[] parts      = infoPkt.PicoPayload.Split(':');
                int      totalBytes  = int.Parse(parts[2]);
                int      totalChunks = int.Parse(parts[3]);
                Log($"Files: {filename} = {totalBytes} bytes, {totalChunks} chunks", Theme.White);

                pbTransfer.Minimum = 0;
                pbTransfer.Maximum = totalChunks;
                pbTransfer.Value   = 0;

                // ── Step 2: GET_CHUNK loop ────────────────────────────────
                var imageData = new System.IO.MemoryStream();
                byte[] fnBytes = System.Text.Encoding.UTF8.GetBytes(filename);

                for (int chunk = 0; chunk < totalChunks; chunk++)
                {
                    bool   success = false;
                    int    retries = 3;

                    while (retries > 0 && !success)
                    {
                        seq = IncSeqNum();
                        FlushRxQueue();

                        // Payload: 0x05 + chunk_index(2 bytes LE) + filename
                        byte[] chunkPayload = new byte[] {
                            0x05,
                            (byte)(chunk & 0xFF),
                            (byte)((chunk >> 8) & 0xFF)
                        }.Concat(fnBytes).ToArray();

                        WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, chunkPayload));
                        var chunkPkt = await WaitForReply(hwid, seq, 10000);

                        if (chunkPkt?.RawPayload != null)
                        {
                            // Find data after "CHUNK:<index>:" header
                            // RawPayload = hwid(2)+seq(2)+flag(1)+opcode(1)+data
                            byte[] raw  = chunkPkt.RawPayload.Skip(6).ToArray();
                            // raw starts with "CHUNK:N:" — find second colon
                            int firstColon  = Array.IndexOf(raw, (byte)':');
                            int secondColon = firstColon >= 0
                                ? Array.IndexOf(raw, (byte)':', firstColon + 1) : -1;

                            if (secondColon >= 0)
                            {
                                byte[] chunkData = raw.Skip(secondColon + 1).ToArray();
                                imageData.Write(chunkData, 0, chunkData.Length);
                                success = true;
                                pbTransfer.Value = chunk + 1;
                                lblTransferStatus.Text =
                                    $"Chunk {chunk + 1}/{totalChunks} — {imageData.Length}/{totalBytes} bytes";
                            }
                        }

                        if (!success)
                        {
                            retries--;
                            Log($"Files: chunk {chunk} failed, {retries} retries left", Theme.Yellow);
                            await Task.Delay(200);
                        }
                    }

                    if (!success)
                    {
                        Log($"Files: transfer failed at chunk {chunk}", Theme.Red);
                        lblTransferStatus.Text = $"Transfer failed at chunk {chunk}.";
                        return;
                    }
                }

                // ── Step 3: Save to Images folder ─────────────────────────
                string savePath = Path.Combine(ImagesFolderPath, filename);
                File.WriteAllBytes(savePath, imageData.ToArray());
                Log($"Files: ✓ {filename} saved ({imageData.Length} bytes) → {savePath}", Theme.Green);
                lblTransferStatus.Text = $"✓ {filename} saved to Images folder.";

                // Open the Images folder
                System.Diagnostics.Process.Start("explorer.exe", ImagesFolderPath);
            }
            catch (Exception ex)
            {
                Log($"Files: transfer exception: {ex.Message}", Theme.Red);
                lblTransferStatus.Text = "Transfer error.";
            }
            finally
            {
                _transferring          = false;
                btnFilesGet.Enabled    = true;
                btnFilesDelete.Enabled = true;
                pbTransfer.Value       = 0;
            }
        }

        // ── Delete selected file ───────────────────────────────────────────
        private async Task DeleteSelectedFileAsync()
        {
            if (!CheckConnected()) return;
            if (lstFiles.SelectedItem == null)
            { Log("Files: select a file first.", Theme.Red); return; }

            string filename = lstFiles.SelectedItem.ToString()!;

            var result = MessageBox.Show(
                $"Delete {filename} from SD card?",
                "Confirm Delete",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (result != DialogResult.Yes) return;

            ushort seq = IncSeqNum();
            FlushRxQueue();
            byte[] delPayload = new byte[] { 0x06 }
                .Concat(System.Text.Encoding.UTF8.GetBytes(filename)).ToArray();
            WritePacket(OpenLstProtocol.BuildPacket(ActiveHwid, seq, 0x20, delPayload));
            var pkt = await WaitForReply(ActiveHwid, seq, 10000);

            if (pkt?.PicoPayload?.StartsWith("DEL:OK") == true)
            {
                Log($"Files: {filename} deleted from SD card", Theme.Green);
                lstFiles.Items.Remove(filename);
                lblTransferStatus.Text = $"{filename} deleted.";
            }
            else
            {
                Log($"Files: delete failed for {filename}", Theme.Red);
                lblTransferStatus.Text = "Delete failed.";
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  PROVISION TAB  —  Flash bootloader onto a fresh CC1110 board
        //                    via CC Debugger + SmartRF Flash Programmer
        // ══════════════════════════════════════════════════════════════════
        private void BuildProvisionTab()
        {
            var p = tpProvision;
            int lx = 14, row = 12;

            // ── Instructions panel ────────────────────────────────────────
            var instrPanel = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(660, 100),
                BackColor = Color.FromArgb(16, 24, 16),
            };
            DrawBorder(instrPanel);

            var instrTitle = MkLabel("◈  Board Provisioning — What this does:", 10, 8, 500,
                Theme.Green, Theme.FontMonoBold);
            var instr1 = MkLabel("1. Patches bootloader hex in memory with your HWID and AES keys", 10, 28, 620, Theme.Silver, Theme.FontSmall);
            var instr2 = MkLabel("2. Writes patched image to a temp file", 10, 44, 620, Theme.Silver, Theme.FontSmall);
            var instr3 = MkLabel("3. Calls SmartRF Flash Programmer to erase, program and verify via CC Debugger", 10, 60, 620, Theme.Silver, Theme.FontSmall);
            var instr4 = MkLabel("⚠  CC Debugger must be plugged in and connected to the target board before flashing", 10, 78, 640, Theme.Yellow, Theme.FontSmall);
            instrPanel.Controls.AddRange(new Control[] { instrTitle, instr1, instr2, instr3, instr4 });
            p.Controls.Add(instrPanel);
            row += 114;

            // ── Detect CC Debugger ────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── Hardware", lx, row)); row += 28;

            btnProvDetect = MkButton("◎ Detect CC Debugger", lx, row, 190, Theme.Cyan, Theme.PanelBack);
            btnProvDetect.Click += BtnProvDetect_Click;

            lblProvStatus = MkLabel("Not checked", lx + 202, row + 4, 400, Theme.Gray, Theme.FontSmall);
            p.Controls.AddRange(new Control[] { btnProvDetect, lblProvStatus });
            row += 42;

            // ── Board identity ────────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── Board Identity", lx, row)); row += 28;

            p.Controls.Add(MkLabel("HWID (hex):", lx, row + 3, 90, Theme.Gray));
            txtProvHwid = new TextBox
            {
                Location    = new Point(lx + 94, row),
                Size        = new Size(70, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMonoBold,
                MaxLength   = 4,
                PlaceholderText = "0001",
            };
            p.Controls.Add(txtProvHwid);
            row += 38;

            // ── AES Keys ──────────────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── AES Signing Keys  (3 × 16 bytes hex)", lx, row)); row += 28;

            string[] keyLabels = { "Key 0:", "Key 1:", "Key 2:" };
            var keyBoxes = new[] { txtProvKey0 = null!, txtProvKey1 = null!, txtProvKey2 = null! };
            for (int k = 0; k < 3; k++)
            {
                int ki = k;
                p.Controls.Add(MkLabel(keyLabels[k], lx, row + 3, 50, Theme.Gray));
                var tb = new TextBox
                {
                    Location    = new Point(lx + 54, row),
                    Size        = new Size(340, 26),
                    BackColor   = Theme.PanelBack,
                    ForeColor   = Theme.Green,
                    BorderStyle = BorderStyle.FixedSingle,
                    Font        = Theme.FontMono,
                    MaxLength   = 32,
                    Text        = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                };
                p.Controls.Add(tb);
                if (k == 0) txtProvKey0 = tb;
                else if (k == 1) txtProvKey1 = tb;
                else txtProvKey2 = tb;
                row += 34;
            }

            row += 6;

            // ── Bootloader hex ────────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── Bootloader Image", lx, row)); row += 28;

            p.Controls.Add(MkLabel("HEX File:", lx, row + 3, 72, Theme.Gray));
            txtProvHexPath = new TextBox
            {
                Location    = new Point(lx + 76, row),
                Size        = new Size(440, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                PlaceholderText = "openlst_437_bootloader.hex",
            };

            // Default to embedded resource path if shipping with app
            string embeddedHex = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "openlst_437_bootloader.hex");
            if (File.Exists(embeddedHex))
                txtProvHexPath.Text = embeddedHex;

            btnProvBrowse = MkButton("Browse…", lx + 526, row, 80, Theme.Gray, Theme.PanelBack);
            btnProvBrowse.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                    { Filter = "Intel HEX (*.hex)|*.hex|All files (*.*)|*.*",
                      Title  = "Select openlst_437_bootloader.hex" };
                if (dlg.ShowDialog() == DialogResult.OK)
                    txtProvHexPath.Text = dlg.FileName;
            };
            p.Controls.AddRange(new Control[] { txtProvHexPath, btnProvBrowse });
            row += 46;

            // ── Flash button ──────────────────────────────────────────────
            btnProvFlash = MkButton("▶  PROVISION BOARD", lx, row, 210, Theme.Green, Color.FromArgb(16, 48, 16));
            btnProvFlash.Font = Theme.FontLarge;
            btnProvFlash.Size = new Size(210, 36);
            btnProvFlash.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnProvFlash.Click += async (s, e) => await RunProvisionAsync();
            p.Controls.Add(btnProvFlash);
        }

        // ── Detect CC Debugger ─────────────────────────────────────────────
        private void BtnProvDetect_Click(object sender, EventArgs e)
        {
            if (!File.Exists(SMARTRF_PATH))
            {
                lblProvStatus.Text      = "SmartRF Flash Programmer not found at expected path.";
                lblProvStatus.ForeColor = Theme.Red;
                Log($"SmartRF not found: {SMARTRF_PATH}", Theme.Red);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = SMARTRF_PATH,
                    Arguments              = "X",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var proc = Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd()
                              + proc.StandardError.ReadToEnd();
                proc.WaitForExit();

                Log("── CC Debugger Detection ──────────────────────────", Theme.Cyan);
                foreach (string line in output.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log("  " + line.Trim(), Theme.White);

                if (output.Contains("CC1110") || output.Contains("CC Debugger"))
                {
                    // Parse the device line for display
                    string devLine = output.Split('\n')
                        .FirstOrDefault(l => l.Contains("Device:") || l.Contains("Chip:"))
                        ?.Trim() ?? "CC Debugger detected";
                    lblProvStatus.Text      = "✓  " + devLine;
                    lblProvStatus.ForeColor = Theme.Green;
                }
                else if (output.Contains("No connected"))
                {
                    lblProvStatus.Text      = "✗  No CC Debugger detected — check USB connection";
                    lblProvStatus.ForeColor = Theme.Red;
                }
                else
                {
                    lblProvStatus.Text      = "? Unexpected response — see log";
                    lblProvStatus.ForeColor = Theme.Yellow;
                }
            }
            catch (Exception ex)
            {
                lblProvStatus.Text      = $"Error: {ex.Message}";
                lblProvStatus.ForeColor = Theme.Red;
                Log($"Detect error: {ex.Message}", Theme.Red);
            }
        }

        // ── Provision — patch hex + call SmartRF ──────────────────────────
        private async Task RunProvisionAsync()
        {
            // ── Validate inputs ───────────────────────────────────────────
            if (!ushort.TryParse(txtProvHwid.Text.Trim(),
                    System.Globalization.NumberStyles.HexNumber, null, out ushort hwid))
            { Log("Invalid HWID — enter 4 hex digits e.g. 0001", Theme.Red); return; }

            if (!File.Exists(txtProvHexPath.Text.Trim()))
            { Log("Bootloader HEX file not found.", Theme.Red); return; }

            if (!File.Exists(SMARTRF_PATH))
            { Log($"SmartRF Flash Programmer not found at:{Environment.NewLine}  {SMARTRF_PATH}", Theme.Red); return; }

            byte[]? key0 = ParseProvKey(txtProvKey0.Text, "Key 0");
            byte[]? key1 = ParseProvKey(txtProvKey1.Text, "Key 1");
            byte[]? key2 = ParseProvKey(txtProvKey2.Text, "Key 2");
            if (key0 == null || key1 == null || key2 == null) return;

            btnProvFlash.Enabled  = false;
            btnProvDetect.Enabled = false;

            Log("═══════════════════════════════════════════════════", Theme.Cyan);
            Log($"  Board Provisioning  →  HWID {hwid:X4}", Theme.Cyan);
            Log("═══════════════════════════════════════════════════", Theme.Cyan);

            string tempHex = "";
            try
            {
                // ── Step 1: Parse bootloader hex ─────────────────────────
                Log("Step 1: Parsing bootloader hex image...", Theme.White);
                byte[] image = OpenLstProtocol.ParseIntelHex(txtProvHexPath.Text.Trim());
                Log($"  Image loaded. Buffer = {image.Length} bytes.", Theme.Green);

                // ── Step 2: Patch image in memory (mirrors flash_bootloader.py) ──
                Log("Step 2: Patching image (HWID + keys)...", Theme.White);
                ProvisionPatchImage(image, hwid, new[] { key0, key1, key2 });
                Log($"  HWID {hwid:X4} inserted at 0x{FLASH_HWID_ADDR:X4}", Theme.Green);
                Log($"  Key 0: {BitConverter.ToString(key0).Replace("-","").ToLower()}", Theme.Gray);
                Log($"  Key 1: {BitConverter.ToString(key1).Replace("-","").ToLower()}", Theme.Gray);
                Log($"  Key 2: {BitConverter.ToString(key2).Replace("-","").ToLower()}", Theme.Gray);

                // ── Step 3: Write patched image to temp hex file ──────────
                Log("Step 3: Writing patched image to temp file...", Theme.White);
                tempHex = Path.Combine(Path.GetTempPath(),
                    $"openlst_boot_{hwid:X4}_{DateTime.Now:HHmmss}.hex");
                File.WriteAllText(tempHex, DumpIntelHex(image));
                Log($"  Temp file: {tempHex}", Theme.Gray);

                // ── Step 4: Call SmartRF Flash Programmer ─────────────────
                // Command mirrors Python: cc-tool -f -e -v -l <LOCKBITS> -w <file>
                // SmartRF equivalent: S EPV F="<file>" LB(4)
                //   S    = System-on-Chip via CC Debugger USB
                //   EPV  = Erase, Program, Verify
                //   LB(4) = Lock boot block + protect top 4KB  (matches Python LOCK_BITS = 0b100<<1 = 4)
                Log("Step 4: Calling SmartRF Flash Programmer...", Theme.White);
                Log($"  SmartRFProgConsole S EPV F=\"{tempHex}\" LB(4)", Theme.Cyan);

                var psi = new ProcessStartInfo
                {
                    FileName               = SMARTRF_PATH,
                    Arguments              = $"S EPV F=\"{tempHex}\" LB(4)",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                // Stream output live to log as it arrives
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log("  " + e.Data.Trim(), Theme.White);
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Log("  [ERR] " + e.Data.Trim(), Theme.Red);
                };
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await Task.Run(() => proc.WaitForExit());

                Log("", Theme.White);
                if (proc.ExitCode == 0)
                {
                    Log("═══════════════════════════════════════════════════", Theme.Cyan);
                    Log($"  PROVISION COMPLETE ✓  Board HWID {hwid:X4} ready.", Theme.Green);
                    Log("  Next: use Firmware tab to flash the radio application.", Theme.Gray);
                    Log("═══════════════════════════════════════════════════", Theme.Cyan);
                }
                else
                {
                    Log($"═══════════════════════════════════════════════════", Theme.Red);
                    Log($"  SmartRF exited with code {proc.ExitCode}. See log above.", Theme.Red);
                    // SmartRF return codes: 1=System 2=Parameter 3=Illegal combo 4=Missing params 5=No image/comms
                    string meaning = proc.ExitCode switch {
                        1 => "System error",
                        2 => "Parameter error",
                        3 => "Illegal parameter combination",
                        4 => "Missing parameters",
                        5 => "Missing image file or communication error — is CC Debugger connected?",
                        _ => "Unknown error"
                    };
                    Log($"  {meaning}", Theme.Red);
                    Log("═══════════════════════════════════════════════════", Theme.Red);
                }
            }
            catch (Exception ex)
            {
                Log($"Provision error: {ex.Message}", Theme.Red);
            }
            finally
            {
                // Always clean up temp file
                if (!string.IsNullOrEmpty(tempHex) && File.Exists(tempHex))
                {
                    try { File.Delete(tempHex); } catch { }
                    Log($"  Temp file cleaned up.", Theme.Gray);
                }
                btnProvFlash.Enabled  = true;
                btnProvDetect.Enabled = true;
            }
        }

        // ── Patch bootloader image in memory (mirrors flash_bootloader.py) ─
        // Flash constants for bootloader region (from flash_constants.py)
        private const int FLASH_SIGNATURE_KEYS     = 0x03CC; // where AES keys go
        private const int FLASH_RESERVED           = 0x03FC; // reserved 2 bytes
        private const int FLASH_HWID_ADDR          = 0x03FE; // HWID location (2 bytes)
        // FLASH_APP_START = 0x0400 — already in OpenLstProtocol
        private const int FLASH_APP_SIGNATURE      = 0x6BF0;
        private const int FLASH_STORAGE_START      = 0x6C00;
        private const int FLASH_UPDATER_START      = 0x7000;

        private static void ProvisionPatchImage(byte[] image, ushort hwid, byte[][] keys)
        {
            // insert_hwid: write 2-byte little-endian HWID at FLASH_HWID_ADDR
            image[FLASH_HWID_ADDR]     = (byte)(hwid & 0xFF);
            image[FLASH_HWID_ADDR + 1] = (byte)((hwid >> 8) & 0xFF);

            // insert_keys: write each 16-byte key sequentially from FLASH_SIGNATURE_KEYS
            for (int k = 0; k < keys.Length; k++)
            {
                int keyStart = FLASH_SIGNATURE_KEYS + k * 16;
                Array.Copy(keys[k], 0, image, keyStart, 16);
            }

            // insert_application: fill app region with 0xFF
            for (int i = OpenLstProtocol.FLASH_APP_START; i < FLASH_APP_SIGNATURE; i++)
                image[i] = 0xFF;

            // insert_signature: fill signature region with 0xFF
            for (int i = FLASH_APP_SIGNATURE; i < FLASH_STORAGE_START; i++)
                image[i] = 0xFF;

            // insert_storage: fill storage region with 0xFF
            for (int i = FLASH_STORAGE_START; i < FLASH_UPDATER_START; i++)
                image[i] = 0xFF;
        }

        // ── Intel HEX writer (mirrors intel_hex.py dump_hex_file) ─────────
        // Writes the patched image back to an Intel HEX string for SmartRF.
        private static string DumpIntelHex(byte[] data, int lineSize = 32)
        {
            var lines = new System.Text.StringBuilder();
            for (int addr = 0; addr < data.Length; addr += lineSize)
            {
                byte[] lineData = data.Skip(addr).Take(lineSize).ToArray();

                // Skip all-0xFF lines (unmodified flash)
                if (lineData.All(b => b == 0xFF)) continue;

                int length = lineData.Length;
                int checksum = length + (addr >> 8) + (addr & 0xFF) + 0; // record type 0
                foreach (byte b in lineData) checksum += b;
                checksum = ((checksum ^ 0xFF) + 1) & 0xFF;

                lines.AppendLine($":{length:X2}{addr:X4}00" +
                    BitConverter.ToString(lineData).Replace("-", "") +
                    $"{checksum:X2}");
            }
            lines.AppendLine(":00000001FF"); // EOF record
            return lines.ToString();
        }

        // ── Helper: parse and validate a provisioning AES key ─────────────
        private byte[]? ParseProvKey(string text, string label)
        {
            string s = text.Trim();
            if (s.Length != 32)
            { Log($"{label} must be exactly 32 hex chars.", Theme.Red); return null; }
            try
            {
                return Enumerable.Range(0, 16)
                    .Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16))
                    .ToArray();
            }
            catch { Log($"{label} contains invalid hex characters.", Theme.Red); return null; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM CLOSE
        // ══════════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            _telemTimer?.Stop();
            _liveLogTimer?.Stop();
            _port?.Close();
            _port?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
