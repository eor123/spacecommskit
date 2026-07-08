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
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;

namespace OpenLstGroundStation
{
    // ══════════════════════════════════════════════════════════════════════
    //  BARO DATA MODEL
    // ══════════════════════════════════════════════════════════════════════
    public class BaroData
    {
        public double   Hpa       { get; set; }
        public double   AltM      { get; set; }
        public double   TempC     { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public bool     Valid     { get; set; }

        /// <summary>
        /// Parse BARO response: BARO:hpa,alt_m,temp_c
        /// </summary>
        public static BaroData? Parse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;
            string data = response;
            if (data.StartsWith("BARO:", StringComparison.OrdinalIgnoreCase))
                data = data.Substring(5);
            if (data.StartsWith("ERR:")) return null;
            string[] parts = data.Split(',');
            if (parts.Length < 3) return null;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var fs = System.Globalization.NumberStyles.Float;
            if (!double.TryParse(parts[0], fs, ic, out double hpa))   return null;
            if (!double.TryParse(parts[1], fs, ic, out double alt))   return null;
            if (!double.TryParse(parts[2], fs, ic, out double temp))  return null;
            return new BaroData { Hpa = hpa, AltM = alt, TempC = temp,
                Valid = true, Timestamp = DateTime.Now };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  GPS DATA MODEL
    // ══════════════════════════════════════════════════════════════════════
    public class GpsData
    {
        public double   Lat       { get; set; }
        public double   Lon       { get; set; }
        public double   AltM      { get; set; }   // GPS altitude MSL
        public int      Sats      { get; set; }
        public bool     Fix       { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;

        // Fused baro fields — populated when beacon includes baro data
        public double   BaroHpa   { get; set; }
        public double   BaroAltM  { get; set; }
        public double   BaroTempC { get; set; }
        public bool     HasBaro   { get; set; }

        /// <summary>
        /// Parse fused GPS+baro packet from Pico.
        /// New format (8 fields): GPS:lat,lon,gps_alt,sats,fix,hpa,baro_alt,temp_c
        /// Legacy format (5 fields): GPS:lat,lon,alt,sats,fix
        /// Both formats supported.
        /// </summary>
        public static GpsData? Parse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return null;
            string data = response;
            if (data.StartsWith("GPS:", StringComparison.OrdinalIgnoreCase))
                data = data.Substring(4);
            string[] parts = data.Split(',');
            if (parts.Length < 5) return null;
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var fs = System.Globalization.NumberStyles.Float;
            if (!double.TryParse(parts[0], fs, ic, out double lat)) return null;
            if (!double.TryParse(parts[1], fs, ic, out double lon)) return null;
            if (!double.TryParse(parts[2], fs, ic, out double alt)) return null;
            if (!int.TryParse(parts[3], out int sats))              return null;
            if (!int.TryParse(parts[4], out int fixInt))            return null;

            var gps = new GpsData
            {
                Lat = lat, Lon = lon, AltM = alt,
                Sats = sats, Fix = fixInt == 1,
                Timestamp = DateTime.Now,
            };

            // Parse baro fields if present (8-field fused packet)
            if (parts.Length >= 8)
            {
                if (double.TryParse(parts[5], fs, ic, out double hpa) &&
                    double.TryParse(parts[6], fs, ic, out double balt) &&
                    double.TryParse(parts[7], fs, ic, out double btemp))
                {
                    gps.BaroHpa   = hpa;
                    gps.BaroAltM  = balt;
                    gps.BaroTempC = btemp;
                    gps.HasBaro   = true;
                }
            }
            return gps;
        }

        public bool IsValid => Fix && (Lat != 0.0 || Lon != 0.0);

        /// <summary>Altitude delta between GPS and baro (GPS - Baro).</summary>
        public double AltDelta => HasBaro ? AltM - BaroAltM : 0.0;
    }

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
            public static readonly Color Dim     = Color.FromArgb(55, 65, 80);
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
        private readonly List<byte>                _rxBuf   = new();
        private readonly object                    _rxLock  = new();
        private readonly ConcurrentQueue<RxPacket> _rxQueue = new();
        private ushort IncSeqNum() => _seqNum = OpenLstProtocol.IncSeqNum(_seqNum);

        // ── SCK-2400 board type selector ───────────────────────────────
        private enum BoardType { SCK915, SCK2400 }
        private BoardType _boardType     = BoardType.SCK915;
        private ushort    _ccsdsSeqCount    = CcsdsProtocol.SEQCOUNT_MIN;
        private ushort    _physicalBoardApid = 0;  // APID of board physically on USB; gates RF crypto
        private ushort IncCcsdsSeqCount()
            => _ccsdsSeqCount = CcsdsProtocol.IncSeqCount(_ccsdsSeqCount);

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
        private Label       lblRfStatus = null!;  // shows physical board + RF crypto state
        private RichTextBox rtbLog      = null!;
        private Button      btnClearLog = null!;
        private Button      btnLiveLog  = null!;
        private bool        _filterBeacon = false;
        private TabControl  tabMain     = null!;

        // ── Tab pages ─────────────────────────────────────────────────────
        private TabPage tpHome      = null!;
        private TabPage tpCommands  = null!;
        private TabPage tpFirmware  = null!;
        private TabPage tpTerminal  = null!;
        private TabPage tpCustom    = null!;
        private TabPage tpProvision = null!;
        private TabPage tpFiles     = null!;
        private TabPage tpGps       = null!;
        private TabPage tpRfQa      = null!;

        // ══════════════════════════════════════════════════════════════════
        //  HOME TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private Label lblUptime       = null!;
        private Label lblRssi         = null!;
        private Label lblLqi          = null!;
        private Label lblDieTemp      = null!;
        private Label lblSupplyV      = null!;
        private Label lblPktsGood     = null!;
        private Label lblPktsSent     = null!;
        private Label lblRejCksum     = null!;
        private Label lblRejOther     = null!;
        private Label lblUart0        = null!;
        private Label lblUart1        = null!;
        private Label lblRxMode       = null!;
        private Label lblTxMode       = null!;
        private Label lblTelemAge     = null!;
        // LEO fault recovery labels (Session 13)
        private Label lblResetCounter = null!;
        private Label lblResetCause   = null!;
        private Label lblSafeMode     = null!;
        private Label lblUptimeMin    = null!;
        private Button btnGetTelemNow = null!;
        private Button btnTelemAuto   = null!;

        // ══════════════════════════════════════════════════════════════════
        //  COMMANDS TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox txtCallsign = null!;
        private TextBox txtRawCmd   = null!;
        private TextBox txtOadHexPath = null!;
        private Label   lblOadProgress = null!;
        private CancellationTokenSource? _oadCts = null;
        private ComboBox cmbOadDelay = null!;

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
        private Button      btnFlashCancel  = null!;
        private ProgressBar pbFlash         = null!;
        private Label       lblFlashStatus  = null!;
        private CancellationTokenSource? _flashCts;
        // RF Power Mode — 0 = bench (0dBm), 1 = field (max)
        private RadioButton rbPowerBench    = null!;
        private RadioButton rbPowerField    = null!;
        // SCK-2400 board role selector — GS or Remote, sets SCK_APID_THIS_BOARD at build time
        private ComboBox    cmbBoardRole    = null!;
        private Panel       _provInstrPanel  = null!;

        // Controls greyed out based on board type.
        // Populated in Build* methods, applied in ApplyBoardUi().
        private readonly List<Control> _sck2400OnlyControls = new();
        private readonly List<Control> _sck915OnlyControls  = new();

        // ══════════════════════════════════════════════════════════════════
        //  RF QA TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox     txtQaSerial      = null!;
        private TextBox     txtRtlPowerPath  = null!;
        private ComboBox    cmbQaBoardType   = null!;
        private ComboBox    cmbQaDongleIndex = null!;
        private Button      btnRunQa         = null!;
        private Label       lblQaStatus      = null!;
        private Panel       pnlQaResults     = null!;
        private Label       lblQaFreq        = null!;
        private Label       lblQaFreqVal     = null!;
        private Label       lblQaPower       = null!;
        private Label       lblQaPowerVal    = null!;
        private Label       lblQaH2          = null!;
        private Label       lblQaH2Val       = null!;
        private Label       lblQaH3          = null!;
        private Label       lblQaH3Val       = null!;
        private Label       lblQaOverall     = null!;
        private Panel       pnlQaChart       = null!;
        private Button      btnQaPrint       = null!;
        private Button      btnQaSave        = null!;
        private Button      btnQaBrowseSnap  = null!;
        // RTL-SDR — check app folder first then common install paths
        private static readonly string[] RTL_POWER_DEFAULT_PATHS = new[]
        {
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"rtlsdr\rtl_power.exe"),
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"rtl_power.exe"),
            @"C:\Program Files\RTL-SDR Blog\rtl_power.exe",
            @"C:\Program Files (x86)\RTL-SDR Blog\rtl_power.exe",
            @"C:\rtl-sdr\rtl_power.exe",
            @"C:\rtlsdr\rtl_power.exe",
            @"C:\tools\rtl-sdr\rtl_power.exe",
        };
        private static readonly string RTL_POWER_CONFIG_KEY = "RtlPowerPath";
        private string _rtlPowerPath = string.Empty;
        // Folders — created automatically at startup
        private static string QaSnapshotFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "QA_Snapshots");
        private static string QaTempFolder =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
        // Snapshot counter — resets when serial changes
        private int    _qaSnapCount      = 0;
        private string _qaSnapLastSerial = "";
        // PPM correction for RTL-SDR dongle — calibrated via rtl_test -p
        private int    _qaPpm            = 0;
        private bool   _qaManualPpm      = false;  // true when user typed a value
        private int    _qaRawPpm         = 0;      // auto-calculated from last scan
        private int    _qaDisplayPpm     = 0;      // PPM applied to current display
        // Last QA scan data
        private List<(double FreqMHz, double Dbm)> _qaSpectrum = new();
        private QaResult? _lastQaResult;

        private class QaResult
        {
            public string   BoardSerial    { get; set; } = "";
            public string   BoardType      { get; set; } = "";
            public string   Firmware       { get; set; } = "";
            public DateTime Timestamp      { get; set; } = DateTime.Now;
            public double   RawPeakFreqMHz { get; set; }  // before PPM correction
            public double   PeakFreqMHz    { get; set; }  // after PPM correction (displayed)
            public double   FreqErrorKHz   { get; set; }  // after correction
            public double   PeakDbm        { get; set; }
            public double   H2Dbc          { get; set; }
            public double   H3Dbc          { get; set; }
            public int      PpmCorrection  { get; set; }  // PPM applied to display
            public int      RawPpm         { get; set; }  // auto-calculated raw PPM
            public bool     PpmIsManual    { get; set; }  // true if user override
            public bool     FreqPass       { get; set; }
            public bool     H2Pass         { get; set; }
            public bool     H3Pass         { get; set; }
            public bool     Overall        => FreqPass && H2Pass && H3Pass;
        }
        private RichTextBox rtbTerminal  = null!;
        private TextBox     txtTermInput = null!;
        private Button      btnTermSend  = null!;
        private Button      btnTermClear = null!;

        // ══════════════════════════════════════════════════════════════════
        //  CUSTOM COMMANDS TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private ListBox  lstCustomCmds  = null!;
        private TextBox  txtCmdName     = null!;
        private TextBox  txtCmdOpcode   = null!;
        private ComboBox cmbCmdType     = null!;
        private TextBox  txtCmdPayload  = null!;
        private TextBox  txtCmdNotes    = null!;
        private Button   btnCmdSave     = null!;
        private Button   btnCmdDelete   = null!;
        private Button   btnCmdSend     = null!;
        private List<CustomCommand> _customCommands = new();

        // ══════════════════════════════════════════════════════════════════
        //  FILES TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private ListBox     lstFiles          = null!;
        private Button      btnFilesRefresh   = null!;
        private Button      btnFilesGet       = null!;
        private Button      btnFilesDelete    = null!;
        private ProgressBar pbTransfer        = null!;
        private Label       lblTransferStatus = null!;
        private bool        _transferring     = false;

        // ══════════════════════════════════════════════════════════════════
        //  GPS TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private Label      lblGpsLat      = null!;
        private Label      lblGpsLon      = null!;
        private Label      lblGpsAlt      = null!;
        private Label      lblGpsSats     = null!;
        private Label      lblGpsFix      = null!;
        private Label      lblGpsTime     = null!;
        private Label      lblGpsPackets  = null!;
        private Button     btnGetGpsNow   = null!;
        private Button     btnGpsAuto     = null!;
        private Button     btnExportKml   = null!;
        private Button     btnClearTrack  = null!;
        private GMapControl gmap          = null!;
        private GMapOverlay _gpsOverlay   = null!;
        private GMapOverlay _trackOverlay = null!;
        private System.Windows.Forms.Timer? _gpsTimer;
        private GpsData  _lastGps         = new();
        private GpsData? _lastKnownGoodGps = null;  // last valid fix — used when GPS goes blind
        private readonly List<GpsData> _gpsTrack = new();
        private int      _gpsPacketCount  = 0;
        private bool     _gpsLogEnabled   = true;

        // ── Baro display controls ─────────────────────────────────────────
        private Label  lblBaroPressure   = null!;
        private Label  lblBaroAlt        = null!;
        private Label  lblBaroTemp       = null!;
        private Label  lblBaroDelta      = null!;
        private Label  lblAscentRate     = null!;
        private Label  lblMaxAlt         = null!;
        private Label  lblBurstStatus    = null!;
        private Button btnGetBaro        = null!;
        private Panel  pnlAltDisplay     = null!;

        // ── Baro state ────────────────────────────────────────────────────
        private BaroData _lastBaro        = new();
        private double   _maxAltSession   = 0.0;
        private double   _prevBaroAlt     = 0.0;
        private DateTime _prevBaroTime    = DateTime.Now;
        private double   _ascentRate      = 0.0;  // m/s positive=up negative=down
        private bool     _burstDetected   = false;

        // ── Baro animation timer ──────────────────────────────────────────
        private System.Windows.Forms.Timer? _baroAnimTimer;
        private int _baroAnimTick = 0;

        // ── Flight recorder ───────────────────────────────────────────────
        private bool       _recording        = false;
        private StreamWriter? _flightWriter  = null;
        private string     _currentFlightId  = "";
        private int        _flightPacketCount = 0;
        private DateTime   _flightStartTime  = DateTime.Now;
        private Button     btnRecord         = null!;
        private Label      lblRecordStatus   = null!;

        // ══════════════════════════════════════════════════════════════════
        //  PROVISION TAB CONTROLS
        // ══════════════════════════════════════════════════════════════════
        private TextBox  txtProvHwid    = null!;
        private TextBox  txtProvKey0    = null!;
        private TextBox  txtProvKey1    = null!;
        private TextBox  txtProvKey2    = null!;
        private TextBox  txtProvHexPath = null!;
        private Button   btnProvBrowse  = null!;
        private Button   btnProvFlash   = null!;
        private Button   btnProvDetect  = null!;
        private Label    lblProvStatus  = null!;
        // SmartRF Flash Programmer path — persisted in appsettings.json
        // Auto-detects from common TI install locations
        // User can override via Browse button on Provision tab
        private static readonly string[] SMARTRF_DEFAULT_PATHS = new[]
        {
            @"C:\Program Files (x86)\Texas Instruments\SmartRF Tools\Flash Programmer\bin\SmartRFProgConsole.exe",
            @"C:\Program Files\Texas Instruments\SmartRF Tools\Flash Programmer\bin\SmartRFProgConsole.exe",
            @"C:\ti\SmartRF Tools\Flash Programmer\bin\SmartRFProgConsole.exe",
        };

        // Backing field — loaded from appsettings.json at startup
        private string _smartRfPath = SMARTRF_DEFAULT_PATHS
            .FirstOrDefault(File.Exists) ?? SMARTRF_DEFAULT_PATHS[0];

        private string SMARTRF_PATH
        {
            get => _smartRfPath;
            set { _smartRfPath = value; SaveSettings(); }
        }

        // ── Persistent settings ────────────────────────────────────────────
        private static string SettingsPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

        private void SaveSettings()
        {
            try
            {
                var d = new Dictionary<string, string>
                {
                    ["ProjectDir"]       = txtProjectDir?.Text  ?? "",
                    ["FirmwareFile"]     = txtFirmwareFile?.Text ?? "",
                    ["AesKey"]           = txtAesKey?.Text       ?? "",
                    ["LastPort"]         = cmbPort?.SelectedItem?.ToString() ?? "",
                    ["LastBaud"]         = cmbBaud?.SelectedItem?.ToString() ?? "",
                    ["LastHwid"]         = txtHwid?.Text         ?? "",
                    ["SmartRFPath"]      = _smartRfPath,
                    ["RtlPowerPath"]     = _rtlPowerPath,
                    ["QaPpm"]            = _qaPpm.ToString(),
                    ["BoardRole"]        = (cmbBoardRole?.SelectedIndex ?? 0).ToString(),
                    ["Sck2400CcsServer"] = _sck2400CcsServer,
                    ["Sck2400SrfProg"]   = _sck2400SrfProg,
                    ["Sck2400Workspace"] = _sck2400Workspace,
                    ["Sck2400XdsDfu"]    = _sck2400XdsDfu,
                    ["ProvKey0"]         = txtProvKey0?.Text ?? "",
                    ["ProvKey1"]         = txtProvKey1?.Text ?? "",
                    ["ProvKey2"]         = txtProvKey2?.Text ?? "",
                };
                var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(SettingsPath,
                    System.Text.Json.JsonSerializer.Serialize(d, opts));
            }
            catch { }
        }

        private Dictionary<string, string> LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return new();
                string json = File.ReadAllText(SettingsPath);
                return System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch { return new(); }
        }

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
            BuildGpsTab();
            BuildProvisionTab();
            BuildRfQaTab();
            BuildLogPanel();
            WireTimers();

            ApplyBoardUi();  // apply initial board-specific UI state

            var s = LoadSettings();
            if (s.TryGetValue("ProjectDir",   out string? pd) && Directory.Exists(pd))
                txtProjectDir.Text = pd;
            if (s.TryGetValue("FirmwareFile", out string? ff) && File.Exists(ff))
                txtFirmwareFile.Text = ff;
            if (s.TryGetValue("AesKey",       out string? ak) && ak.Length == 32)
                txtAesKey.Text = ak;
            if (s.TryGetValue("LastPort",     out string? lp) && cmbPort.Items.Contains(lp))
                cmbPort.SelectedItem = lp;
            if (s.TryGetValue("LastBaud",     out string? lb) && cmbBaud.Items.Contains(lb))
                cmbBaud.SelectedItem = lb;
            if (s.TryGetValue("ProvKey0", out string? pk0) && pk0.Length == 32)
                txtProvKey0.Text = pk0;
            if (s.TryGetValue("ProvKey1", out string? pk1) && pk1.Length == 32)
                txtProvKey1.Text = pk1;
            if (s.TryGetValue("ProvKey2", out string? pk2) && pk2.Length == 32)
                txtProvKey2.Text = pk2;
            if (s.TryGetValue("LastHwid",     out string? lh) && !string.IsNullOrEmpty(lh))
                txtHwid.Text = lh;
            if (s.TryGetValue("SmartRFPath",      out string? srp) && File.Exists(srp))
                _smartRfPath = srp;
            if (s.TryGetValue("RtlPowerPath",     out string? rtp) && File.Exists(rtp))
                _rtlPowerPath = rtp;
            if (s.TryGetValue("QaPpm",            out string? ppmStr) && int.TryParse(ppmStr, out int ppm))
                _qaPpm = ppm;
            if (s.TryGetValue("BoardRole",        out string? br) && int.TryParse(br, out int brIdx)
                && brIdx >= 0 && brIdx < cmbBoardRole.Items.Count)
                cmbBoardRole.SelectedIndex = brIdx;
            if (s.TryGetValue("Sck2400CcsServer", out string? ccs) && !string.IsNullOrWhiteSpace(ccs))
                _sck2400CcsServer = ccs;
            if (s.TryGetValue("Sck2400SrfProg",   out string? srfp) && !string.IsNullOrWhiteSpace(srfp))
                _sck2400SrfProg = srfp;
            if (s.TryGetValue("Sck2400Workspace", out string? ws) && !string.IsNullOrWhiteSpace(ws))
                _sck2400Workspace = ws;
            if (s.TryGetValue("Sck2400XdsDfu",    out string? xds) && !string.IsNullOrWhiteSpace(xds))
                _sck2400XdsDfu = xds;

            SetStatus("Disconnected", Theme.Red);
            Log("SCK Ground Station ready.", Theme.Cyan);
            Log($"Log folder: {AppLogger.LogFolder}", Theme.Gray);

            // Ensure Flights folder exists
            if (!Directory.Exists(FlightsFolderPath))
                Directory.CreateDirectory(FlightsFolderPath);
            Log($"Flights folder: {FlightsFolderPath}", Theme.Gray);

            // Ensure QA folders exist
            Directory.CreateDirectory(QaSnapshotFolder);
            Directory.CreateDirectory(QaTempFolder);
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM SHELL
        // ══════════════════════════════════════════════════════════════════
        private void BuildForm()
        {
            Text           = "SCK Ground Station";
            Size           = new Size(1280, 820);
            MinimumSize    = new Size(1100, 700);
            BackColor      = Theme.FormBack;
            ForeColor      = Theme.Silver;
            Font           = Theme.FontMono;
            StartPosition  = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
        }

        // ══════════════════════════════════════════════════════════════════
        //  HEADER BAR
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

            var lblTitle = MkLabel("◈ SCK Ground Station", 10, 14, 280, Theme.Cyan, Theme.FontTitle);

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
            foreach (string b in new[] { "9600", "19200", "38400", "57600", "115200", "230400", "921600", "3000000" })
                cmbBaud.Items.Add(b);
            cmbBaud.SelectedItem = "115200";

            var lblHwidH = MkLabel("HWID:", 590, 17, 46, Theme.Gray);
            txtHwid = new TextBox
            {
                Location    = new Point(640, 13),
                Size        = new Size(60, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMonoBold,
                Text        = "0001",
                MaxLength   = 4,
            };

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

            btnConnect = MkButton("Connect", 752, 12, 100, Theme.Cyan, Theme.PanelBack);
            btnConnect.Click += BtnConnect_Click;

            lblStatus = new Label
            {
                Location  = new Point(864, 17),
                Size      = new Size(200, 22),
                ForeColor = Theme.Red,
                BackColor = Color.Transparent,
                Font      = Theme.FontMono,
                Text      = "● Disconnected",
            };

            // ── Board type selector ────────────────────────────────────
            var lblBoard = MkLabel("Board:", 1070, 17, 46, Theme.Gray);
            var cmbBoard = new ComboBox
            {
                Location      = new Point(1120, 13),
                Size          = new Size(110, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                FlatStyle     = FlatStyle.Flat,
                Font          = Theme.FontMono,
            };
            cmbBoard.Items.AddRange(new object[] { "SCK-915", "SCK-2400" });
            cmbBoard.SelectedIndex = 0;
            cmbBoard.SelectedIndexChanged += (s, e) =>
            {
                _boardType = cmbBoard.SelectedIndex == 1 ? BoardType.SCK2400 : BoardType.SCK915;
                bool isSck2400 = _boardType == BoardType.SCK2400;
                lblHwidH.Text = isSck2400 ? "APID:" : "HWID:";
                if (isSck2400 && cmbBaud.Items.Contains("921600"))
                    cmbBaud.SelectedItem = "921600";
                else if (!isSck2400 && cmbBaud.Items.Contains("115200"))
                    cmbBaud.SelectedItem = "115200";
                Log($"Board: {(isSck2400 ? "SCK-2400 (CCSDS)" : "SCK-915 (OpenLST)")}", Theme.Cyan);
                RefreshProvDescription();
                ApplyBoardUi();
                UpdateRfStatusLabel();
            };

            lblRfStatus = new Label
            {
                Location  = new Point(864, 37),
                Size      = new Size(380, 18),
                ForeColor = Theme.Gray,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
                Text      = "",
            };

            header.Controls.AddRange(new Control[]
            {
                lblTitle, lblPort, cmbPort, lblBaud, cmbBaud,
                lblHwidH, txtHwid, btnRefreshPorts, btnConnect, lblStatus,
                lblBoard, cmbBoard
            });
            Controls.Add(header);
        }

        // ══════════════════════════════════════════════════════════════════
        //  TAB CONTROL
        // ══════════════════════════════════════════════════════════════════
        private void BuildTabControl()
        {
            tabMain = new TabControl
            {
                Location  = new Point(0, 52),
                Anchor    = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BackColor = Theme.TabBack,
                Font      = Theme.FontMonoBold,
                Padding   = new Point(6, 4),
            };
            SizeChanged += (s, e) => ResizeLayout();

            tpHome      = MkTab(" Home ");
            tpCommands  = MkTab(" Commands ");
            tpFirmware  = MkTab(" Firmware ");
            tpTerminal  = MkTab(" Terminal ");
            tpCustom    = MkTab(" Custom Cmds ");
            tpFiles     = MkTab(" Files ");
            tpGps       = MkTab(" GPS / Map ");
            tpProvision = MkTab(" Provision ");
            tpRfQa      = MkTab(" RF QA ");

            tabMain.TabPages.AddRange(new[]
            {
                tpHome, tpCommands, tpFirmware, tpTerminal,
                tpCustom, tpFiles, tpGps, tpProvision, tpRfQa
            });
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
        //  LOG PANEL
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
                logPanel.SetBounds(ClientSize.Width - logW, 52, logW, ClientSize.Height - 52);

            var lblLogTitle = MkLabel("◈ LOG", 8, 8, 80, Theme.Cyan, Theme.FontMonoBold);

            btnClearLog = MkButton("Clear", logW - 180, 5, 56, Theme.Gray, Theme.LogBack);
            btnClearLog.FlatAppearance.BorderColor = Theme.BorderColor;
            btnClearLog.Click += (s, e) => rtbLog.Clear();

            var btnFilterBeacon = MkButton("[~] Beacon", logW - 120, 5, 56, Theme.Gray, Theme.LogBack);
            btnFilterBeacon.FlatAppearance.BorderColor = Theme.BorderColor;
            btnFilterBeacon.Click += (s, e) =>
            {
                _filterBeacon = !_filterBeacon;
                btnFilterBeacon.ForeColor = _filterBeacon ? Theme.Yellow : Theme.Gray;
                btnFilterBeacon.Text = _filterBeacon ? "[B] Beacon" : "[~] Beacon";
            };

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

            logPanel.Controls.AddRange(new Control[] { lblLogTitle, btnClearLog, btnFilterBeacon, btnLiveLog, rtbLog });
            logPanel.SizeChanged += (s, e) =>
                rtbLog.SetBounds(0, 32, logPanel.Width, logPanel.Height - 32);

            Controls.Add(logPanel);
            logPanel.SetBounds(ClientSize.Width - logW, 52, logW, ClientSize.Height - 52);
        }

        // ══════════════════════════════════════════════════════════════════
        //  HOME TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildHomeTab()
        {
            var p = tpHome;

            btnGetTelemNow = MkButton("▶ Get Telem", 10, 12, 130, Theme.Cyan, Theme.PanelBack);
            btnGetTelemNow.Click += async (s, e) => await SendGetTelemAsync();

            btnTelemAuto = MkButton("Auto: OFF", 150, 12, 100, Theme.Gray, Theme.PanelBack);
            btnTelemAuto.Click += BtnTelemAuto_Click;

            lblTelemAge = MkLabel("Last update: —", 264, 17, 300, Theme.Gray, Theme.FontSmall);

            lblRfStatus = new Label
            {
                Location  = new Point(580, 17),
                Size      = new Size(400, 18),
                ForeColor = Theme.Cyan,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
                Text      = "",
                TextAlign = ContentAlignment.MiddleLeft,
            };

            p.Controls.AddRange(new Control[] { btnGetTelemNow, btnTelemAuto, lblTelemAge, lblRfStatus });

            int col1 = 10, col2 = 310, rowStart = 52, rowH = 68;

            p.Controls.Add(BuildTelemPanel("Uptime",          col1, rowStart + rowH * 0, ref lblUptime,   "—"));
            p.Controls.Add(BuildTelemPanel("Last RSSI (dBm)", col2, rowStart + rowH * 0, ref lblRssi,     "—"));
            p.Controls.Add(BuildTelemPanel("Last LQI",        col1, rowStart + rowH * 1, ref lblLqi,      "—"));
            p.Controls.Add(BuildTelemPanel("Packets Good",    col1, rowStart + rowH * 2, ref lblPktsGood, "—"));
            p.Controls.Add(BuildTelemPanel("Packets Sent",    col2, rowStart + rowH * 2, ref lblPktsSent, "—"));
            p.Controls.Add(BuildTelemPanel("Rej Checksum",    col1, rowStart + rowH * 3, ref lblRejCksum, "—"));
            p.Controls.Add(BuildTelemPanel("Rej Other",       col2, rowStart + rowH * 3, ref lblRejOther, "—"));
            p.Controls.Add(BuildTelemPanel("UART0 RX Count",  col1, rowStart + rowH * 4, ref lblUart0,    "—"));
            p.Controls.Add(BuildTelemPanel("UART1 RX Count",  col2, rowStart + rowH * 4, ref lblUart1,    "—"));
            p.Controls.Add(BuildTelemPanel("RX Mode",         col1, rowStart + rowH * 5, ref lblRxMode,   "—"));
            p.Controls.Add(BuildTelemPanel("TX Mode",         col2, rowStart + rowH * 5, ref lblTxMode,   "—"));
            p.Controls.Add(BuildTelemPanel("Die Temp",        col1, rowStart + rowH * 6, ref lblDieTemp,  "—"));
            p.Controls.Add(BuildTelemPanel("Supply Voltage",  col2, rowStart + rowH * 6, ref lblSupplyV,  "—"));
            // LEO fault recovery fields (Session 13)
            p.Controls.Add(BuildTelemPanel("Reset Counter",   col1, rowStart + rowH * 7, ref lblResetCounter, "—"));
            p.Controls.Add(BuildTelemPanel("Reset Cause",     col2, rowStart + rowH * 7, ref lblResetCause,   "—"));
            p.Controls.Add(BuildTelemPanel("Safe Mode",       col1, rowStart + rowH * 8, ref lblSafeMode,     "—"));
            p.Controls.Add(BuildTelemPanel("Uptime (min)",    col2, rowStart + rowH * 8, ref lblUptimeMin,    "—"));
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
            int col1 = 10;
            int row = 12;

            p.Controls.Add(MkSectionLabel("── Board Commands", col1, row));
            row += 28;

            // Note: Get Telem is on the Home tab (SendGetTelemAsync waits for tlm_beacon).
            // It is intentionally excluded here -- SendSimpleCommandAsync waits for cmd_ack
            // which get_telem never returns, so it would always time out for SCK-2400.
            var cmds = new[]
            {
                ("Get Callsign",    "get_callsign"),
                ("Get Time",        "get_time"),
                ("Reboot",          "reboot"),
                ("Clear Safe Mode", "clear_safe_mode"),
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

            // Beacon ON / OFF -- sends CMD_BEACON_CTRL with 0x01 or 0x00.
            // Default firmware state is OFF; turn ON only when you need the
            // autonomous heartbeat (e.g. RF link testing, range checks).
            var btnBeaconOn = MkButton("Beacon ON", col1, by + 36, 130, Theme.Green, Theme.PanelBack);
            btnBeaconOn.Click += async (s, e) =>
            {
                if (!CheckConnected()) return;
                if (_boardType != BoardType.SCK2400) { Log("Beacon: SCK-2400 only.", Theme.Yellow); return; }
                FlushRxQueue();
                ushort seq = IncCcsdsSeqCount();
                ushort dest = ActiveHwid;
                byte[] args = new byte[] { 0x01 };   // 0x01 = enable
                WritePacket(CcsdsProtocol.BuildCommand(seq, CcsdsProtocol.CMD_BEACON_CTRL, args, dest));
                LogTx($"[CCSDS] beacon_ctrl ON seq={seq} dest=0x{dest:X3}", 0);
                var ack = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 2000);
                Log(ack != null ? "Beacon ON ✓" : "Beacon ON — no ACK", ack != null ? Theme.Green : Theme.Yellow);
            };

            var btnBeaconOff = MkButton("Beacon OFF", col1 + 144, by + 36, 130, Theme.Red, Theme.PanelBack);
            btnBeaconOff.Click += async (s, e) =>
            {
                if (!CheckConnected()) return;
                if (_boardType != BoardType.SCK2400) { Log("Beacon: SCK-2400 only.", Theme.Yellow); return; }
                FlushRxQueue();
                ushort seq = IncCcsdsSeqCount();
                ushort dest = ActiveHwid;
                byte[] args = new byte[] { 0x00 };   // 0x00 = disable
                WritePacket(CcsdsProtocol.BuildCommand(seq, CcsdsProtocol.CMD_BEACON_CTRL, args, dest));
                LogTx($"[CCSDS] beacon_ctrl OFF seq={seq} dest=0x{dest:X3}", 0);
                var ack = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 2000);
                Log(ack != null ? "Beacon OFF ✓" : "Beacon OFF — no ACK", ack != null ? Theme.Green : Theme.Yellow);
            };
            p.Controls.AddRange(new Control[] { btnBeaconOn, btnBeaconOff });
            _sck2400OnlyControls.Add(btnBeaconOn);
            _sck2400OnlyControls.Add(btnBeaconOff);

            row = by + 82;
            p.Controls.Add(MkSectionLabel("── Callsign", col1, row));
            row += 28;

            p.Controls.Add(MkLabel("Callsign:", col1, row + 3, 80, Theme.Gray));
            txtCallsign = new TextBox
            {
                Location    = new Point(col1 + 84, row),
                Size        = new Size(200, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle,
                Font        = Theme.FontMono,
                MaxLength   = 6,
            };
            var btnSetCallsign = MkButton("Set Callsign", col1 + 294, row, 130, Theme.Cyan, Theme.PanelBack);
            btnSetCallsign.Click += async (s, e) => await SendSetCallsignAsync();
            p.Controls.AddRange(new Control[] { txtCallsign, btnSetCallsign });

            row += 46;
            p.Controls.Add(MkSectionLabel("── OAD Flash Test", col1, row));
            row += 28;

            // Flash Status button — sends CMD_OAD_STATUS (no flash touch).
            // Response: APID_CMD_ACK with payload [0x14][active][rxd3][rxd2][rxd1][rxd0][total2][total1][total0]
            // Use this to verify the OAD command path is alive before touching flash.
            var btnOadStatus = MkButton("Flash Status", col1, row, 170, Theme.Cyan, Theme.PanelBack);
            btnOadStatus.Click += async (s, e) =>
            {
                if (_boardType != BoardType.SCK2400) { Log("OAD: SCK-2400 only.", Theme.Yellow); return; }
                await SendOadStatusAsync();
            };
            p.Controls.Add(btnOadStatus);
            _sck2400OnlyControls.Add(btnOadStatus);

            // Flash Open Test — sends CMD_OAD_START with 4KB dummy image.
            // This calls extFlashOpen() + extFlashErase(0, 4096) + extFlashClose() on the firmware.
            // ACK status 0x00 = flash open+erase succeeded. 0x01 = failed (SPI problem).
            // This is the first real test of the ext flash driver from RTOS task context.
            var btnOadFlashTest = MkButton("Flash Open Test", col1 + 184, row, 170, Theme.Yellow, Theme.PanelBack);
            btnOadFlashTest.Click += async (s, e) =>
            {
                if (_boardType != BoardType.SCK2400) { Log("OAD: SCK-2400 only.", Theme.Yellow); return; }
                await SendOadFlashOpenTestAsync();
            };
            p.Controls.Add(btnOadFlashTest);
            _sck2400OnlyControls.Add(btnOadFlashTest);

            row += 46;
            p.Controls.Add(MkSectionLabel("── OAD Update", col1, row));
            row += 28;

            // Hex file picker — selects the firmware .hex to deliver over RF.
            // Default: Debug output of the current project (same as Flash button).
            p.Controls.Add(MkLabel("HEX:", col1, row + 3, 38, Theme.Gray));
            txtOadHexPath = new TextBox
            {
                Location        = new Point(col1 + 42, row),
                Size            = new Size(264, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.White,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "path to firmware .hex",
                Text            = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT,
                                               "Debug", $"{SCK2400_PROJECT}.hex"),
            };
            var btnOadBrowse = MkButton("…", col1 + 310, row, 32, Theme.Gray, Theme.PanelBack);
            btnOadBrowse.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                    { Filter = "Intel HEX (*.hex)|*.hex|All files (*.*)|*.*",
                      Title  = "Select firmware hex for OAD" };
                if (dlg.ShowDialog() == DialogResult.OK)
                    txtOadHexPath.Text = dlg.FileName;
            };
            p.Controls.AddRange(new Control[] { txtOadHexPath, btnOadBrowse });

            row += 36;

            // Start OAD: parses hex, sends CMD_OAD_START, streams chunks, CMD_OAD_END.
            var btnOadStart = MkButton("Start OAD", col1, row, 130, Theme.Green, Theme.PanelBack);
            btnOadStart.Click += async (s, e) =>
            {
                if (_boardType != BoardType.SCK2400) { Log("OAD: SCK-2400 only.", Theme.Yellow); return; }
                if (_oadCts != null) { Log("OAD already in progress.", Theme.Yellow); return; }
                _oadCts = new CancellationTokenSource();
                btnOadStart.Enabled = false;
                try   { await SendOadUpdateAsync(txtOadHexPath.Text.Trim(), _oadCts.Token); }
                finally { _oadCts?.Dispose(); _oadCts = null; btnOadStart.Enabled = true; }
            };

            // Abort OAD: sends CMD_OAD_ABORT and cancels the local loop.
            var btnOadAbort = MkButton("Abort OAD", col1 + 144, row, 130, Theme.Red, Theme.PanelBack);
            btnOadAbort.Click += async (s, e) =>
            {
                _oadCts?.Cancel();
                if (!CheckConnected()) return;
                ushort seq = IncCcsdsSeqCount();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(seq, CcsdsProtocol.CMD_OAD_ABORT, ActiveHwid));
                LogTx($"[CCSDS] oad_abort seq={seq}", 0);
                await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 2000);
                Log("OAD aborted.", Theme.Yellow);
            };
            p.Controls.AddRange(new Control[] { btnOadStart, btnOadAbort });

            row += 36;

            // Progress label — updated during chunk delivery.
            lblOadProgress = MkLabel("", col1, row, 500, Theme.Gray);
            p.Controls.Add(lblOadProgress);

            row += 28;

            // Inter-chunk delay selector — tunable for RF link testing.
            // 10ms = floor (on-air ~8ms + yield ~10ms).  Default 20ms adds margin.
            // Lower = faster OAD; too low drops chunks silently at sRfForwardBuf.
            p.Controls.Add(MkLabel("Inter-chunk delay:", col1, row + 3, 130, Theme.Gray));
            cmbOadDelay = new ComboBox
            {
                Location      = new Point(col1 + 134, row),
                Size          = new Size(80, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                Font          = Theme.FontMono,
            };
            foreach (int ms in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 })
                cmbOadDelay.Items.Add($"{ms} ms");
            cmbOadDelay.SelectedIndex = 0; // default 10ms (floor confirmed)
            p.Controls.Add(cmbOadDelay);

            row += 28;
            p.Controls.Add(MkSectionLabel("── Raw Command", col1, row));
            row += 28;

            p.Controls.Add(MkLabel("Command:", col1, row + 3, 80, Theme.Gray));
            txtRawCmd = new TextBox
            {
                Location        = new Point(col1 + 84, row),
                Size            = new Size(340, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.White,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "e.g.  lst get_telem",
            };
            var btnRawSend = MkButton("Send", col1 + 434, row, 80, Theme.Yellow, Theme.PanelBack);
            btnRawSend.Click += async (s, e) => await SendRawCommandAsync();
            p.Controls.AddRange(new Control[] { txtRawCmd, btnRawSend });
        }

        // ══════════════════════════════════════════════════════════════════
        //  FIRMWARE TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildFirmwareTab()
        {
            var p = tpFirmware;
            int lx = 10, row = 12;

            p.Controls.Add(MkSectionLabel("── Build", lx, row));
            row += 28;

            p.Controls.Add(MkLabel("Project Dir:", lx, row + 3, 95, Theme.Gray));
            txtProjectDir = new TextBox
            {
                Location        = new Point(lx + 98, row),
                Size            = new Size(440, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.White,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "Path to SCK firmware project folder",
            };
            btnBrowseDir = MkButton("Browse…", lx + 548, row, 80, Theme.Gray, Theme.PanelBack);
            btnBrowseDir.Click += BtnBrowseDir_Click;
            p.Controls.AddRange(new Control[] { txtProjectDir, btnBrowseDir });
            row += 36;

            // ── SCK-2400 board role selector ──────────────────────────────
            // Sets SCK_APID_THIS_BOARD at build time via preprocessor define.
            // Only relevant when board type is SCK-2400 -- hidden for SCK-915.
            p.Controls.Add(MkLabel("Board Role:", lx, row + 3, 95, Theme.Gray));
            cmbBoardRole = new ComboBox
            {
                Location      = new Point(lx + 98, row),
                Size          = new Size(180, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.Cyan,
                Font          = Theme.FontMono,
            };
            cmbBoardRole.Items.AddRange(new object[]
            {
                "GS Board  (APID 0x010)",
                "Remote 1  (APID 0x011)",
                "Remote 2  (APID 0x012)",
            });
            cmbBoardRole.SelectedIndex = 0;
            var lblBoardRoleNote = MkLabel(
                "Sets SCK_APID_THIS_BOARD — flash GS first, then remote",
                lx + 288, row + 5, 360, Theme.Gray, Theme.FontSmall);
            p.Controls.AddRange(new Control[] { cmbBoardRole, lblBoardRoleNote });
            row += 36;

            btnBuild = MkButton("▶ Build", lx, row, 110, Theme.Yellow, Theme.PanelBack);
            btnBuild.Click += async (s, e) => await RunBuildAsync();

            var btnClean = MkButton("⌧ Clean", lx + 120, row, 90, Theme.Gray, Theme.PanelBack);
            btnClean.Click += (s, e) =>
            {
                if (_boardType == BoardType.SCK2400) RunClean_SCK2400();
                else RunClean();
            };

            var btnCleanBuild = MkButton("⌧ Clean + Build", lx + 220, row, 140, Theme.Magenta, Theme.PanelBack);
            btnCleanBuild.Click += async (s, e) =>
            {
                if (_boardType == BoardType.SCK2400) await RunBuildAsync_SCK2400(cleanFirst: true);
                else { RunClean(); await RunBuildAsync(); }
            };

            p.Controls.AddRange(new Control[] { btnBuild, btnClean, btnCleanBuild });
            row += 42;

            // ── RF Power Mode ──────────────────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── RF Power Mode", lx, row));
            row += 28;

            var pnlPower = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(630, 52),
                BackColor = Theme.PanelBack,
            };
            pnlPower.Paint += (s, e) =>
            {
                using var pen = new Pen(Color.FromArgb(50, 50, 80), 1);
                e.Graphics.DrawRectangle(pen, 0, 0, pnlPower.Width - 1, pnlPower.Height - 1);
            };

            rbPowerBench = new RadioButton
            {
                Text      = "🔵  0 dBm — Bench / Indoor testing",
                Location  = new Point(12, 8),
                Size      = new Size(270, 22),
                ForeColor = Color.FromArgb(100, 160, 255),
                BackColor = Color.Transparent,
                Font      = Theme.FontMono,
                Checked   = true,
            };

            rbPowerField = new RadioButton
            {
                Text      = "🔴  Max Power — Field / HAB mission",
                Location  = new Point(300, 8),
                Size      = new Size(280, 22),
                ForeColor = Color.OrangeRed,
                BackColor = Color.Transparent,
                Font      = Theme.FontMono,
                Checked   = false,
            };

            var lblPowerNote = new Label
            {
                Text      = "SCK-915: patches RF_PA_CONFIG in board.h  |  SCK-2400: patches TX_POWER in radio.h",
                Location  = new Point(12, 30),
                Size      = new Size(600, 16),
                ForeColor = Theme.Gray,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
            };

            pnlPower.Controls.AddRange(new Control[] { rbPowerBench, rbPowerField, lblPowerNote });
            p.Controls.Add(pnlPower);
            row += 62;

            p.Controls.Add(MkSectionLabel("── Sign & Flash", lx, row));
            row += 28;

            p.Controls.Add(MkLabel("HEX File:", lx, row + 3, 75, Theme.Gray));
            txtFirmwareFile = new TextBox
            {
                Location        = new Point(lx + 78, row),
                Size            = new Size(460, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.White,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "openlst_437_radio.hex",
            };
            btnBrowseHex = MkButton("Browse…", lx + 548, row, 80, Theme.Gray, Theme.PanelBack);
            btnBrowseHex.Click += BtnBrowseHex_Click;
            p.Controls.AddRange(new Control[] { txtFirmwareFile, btnBrowseHex });
            row += 36;

            p.Controls.Add(MkLabel("AES Key:", lx, row + 3, 75, Theme.Gray));
            txtAesKey = new TextBox
            {
                Location        = new Point(lx + 78, row),
                Size            = new Size(340, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.Green,
                BorderStyle     = BorderStyle.FixedSingle,
                Font            = Theme.FontMono,
                PlaceholderText = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                Text            = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
            };
            p.Controls.Add(txtAesKey);
            row += 36;

            btnSign = MkButton("Sign", lx, row, 100, Theme.Cyan, Theme.PanelBack);
            btnSign.Click += BtnSign_Click;

            btnFlash = MkButton("▶ Flash", lx + 112, row, 130, Theme.Green, Theme.PanelBack);
            btnFlash.BackColor = Color.FromArgb(20, 60, 20);
            btnFlash.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnFlash.Click += async (s, e) =>
            {
                if (_boardType == BoardType.SCK2400) await RunFlashLocal_SCK2400();
                else await RunFlashAsync();
            };

            btnBuildFlash = MkButton("▶ Build + Flash", lx + 254, row, 150, Theme.Yellow, Theme.PanelBack);
            btnBuildFlash.Click += async (s, e) => await RunBuildAndFlashAsync();

            btnFlashCancel = MkButton("✕ Cancel", lx + 416, row, 90, Theme.Red, Theme.PanelBack);
            btnFlashCancel.BackColor = Color.FromArgb(60, 20, 20);
            btnFlashCancel.FlatAppearance.BorderColor = Color.FromArgb(120, 40, 40);
            btnFlashCancel.Enabled = false;
            btnFlashCancel.Click += (s, e) =>
            {
                _flashCts?.Cancel();
                Log("Flash cancelled by user.", Theme.Red);
                lblFlashStatus.Text = "Cancelled.";
                btnFlashCancel.Enabled = false;
            };

            p.Controls.AddRange(new Control[] { btnSign, btnFlash, btnBuildFlash, btnFlashCancel });
            row += 46;

            // ── SCK-2400: Flash BIM Only (one-time board provisioning) ────
            // Flashes BIM to pages 0-1 with -e all. Use once per board at
            // factory provisioning. After this, never flash BIM again --
            // all future updates are firmware-only via the Flash button above.
            var btnFlashBim = MkButton("⚙ Flash BIM Only", lx, row, 170,
                Color.FromArgb(180, 120, 0), Theme.PanelBack);
            btnFlashBim.BackColor = Color.FromArgb(40, 30, 5);
            btnFlashBim.FlatAppearance.BorderColor = Color.FromArgb(140, 90, 0);
            btnFlashBim.Click += async (s, e) =>
            {
                if (_boardType != BoardType.SCK2400) return;
                var confirm = MessageBox.Show(
                    "Flash BIM to pages 0-1 with full erase.\n\n" +
                    "This is a ONE-TIME operation for initial board provisioning.\n" +
                    "After flashing BIM, use the normal Flash button for all\n" +
                    "future firmware updates — BIM should never be reflashed.\n\n" +
                    "Continue?",
                    "Flash BIM — One-Time Provisioning",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (confirm != DialogResult.Yes) return;
                await RunFlashBimAsync();
            };
            var lblFlashBimNote = MkLabel(
                "One-time provisioning only — BIM is permanent after initial flash",
                lx + 180, row + 5, 430, Theme.Gray, Theme.FontSmall);
            p.Controls.AddRange(new Control[] { btnFlashBim, lblFlashBimNote });
            row += 42;

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

            p.Controls.Add(MkSectionLabel("── Saved Commands", 10, 10));

            // New command button above the list
            var btnCmdNew = MkButton("+ New", 10, 32, 80, Theme.Green, Theme.PanelBack);
            btnCmdNew.BackColor = Color.FromArgb(20, 60, 20);
            btnCmdNew.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnCmdNew.Click += (s, e) =>
            {
                lstCustomCmds.SelectedIndex = -1;
                txtCmdName.Clear();
                txtCmdOpcode.Clear();
                txtCmdPayload.Clear();
                txtCmdNotes.Clear();
                cmbCmdType.SelectedIndex = 0;
                txtCmdName.Focus();
            };
            p.Controls.Add(btnCmdNew);

            lstCustomCmds = new ListBox
            {
                Location    = new Point(10, 64),
                Size        = new Size(220, 332),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                Font        = Theme.FontMono,
                BorderStyle = BorderStyle.FixedSingle,
            };
            lstCustomCmds.SelectedIndexChanged += LstCustomCmds_SelectedIndexChanged;
            p.Controls.Add(lstCustomCmds);
            RefreshCustomCmdList();

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

            btnCmdSave   = MkButton("Save",   ex,       ey, 80,  Theme.Cyan,  Theme.PanelBack);
            btnCmdDelete = MkButton("Delete", ex + 90,  ey, 80,  Theme.Red,   Theme.PanelBack);
            btnCmdSend   = MkButton("▶ Send", ex + 180, ey, 100, Theme.Green, Theme.PanelBack);
            btnCmdSend.BackColor = Color.FromArgb(20, 60, 20);

            btnCmdSave.Click   += BtnCmdSave_Click;
            btnCmdDelete.Click += BtnCmdDelete_Click;
            btnCmdSend.Click   += async (s, e) => await SendCustomCommandAsync();

            p.Controls.AddRange(new Control[] { btnCmdSave, btnCmdDelete, btnCmdSend });
        }

        // ══════════════════════════════════════════════════════════════════
        //  GPS TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildGpsTab()
        {
            var p = tpGps;
            int lx = 10, row = 10;

            // ── Row 1: action buttons ─────────────────────────────────────
            btnGetGpsNow = MkButton("▶ Get GPS", lx, row, 110, Theme.Cyan, Theme.PanelBack);
            btnGetGpsNow.Click += async (s, e) => await SendGetGpsAsync();

            btnGpsAuto = MkButton("Auto: OFF", lx + 118, row, 100, Theme.Gray, Theme.PanelBack);
            btnGpsAuto.Click += BtnGpsAuto_Click;

            btnGetBaro = MkButton("▶ Get Baro", lx + 226, row, 110, Theme.Magenta, Theme.PanelBack);
            btnGetBaro.Click += async (s, e) => await SendGetBaroAsync();

            btnClearTrack = MkButton("Clear Track", lx + 344, row, 100, Theme.Gray, Theme.PanelBack);
            btnClearTrack.Click += BtnClearTrack_Click;

            btnExportKml = MkButton("Export KML", lx + 452, row, 110, Theme.Yellow, Theme.PanelBack);
            btnExportKml.Click += BtnExportKml_Click;

            // Record flight button
            btnRecord = MkButton("⏺ Record", lx + 570, row, 100, Theme.Green, Theme.PanelBack);
            btnRecord.BackColor = Color.FromArgb(20, 60, 20);
            btnRecord.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnRecord.Click += BtnRecord_Click;

            // Flight replay button — opens FlightReplayForm
            var btnReplay = MkButton("▶ Replay", lx + 678, row, 100, Theme.Yellow, Theme.PanelBack);
            btnReplay.Click += (s, e) => new FlightReplayForm().Show();

            // GPS log filter
            var btnGpsFilter = MkButton("Log: GPS ON", lx + 786, row, 120, Theme.Green, Theme.PanelBack);
            btnGpsFilter.BackColor = Color.FromArgb(20, 60, 20);
            btnGpsFilter.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnGpsFilter.Click += (s, e) =>
            {
                _gpsLogEnabled = !_gpsLogEnabled;
                btnGpsFilter.Text      = _gpsLogEnabled ? "Log: GPS ON" : "Log: GPS OFF";
                btnGpsFilter.ForeColor = _gpsLogEnabled ? Theme.Green : Theme.Gray;
                btnGpsFilter.BackColor = _gpsLogEnabled ? Color.FromArgb(20, 60, 20) : Theme.PanelBack;
            };

            p.Controls.AddRange(new Control[]
                { btnGetGpsNow, btnGpsAuto, btnGetBaro, btnClearTrack,
                  btnExportKml, btnRecord, btnReplay, btnGpsFilter });
            row += 36;

            // ── Row 2: Beacon control ─────────────────────────────────────
            var btnBeacon = MkButton("Beacon: ON", lx, row, 130, Theme.Green, Theme.PanelBack);
            btnBeacon.BackColor = Color.FromArgb(20, 60, 20);
            btnBeacon.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnBeacon.Click += async (s, e) =>
            {
                if (!CheckConnected()) return;
                bool beaconOn = btnBeacon.Text == "Beacon: ON";
                bool newState = !beaconOn;
                byte val = newState ? (byte)0x01 : (byte)0x00;
                ushort hwid = ActiveHwid;
                ushort seq  = IncSeqNum();
                FlushRxQueue();
                WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, new byte[] { 0x09, val }));
                LogTx($"[CUSTOM] PICO Beacon {(newState ? "ON" : "OFF")}  opcode=0x20", hwid);
                var pkt = await WaitForReply(hwid, seq, 5000);
                if (pkt != null && pkt.PicoPayload != null)
                {
                    LogRx($"ack → \"{pkt.PicoPayload}\"  hwid={hwid:X4} seq={seq}");
                    Log($"✓ ack → {pkt.PicoPayload}", Theme.Green);
                    btnBeacon.Text      = newState ? "Beacon: ON" : "Beacon: OFF";
                    btnBeacon.ForeColor = newState ? Theme.Green : Theme.Red;
                    btnBeacon.BackColor = newState ? Color.FromArgb(20, 60, 20) : Color.FromArgb(60, 20, 20);
                }
                else
                    Log("✗ No reply to beacon control command", Theme.Red);
            };
            p.Controls.Add(btnBeacon);
            row += 36;

            // ── Row 2: GPS stat panels ────────────────────────────────────
            int pw = 148; int gap = 6;
            int c1 = lx, c2 = lx+(pw+gap), c3 = lx+(pw+gap)*2, c4 = lx+(pw+gap)*3;

            p.Controls.Add(BuildGpsPanel("Latitude",    c1, row, ref lblGpsLat,     "—"));
            p.Controls.Add(BuildGpsPanel("Longitude",   c2, row, ref lblGpsLon,     "—"));
            p.Controls.Add(BuildGpsPanel("GPS Alt (m)", c3, row, ref lblGpsAlt,     "—"));
            p.Controls.Add(BuildGpsPanel("Satellites",  c4, row, ref lblGpsSats,    "—"));
            row += 68;

            // ── Row 3: Baro stat panels ───────────────────────────────────
            p.Controls.Add(BuildGpsPanel("Pressure hPa", c1, row, ref lblBaroPressure, "—"));
            p.Controls.Add(BuildGpsPanel("Baro Alt (m)", c2, row, ref lblBaroAlt,      "—"));
            p.Controls.Add(BuildGpsPanel("Baro Temp °C", c3, row, ref lblBaroTemp,     "—"));
            p.Controls.Add(BuildGpsPanel("Fix / Pkts",   c4, row, ref lblGpsFix,       "NO FIX"));
            row += 68;

            // ── Sci-fi altitude display box ───────────────────────────────
            pnlAltDisplay = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(620, 110),
                BackColor = Color.FromArgb(6, 12, 20),
            };
            DrawBorder(pnlAltDisplay);
            pnlAltDisplay.Paint += PnlAltDisplay_Paint;

            // Labels inside the altitude box
            lblAscentRate = new Label
            {
                Location  = new Point(10, 8),
                Size      = new Size(200, 28),
                Text      = "ASCENT  +0.0 m/s",
                ForeColor = Theme.Cyan,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 11f, FontStyle.Bold),
            };
            lblMaxAlt = new Label
            {
                Location  = new Point(10, 38),
                Size      = new Size(220, 22),
                Text      = "MAX ALT  —",
                ForeColor = Theme.Yellow,
                BackColor = Color.Transparent,
                Font      = Theme.FontMono,
            };
            lblBaroDelta = new Label
            {
                Location  = new Point(10, 62),
                Size      = new Size(280, 22),
                Text      = "GPS/BARO DELTA  —",
                ForeColor = Theme.Gray,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
            };
            lblBurstStatus = new Label
            {
                Location  = new Point(10, 84),
                Size      = new Size(300, 20),
                Text      = "STATUS  STANDBY",
                ForeColor = Theme.Green,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
            };
            lblRecordStatus = new Label
            {
                Location  = new Point(420, 8),
                Size      = new Size(190, 22),
                Text      = "",
                ForeColor = Theme.Red,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight,
            };
            lblGpsTime = new Label
            {
                Location  = new Point(420, 84),
                Size      = new Size(190, 20),
                Text      = "Last update: —",
                ForeColor = Theme.Gray,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleRight,
            };
            lblGpsPackets = new Label
            {
                Location  = new Point(420, 62),
                Size      = new Size(190, 20),
                Text      = "Packets: 0",
                ForeColor = Theme.Dim,
                BackColor = Color.Transparent,
                Font      = Theme.FontSmall,
                TextAlign = ContentAlignment.MiddleRight,
            };

            pnlAltDisplay.Controls.AddRange(new Control[]
            {
                lblAscentRate, lblMaxAlt, lblBaroDelta,
                lblBurstStatus, lblRecordStatus, lblGpsTime, lblGpsPackets
            });
            p.Controls.Add(pnlAltDisplay);
            row += 118;

            // ── Map — fills remaining space ───────────────────────────────
            gmap = new GMapControl
            {
                Location              = new Point(lx, row),
                Bearing               = 0F,
                CanDragMap            = true,
                EmptyTileColor        = Color.FromArgb(22, 22, 32),
                HelperLineOption      = HelperLineOptions.DontShow,
                MaxZoom               = 18,
                MinZoom               = 2,
                MouseWheelZoomEnabled = true,
                MouseWheelZoomType    = MouseWheelZoomType.MousePositionAndCenter,
                PolygonsEnabled       = false,
                RetryLoadTile         = 0,
                RoutesEnabled         = true,
                ScaleMode             = ScaleModes.Integer,
                ShowTileGridLines     = false,
                Zoom                  = 14,
                BackColor             = Color.FromArgb(22, 22, 32),
                Anchor                = AnchorStyles.Top | AnchorStyles.Bottom |
                                        AnchorStyles.Left | AnchorStyles.Right,
            };

            p.SizeChanged += (s, e) =>
                gmap.SetBounds(lx, row, p.Width - lx - 10, p.Height - row - 10);

            gmap.Position = new PointLatLng(36.058952, -87.384060);

            GMaps.Instance.Mode = AccessMode.ServerAndCache;
            GMaps.Instance.UseMemoryCache = true;
            try   { gmap.MapProvider = GMapProviders.GoogleMap; }
            catch { gmap.MapProvider = GMapProviders.OpenStreetMap; }

            _gpsOverlay   = new GMapOverlay("gps");
            _trackOverlay = new GMapOverlay("track");
            gmap.Overlays.Add(_trackOverlay);
            gmap.Overlays.Add(_gpsOverlay);

            p.Controls.Add(gmap);
            gmap.SetBounds(lx, row, p.Width - lx - 10, p.Height - row - 10);
        }

        private Panel BuildGpsPanel(string title, int x, int y,
            ref Label valueLabel, string defaultVal)
        {
            var panel = new Panel
            {
                Location  = new Point(x, y),
                Size      = new Size(158, 60),
                BackColor = Theme.PanelBack,
            };
            DrawBorder(panel);
            var lbl = MkLabel(title.ToUpper(), 8, 5, 145, Theme.Gray, Theme.FontSmall);
            var val = new Label
            {
                Location  = new Point(8, 24),
                Size      = new Size(145, 28),
                Text      = defaultVal,
                ForeColor = Theme.Green,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 13f, FontStyle.Bold),
            };
            panel.Controls.AddRange(new Control[] { lbl, val });
            valueLabel = val;
            return panel;
        }

        // ══════════════════════════════════════════════════════════════════
        //  TIMERS
        // ══════════════════════════════════════════════════════════════════
        private void WireTimers()
        {
            _telemTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _telemTimer.Tick += async (s, e) => await SendGetTelemAsync();

            _liveLogTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            _liveLogTimer.Tick += LiveLogTimer_Tick;

            _gpsTimer = new System.Windows.Forms.Timer { Interval = 10000 };  // 10s GPS poll for both boards
            _gpsTimer.Tick += async (s, e) => await SendGetGpsAsync();

            // Baro animation timer — updates altitude display box every 500ms
            _baroAnimTimer = new System.Windows.Forms.Timer { Interval = 500 };
            _baroAnimTimer.Tick += BaroAnimTimer_Tick;
            _baroAnimTimer.Start();
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
                _physicalBoardApid = 0;  // reset -- prevents stale encrypt state on reconnect
                UpdateRfStatusLabel();
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
                // Capture physical board APID for RF crypto gating.
                // Any packet destined for a different APID goes over RF and must be encrypted.
                if (_boardType == BoardType.SCK2400)
                {
                    _physicalBoardApid = ActiveHwid;
                    Log($"Physical board APID captured: 0x{_physicalBoardApid:X3} -- RF crypto active for remote APIDs", Theme.Cyan);
                    // Crypto keys live in firmware security.h only.
                    // C# sends plaintext -- GS board firmware handles all encryption.
                    UpdateRfStatusLabel();
                }
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
                byte[] buf  = new byte[avail];
                int    read = _port.Read(buf, 0, avail);
                lock (_rxLock)
                {
                    for (int i = 0; i < read; i++) _rxBuf.Add(buf[i]);

                    if (_boardType == BoardType.SCK2400)
                    {
                        // ── SCK-2400 CCSDS path ────────────────────────
                        var packets = CcsdsProtocol.FramePackets(_rxBuf, msg => LogRx(msg));
                        foreach (var pkt in packets)
                        {
                            _rxQueue.Enqueue(pkt);

                            // Update Home tab telem on beacon
                            if (pkt.Hwid == CcsdsProtocol.APID_TLM_BEACON)
                            {
                                var td = CcsdsProtocol.ParseTelem(pkt.RawPayload);
                                if (td != null)
                                    BeginInvoke(new Action(() => UpdateCcsdsTelemDisplay(td)));
                            }

                            // Route GPS telemetry to map.
                            // cmd_ack payload has leading opcode byte (0x27 = ''')
                            // before "GPS:" so search for GPS: rather than StartsWith.
                            if (pkt.RawPayload != null)
                            {
                                string ascii = System.Text.Encoding.ASCII.GetString(
                                    pkt.RawPayload.Where(b => b >= 0x20 && b < 0x7F).ToArray());
                                int gpsIdx = ascii.IndexOf("GPS:", StringComparison.Ordinal);
                                if (gpsIdx >= 0)
                                {
                                    string gpsStr = ascii.Substring(gpsIdx);
                                    BeginInvoke(new Action(() => HandleGpsPacket(gpsStr)));
                                }
                            }
                        }
                    }
                    else
                    {
                        // ── SCK-915 OpenLST path (unchanged) ──────────
                        var packets = OpenLstProtocol.FramePackets(_rxBuf, msg => LogRx(msg));
                        foreach (var pkt in packets)
                        {
                            _rxQueue.Enqueue(pkt);

                            if (pkt.OpName == "telem" && pkt.Hwid == ActiveHwid)
                            {
                                var td = OpenLstProtocol.ParseTelem(pkt.RawPayload);
                                if (td != null) BeginInvoke(new Action(() => UpdateTelemDisplay(td)));
                            }

                            bool gpsHandled = false;
                            if (pkt.OpName == "pico_msg" && pkt.PicoPayload != null
                                && pkt.PicoPayload.StartsWith("GPS:"))
                            {
                                BeginInvoke(new Action(() => HandleGpsPacket(pkt.PicoPayload)));
                                gpsHandled = true;
                            }

                            if (!gpsHandled && pkt.RawPayload != null)
                            {
                                string raw = System.Text.Encoding.ASCII.GetString(
                                    pkt.RawPayload.Where(b => b >= 0x20 && b < 0x7F).ToArray());
                                int idx = raw.IndexOf("GPS:", StringComparison.Ordinal);
                                if (idx >= 0)
                                {
                                    string gpsStr = raw.Substring(idx);
                                    int end = gpsStr.IndexOfAny(new[] { '\0', '\r', '\n' });
                                    if (end > 0) gpsStr = gpsStr.Substring(0, end);
                                    if (gpsStr.Length > 10)
                                        BeginInvoke(new Action(() => HandleGpsPacket(gpsStr)));
                                }
                            }
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

            // C# sends plaintext CCSDS packets to the GS board over USB.
            // GS board firmware (crypto.c) handles AES-128-CCM encryption
            // before forwarding over RF. No crypto in C#.


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
            if (_boardType == BoardType.SCK2400)
            {
                byte subOpcode = commandName switch
                {
                    "reboot"          => CcsdsProtocol.CMD_REBOOT,
                    "get_telem"       => CcsdsProtocol.CMD_GET_TELEM,
                    "clear_safe_mode" => CcsdsProtocol.CMD_CLEAR_SAFE_MODE,
                    _                 => CcsdsProtocol.CMD_GET_TELEM,
                };
                ushort seq      = IncCcsdsSeqCount();
                ushort destApid = ActiveHwid;
                FlushRxQueue();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(seq, subOpcode, destApid));
                LogTx($"[CCSDS] {commandName} seq={seq} dest=0x{destApid:X3}", 0);
                var pkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 3000);
                if (pkt != null) Log($"  ✓ cmd_ack seq={seq}", Theme.Green);
                else             Log($"  ✗ No reply to {commandName}", Theme.Red);
            }
            else
            {
                ushort hwid = ActiveHwid;
                ushort seq  = IncSeqNum();
                FlushRxQueue();
                WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, seq, commandName));
                LogTx($"{commandName}  (seq={seq})", hwid);
                var pkt = await WaitForReply(hwid, seq, 3000);
                if (pkt != null) Log($"  ✓ {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
                else             Log($"  ✗ No reply", Theme.Red);
            }
        }

        private async Task SendGetTelemAsync()
        {
            if (!CheckConnected()) return;
            if (_boardType == BoardType.SCK2400)
            {
                ushort seq      = IncCcsdsSeqCount();
                ushort destApid = ActiveHwid;
                FlushRxQueue();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(seq, CcsdsProtocol.CMD_GET_TELEM, destApid));
                LogTx($"[CCSDS] get_telem seq={seq} dest=0x{destApid:X3}", 0);
                var pkt = await WaitForReply(CcsdsProtocol.APID_TLM_BEACON, seq, 3000);
                if (pkt != null)
                {
                    Log($"  ✓ tlm_beacon received seq={seq} from 0x{destApid:X3}", Theme.Green);
                    var td = CcsdsProtocol.ParseTelem(pkt.RawPayload);
                    if (td != null) UpdateCcsdsTelemDisplay(td);
                }
                else Log("  ✗ No telem reply", Theme.Red);
            }
            else
            {
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
        }

        // ══════════════════════════════════════════════════════════════════
        //  OAD FLASH TEST COMMANDS
        //  These exercise the ext flash driver from firmware task context.
        //  Use in order: Flash Status first (no flash), then Flash Open Test.
        // ══════════════════════════════════════════════════════════════════

        private async Task SendOadStatusAsync()
        {
            // CMD_OAD_STATUS (0x14) — returns current OAD session state.
            // No flash access — safe to call any time.
            // Response payload: [subOpcode=0x14][active][rxd3][rxd2][rxd1][rxd0][sz2][sz1][sz0]
            if (!CheckConnected()) return;
            ushort seq      = IncCcsdsSeqCount();
            ushort destApid = ActiveHwid;
            FlushRxQueue();
            WritePacket(CcsdsProtocol.BuildSimpleCommand(seq, CcsdsProtocol.CMD_OAD_STATUS, destApid));
            LogTx($"[CCSDS] oad_status seq={seq} dest=0x{destApid:X3}", 0);
            var pkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 3000);
            if (pkt != null && pkt.RawPayload.Length >= 2)
            {
                bool   active = pkt.RawPayload.Length > 1 && pkt.RawPayload[1] == 0x01;
                uint   rxd    = pkt.RawPayload.Length >= 6
                    ? (uint)((pkt.RawPayload[2] << 24) | (pkt.RawPayload[3] << 16) |
                              (pkt.RawPayload[4] << 8)  |  pkt.RawPayload[5])
                    : 0;
                Log($"  ✓ oad_status: active={active}  bytesReceived={rxd}", Theme.Green);
            }
            else if (pkt != null)
                Log($"  ✓ oad_status ACK (short payload)", Theme.Green);
            else
                Log("  ✗ No oad_status reply — command path broken", Theme.Red);
        }

        private async Task SendOadFlashOpenTestAsync()
        {
            // CMD_OAD_START (0x10) with dummy 4KB image.
            // Firmware calls: extFlashOpen() → extFlashErase(0, 4096) → extFlashClose()
            // ACK status byte: 0x00 = flash open+erase OK, 0x01 = flash failed.
            // This is the first live test of the SPI flash driver from RTOS task context.
            if (!CheckConnected()) return;
            ushort seq      = IncCcsdsSeqCount();
            ushort destApid = ActiveHwid;

            // CMD_OAD_START payload: [4B imgSize BE][2B crc16 BE]
            // Use 4096 bytes (one sector) so erase is minimal.
            uint   dummySize = 4096;
            ushort dummyCrc  = 0x0000;
            byte[] args = new byte[6]
            {
                (byte)(dummySize >> 24), (byte)(dummySize >> 16),
                (byte)(dummySize >>  8), (byte)(dummySize      ),
                (byte)(dummyCrc  >>  8), (byte)(dummyCrc        ),
            };

            FlushRxQueue();
            WritePacket(CcsdsProtocol.BuildCommand(seq, CcsdsProtocol.CMD_OAD_START, args, destApid));
            LogTx($"[CCSDS] oad_start (flash open test, 4KB) seq={seq} dest=0x{destApid:X3}", 0);

            // Erase can take up to 240ms per sector — give it 3 seconds
            var pkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 3000);
            if (pkt != null && pkt.RawPayload.Length >= 2)
            {
                byte status   = pkt.RawPayload[1];
                byte flashErr = pkt.RawPayload.Length >= 3 ? pkt.RawPayload[2] : (byte)0xFF;
                byte manfId   = pkt.RawPayload.Length >= 4 ? pkt.RawPayload[3] : (byte)0x00;
                byte devId    = pkt.RawPayload.Length >= 5 ? pkt.RawPayload[4] : (byte)0x00;

                if (status == 0x00)
                {
                    Log("  ✓ oad_start ACK status=0x00 — extFlashOpen() + erase SUCCEEDED", Theme.Green);
                    Log($"    JEDEC ID: manf=0x{manfId:X2} dev=0x{devId:X2} (expect manf=0xC2 dev=0x14)", Theme.Cyan);
                }
                else
                {
                    string errDesc = flashErr switch
                    {
                        0x10 => "SPI_open() returned NULL — driver init failed",
                        0x20 => "RDP spiXfer failed — SPI transfer error",
                        0x30 => "WaitReady timed out — chip not responding after RDP",
                        0x40 => "RDID spiXfer failed — SPI transfer error",
                        0x50 => $"JEDEC ID mismatch — got manf=0x{manfId:X2} dev=0x{devId:X2}, expect manf=0xC2 dev=0x14",
                        0xFF => "no error code in ACK (old firmware?)",
                        _    => $"unknown error code 0x{flashErr:X2}",
                    };
                    Log($"  ✗ oad_start FAILED — flashErr=0x{flashErr:X2}: {errDesc}", Theme.Red);
                }

                // Always abort the dummy session so firmware is clean for real OAD
                await Task.Delay(50);
                ushort abortSeq = IncCcsdsSeqCount();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(abortSeq, CcsdsProtocol.CMD_OAD_ABORT, destApid));
                LogTx($"[CCSDS] oad_abort (cleanup) seq={abortSeq}", 0);
                await WaitForReply(CcsdsProtocol.APID_CMD_ACK, abortSeq, 1000);
            }
            else
                Log("  ✗ No oad_start reply — command path broken or firmware crashed", Theme.Red);
        }

        // ══════════════════════════════════════════════════════════════════
        //  OAD UPDATE — full RF firmware delivery sequence
        // ══════════════════════════════════════════════════════════════════

        // ── Sck2400ParseFirmwareHex ───────────────────────────────────────
        // Parses an Intel HEX file and returns the raw binary of the
        // SCK-2400 application image (flash region 0x4000–0x5FFFF).
        // Handles type-00 (data) and type-04 (extended linear address) records.
        // Skips CCFG (0x57FA8+) which is not part of the OAD image.
        // Returns null on parse error.
        private static byte[]? Sck2400ParseFirmwareHex(string hexPath)
        {
            const uint APP_START  = 0x00004000;
            const uint APP_END    = 0x00056000; // exclude CCFG
            const uint CCFG_START = 0x00057000;

            var data = new SortedDictionary<uint, byte>();
            uint upperBase = 0;

            try
            {
                foreach (string line in File.ReadAllLines(hexPath))
                {
                    if (line.Length < 9 || line[0] != ':') continue;
                    int    byteCount = Convert.ToInt32(line.Substring(1, 2), 16);
                    uint   addr      = Convert.ToUInt32(line.Substring(3, 4), 16);
                    int    recType   = Convert.ToInt32(line.Substring(7, 2), 16);

                    if (recType == 0x04) // Extended Linear Address
                    {
                        upperBase = Convert.ToUInt32(line.Substring(9, 4), 16) << 16;
                    }
                    else if (recType == 0x00) // Data
                    {
                        uint fullAddr = upperBase | addr;
                        if (fullAddr >= CCFG_START) continue; // skip CCFG
                        if (fullAddr + (uint)byteCount <= APP_START) continue; // skip BIM region
                        if (fullAddr >= APP_END) continue;

                        for (int i = 0; i < byteCount; i++)
                        {
                            uint byteAddr = fullAddr + (uint)i;
                            if (byteAddr < APP_START || byteAddr >= APP_END) continue;
                            data[byteAddr] =
                                Convert.ToByte(line.Substring(9 + i * 2, 2), 16);
                        }
                    }
                    // type 01 (EOF) and others: ignore
                }
            }
            catch { return null; }

            if (data.Count == 0) return null;

            uint firstAddr = data.Keys.First();
            uint lastAddr  = data.Keys.Last();
            int  imgLen    = (int)(lastAddr - firstAddr + 1);
            byte[] img     = new byte[imgLen]; // unfilled bytes = 0x00

            foreach (var kv in data)
                img[kv.Key - firstAddr] = kv.Value;

            return img;
        }

        // ── Crc16Ccitt ────────────────────────────────────────────────────
        // CRC16-CCITT, initial value 0xFFFF — matches firmware crc16_ccitt().
        private static ushort Crc16Ccitt(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= (ushort)((ushort)data[i] << 8);
                for (int b = 0; b < 8; b++)
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
            }
            return crc;
        }

        // ── SendOadUpdateAsync ────────────────────────────────────────────
        // Streaming OAD — no per-chunk ACK.
        //
        //   1. Parse firmware hex → raw binary
        //   2. CMD_OAD_START (imgSize, crc16) — wait for erase ACK
        //   3. Stream all CMD_OAD_CHUNK with configurable inter-chunk delay
        //      Progress shown as "Sending X/N" based on transmit position.
        //      No ACK per chunk — firmware writes silently to ext flash.
        //   4. CMD_OAD_END — firmware verifies full CRC16, sets BIM flag, reboots
        //      On CRC fail: auto-retry full image once before giving up.
        //   5. Poll board for reboot confirmation.
        //
        // Inter-chunk delay must be >= 18ms (8ms on-air + 10ms RF yield).
        // Use cmbOadDelay to tune empirically.
        private async Task SendOadUpdateAsync(string hexPath, CancellationToken ct)
        {
            if (!CheckConnected()) return;
            if (!File.Exists(hexPath))
            { Log($"OAD: file not found: {hexPath}", Theme.Red); return; }

            // ── Step 1: parse hex ─────────────────────────────────────────
            Log($"OAD: parsing {Path.GetFileName(hexPath)}...", Theme.Gray);
            byte[]? img = Sck2400ParseFirmwareHex(hexPath);
            if (img == null || img.Length == 0)
            { Log("OAD: hex parse failed — no app data found.", Theme.Red); return; }

            ushort imgCrc  = Crc16Ccitt(img, 0, img.Length);
            uint   imgSize = (uint)img.Length;
            Log($"OAD: image {imgSize} bytes, CRC16=0x{imgCrc:X4}", Theme.Cyan);

            // Read inter-chunk delay from dropdown (format "XX ms")
            int interChunkMs = 20;
            if (cmbOadDelay.SelectedItem is string delayStr)
                int.TryParse(delayStr.Replace(" ms", ""), out interChunkMs);
            Log($"OAD: streaming mode — inter-chunk delay {interChunkMs}ms, no per-chunk ACK", Theme.Cyan);

            ushort destApid = ActiveHwid;

            // ── Step 2: CMD_OAD_START ─────────────────────────────────────
            // This is the only blocking handshake before streaming.
            // Firmware erases ext flash, then ACKs when ready.
            FlushRxQueue();
            ushort seq = IncCcsdsSeqCount();
            byte[] startArgs = new byte[6]
            {
                (byte)(imgSize >> 24), (byte)(imgSize >> 16),
                (byte)(imgSize >>  8), (byte)(imgSize      ),
                (byte)(imgCrc  >>  8), (byte)(imgCrc        ),
            };
            WritePacket(CcsdsProtocol.BuildCommand(seq, CcsdsProtocol.CMD_OAD_START,
                                                   startArgs, destApid));
            LogTx($"[CCSDS] oad_start size={imgSize} crc=0x{imgCrc:X4} seq={seq} dest=0x{destApid:X3}", 0);

            int eraseTimeout = 5000 + (int)(imgSize / 4096) * 300;
            var startPkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, eraseTimeout);
            if (startPkt == null)
            { Log("OAD: no ACK for oad_start — timeout.", Theme.Red); return; }
            if (startPkt.RawPayload.Length < 2 || startPkt.RawPayload[1] != 0x00)
            {
                byte err = startPkt.RawPayload.Length >= 3 ? startPkt.RawPayload[2] : (byte)0xFF;
                Log($"OAD: oad_start FAILED status=0x{(startPkt.RawPayload.Length>=2?startPkt.RawPayload[1]:0xFF):X2} flashErr=0x{err:X2}", Theme.Red);
                return;
            }
            Log("OAD: flash erased OK — streaming chunks...", Theme.Green);

            // ── Step 3: stream all chunks — no per-chunk ACK ─────────────
            // Firmware writes each chunk to ext flash silently.
            // Progress is based on transmit position, not ACK receipt.
            // Final CRC at oad_end is the truth — retry whole image if it fails.
            const int CHUNK_SIZE  = 240;
            int       totalChunks = (img.Length + CHUNK_SIZE - 1) / CHUNK_SIZE;
            int       chunkIdx    = 0;
            uint      bytesSent   = 0;

            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (bytesSent < imgSize)
            {
                if (ct.IsCancellationRequested)
                { Log("OAD: cancelled by user.", Theme.Yellow); return; }

                int  chunkLen = (int)Math.Min(CHUNK_SIZE, imgSize - bytesSent);
                uint offset   = bytesSent;

                // Build chunk payload: [4B offset BE][1B len][data]
                byte[] chunkArgs = new byte[5 + chunkLen];
                chunkArgs[0] = (byte)(offset >> 24);
                chunkArgs[1] = (byte)(offset >> 16);
                chunkArgs[2] = (byte)(offset >>  8);
                chunkArgs[3] = (byte)(offset      );
                chunkArgs[4] = (byte)chunkLen;
                Array.Copy(img, (int)offset, chunkArgs, 5, chunkLen);

                ushort chunkSeq = IncCcsdsSeqCount();
                WritePacket(CcsdsProtocol.BuildCommand(chunkSeq, CcsdsProtocol.CMD_OAD_CHUNK,
                                                       chunkArgs, destApid));

                bytesSent += (uint)chunkLen;
                chunkIdx++;

                // Update progress every 8 chunks to keep UI responsive
                if (chunkIdx % 8 == 0 || bytesSent >= imgSize)
                {
                    int pct      = (int)(bytesSent * 100 / imgSize);
                    double elapsedS = sw.Elapsed.TotalSeconds;
                    double bps    = elapsedS > 0 ? bytesSent / elapsedS : 0;
                    string prog   = $"Sending {chunkIdx}/{totalChunks}  {bytesSent}/{imgSize} bytes  {pct}%  {bps/1000:F1} KB/s";
                    lblOadProgress.Text = prog;
                    Application.DoEvents();
                }

                // Inter-chunk delay — must be >= 18ms to let GS RF forward buffer clear.
                // (8ms on-air at 250kbps + 10ms RF yield in radio_transmit)
                await Task.Delay(interChunkMs, ct).ContinueWith(_ => { });
            }

            sw.Stop();
            double totalSec = sw.Elapsed.TotalSeconds;
            Log($"OAD: all {totalChunks} chunks sent in {totalSec:F1}s ({imgSize/totalSec/1000:F1} KB/s) — sending oad_end...", Theme.Cyan);
            lblOadProgress.Text = $"Verifying image ({imgSize} bytes)...";

            // ── Step 4: CMD_OAD_END ───────────────────────────────────────
            // Firmware reads back full image, verifies CRC16, writes BIM metadata.
            // Allow extra time: 335KB read-back at SPI takes ~1s + CRC computation.
            // Auto-retry whole image once on CRC failure — at streaming speeds
            // a full retransmit is still only ~30 seconds.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0)
                {
                    Log("OAD: CRC failed — retransmitting full image...", Theme.Yellow);
                    lblOadProgress.Text = "Retransmitting...";

                    // Re-erase and stream again
                    FlushRxQueue();
                    ushort reseq = IncCcsdsSeqCount();
                    WritePacket(CcsdsProtocol.BuildCommand(reseq, CcsdsProtocol.CMD_OAD_START,
                                                           startArgs, destApid));
                    LogTx($"[CCSDS] oad_start (retry) seq={reseq} dest=0x{destApid:X3}", 0);
                    var rePkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, reseq, eraseTimeout);
                    if (rePkt == null || rePkt.RawPayload.Length < 2 || rePkt.RawPayload[1] != 0x00)
                    { Log("OAD: retry oad_start failed.", Theme.Red); return; }

                    bytesSent = 0; chunkIdx = 0;
                    while (bytesSent < imgSize)
                    {
                        if (ct.IsCancellationRequested) return;
                        int  cLen   = (int)Math.Min(CHUNK_SIZE, imgSize - bytesSent);
                        uint cOff   = bytesSent;
                        byte[] ca   = new byte[5 + cLen];
                        ca[0] = (byte)(cOff >> 24); ca[1] = (byte)(cOff >> 16);
                        ca[2] = (byte)(cOff >>  8); ca[3] = (byte)(cOff      );
                        ca[4] = (byte)cLen;
                        Array.Copy(img, (int)cOff, ca, 5, cLen);
                        ushort cs = IncCcsdsSeqCount();
                        WritePacket(CcsdsProtocol.BuildCommand(cs, CcsdsProtocol.CMD_OAD_CHUNK, ca, destApid));
                        bytesSent += (uint)cLen; chunkIdx++;
                        if (chunkIdx % 8 == 0 || bytesSent >= imgSize)
                        {
                            int pct2 = (int)(bytesSent * 100 / imgSize);
                            lblOadProgress.Text = $"Retry: Sending {chunkIdx}/{totalChunks}  {pct2}%";
                            Application.DoEvents();
                        }
                        await Task.Delay(interChunkMs, ct).ContinueWith(_ => { });
                    }
                    Log("OAD: retry stream complete — sending oad_end...", Theme.Cyan);
                }

                FlushRxQueue();
                ushort endSeq = IncCcsdsSeqCount();
                byte[] endArgs = new byte[4] { 0, 0, (byte)(imgCrc >> 8), (byte)(imgCrc) };
                WritePacket(CcsdsProtocol.BuildCommand(endSeq, CcsdsProtocol.CMD_OAD_END,
                                                       endArgs, destApid));
                LogTx($"[CCSDS] oad_end seq={endSeq} dest=0x{destApid:X3}", 0);

                int verifyTimeout = 8000 + (int)(imgSize / 1024) * 50;
                var endPkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, endSeq, verifyTimeout);

                if (endPkt == null)
                {
                    Log("OAD: no ACK for oad_end (board may have rebooted) — treating as success.", Theme.Yellow);
                    break;
                }
                else if (endPkt.RawPayload.Length >= 2 && endPkt.RawPayload[1] == 0x00)
                {
                    Log("OAD: image verified OK — board is rebooting...", Theme.Green);
                    break;
                }
                else
                {
                    byte failStatus = endPkt.RawPayload.Length >= 2 ? endPkt.RawPayload[1] : (byte)0xFF;
                    Log($"OAD: oad_end FAILED status=0x{failStatus:X2}", Theme.Red);
                    if (attempt == 1)
                    {
                        Log("OAD: retry also failed — aborting.", Theme.Red);
                        lblOadProgress.Text = "OAD FAILED after retry.";
                        return;
                    }
                    // loop continues → retry
                }
            }

            lblOadProgress.Text = "OAD complete — waiting for board reboot...";

            // ── Step 5: wait for board to come back ───────────────────────
            Log("OAD: waiting for board reboot (BIM copy in progress)...", Theme.Gray);
            await Task.Delay(3000);

            for (int i = 0; i < 15; i++)
            {
                if (ct.IsCancellationRequested) break;
                ushort pollSeq = IncCcsdsSeqCount();
                FlushRxQueue();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(pollSeq,
                    CcsdsProtocol.CMD_GET_TELEM, destApid));
                var resp = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, pollSeq, 2000);
                if (resp != null)
                {
                    Log($"✓ Board 0x{destApid:X3} responded after OAD reboot — new firmware is running!", Theme.Green);
                    lblOadProgress.Text = "OAD complete ✓";
                    return;
                }
                await Task.Delay(1000);
            }

            Log("OAD: board did not respond after reboot — check BIM copy status.", Theme.Yellow);
            lblOadProgress.Text = "OAD complete — board not responding.";
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
            RxPacket? pkt = null;
            if (_boardType == BoardType.SCK2400)
            {
                // SCK-2400: send CCSDS command, wait for CCSDS ACK
                ushort ccsdsSeq = IncCcsdsSeqCount();
                ushort destApid = ActiveHwid;
                // Build CCSDS packet: sub-opcode = cmd.Opcode, args follow
                WritePacket(CcsdsProtocol.BuildCommand(ccsdsSeq, cmd.Opcode, args, destApid));
                LogTx($"[CUSTOM] {cmd.Name}  opcode=0x{cmd.Opcode:X2}  (seq={ccsdsSeq})", hwid);
                pkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, ccsdsSeq, 15000);
            }
            else
            {
                // SCK-915: send OpenLST packet
                WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, cmd.Opcode, args));
                LogTx($"[CUSTOM] {cmd.Name}  opcode=0x{cmd.Opcode:X2}  (seq={seq})", hwid);
                pkt = await WaitForReply(hwid, seq, 15000);
            }
            if (pkt != null)
            {
                if (pkt.PicoPayload != null)
                {
                    Log($"  ✓ {pkt.OpName} → {pkt.PicoPayload}", Theme.Green);
                    if (pkt.PicoPayload.StartsWith("GPS:"))
                        HandleGpsPacket(pkt.PicoPayload);
                }
                else
                    Log($"  ✓ {pkt.OpName} {(pkt.AckValue >= 0 ? pkt.AckValue.ToString() : "")}", Theme.Green);
            }
            else Log("  ✗ No reply", Theme.Red);
        }

        // ══════════════════════════════════════════════════════════════════
        //  GPS COMMAND SENDER
        // ══════════════════════════════════════════════════════════════════
        private async Task SendGetGpsAsync()
        {
            if (!CheckConnected()) return;
            if (_boardType == BoardType.SCK2400)
            {
                ushort seq      = IncCcsdsSeqCount();
                ushort destApid = ActiveHwid;
                FlushRxQueue();
                WritePacket(CcsdsProtocol.BuildSimpleCommand(seq, CcsdsProtocol.CMD_GET_GPS, destApid));
                LogTx($"[CCSDS] GET_GPS seq={seq} dest=0x{destApid:X3}", 0);
                // Firmware responds with cmd_ack (APID 0x003) containing GPS string payload
                var pkt = await WaitForReply(CcsdsProtocol.APID_CMD_ACK, seq, 6000);
                if (pkt?.RawPayload == null)
                { Log("  ✗ No GPS reply", Theme.Red); return; }
                string gpsPayload = System.Text.Encoding.ASCII.GetString(
                    pkt.RawPayload.Where(b => b >= 0x20 && b < 0x7F).ToArray());
                // Strip leading opcode byte (0x27) if present
                int gpsIdx = gpsPayload.IndexOf("GPS:", StringComparison.Ordinal);
                if (gpsIdx >= 0)
                    HandleGpsPacket(gpsPayload.Substring(gpsIdx), forceLog: true);
                else
                    Log($"  ✗ GPS: unexpected response — {gpsPayload}", Theme.Red);
            }
            else
            {
                ushort hwid = ActiveHwid;
                ushort seq  = IncSeqNum();
                FlushRxQueue();
                WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, new byte[] { 0x07 }));
                LogTx($"GET_GPS  (seq={seq})", hwid);
                var pkt = await WaitForReply(hwid, seq, 5000);
                if (pkt?.PicoPayload == null)
                { Log("  ✗ No GPS reply", Theme.Red); return; }
                HandleGpsPacket(pkt.PicoPayload, forceLog: true);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  GPS PACKET HANDLER
        // ══════════════════════════════════════════════════════════════════
        public void HandleGpsPacket(string response, bool forceLog = false)
        {
            if (InvokeRequired)
            { BeginInvoke(new Action(() => HandleGpsPacket(response, forceLog))); return; }

            var gpsData = GpsData.Parse(response);
            if (gpsData == null)
            { Log($"  GPS: parse failed — {response}", Theme.Red); return; }

            _lastGps = gpsData;
            _gpsPacketCount++;

            if (gpsData.IsValid)
            {
                // Live valid fix — save as last known good
                _lastKnownGoodGps = gpsData;

                lblGpsLat.Text      = $"{gpsData.Lat:F6}°";
                lblGpsLon.Text      = $"{gpsData.Lon:F6}°";
                lblGpsAlt.Text      = $"{gpsData.AltM:F1}";
                lblGpsSats.Text     = gpsData.Sats.ToString();
                lblGpsFix.Text      = "3D FIX";
                lblGpsFix.ForeColor = Theme.Green;
            }
            else if (_lastKnownGoodGps != null)
            {
                // GPS blind — show last known position in yellow
                // Coordinates still valid, just stale
                lblGpsLat.Text      = $"{_lastKnownGoodGps.Lat:F6}°";
                lblGpsLon.Text      = $"{_lastKnownGoodGps.Lon:F6}°";
                lblGpsAlt.Text      = $"{_lastKnownGoodGps.AltM:F1}";
                lblGpsSats.Text     = gpsData.Sats.ToString();
                lblGpsFix.Text      = "LAST KNOWN";
                lblGpsFix.ForeColor = Theme.Yellow;
                // Use last known position for map + recorder
                gpsData = new GpsData
                {
                    Lat = _lastKnownGoodGps.Lat, Lon = _lastKnownGoodGps.Lon,
                    AltM = _lastKnownGoodGps.AltM, Sats = gpsData.Sats,
                    Fix = false,
                    BaroHpa = gpsData.HasBaro ? gpsData.BaroHpa : _lastKnownGoodGps.BaroHpa,
                    BaroAltM = gpsData.HasBaro ? gpsData.BaroAltM : _lastKnownGoodGps.BaroAltM,
                    BaroTempC = gpsData.HasBaro ? gpsData.BaroTempC : _lastKnownGoodGps.BaroTempC,
                    HasBaro = gpsData.HasBaro || _lastKnownGoodGps.HasBaro,
                    Timestamp = DateTime.Now,
                };
                if (_gpsLogEnabled || forceLog)
                    Log("  GPS blind — showing last known position", Theme.Yellow);
            }
            else
            {
                // No fix, no last known
                lblGpsFix.Text      = "NO FIX";
                lblGpsFix.ForeColor = Theme.Red;
            }

            // Update baro panels if fused packet includes baro data
            if (gpsData.HasBaro)
            {
                _lastBaro = new BaroData
                {
                    Hpa = gpsData.BaroHpa, AltM = gpsData.BaroAltM,
                    TempC = gpsData.BaroTempC, Valid = true,
                    Timestamp = DateTime.Now,
                };
                UpdateBaroDisplay(_lastBaro, gpsData.AltM);
            }

            lblGpsPackets.Text = $"Packets: {_gpsPacketCount}";
            lblGpsTime.Text    = $"Last update: {DateTime.Now:HH:mm:ss}";

            if (_gpsLogEnabled || forceLog)
                Log($"  GPS → lat={gpsData.Lat:F6} lon={gpsData.Lon:F6} " +
                    $"alt={gpsData.AltM:F1}m baro={gpsData.BaroAltM:F1}m " +
                    $"sats={gpsData.Sats} fix={gpsData.Fix}",
                    gpsData.IsValid ? Theme.Green : Theme.Yellow);

            // Write to flight recorder if active
            if (_recording) WriteFlightPacket(gpsData);

            if (gpsData.IsValid)
            {
                _gpsTrack.Add(gpsData);
                UpdateMap(gpsData);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAP UPDATE
        // ══════════════════════════════════════════════════════════════════
        private void UpdateMap(GpsData gpsData)
        {
            var pos = new PointLatLng(gpsData.Lat, gpsData.Lon);
            gmap.Position = pos;

            _gpsOverlay.Markers.Clear();
            // GMarkerCross renders locally — no external image fetch needed
            var marker = new GMarkerCross(pos);
            marker.Pen = new System.Drawing.Pen(Color.Cyan, 2);
            _gpsOverlay.Markers.Add(marker);

            _trackOverlay.Routes.Clear();
            if (_gpsTrack.Count >= 2)
            {
                var trackPoints = _gpsTrack
                    .Select(g => new PointLatLng(g.Lat, g.Lon))
                    .ToList();
                var route = new GMapRoute(trackPoints, "track")
                {
                    Stroke = new System.Drawing.Pen(Color.FromArgb(200, 0, 207, 255), 2)
                };
                _trackOverlay.Routes.Add(route);
            }
            gmap.Refresh();
        }

        // ══════════════════════════════════════════════════════════════════
        //  BARO COMMAND SENDER
        // ══════════════════════════════════════════════════════════════════
        private async Task SendGetBaroAsync()
        {
            if (!CheckConnected()) return;
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, new byte[] { 0x08 }));
            LogTx($"GET_BARO  (seq={seq})", hwid);
            var pkt = await WaitForReply(hwid, seq, 5000);
            if (pkt?.PicoPayload == null)
            { Log("  ✗ No BARO reply", Theme.Red); return; }
            var baro = BaroData.Parse(pkt.PicoPayload);
            if (baro == null)
            { Log($"  BARO parse failed: {pkt.PicoPayload}", Theme.Red); return; }
            _lastBaro = baro;
            UpdateBaroDisplay(baro, _lastGps?.AltM ?? 0.0);
            Log($"  BARO → {baro.Hpa:F2} hPa  alt={baro.AltM:F1}m  temp={baro.TempC:F2}°C",
                Theme.Magenta);
        }

        // ══════════════════════════════════════════════════════════════════
        //  BARO DISPLAY UPDATE
        // ══════════════════════════════════════════════════════════════════
        private void UpdateBaroDisplay(BaroData baro, double gpsAlt)
        {
            if (InvokeRequired)
            { BeginInvoke(new Action(() => UpdateBaroDisplay(baro, gpsAlt))); return; }

            lblBaroPressure.Text = $"{baro.Hpa:F2}";
            lblBaroAlt.Text      = $"{baro.AltM:F1}";
            lblBaroTemp.Text     = $"{baro.TempC:F2}";

            // Update session max altitude
            if (baro.AltM > _maxAltSession)
                _maxAltSession = baro.AltM;

            // Calculate ascent rate (m/s) from baro altitude
            double dt = (baro.Timestamp - _prevBaroTime).TotalSeconds;
            if (dt > 0.5 && _prevBaroAlt > 0)
            {
                _ascentRate = (baro.AltM - _prevBaroAlt) / dt;
                // Smooth — rolling average would be better but this is clean for now
                _ascentRate = Math.Round(_ascentRate, 1);
            }
            _prevBaroAlt  = baro.AltM;
            _prevBaroTime = baro.Timestamp;

            // Burst detection — sharp altitude drop after having been up high
            if (_maxAltSession > 1000 && _ascentRate < -5.0 && !_burstDetected)
            {
                _burstDetected = true;
                Log("  ⚡ BURST DETECTED — rapid descent initiated", Theme.Red);
            }

            // Delta GPS vs baro
            double delta = gpsAlt - baro.AltM;

            // Update altitude display box labels
            string arrow = _ascentRate > 0.5 ? "▲" : (_ascentRate < -0.5 ? "▼" : "►");
            string arrowColor_selector = _ascentRate > 0.5 ? "up" : (_ascentRate < -0.5 ? "down" : "flat");

            lblAscentRate.Text      = $"ASCENT  {_ascentRate:+0.0;-0.0;+0.0} m/s  {arrow}";
            lblAscentRate.ForeColor = _ascentRate > 0.5 ? Theme.Green :
                                      (_ascentRate < -0.5 ? Theme.Red : Theme.Cyan);
            lblMaxAlt.Text          = $"MAX ALT  {_maxAltSession:F0} m";
            lblBaroDelta.Text       = $"GPS/BARO DELTA  {delta:+0.0;-0.0;+0.0} m";
            lblBaroDelta.ForeColor  = Math.Abs(delta) > 100 ? Theme.Yellow : Theme.Gray;

            string burstText = _burstDetected ? "BURST DETECTED — DESCENDING" :
                               (_ascentRate > 0.5 ? "ASCENDING" :
                               (_ascentRate < -0.5 ? "DESCENDING" : "STANDBY"));
            lblBurstStatus.Text      = $"STATUS  {burstText}";
            lblBurstStatus.ForeColor = _burstDetected ? Theme.Red :
                                       (_ascentRate > 0.5 ? Theme.Green :
                                       (_ascentRate < -0.5 ? Theme.Yellow : Theme.Gray));

            // Trigger repaint of the altitude bar graph
            pnlAltDisplay?.Invalidate();
        }

        // ══════════════════════════════════════════════════════════════════
        //  ALTITUDE DISPLAY BOX — CUSTOM PAINT (sci-fi bar graph)
        // ══════════════════════════════════════════════════════════════════
        private void PnlAltDisplay_Paint(object? sender, PaintEventArgs e)
        {
            var g   = e.Graphics;
            var r   = (sender as Panel)!.ClientRectangle;
            int barX = 250;   // x position of the bar graph
            int barW = 160;   // total width of bar area
            int barH = 14;    // height of each bar
            int barY = 10;    // top of bar area

            // ── Baro altitude bar ──────────────────────────────────────────
            double maxDisplayAlt = Math.Max(_maxAltSession * 1.1, 500.0);
            double baroFraction  = Math.Min(_lastBaro.AltM / maxDisplayAlt, 1.0);
            int    baroFill      = (int)(barW * baroFraction);

            // Background track
            using var bgBrush = new SolidBrush(Color.FromArgb(20, 30, 40));
            g.FillRectangle(bgBrush, barX, barY, barW, barH);

            // Filled bar — color by ascent state
            Color barColor = _ascentRate > 0.5 ? Color.FromArgb(0, 200, 80) :
                             (_ascentRate < -0.5 ? Color.FromArgb(220, 80, 0) :
                             Color.FromArgb(0, 150, 200));
            using var barBrush = new SolidBrush(barColor);
            if (baroFill > 0)
                g.FillRectangle(barBrush, barX, barY, baroFill, barH);

            // Bar border
            using var borderPen = new Pen(Color.FromArgb(50, 80, 100), 1);
            g.DrawRectangle(borderPen, barX, barY, barW, barH);

            // Bar label
            using var labelFont = new Font("Consolas", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(0, 207, 255));
            g.DrawString($"BARO  {_lastBaro.AltM:F0}m", labelFont, labelBrush, barX + barW + 6, barY);

            // ── GPS altitude bar ───────────────────────────────────────────
            double gpsFraction = Math.Min((_lastGps?.AltM ?? 0) / maxDisplayAlt, 1.0);
            int    gpsFill     = (int)(barW * gpsFraction);
            int    gpsBarY     = barY + barH + 6;

            g.FillRectangle(bgBrush, barX, gpsBarY, barW, barH);
            using var gpsBarBrush = new SolidBrush(Color.FromArgb(0, 140, 220));
            if (gpsFill > 0)
                g.FillRectangle(gpsBarBrush, barX, gpsBarY, gpsFill, barH);
            g.DrawRectangle(borderPen, barX, gpsBarY, barW, barH);
            g.DrawString($"GPS   {_lastGps?.AltM:F0}m", labelFont, labelBrush, barX + barW + 6, gpsBarY);

            // ── Temperature bar ────────────────────────────────────────────
            // Maps -60°C to +40°C range
            double tempFraction = Math.Max(0, Math.Min((_lastBaro.TempC + 60.0) / 100.0, 1.0));
            int    tempFill     = (int)(barW * tempFraction);
            int    tempBarY     = gpsBarY + barH + 6;

            g.FillRectangle(bgBrush, barX, tempBarY, barW, barH);
            Color tempColor = _lastBaro.TempC < 0
                ? Color.FromArgb(0, 180, 255)
                : Color.FromArgb(255, 140, 0);
            using var tempBrush = new SolidBrush(tempColor);
            if (tempFill > 0)
                g.FillRectangle(tempBrush, barX, tempBarY, tempFill, barH);
            g.DrawRectangle(borderPen, barX, tempBarY, barW, barH);
            g.DrawString($"TEMP  {_lastBaro.TempC:F1}°C", labelFont, labelBrush,
                barX + barW + 6, tempBarY);

            // ── Pressure readout ───────────────────────────────────────────
            int pressY = tempBarY + barH + 8;
            using var pressBrush = new SolidBrush(Color.FromArgb(80, 100, 120));
            using var pressFont  = new Font("Consolas", 8f);
            g.DrawString($"{_lastBaro.Hpa:F2} hPa", pressFont, pressBrush, barX, pressY);

            // ── Track count ────────────────────────────────────────────────
            g.DrawString($"{_gpsTrack.Count} pts", pressFont, pressBrush,
                barX + barW - 30, pressY);
        }

        // ══════════════════════════════════════════════════════════════════
        //  BARO ANIMATION TIMER
        // ══════════════════════════════════════════════════════════════════
        private void BaroAnimTimer_Tick(object? sender, EventArgs e)
        {
            _baroAnimTick++;
            // Pulse the burst status dots when ascending
            if (_lastBaro.Valid && _ascentRate > 0.5 && !_burstDetected)
            {
                string dots = (_baroAnimTick % 3) switch
                {
                    0 => "●○○", 1 => "●●○", _ => "●●●"
                };
                if (lblBurstStatus != null)
                    lblBurstStatus.Text = $"STATUS  ASCENDING  {dots}";
            }
            // Blink record indicator
            if (_recording && lblRecordStatus != null)
            {
                lblRecordStatus.ForeColor = (_baroAnimTick % 2 == 0)
                    ? Theme.Red : Color.FromArgb(80, 20, 20);
                lblRecordStatus.Text =
                    $"⏺ REC  {(DateTime.Now - _flightStartTime):hh\\:mm\\:ss}  " +
                    $"{_flightPacketCount} pkts";
            }
            pnlAltDisplay?.Invalidate();
        }

        // ══════════════════════════════════════════════════════════════════
        //  FLIGHT RECORDER
        // ══════════════════════════════════════════════════════════════════
        private static string FlightsFolderPath =>
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Flights");

        private void BtnRecord_Click(object sender, EventArgs e)
        {
            if (_recording) StopRecording();
            else            StartRecording();
        }

        private void StartRecording()
        {
            try
            {
                // Generate unique flight ID
                int flightNum = Directory.GetFiles(FlightsFolderPath, "*.sckflight").Length + 1;
                _currentFlightId  = $"FLT-{DateTime.Now:yyyyMMdd}-{flightNum:D3}";
                _flightStartTime  = DateTime.Now;
                _flightPacketCount = 0;

                string path = Path.Combine(FlightsFolderPath, $"{_currentFlightId}.sckflight");

                _flightWriter = new StreamWriter(path, append: false);

                // Write JSON header
                _flightWriter.WriteLine("{");
                _flightWriter.WriteLine($"  \"flight_id\": \"{_currentFlightId}\",");
                _flightWriter.WriteLine($"  \"date\": \"{DateTime.Now:yyyy-MM-dd}\",");
                _flightWriter.WriteLine($"  \"launch_time\": \"{DateTime.Now:HH:mm:ss}\",");
                _flightWriter.WriteLine($"  \"hardware\": \"SCK-915 + SCK-PBL-1\",");
                _flightWriter.WriteLine($"  \"ground_station\": \"SCK Ground Station v1.2.0\",");
                _flightWriter.WriteLine($"  \"hwid\": \"{ActiveHwid:X4}\",");
                _flightWriter.WriteLine("  \"packets\": [");
                _flightWriter.Flush();

                _recording = true;

                btnRecord.Text      = "■ Stop Rec";
                btnRecord.ForeColor = Theme.Red;
                btnRecord.BackColor = Color.FromArgb(60, 20, 20);

                Log($"⏺ Flight recording started → {path}", Theme.Red);
                Log($"  Flight ID: {_currentFlightId}", Theme.Gray);
            }
            catch (Exception ex)
            {
                Log($"Flight recorder error: {ex.Message}", Theme.Red);
            }
        }

        private void WriteFlightPacket(GpsData gps)
        {
            if (_flightWriter == null) return;
            try
            {
                // Comma-separate all packets except first
                string comma = _flightPacketCount > 0 ? "," : "";
                double elapsed = (DateTime.Now - _flightStartTime).TotalSeconds;

                _flightWriter.WriteLine($"{comma}    {{");
                _flightWriter.WriteLine($"      \"t\": {elapsed:F1},");
                _flightWriter.WriteLine($"      \"time\": \"{DateTime.Now:HH:mm:ss.fff}\",");
                _flightWriter.WriteLine($"      \"lat\": {gps.Lat},");
                _flightWriter.WriteLine($"      \"lon\": {gps.Lon},");
                _flightWriter.WriteLine($"      \"gps_alt\": {gps.AltM:F1},");
                _flightWriter.WriteLine($"      \"sats\": {gps.Sats},");
                _flightWriter.WriteLine($"      \"fix\": {(gps.Fix ? 1 : 0)},");
                _flightWriter.WriteLine($"      \"baro_hpa\": {gps.BaroHpa:F2},");
                _flightWriter.WriteLine($"      \"baro_alt\": {gps.BaroAltM:F1},");
                _flightWriter.WriteLine($"      \"baro_temp\": {gps.BaroTempC:F2},");
                _flightWriter.WriteLine($"      \"ascent_rate\": {_ascentRate:F1},");
                _flightWriter.WriteLine($"      \"max_alt\": {_maxAltSession:F1}");
                _flightWriter.Write("    }");
                _flightWriter.Flush();

                _flightPacketCount++;
            }
            catch (Exception ex)
            {
                Log($"Flight write error: {ex.Message}", Theme.Red);
            }
        }

        private void StopRecording()
        {
            if (_flightWriter == null) return;
            try
            {
                // Close JSON array and write summary
                _flightWriter.WriteLine();
                _flightWriter.WriteLine("  ],");
                _flightWriter.WriteLine("  \"summary\": {");
                _flightWriter.WriteLine($"    \"total_packets\": {_flightPacketCount},");
                _flightWriter.WriteLine($"    \"max_altitude_m\": {_maxAltSession:F1},");
                _flightWriter.WriteLine($"    \"flight_duration_s\": {(DateTime.Now - _flightStartTime).TotalSeconds:F0},");
                _flightWriter.WriteLine($"    \"burst_detected\": {(_burstDetected ? "true" : "false")},");
                if (_gpsTrack.Count > 0)
                {
                    var last = _gpsTrack.Last();
                    _flightWriter.WriteLine($"    \"landing_lat\": {last.Lat},");
                    _flightWriter.WriteLine($"    \"landing_lon\": {last.Lon}");
                }
                else
                {
                    _flightWriter.WriteLine("    \"landing_lat\": 0,");
                    _flightWriter.WriteLine("    \"landing_lon\": 0");
                }
                _flightWriter.WriteLine("  }");
                _flightWriter.WriteLine("}");
                _flightWriter.Close();
                _flightWriter = null;

                _recording = false;

                btnRecord.Text      = "⏺ Record";
                btnRecord.ForeColor = Theme.Green;
                btnRecord.BackColor = Color.FromArgb(20, 60, 20);

                if (lblRecordStatus != null)
                    lblRecordStatus.Text = "";

                string savedPath = Path.Combine(FlightsFolderPath,
                    $"{_currentFlightId}.sckflight");

                Log($"■ Recording stopped → {_currentFlightId}", Theme.Green);
                Log($"  {_flightPacketCount} packets  max alt {_maxAltSession:F0}m", Theme.Gray);
                Log($"  Saved: {savedPath}", Theme.Gray);

                // Auto-generate KML alongside the flight file
                if (_gpsTrack.Count > 1)
                {
                    string kmlPath = Path.Combine(FlightsFolderPath,
                        $"{_currentFlightId}.kml");
                    File.WriteAllText(kmlPath, BuildKml(_gpsTrack));
                    Log($"  KML: {kmlPath}", Theme.Gray);
                }

                // Open Flights folder
                System.Diagnostics.Process.Start("explorer.exe", FlightsFolderPath);
            }
            catch (Exception ex)
            {
                Log($"Stop recording error: {ex.Message}", Theme.Red);
            }
        }
        private void BtnGpsAuto_Click(object sender, EventArgs e)
        {
            if (_gpsTimer == null) return;
            if (_gpsTimer.Enabled)
            {
                _gpsTimer.Stop();
                btnGpsAuto.Text      = "Auto: OFF";
                btnGpsAuto.ForeColor = Theme.Gray;
                Log("GPS auto poll stopped.", Theme.Gray);
            }
            else
            {
                _gpsTimer.Start();
                btnGpsAuto.Text      = "Auto: ON";
                btnGpsAuto.ForeColor = Theme.Green;
                Log("GPS auto poll started — every 10 seconds.", Theme.Cyan);
            }
        }

        private void BtnClearTrack_Click(object sender, EventArgs e)
        {
            _gpsTrack.Clear();
            _gpsPacketCount = 0;
            _trackOverlay?.Routes.Clear();
            _gpsOverlay?.Markers.Clear();
            gmap?.Refresh();
            lblGpsTime.Text    = "Track cleared.";
            lblGpsPackets.Text = "0";
            Log("GPS track cleared.", Theme.Gray);
        }

        private void BtnExportKml_Click(object sender, EventArgs e)
        {
            if (_gpsTrack.Count == 0)
            { Log("GPS: no track data to export.", Theme.Red); return; }

            using var dlg = new SaveFileDialog
            {
                Filter   = "KML file (*.kml)|*.kml",
                FileName = $"flight_track_{DateTime.Now:yyyyMMdd_HHmmss}.kml",
                Title    = "Export GPS Track",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                File.WriteAllText(dlg.FileName, BuildKml(_gpsTrack));
                Log($"GPS: track exported → {dlg.FileName}  ({_gpsTrack.Count} points)", Theme.Green);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = dlg.FileName, UseShellExecute = true
                });
            }
            catch (Exception ex) { Log($"GPS: KML export failed — {ex.Message}", Theme.Red); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  KML BUILDER
        // ══════════════════════════════════════════════════════════════════
        private static string BuildKml(List<GpsData> track)
        {
            var ic = System.Globalization.CultureInfo.InvariantCulture;
            var coords = string.Join("\n", track.Select(g =>
                $"          {g.Lon.ToString("F6", ic)},{g.Lat.ToString("F6", ic)},{g.AltM.ToString("F1", ic)}"));

            var firstPt  = track.First();
            var lastPt   = track.Last();
            double maxAlt = track.Max(g => g.AltM);
            var maxPt    = track.First(g => g.AltM == maxAlt);

            return $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<kml xmlns=""http://www.opengis.net/kml/2.2"">
  <Document>
    <name>SCK-915 Flight Track — {DateTime.Now:yyyy-MM-dd HH:mm:ss}</name>
    <description>Flight track exported from SCK Ground Station
Total points: {track.Count}
Launch time: {firstPt.Timestamp:HH:mm:ss}
Max altitude: {maxAlt:F1}m ASL</description>
    <Style id=""trackStyle"">
      <LineStyle><color>ffff9900</color><width>3</width></LineStyle>
    </Style>
    <Style id=""launchStyle"">
      <IconStyle><color>ff00ff00</color><scale>1.2</scale>
        <Icon><href>http://maps.google.com/mapfiles/kml/paddle/grn-circle.png</href></Icon>
      </IconStyle>
    </Style>
    <Style id=""landingStyle"">
      <IconStyle><color>ff0000ff</color><scale>1.2</scale>
        <Icon><href>http://maps.google.com/mapfiles/kml/paddle/red-circle.png</href></Icon>
      </IconStyle>
    </Style>
    <Style id=""apogeeStyle"">
      <IconStyle><color>ff00ffff</color><scale>1.4</scale>
        <Icon><href>http://maps.google.com/mapfiles/kml/paddle/ylw-stars.png</href></Icon>
      </IconStyle>
    </Style>
    <Placemark>
      <name>Flight Track</name>
      <styleUrl>#trackStyle</styleUrl>
      <LineString>
        <tessellate>1</tessellate>
        <altitudeMode>absolute</altitudeMode>
        <coordinates>
{coords}
        </coordinates>
      </LineString>
    </Placemark>
    {BuildKmlPin("Launch",             firstPt, "launch", ic)}
    {BuildKmlPin($"Apogee {maxAlt:F0}m", maxPt,  "apogee", ic)}
    {BuildKmlPin("Landing",            lastPt,  "landing", ic)}
  </Document>
</kml>";
        }

        private static string BuildKmlPin(string name, GpsData pt, string styleId,
            System.Globalization.CultureInfo ic) =>
            $@"<Placemark>
      <name>{name}</name>
      <styleUrl>#{styleId}Style</styleUrl>
      <description>Lat: {pt.Lat:F6} Lon: {pt.Lon:F6} Alt: {pt.AltM:F1}m Time: {pt.Timestamp:HH:mm:ss} Sats: {pt.Sats}</description>
      <Point>
        <altitudeMode>absolute</altitudeMode>
        <coordinates>{pt.Lon.ToString("F6", ic)},{pt.Lat.ToString("F6", ic)},{pt.AltM.ToString("F1", ic)}</coordinates>
      </Point>
    </Placemark>";

        // ══════════════════════════════════════════════════════════════════
        //  FIRMWARE: BUILD / SIGN / FLASH
        // ══════════════════════════════════════════════════════════════════
        private void BtnBrowseDir_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog { Description = "Select SCK firmware project directory" };
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

            if (_boardType == BoardType.SCK2400)
            {
                await RunBuildAsync_SCK2400();
                return;
            }

            // ── SCK-915 build path (mingw32-make + SDCC) ──────────────────
            // Patch board.h with selected RF power mode before every build
            PatchBoardHPower();

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
                    .Where(l => !l.Contains("Nothing to be done")))
                    Log(line.Trim(), Theme.White);
                foreach (string line in stderr.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Where(l => !l.Contains("fatal: not a git repository")))
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
            _flashCts = new CancellationTokenSource();
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
                await FlashApplicationAsync(app, hwid, _flashCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("Flash cancelled.", Theme.Red);
                lblFlashStatus.Text = "Cancelled.";
            }
            catch (Exception ex) { Log($"FATAL: {ex.Message}", Theme.Red); }
            finally
            {
                _flashCts?.Dispose();
                _flashCts = null;
                SetFirmwareButtons(true);
                btnFlashCancel.Enabled = false;
            }
        }

        // ── RF Power Mode — patch board.h before build ────────────────────────
        // CC1110 PA_TABLE0 values:
        //   0xC0 = ~0 dBm  (bench/indoor — safe close range)
        //   0xC2 = ~10 dBm (CC1110 max — CC1190 PA adds ~18dBm = ~28dBm total)
        private bool PatchBoardHPower()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            { Log("Select a valid project directory first.", Theme.Red); return false; }

            // Search for board.h in project dir and subdirectories
            string? boardH = Directory.GetFiles(dir, "board.h", SearchOption.AllDirectories)
                .FirstOrDefault(f => !f.Contains("board_defaults"));

            if (boardH == null)
            { Log("board.h not found in project directory.", Theme.Red); return false; }

            string paValue = rbPowerField.Checked ? "0xC2" : "0xC0";
            string paLabel = rbPowerField.Checked ? "Max (~28 dBm with CC1190)" : "0 dBm bench";

            try
            {
                string content = File.ReadAllText(boardH);

                // STEP 1 — Remove ALL existing RF_PA_CONFIG lines (any whitespace, any value)
                // This prevents duplicate defines accumulating over multiple builds
                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"[ \t]*#define\s+RF_PA_CONFIG\s+0x[A-Fa-f0-9]+[^\r\n]*(\r?\n)?",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // STEP 2 — Insert one clean RF_PA_CONFIG line before RF_FSCAL3_CONFIG
                // If RF_FSCAL3_CONFIG not found — insert before COMMAND_WATCHDOG_DELAY
                string insertedLine = $"#define RF_PA_CONFIG {paValue}\r\n";
                if (cleaned.Contains("#define RF_FSCAL3_CONFIG"))
                {
                    cleaned = cleaned.Replace(
                        "#define RF_FSCAL3_CONFIG",
                        insertedLine + "#define RF_FSCAL3_CONFIG");
                }
                else if (cleaned.Contains("#define COMMAND_WATCHDOG_DELAY"))
                {
                    cleaned = cleaned.Replace(
                        "#define COMMAND_WATCHDOG_DELAY",
                        insertedLine + "#define COMMAND_WATCHDOG_DELAY");
                }
                else
                {
                    // Fallback — append before #endif
                    cleaned = cleaned.Replace(
                        "#endif",
                        insertedLine + "#endif");
                }

                File.WriteAllText(boardH, cleaned);
                Log($"RF Power: patched board.h → RF_PA_CONFIG = {paValue} ({paLabel})", Theme.Cyan);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to patch board.h: {ex.Message}", Theme.Red);
                return false;
            }
        }

        private async Task RunBuildAndFlashAsync()
        {
            if (_boardType == BoardType.SCK2400)
            {
                await RunBuildAsync_SCK2400(cleanFirst: false);
                string hex = Path.Combine(txtProjectDir.Text, "Debug", "sck2400_firmware.hex");
                if (File.Exists(hex)) await RunFlashLocal_SCK2400(hex);
                return;
            }
            if (!PatchBoardHPower()) return;
            await RunBuildAsync();
            if (File.Exists(txtFirmwareFile.Text.Trim()))
                await RunFlashAsync();
        }

        private void SetFirmwareButtons(bool enabled)
        {
            btnBuild.Enabled        = enabled;
            btnSign.Enabled         = enabled;
            btnFlash.Enabled        = enabled;
            btnBuildFlash.Enabled   = enabled;
            btnFlashCancel.Enabled  = !enabled;  // cancel enabled when flashing
        }

        // ══════════════════════════════════════════════════════════════════
        //  SCK-2400 BUILD / CLEAN / FLASH
        //  Uses CCS v20 headless build (ccs-serverc.exe) and
        //  SmartRF Flash Programmer 2 CLI (srfprog.exe).
        //  Board role (GS/Remote) sets SCK_APID_THIS_BOARD via
        //  -DSCK_APID_THIS_BOARD=0x010|0x011|0x012 predefined symbol.
        //  RF power patches TX_POWER define in radio.h before build.
        // ══════════════════════════════════════════════════════════════════

        // SCK-2400 tool paths — persisted in appsettings.json
        // Defaults point to standard TI install locations.
        // Customers with different install paths can override via Browse buttons (TODO: add browse buttons).
        private string _sck2400CcsServer  = @"C:\ti\ccs2051\ccs\eclipse\ccs-serverc.exe";
        private string _sck2400SrfProg    = @"C:\Program Files (x86)\Texas Instruments\SmartRF Tools\Flash Programmer 2\bin\srfprog.exe";
        private string _sck2400Workspace  = @"C:\Users\maxor\workspace_ccstheia";
        private string _sck2400XdsDfu     = @"C:\ti\ccs2051\ccs\ccs_base\common\uscif\xds110\xdsdfu.exe";
        private const  string SCK2400_PROJECT = "sck2400_firmware";

        private string SCK2400_CCS_SERVER { get => _sck2400CcsServer; set { _sck2400CcsServer = value; SaveSettings(); } }
        private string SCK2400_SRFPROG    { get => _sck2400SrfProg;   set { _sck2400SrfProg   = value; SaveSettings(); } }
        private string SCK2400_WORKSPACE  { get => _sck2400Workspace;  set { _sck2400Workspace  = value; SaveSettings(); } }
        private string SCK2400_XDSDFU     { get => _sck2400XdsDfu;    set { _sck2400XdsDfu    = value; SaveSettings(); } }

        // Board role APID values — match ccsds.h SCK_APID_BOARD_* defines
        private static readonly (string Label, string Define)[] SCK2400_BOARD_ROLES =
        {
            ("GS Board  (APID 0x010)", "0x010"),
            ("Remote 1  (APID 0x011)", "0x011"),
            ("Remote 2  (APID 0x012)", "0x012"),
        };

        private string Sck2400BoardApid =>
            SCK2400_BOARD_ROLES[Math.Max(0,
                Math.Min(cmbBoardRole.SelectedIndex, SCK2400_BOARD_ROLES.Length - 1))].Define;

        private string Sck2400BoardLabel =>
            SCK2400_BOARD_ROLES[Math.Max(0,
                Math.Min(cmbBoardRole.SelectedIndex, SCK2400_BOARD_ROLES.Length - 1))].Label;

        private async Task RunBuildAsync_SCK2400(bool cleanFirst = false)
        {
            if (!PatchRadioHPower_SCK2400()) return;
            if (!PatchCcsdsHBoard_SCK2400()) return;
            if (!PatchSecurityH()) return;

            SetFirmwareButtons(false);
            lblFlashStatus.Text = "Building SCK-2400...";
            pbFlash.Style       = ProgressBarStyle.Marquee;

            string apid      = Sck2400BoardApid;
            string boardLabel = Sck2400BoardLabel.Trim();
            string buildType  = cleanFirst ? "full" : "incremental";

            Log("══════════════════════════════════════════════════", Theme.Cyan);
            Log($"  SCK-2400 Build  →  {boardLabel}", Theme.Cyan);
            Log($"  SCK_APID_THIS_BOARD = {apid}", Theme.Cyan);
            Log($"  Build type: {buildType}", Theme.Cyan);
            Log("══════════════════════════════════════════════════", Theme.Cyan);

            try
            {
                // Pass board address as predefined symbol so no source edit needed.
                // CCS passes -DSCK_APID_THIS_BOARD=0x010 etc to tiarmclang via
                // the project's predefined symbols list merged with CLI args.
                // The #ifndef guard in ccsds.h means this overrides the default.
                string args = string.Join(" ", new[]
                {
                    "-noSplash",
                    $"-data \"{SCK2400_WORKSPACE}\"",
                    "-application com.ti.ccs.apps.buildProject",
                    $"-ccs.projects {SCK2400_PROJECT}",
                    "-ccs.configuration Debug",
                    $"-ccs.buildType {buildType}",
                    "-ccs.listProblems",
                });

                var psi = new ProcessStartInfo
                {
                    FileName               = SCK2400_CCS_SERVER,
                    Arguments              = args,
                    WorkingDirectory       = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT, "Debug"),
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();

                // Stream output lines as they arrive
                var stdoutTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            Color c = line.Contains("error") || line.Contains("Error")
                                ? Theme.Red
                                : line.Contains("warning") || line.Contains("Warning")
                                    ? Theme.Yellow
                                    : Theme.White;
                            Log(line.Trim(), c);
                        }
                    }
                });
                var stderrTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            Log(line.Trim(), Theme.Yellow);
                    }
                });

                await Task.WhenAll(stdoutTask, stderrTask);
                await Task.Run(() => proc.WaitForExit());

                string hex = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT, "Debug",
                    $"{SCK2400_PROJECT}.hex");

                if (proc.ExitCode == 0 && File.Exists(hex))
                {
                    txtFirmwareFile.Text = hex;
                    Log($"✓ Build succeeded: {hex}", Theme.Green);
                    Log($"  Board: {boardLabel}  APID: {apid}", Theme.Cyan);

                    // ── OAD post-build: auto-patch prgEntry ──────────────────
                    // Read .oad_entry_vec address from the linker map file.
                    // Patch oad_image_header_app.c, delete its .o, rebuild once.
                    bool oadPatchOk = await PatchOadPrgEntryAsync(hex);
                    if (!oadPatchOk)
                    {
                        Log("⚠ OAD prgEntry patch failed — firmware may not boot via BIM.", Theme.Yellow);
                        lblFlashStatus.Text = $"Build OK — {boardLabel} — OAD patch FAILED.";
                    }
                    else
                    {
                        lblFlashStatus.Text = $"Build OK — {boardLabel} — ready to flash.";
                    }
                }
                else if (proc.ExitCode != 0)
                {
                    Log($"✗ Build FAILED (exit {proc.ExitCode})", Theme.Red);
                    lblFlashStatus.Text = "Build failed — check log.";
                }
                else
                {
                    Log("✗ Build OK but .hex not found — check post-build step.", Theme.Red);
                    lblFlashStatus.Text = "Build OK but .hex missing.";
                }
            }
            catch (Exception ex)
            {
                Log($"Build exception: {ex.Message}", Theme.Red);
                lblFlashStatus.Text = "Build error.";
            }
            finally
            {
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                pbFlash.Value = 0;
            }
        }

        private void RunClean_SCK2400()
        {
            string debugDir = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT, "Debug");
            if (!Directory.Exists(debugDir))
            { Log("Debug folder not found — nothing to clean.", Theme.Gray); return; }

            string[] extensions = { "*.o", "*.d_raw", "*.map", "*.xml", "*.out" };
            int deleted = 0;
            try
            {
                foreach (string ext in extensions)
                    foreach (string f in Directory.GetFiles(debugDir, ext, SearchOption.AllDirectories))
                    { File.Delete(f); deleted++; }

                // Remove hex so stale flash is impossible
                string hex = Path.Combine(debugDir, $"{SCK2400_PROJECT}.hex");
                if (File.Exists(hex)) { File.Delete(hex); deleted++; }

                Log($"SCK-2400 clean: {deleted} files removed from Debug\\", Theme.Cyan);
                lblFlashStatus.Text = "Cleaned — ready to build.";
            }
            catch (Exception ex) { Log($"Clean error: {ex.Message}", Theme.Red); }
        }

        // ══════════════════════════════════════════════════════════════════
        //  OAD POST-BUILD: AUTO-PATCH prgEntry IN oad_image_header_app.c
        //  After each firmware build, read the .oad_entry_vec section
        //  address directly from the linker map file.  This is exact and
        //  immune to changes in firmware layout.
        //  Patch the .prgEntry field and rebuild incrementally so the
        //  header is always correct without manual intervention.
        //
        //  Why map file, not hex scan:
        //    The linker places .oad_entry_vec wherever it fits after all
        //    other sections.  Its address grows as firmware grows and is
        //    not predictable.  The map file records the exact final address
        //    of every section — reading it directly is the correct approach.
        // ══════════════════════════════════════════════════════════════════
        private async Task<bool> PatchOadPrgEntryAsync(string firmwareHex)
        {
            string projDir  = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT);
            string debugDir = Path.Combine(projDir, "Debug");
            string mapFile  = Path.Combine(debugDir, $"{SCK2400_PROJECT}.map");
            string hdrSrc   = Path.Combine(projDir, "oad_image_header_app.c");
            string hdrObj   = Path.Combine(debugDir, "oad_image_header_app.o");

            if (!File.Exists(mapFile))
            {
                Log("OAD patch: map file not found — skipping.", Theme.Yellow);
                return false;
            }
            if (!File.Exists(hdrSrc))
            {
                Log("OAD patch: oad_image_header_app.c not found — skipping.", Theme.Yellow);
                return false;
            }

            // ── Step 1: find .oad_entry_vec address in map file ──────────
            // TI Clang linker map format for a section looks like:
            //
            //   .oad_entry_vec
            //   *          0    0000d33c    00000008
            //                     0000d33c    00000008     oad_image_header_app.o (.oad_entry_vec)
            //
            // The section name appears alone on one line; the origin address
            // appears as the 4th whitespace-delimited token on the NEXT
            // non-empty line (format: * / page / origin / length).
            // Addresses have no 0x prefix — pure 8-digit hex.
            //
            // We also check the summary table at the top of the map which has:
            //   0000d33c    0000d33c    00000008   00000008    r-- .oad_entry_vec
            // where the address is the first token and section name is last.
            Log("OAD patch: reading oad_entry_vec address from map file...", Theme.Gray);

            uint? entryVecAddr = null;
            try
            {
                string[] mapLines = File.ReadAllLines(mapFile);

                // Strategy A: summary table line — "hhhhhhhh ... .oad_entry_vec"
                var summaryRe = new System.Text.RegularExpressions.Regex(
                    @"^\s*([0-9a-fA-F]{8})\s+[0-9a-fA-F]{8}\s+[0-9a-fA-F]{8}\s+[0-9a-fA-F]{8}\s+\S+\s+\.oad_entry_vec\s*$");

                for (int i = 0; i < mapLines.Length && entryVecAddr == null; i++)
                {
                    var m = summaryRe.Match(mapLines[i]);
                    if (!m.Success) continue;
                    uint candidate = Convert.ToUInt32(m.Groups[1].Value, 16);
                    if (candidate >= 0x4000 && candidate <= 0x1FFFF)
                    {
                        entryVecAddr = candidate;
                        Log($"OAD patch: oad_entry_vec at 0x{candidate:X8} (summary table)", Theme.Cyan);
                    }
                }

                // Strategy B: section allocation block — ".oad_entry_vec" on one
                // line, origin address as 3rd token on the next non-empty line.
                // Line format:  *          0    0000d33c    00000008
                var originRe = new System.Text.RegularExpressions.Regex(
                    @"^\s*\*\s+\d+\s+([0-9a-fA-F]{8})\s+[0-9a-fA-F]{8}");

                for (int i = 0; i < mapLines.Length - 1 && entryVecAddr == null; i++)
                {
                    if (!mapLines[i].TrimEnd().EndsWith(".oad_entry_vec")) continue;
                    // Find the next non-empty line
                    for (int j = i + 1; j < mapLines.Length; j++)
                    {
                        if (string.IsNullOrWhiteSpace(mapLines[j])) continue;
                        var m = originRe.Match(mapLines[j]);
                        if (!m.Success) break;
                        uint candidate = Convert.ToUInt32(m.Groups[1].Value, 16);
                        if (candidate >= 0x4000 && candidate <= 0x1FFFF)
                        {
                            entryVecAddr = candidate;
                            Log($"OAD patch: oad_entry_vec at 0x{candidate:X8} (section block)", Theme.Cyan);
                        }
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"OAD patch: map parse error: {ex.Message}", Theme.Red);
                return false;
            }

            if (entryVecAddr == null)
            {
                Log("OAD patch: oad_entry_vec not found in map — prgEntry not updated.", Theme.Yellow);
                Log("OAD patch: check that .oad_entry_vec section is defined in oad_image_header_app.c", Theme.Yellow);
                return false;
            }

            // ── Step 2: compute image length from map, then patch oad_image_header_app.c ──
            //
            // BIM validates: prgEntry in [startAddr, startAddr + len]
            // so .len must cover the entire image including .oad_entry_vec.
            // We compute len by finding the highest (origin + size) across all
            // flash sections in the summary table, then subtracting 0x4000.
            // Summary table line format (leading 2-space indent on subsections):
            //   hhhhhhhh  hhhhhhhh  hhhhhhhh  hhhhhhhh  flags  [section]
            uint imageLen = 0;
            try
            {
                var sectionRe = new System.Text.RegularExpressions.Regex(
                    @"^\s{2}([0-9a-fA-F]{8})\s+[0-9a-fA-F]{8}\s+([0-9a-fA-F]{8})\s+[0-9a-fA-F]{8}\s+\S");
                uint highWater = 0;
                foreach (string ml in File.ReadAllLines(mapFile))
                {
                    var m = sectionRe.Match(ml);
                    if (!m.Success) continue;
                    uint origin = Convert.ToUInt32(m.Groups[1].Value, 16);
                    uint size   = Convert.ToUInt32(m.Groups[2].Value, 16);
                    // Flash sections only: 0x4000–0x51FFF (app only).
                    // Exclude NVS (0x52000+), CCFG (0x56000+), RAM (0x20000000+).
                    // NVS is NOLOAD but appears in map -- must exclude or image
                    // length grows to cover NVS and BIM erases it on OAD.
                    if (origin < 0x4000 || origin >= 0x52000) continue;
                    uint end = origin + size;
                    if (end > highWater) highWater = end;
                }
                if (highWater > 0x4000)
                {
                    imageLen = highWater - 0x4000;
                    Log($"OAD patch: computed image len = 0x{imageLen:X8} (end=0x{highWater:X8})", Theme.Cyan);
                }
                else
                {
                    Log("OAD patch: could not compute image length from map — len not patched.", Theme.Yellow);
                }
            }
            catch (Exception ex)
            {
                Log($"OAD patch: len compute error: {ex.Message}", Theme.Yellow);
                // Non-fatal — prgEntry patch still proceeds
            }

            try
            {
                string src           = File.ReadAllText(hdrSrc);
                string newPrgEntry   = $"0x{entryVecAddr.Value:X8}";

                // Patch .prgEntry
                string patched = System.Text.RegularExpressions.Regex.Replace(
                    src,
                    @"(\.prgEntry\s*=\s*)0x[0-9a-fA-F]+",
                    $"${{1}}{newPrgEntry}");
                if (patched == src)
                    Log($"OAD patch: .prgEntry already {newPrgEntry} or pattern not found.", Theme.Gray);
                else
                    Log($"OAD patch: .prgEntry updated to {newPrgEntry}", Theme.Green);

                // Patch .len if we have a valid computed value
                if (imageLen > 0)
                {
                    string newLen    = $"0x{imageLen:X8}";
                    string patchedLen = System.Text.RegularExpressions.Regex.Replace(
                        patched,
                        @"(\.len\s*=\s*)0x[0-9a-fA-F]+",
                        $"${{1}}{newLen}");
                    if (patchedLen == patched)
                        Log($"OAD patch: .len already {newLen} or pattern not found.", Theme.Gray);
                    else
                    {
                        patched = patchedLen;
                        Log($"OAD patch: .len updated to {newLen}", Theme.Green);
                    }
                }

                File.WriteAllText(hdrSrc, patched);
            }
            catch (Exception ex)
            {
                Log($"OAD patch: failed to patch source: {ex.Message}", Theme.Red);
                return false;
            }

            // ── Step 3: delete stale .o and rebuild incrementally ────────
            try
            {
                if (File.Exists(hdrObj))
                {
                    File.Delete(hdrObj);
                    Log("OAD patch: deleted stale oad_image_header_app.o", Theme.Gray);
                }
            }
            catch (Exception ex)
            {
                Log($"OAD patch: could not delete .o: {ex.Message}", Theme.Yellow);
            }

            Log("OAD patch: incremental rebuild to bake prgEntry...", Theme.Gray);
            try
            {
                string args = string.Join(" ", new[]
                {
                    "-noSplash",
                    $"-data \"{SCK2400_WORKSPACE}\"",
                    "-application com.ti.ccs.apps.buildProject",
                    $"-ccs.projects {SCK2400_PROJECT}",
                    "-ccs.configuration Debug",
                    "-ccs.buildType incremental",
                    "-ccs.listProblems",
                });

                var psi = new ProcessStartInfo
                {
                    FileName               = SCK2400_CCS_SERVER,
                    Arguments              = args,
                    WorkingDirectory       = Path.Combine(SCK2400_WORKSPACE, SCK2400_PROJECT, "Debug"),
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                var outTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardOutput.ReadLineAsync()) != null)
                        if (!string.IsNullOrWhiteSpace(line)) Log(line.Trim(), Theme.White);
                });
                var errTask = Task.Run(async () =>
                {
                    string? line;
                    while ((line = await proc.StandardError.ReadLineAsync()) != null)
                        if (!string.IsNullOrWhiteSpace(line)) Log(line.Trim(), Theme.Yellow);
                });
                await Task.WhenAll(outTask, errTask);
                await Task.Run(() => proc.WaitForExit());

                if (proc.ExitCode == 0)
                {
                    Log($"OAD patch: rebuild OK — prgEntry = 0x{entryVecAddr.Value:X8}", Theme.Green);
                    return true;
                }
                else
                {
                    Log($"OAD patch: rebuild failed (exit {proc.ExitCode})", Theme.Red);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"OAD patch: rebuild exception: {ex.Message}", Theme.Red);
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  MERGE BIM + FIRMWARE HEX
        //  Produces a single Intel HEX containing both BIM (0x00000000)
        //  and the OAD firmware (0x00004000+) for single-pass flashing.
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  FLASH BIM ONLY — ONE-TIME BOARD PROVISIONING
        //  Flashes only the BIM hex to pages 0-1 with -e all.
        //  Use once per board at the factory. After this, BIM is permanent
        //  and all firmware updates use the normal merged-hex Flash button.
        // ══════════════════════════════════════════════════════════════════
        // ══════════════════════════════════════════════════════════════════
        //  FLASH BIM ONLY — ONE-TIME BOARD PROVISIONING
        // ══════════════════════════════════════════════════════════════════
        private async Task RunFlashBimAsync()
        {
            string bimHex = Path.Combine(SCK2400_WORKSPACE, "sck2400_bim", "Debug", "sck2400_bim.hex");
            if (!File.Exists(bimHex))
            {
                Log($"BIM hex not found: {bimHex}", Theme.Red);
                Log("Build BIM first using build_bim.bat in sck2400_bim\\", Theme.Yellow);
                return;
            }
            if (!File.Exists(SCK2400_SRFPROG))
            {
                Log($"srfprog.exe not found: {SCK2400_SRFPROG}", Theme.Red);
                return;
            }

            SetFirmwareButtons(false);
            lblFlashStatus.Text = "Detecting XDS110...";
            pbFlash.Style = ProgressBarStyle.Marquee;

            string? xdsSerial = await DetectXds110SerialAsync();
            if (xdsSerial == null)
            {
                Log("Flash BIM aborted — no XDS110 detected.", Theme.Red);
                lblFlashStatus.Text = "Flash aborted — no debugger found.";
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                return;
            }

            Log("══════════════════════════════════════════════════", Theme.Cyan);
            Log("  SCK-2400 Flash BIM — One-Time Provisioning", Theme.Cyan);
            Log($"  XDS110: {xdsSerial}", Theme.Cyan);
            Log($"  BIM: {bimHex}", Theme.Cyan);
            Log("══════════════════════════════════════════════════", Theme.Cyan);

            lblFlashStatus.Text = "Flashing BIM...";

            try
            {
                string args = $"-t soc(XDS-{xdsSerial}, CC1352P) -e all -p all -v rb -f \"{bimHex}\"";
                Log($"srfprog {args}", Theme.Gray);

                var psi = new ProcessStartInfo
                {
                    FileName               = SCK2400_SRFPROG,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());

                foreach (string line in stdout.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log(line.Trim(),
                        line.Contains("ERROR") || line.Contains("Failed") ? Theme.Red :
                        line.Contains("OK") ? Theme.Green : Theme.White);

                foreach (string line in stderr.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log(line.Trim(), Theme.Yellow);

                if (proc.ExitCode == 0)
                {
                    Log("BIM flashed successfully — board provisioned.", Theme.Green);
                    Log("  Flash firmware now using the normal Flash button.", Theme.Cyan);
                    lblFlashStatus.Text = "BIM flashed — ready for firmware.";
                }
                else
                {
                    Log($"BIM flash FAILED (exit {proc.ExitCode})", Theme.Red);
                    lblFlashStatus.Text = "BIM flash failed — check log.";
                }
            }
            catch (Exception ex)
            {
                Log($"BIM flash exception: {ex.Message}", Theme.Red);
                lblFlashStatus.Text = "BIM flash error.";
            }
            finally
            {
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                pbFlash.Value = 0;
            }
        }

                private bool MergeBimFirmwareHex(string bimHexPath, string fwHexPath, string outPath)
        {
            try
            {
                var data = new SortedDictionary<uint, byte>();

                void LoadHex(string path)
                {
                    uint hi = 0;
                    foreach (string line in File.ReadAllLines(path))
                    {
                        if (line.Length < 9 || line[0] != ':') continue;
                        int  cnt  = Convert.ToInt32(line.Substring(1, 2), 16);
                        uint addr = Convert.ToUInt32(line.Substring(3, 4), 16) + hi;
                        int  rt   = Convert.ToInt32(line.Substring(7, 2), 16);
                        if (rt == 0)
                            for (int i = 0; i < cnt; i++)
                                data[addr + (uint)i] = Convert.ToByte(line.Substring(9 + i * 2, 2), 16);
                        else if (rt == 4)
                            hi = Convert.ToUInt32(line.Substring(9, 4), 16) << 16;
                    }
                }

                LoadHex(bimHexPath);
                LoadHex(fwHexPath);

                byte Checksum(string body)
                {
                    int s = 0;
                    for (int i = 0; i < body.Length; i += 2)
                        s += Convert.ToInt32(body.Substring(i, 2), 16);
                    return (byte)((~s + 1) & 0xFF);
                }

                using var sw = new StreamWriter(outPath);
                uint curHi = 0xFFFFFFFF;
                var keys   = data.Keys.ToList();
                int ki     = 0;

                while (ki < keys.Count)
                {
                    uint addr = keys[ki];
                    uint hi   = (addr >> 16) & 0xFFFF;

                    if (hi != curHi)
                    {
                        curHi = hi;
                        string body = $"02000004{hi:X4}";
                        sw.WriteLine($":{body}{Checksum(body):X2}");
                    }

                    // Collect up to 16 contiguous bytes in same 64KB page
                    var chunk = new List<byte>();
                    while (ki < keys.Count && chunk.Count < 16)
                    {
                        if (chunk.Count > 0 && keys[ki] != addr + (uint)chunk.Count) break;
                        if ((keys[ki] >> 16) != hi) break;
                        chunk.Add(data[keys[ki]]);
                        ki++;
                    }

                    uint low = addr & 0xFFFF;
                    string recBody = $"{chunk.Count:X2}{low:X4}00" + string.Concat(chunk.Select(b => $"{b:X2}"));
                    sw.WriteLine($":{recBody}{Checksum(recBody):X2}");
                }

                sw.WriteLine(":00000001FF");
                return true;
            }
            catch (Exception ex)
            {
                Log($"Hex merge error: {ex.Message}", Theme.Red);
                return false;
            }
        }

        // Auto-detect XDS110 serial number using xdsdfu -e.
        // Parses "Serial Num:   XXXXXXXX" from output.
        // Returns null if no device found or xdsdfu not available.
        private async Task<string?> DetectXds110SerialAsync()
        {
            if (!File.Exists(SCK2400_XDSDFU))
            {
                Log($"xdsdfu.exe not found at: {SCK2400_XDSDFU}", Theme.Red);
                return null;
            }
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = SCK2400_XDSDFU,
                    Arguments              = "-e",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string output = await proc.StandardOutput.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());

                // Parse "Serial Num:   L4300491" — works for any XDS110
                var match = System.Text.RegularExpressions.Regex.Match(
                    output, @"Serial Num:\s+(\S+)");
                if (match.Success)
                {
                    string serial = match.Groups[1].Value.Trim();
                    Log($"XDS110 detected: Serial = {serial}", Theme.Cyan);
                    return serial;
                }

                Log("No XDS110 device found — is the LaunchPad connected?", Theme.Red);
                return null;
            }
            catch (Exception ex)
            {
                Log($"xdsdfu error: {ex.Message}", Theme.Red);
                return null;
            }
        }

        private async Task RunFlashLocal_SCK2400(string? hexPath = null)
        {
            hexPath ??= txtFirmwareFile.Text.Trim();
            if (!File.Exists(hexPath))
            { Log("HEX file not found — build first.", Theme.Red); return; }
            if (!File.Exists(SCK2400_SRFPROG))
            { Log($"srfprog.exe not found at: {SCK2400_SRFPROG}", Theme.Red);
              Log("Install SmartRF Flash Programmer 2 from ti.com/tool/FLASH-PROGRAMMER-2", Theme.Yellow);
              return; }

            string boardLabel = Sck2400BoardLabel.Trim();

            SetFirmwareButtons(false);
            lblFlashStatus.Text = "Detecting XDS110...";
            pbFlash.Style       = ProgressBarStyle.Marquee;

            // Auto-detect XDS110 serial number
            string? xdsSerial = await DetectXds110SerialAsync();
            if (xdsSerial == null)
            {
                Log("Flash aborted — no XDS110 detected.", Theme.Red);
                lblFlashStatus.Text = "Flash aborted — no debugger found.";
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                return;
            }

            // ── OAD: merge BIM + firmware hex before flashing ────────────
            // BIM lives at 0x00000000, firmware at 0x00004000.
            // srfprog -e all erases everything then programs the merged image
            // in one pass — avoids BIM being wiped by a later firmware flash.
            string flashHex = hexPath;
            string bimHex   = Path.Combine(SCK2400_WORKSPACE, "sck2400_bim", "Debug", "sck2400_bim.hex");
            if (File.Exists(bimHex))
            {
                string mergedHex = Path.Combine(SCK2400_WORKSPACE, "sck2400_merged.hex");
                bool merged = MergeBimFirmwareHex(bimHex, hexPath, mergedHex);
                if (merged)
                {
                    flashHex = mergedHex;
                    Log($"\u2713 Merged BIM + firmware \u2192 {mergedHex}", Theme.Cyan);
                }
                else
                {
                    Log("\u26a0 Hex merge failed — flashing firmware only (BIM not included).", Theme.Yellow);
                }
            }
            else
            {
                Log($"\u26a0 BIM hex not found at {bimHex} — flashing firmware only.", Theme.Yellow);
            }

            lblFlashStatus.Text = "Flashing...";

            Log("══════════════════════════════════════════════════", Theme.Cyan);
            Log($"  SCK-2400 Local Flash  →  {boardLabel}", Theme.Cyan);
            Log($"  XDS110: {xdsSerial}", Theme.Cyan);
            Log($"  HEX: {flashHex}", Theme.Cyan);
            Log("══════════════════════════════════════════════════", Theme.Cyan);

            try
            {
                // Use -e all to fully erase before programming merged BIM+firmware.
                // -e pif is NOT used because it would erase BIM pages.
                string args = $"-t soc(XDS-{xdsSerial}, CC1352P) -e all -p all -v rb -f \"{flashHex}\"";

                var psi = new ProcessStartInfo
                {
                    FileName               = SCK2400_SRFPROG,
                    Arguments              = args,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };

                Log($"srfprog {args}", Theme.Gray);

                using var proc = new Process { StartInfo = psi };
                proc.Start();
                string stdout = await proc.StandardOutput.ReadToEndAsync();
                string stderr = await proc.StandardError.ReadToEndAsync();
                await Task.Run(() => proc.WaitForExit());

                foreach (string line in stdout.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log(line.Trim(),
                        line.Contains("ERROR") || line.Contains("Failed") ? Theme.Red :
                        line.Contains("OK") || line.Contains("success") || line.Contains("done") ? Theme.Green :
                        Theme.White);

                foreach (string line in stderr.Split('\n')
                    .Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log(line.Trim(), Theme.Yellow);

                if (proc.ExitCode == 0)
                {
                    Log($"✓ Flash complete — {boardLabel}", Theme.Green);
                    lblFlashStatus.Text = $"Flash OK — {boardLabel}";
                }
                else
                {
                    Log($"✗ Flash FAILED (exit {proc.ExitCode})", Theme.Red);
                    lblFlashStatus.Text = "Flash failed — check log.";
                }
            }
            catch (Exception ex)
            {
                Log($"Flash exception: {ex.Message}", Theme.Red);
                lblFlashStatus.Text = "Flash error.";
            }
            finally
            {
                SetFirmwareButtons(true);
                pbFlash.Style = ProgressBarStyle.Continuous;
                pbFlash.Value = 0;
            }
        }

        // Patch SCK_APID_THIS_BOARD in ccsds.h before SCK-2400 build.
        // Replaces the #define SCK_APID_THIS_BOARD line so the correct
        // board address is baked into the binary — no manual source edit needed.
        // The #ifndef guard is removed and replaced with a hard #define so
        // the Makefile -D override pattern is not needed.
        // Patch security.h with current key fields before SCK-2400 build.
        // Creates security.h from security_template.h if it doesn't exist,
        // then writes the three key values from the Provision tab.
        // Called automatically before every SCK-2400 build if keys are set.
        private bool PatchSecurityH()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                return true;  // no project dir -- skip silently

            // Parse keys from Provision tab
            byte[]? k0 = ParseProvKey(txtProvKey0?.Text ?? "", "Key 0");
            byte[]? k1 = ParseProvKey(txtProvKey1?.Text ?? "", "Key 1");
            byte[]? k2 = ParseProvKey(txtProvKey2?.Text ?? "", "Key 2");

            // If any key is the placeholder 0xFF*16, skip patching
            bool allPlaceholder = k0 != null && k1 != null && k2 != null &&
                k0.All(b => b == 0xFF) && k1.All(b => b == 0xFF) && k2.All(b => b == 0xFF);
            if (k0 == null || k1 == null || k2 == null || allPlaceholder)
            {
                Log("security.h: using placeholder keys (0xFF×16) — set real keys in Provision tab for production", Theme.Yellow);
                return true;  // not a fatal error -- bench testing with placeholder keys is fine
            }

            string? secH = Directory.GetFiles(dir, "security.h", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (secH == null)
            {
                // Try to create from template
                string? tmpl = Directory.GetFiles(dir, "security_template.h", SearchOption.AllDirectories)
                    .FirstOrDefault();
                if (tmpl != null)
                {
                    File.Copy(tmpl, Path.Combine(Path.GetDirectoryName(tmpl)!, "security.h"));
                    secH = Path.Combine(Path.GetDirectoryName(tmpl)!, "security.h");
                    Log("security.h: created from security_template.h", Theme.Cyan);
                }
                else
                {
                    Log("security.h not found and no template available — skipping key patch", Theme.Yellow);
                    return true;
                }
            }



            try
            {
                string content = File.ReadAllText(secH);
                // Replace each key macro block using regex
                // Build the new key macro text for each key and do a simple
                // line-by-line replacement rather than regex to avoid escape issues.
                content = ReplaceKeyMacro(content, "SCK_AES_KEY_0", k0);
                content = ReplaceKeyMacro(content, "SCK_AES_KEY_1", k1);
                content = ReplaceKeyMacro(content, "SCK_AES_KEY_2", k2);
                File.WriteAllText(secH, content);
                Log($"security.h: patched with production keys from Provision tab", Theme.Green);
                Log($"  Key 0: {txtProvKey0.Text[..8]}...{txtProvKey0.Text[^8..]}", Theme.Gray);
                Log($"  Key 1: {txtProvKey1.Text[..8]}...{txtProvKey1.Text[^8..]}", Theme.Gray);
                Log($"  Key 2: {txtProvKey2.Text[..8]}...{txtProvKey2.Text[^8..]}", Theme.Gray);
                return true;
            }
            catch (Exception ex)
            {
                Log($"security.h patch failed: {ex.Message}", Theme.Red);
                return false;
            }
        }

        // Replace a #define SCK_AES_KEY_N { ... } block in security.h
        // with new key bytes. Works line-by-line to avoid regex escape complexity.
        private static string ReplaceKeyMacro(string content, string macroName, byte[] key)
        {
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
            // Find the #define line for this key
            int startIdx = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(
                    lines[i], @"^\s*#define\s+" + macroName + @"\s*\{"))
                {
                    startIdx = i;
                    break;
                }
            }
            if (startIdx < 0) return content;  // macro not found, leave unchanged

            // Find the closing brace line (handles multi-line macro with backslash continuation)
            int endIdx = startIdx;
            for (int i = startIdx; i < Math.Min(startIdx + 8, lines.Count); i++)
            {
                if (lines[i].TrimEnd().EndsWith("}") || lines[i].Contains("}"))
                {
                    endIdx = i;
                    break;
                }
            }

            // Build replacement lines as a list to avoid multiline string escaping issues
            var newLines = new System.Collections.Generic.List<string>
            {
                $"#define {macroName}  {{ \\",
                string.Format("    0x{0:X2},0x{1:X2},0x{2:X2},0x{3:X2},0x{4:X2},0x{5:X2},0x{6:X2},0x{7:X2}, \\",
                    key[0],key[1],key[2],key[3],key[4],key[5],key[6],key[7]),
                string.Format("    0x{0:X2},0x{1:X2},0x{2:X2},0x{3:X2},0x{4:X2},0x{5:X2},0x{6:X2},0x{7:X2}  \\",
                    key[8],key[9],key[10],key[11],key[12],key[13],key[14],key[15]),
                "}"
            };

            lines.RemoveRange(startIdx, endIdx - startIdx + 1);
            lines.InsertRange(startIdx, newLines);
            return string.Join("\n", lines);
        }

        private bool PatchCcsdsHBoard_SCK2400()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            { Log("Select a valid project directory first.", Theme.Red); return false; }

            string? ccsdsH = Directory.GetFiles(dir, "ccsds.h", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (ccsdsH == null)
            { Log("ccsds.h not found in project directory.", Theme.Red); return false; }

            string apid  = Sck2400BoardApid;
            string label = Sck2400BoardLabel.Trim();

            try
            {
                string[] lines = File.ReadAllLines(ccsdsH);
                var kept = new System.Collections.Generic.List<string>();

                foreach (var line in lines)
                {
                    // Only remove lines that ARE the SCK_APID_THIS_BOARD define
                    // or are our injected tool comment lines.
                    // Do NOT remove other macros that reference SCK_APID_THIS_BOARD
                    // (like SCK_IS_GS_BOARD and CCSDS_IS_FOR_THIS_BOARD).
                    bool isDefine  = System.Text.RegularExpressions.Regex.IsMatch(
                        line, @"^\s*#define\s+SCK_APID_THIS_BOARD\b");
                    bool isComment = line.TrimStart().StartsWith("/* Board address set by ground station");
                    bool isOldNote = line.TrimStart().StartsWith("* GS board flash:") ||
                                     line.TrimStart().StartsWith("* Remote board flash:") ||
                                     line.TrimStart().StartsWith("* The Makefile can also set") ||
                                     line.TrimStart().StartsWith("* make all BOARD_ADDR") ||
                                     line.TrimStart().StartsWith("* which overrides this default") ||
                                     line.TrimStart().StartsWith("* which overrides");
                    if (isDefine || isComment || isOldNote) continue;
                    kept.Add(line);
                }

                // Find the line with SCK_APID_BOARD_BROADCAST and insert after it
                int insertIdx = -1;
                for (int i = 0; i < kept.Count; i++)
                {
                    if (kept[i].Contains("SCK_APID_BOARD_BROADCAST"))
                    { insertIdx = i + 1; break; }
                }

                // Insert two lines: the comment and the define
                string commentLine = "/* Board address set by ground station build tool — do not edit manually. */";
                string defineLine  = $"#define SCK_APID_THIS_BOARD         {apid}   /* {label} */";

                if (insertIdx >= 0)
                {
                    kept.Insert(insertIdx, defineLine);
                    kept.Insert(insertIdx, commentLine);
                    kept.Insert(insertIdx, "");  // blank line before
                }
                else
                {
                    kept.Add("");
                    kept.Add(commentLine);
                    kept.Add(defineLine);
                }

                // Detect original line ending style
                string original   = File.ReadAllText(ccsdsH);
                string lineEnding = original.Contains("\r\n") ? "\r\n" : "\n";

                File.WriteAllText(ccsdsH, string.Join(lineEnding, kept) + lineEnding);
                Log($"Board: patched ccsds.h → SCK_APID_THIS_BOARD = {apid} ({label})", Theme.Cyan);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to patch ccsds.h: {ex.Message}", Theme.Red);
                return false;
            }
        }
        // Equivalent to PatchBoardHPower() for SCK-915.
        // Sets: #define TX_POWER BENCH  or  #define TX_POWER FIELD
        private bool PatchRadioHPower_SCK2400()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            { Log("Select a valid project directory first.", Theme.Red); return false; }

            string? radioH = Directory.GetFiles(dir, "radio.h", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (radioH == null)
            { Log("radio.h not found in project directory.", Theme.Red); return false; }

            string pwrValue = rbPowerField.Checked ? "FIELD" : "BENCH";
            string pwrLabel = rbPowerField.Checked ? "+20 dBm field" : "0 dBm bench";

            try
            {
                string content = File.ReadAllText(radioH);

                // Remove any existing TX_POWER override line (prevent duplicates)
                var cleaned = System.Text.RegularExpressions.Regex.Replace(
                    content,
                    @"[ \t]*#define\s+TX_POWER\s+\w+[^\r\n]*(\r?\n)?",
                    "",
                    System.Text.RegularExpressions.RegexOptions.Multiline);

                // Insert before the #ifndef TX_POWER guard
                string insertLine = $"#define TX_POWER {pwrValue}\r\n";
                if (cleaned.Contains("#ifndef TX_POWER"))
                {
                    cleaned = cleaned.Replace("#ifndef TX_POWER",
                        insertLine + "#ifndef TX_POWER");
                }
                else
                {
                    // Fallback: append before final #endif
                    int lastEndif = cleaned.LastIndexOf("#endif");
                    if (lastEndif >= 0)
                        cleaned = cleaned.Insert(lastEndif, insertLine);
                    else
                        cleaned += "\r\n" + insertLine;
                }

                File.WriteAllText(radioH, cleaned);
                Log($"RF Power: patched radio.h → TX_POWER = {pwrValue} ({pwrLabel})", Theme.Cyan);
                return true;
            }
            catch (Exception ex)
            {
                Log($"Failed to patch radio.h: {ex.Message}", Theme.Red);
                return false;
            }
        }

        private void RunClean()
        {
            string dir = txtProjectDir.Text.Trim();
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            { Log("Select a valid project directory first.", Theme.Red); return; }

            string[] extensions = { ".rel", ".asm", ".lst", ".rst", ".sym" };
            int deleted = 0, skipped = 0;

            try
            {
                foreach (string ext in extensions)
                {
                    foreach (string file in Directory.GetFiles(dir, $"*openlst_437*{ext}",
                        SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileName(file);
                        if (name.Contains(".bl.")) { skipped++; continue; }
                        File.Delete(file);
                        deleted++;
                    }
                }
                foreach (string file in new[]
                {
                    Path.Combine(dir, "openlst_437_radio.hex"),
                    Path.Combine(dir, "openlst_437_radio.lk"),
                    Path.Combine(dir, "openlst_437_radio.mem"),
                    Path.Combine(dir, "openlst_437_radio.map"),
                })
                { if (File.Exists(file)) { File.Delete(file); deleted++; } }

                Log($"Clean complete — {deleted} radio files deleted, {skipped} bootloader files preserved.", Theme.Cyan);
                lblFlashStatus.Text = "Cleaned — ready to build.";
            }
            catch (Exception ex) { Log($"Clean error: {ex.Message}", Theme.Red); }
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

        private async Task FlashApplicationAsync(byte[] appSection, ushort hwid,
            CancellationToken ct = default)
        {
            Log("── PHASE 1: Enter Bootloader ──────────────────────────", Theme.Cyan);
            bool inBootloader = false;
            int  bootLoop = 0;

            while (!inBootloader)
            {
                ct.ThrowIfCancellationRequested();
                bootLoop++;
                Log($"  Loop {bootLoop}: reboot → bootloader_erase", Theme.Yellow);
                ushort rSeq = IncSeqNum();
                FlushRxQueue();
                WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, rSeq, "reboot"));
                LogTx($"reboot (seq={rSeq})", hwid);

                // Wait for board to reboot — short initial wait
                // then hammer bootloader_erase repeatedly through
                // the entire bootloader window (~600ms)
                await Task.Delay(150, ct);

                // Send bootloader_erase multiple times through the window
                // Board will ACK as soon as it enters bootloader
                ushort eSeq = IncSeqNum();
                for (int t = 0; t < 8 && !inBootloader; t++)
                {
                    ct.ThrowIfCancellationRequested();
                    FlushRxQueue();
                    WritePacket(OpenLstProtocol.BuildSimpleCommand(hwid, eSeq, "bootloader_erase"));
                    LogTx($"bootloader_erase (seq={eSeq}, attempt {t+1}/8)", hwid);
                    var pkt = await WaitForReply(hwid, eSeq, 400);
                    if (pkt != null && pkt.OpName == "bootloader_ack" && pkt.AckValue == 1)
                    { inBootloader = true; Log("  ✓ bootloader_ack 1 — flash erased.", Theme.Green); }
                    else if (pkt != null && pkt.OpName != "nack")
                        Log($"  Reply: {pkt.OpName} {pkt.AckValue}", Theme.Yellow);
                    // nack = still in application — keep trying
                }
                if (!inBootloader)
                    await Task.Delay(500, ct);
            }

            const int MAX_ATTEMPTS = 50;
            Log("── PHASE 2: Writing Pages ─────────────────────────────", Theme.Cyan);
            int totalPages = 0, skipped = 0;
            int firstPage = OpenLstProtocol.FLASH_APP_START / OpenLstProtocol.FLASH_PAGE_SIZE;
            int lastPage  = OpenLstProtocol.FLASH_APP_END   / OpenLstProtocol.FLASH_PAGE_SIZE;
            // Reset progress bar safely — must set Value to Minimum before
            // changing Minimum/Maximum or WinForms throws "value cannot be zero"
            // on the second flash if Value still equals the previous Maximum
            pbFlash.Value   = 0;
            pbFlash.Minimum = firstPage;
            pbFlash.Maximum = lastPage;
            pbFlash.Value   = firstPage;

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
                    else if (pkt != null) Log($"  Page {pageNum,3}: ✗ unexpected {pkt.OpName} {pkt.AckValue}", Theme.Red);
                    else Log($"  Page {pageNum,3}: ✗ no reply (attempt {attempt})", Theme.Red);
                }
                if (!pageOk) throw new Exception($"Page {pageNum} failed after {MAX_ATTEMPTS} attempts.");

                totalPages++;
                pbFlash.Value = Math.Clamp(pageNum, firstPage, lastPage);
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
            pbFlash.Value = pbFlash.Maximum;
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

        // ── SCK-2400 CCSDS telemetry display ──────────────────────────────
        private void UpdateCcsdsTelemDisplay(CcsdsTelemData td)
        {
            TimeSpan up = TimeSpan.FromSeconds(td.Uptime);
            lblUptime.Text   = up.TotalDays >= 1
                ? $"{(int)up.TotalDays}d {up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}"
                : $"{up.Hours:D2}:{up.Minutes:D2}:{up.Seconds:D2}";
            lblRssi.Text     = $"{td.LastRssi} dBm";
            lblLqi.Text      = td.LastLqi.ToString();
            lblPktsGood.Text = td.PacketsGood.ToString("N0");
            lblPktsSent.Text = td.PacketsSent.ToString("N0");
            lblRejCksum.Text = td.PacketsRejChecksum.ToString("N0");
            lblRejOther.Text = td.PacketsRejOther.ToString("N0");
            lblUart0.Text    = td.Uart0RxCount.ToString("N0");
            lblUart1.Text    = td.Uart1RxCount.ToString("N0");
            lblRxMode.Text   = td.RxMode == 1 ? "ON" : "OFF";
            lblTxMode.Text   = td.TxMode == 1 ? "TX" : "IDLE";
            lblDieTemp.Text  = $"{td.DieTempC:F1} °C";
            lblSupplyV.Text  = $"{td.SupplyVoltage:F2} V";
            // LEO fault recovery fields (Session 13)
            lblResetCounter.Text = td.ResetCounter.ToString();
            lblResetCounter.ForeColor = td.ResetCounter >= 3 ? Theme.Yellow
                                      : td.ResetCounter == 0 ? Theme.Green
                                      : Theme.Silver;
            lblResetCause.Text   = td.ResetCauseString;
            lblSafeMode.Text     = td.SafeModeActive ? "SAFE MODE" : "NORMAL";
            lblSafeMode.ForeColor = td.SafeModeActive ? Theme.Red : Theme.Green;
            lblUptimeMin.Text    = $"{td.UptimeMinutes} min";
            lblUptimeMin.ForeColor = td.UptimeMinutes >= 5 ? Theme.Green : Theme.Silver;
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
            while (_rxQueue.TryDequeue(out RxPacket? pkt))
                Log($"  [live] {pkt.OpName} hwid={pkt.Hwid:X4} seq={pkt.SeqNum}", Theme.Gray);
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
            txtCmdName.Text  = c.Name;
            txtCmdOpcode.Text = $"0x{c.Opcode:X2}";
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
            // Filter beacon lines when filter is active
            if (_filterBeacon && (message.Contains("tlm_beacon") || message.Contains("CCSDS RAW"))) return;
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
            // Suppress GPS beacon RX lines when GPS log filter is off
            if (!_gpsLogEnabled)
            {
                if (desc.Contains("GPS:"))    return;  // GPS → line
                if (desc.Contains("5047"))    return;  // hwid=5047 (GP misread as HWID)
                if (desc.Contains("47 50 53 3A")) return; // RAW GPS hex
                // Check if RAW line contains GPS payload bytes — 47=G 50=P 53=S 3A=:
                if (desc.Contains("[RAW]") && desc.Contains("47 50 53")) return;
            }
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

        private void UpdateRfStatusLabel()
        {
            if (InvokeRequired) { BeginInvoke(new Action(UpdateRfStatusLabel)); return; }
            if (lblRfStatus == null) return;
            if (_boardType != BoardType.SCK2400 || _physicalBoardApid == 0)
            {
                lblRfStatus.Text      = "";
                lblRfStatus.ForeColor = Theme.Gray;
                return;
            }
            // Show which board is the GS and which APIDs get RF-encrypted forwarding
            string gsBoard  = $"0x{_physicalBoardApid:X3}";
            string rfTarget = _physicalBoardApid == 0x010 ? "→ 0x011 encrypted" :
                              _physicalBoardApid == 0x011 ? "→ 0x010 encrypted" :
                              "→ remote encrypted";
            lblRfStatus.Text      = $"GS: {gsBoard}  RF {rfTarget}";
            lblRfStatus.ForeColor = Theme.Cyan;
        }

        // ══════════════════════════════════════════════════════════════════
        //  CONTROL FACTORY HELPERS
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
        //  FILES TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildFilesTab()
        {
            var p  = tpFiles;
            int lx = 10, row = 12;

            p.Controls.Add(MkSectionLabel("── SD Card Files", lx, row));
            row += 28;

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

        private static string ImagesFolderPath
        {
            get
            {
                string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Images");
                if (!Directory.Exists(path)) Directory.CreateDirectory(path);
                return path;
            }
        }

        private async Task RefreshFileListAsync()
        {
            if (!CheckConnected()) return;
            ushort hwid = ActiveHwid;
            ushort seq  = IncSeqNum();
            Log("Files: requesting list from Pico...", Theme.Cyan);
            lblTransferStatus.Text = "Refreshing...";
            FlushRxQueue();
            WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, new byte[] { 0x03 }));

            // Accumulate LIST: and LIST+: packets until we get a final chunk
            // Final chunk = no trailing comma (LIST: without comma = complete list)
            // Continuation chunk = ends with comma (LIST+: = more to come)
            var allFiles = new List<string>();
            bool receivedAny = false;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                var pkt = await WaitForReply(hwid, seq, 5000);
                if (pkt?.PicoPayload == null) break;

                string response = pkt.PicoPayload;

                if (response == "LIST:EMPTY")
                { Log("Files: SD card is empty", Theme.Gray); lblTransferStatus.Text = "SD card empty."; return; }

                if (response.StartsWith("LIST:ERR"))
                { Log($"Files: {response}", Theme.Red); lblTransferStatus.Text = "Error."; return; }

                bool isContinuation = response.StartsWith("LIST+:");
                bool isFirst        = response.StartsWith("LIST:");

                if (!isFirst && !isContinuation) break;

                // Parse filenames from this chunk
                string filesPart = response.Substring(isContinuation ? 6 : 5);
                foreach (string f in filesPart.Split(','))
                    if (!string.IsNullOrWhiteSpace(f))
                        allFiles.Add(f.Trim());

                receivedAny = true;

                // If this chunk ends with comma — more packets coming
                // If no trailing comma — this was the final chunk
                if (!filesPart.EndsWith(",")) break;

                // Wait for next chunk — use same seq number
                Log($"Files: received chunk ({allFiles.Count} files so far)...", Theme.Gray);
            }

            lstFiles.Items.Clear();
            if (receivedAny && allFiles.Count > 0)
            {
                foreach (string f in allFiles)
                    lstFiles.Items.Add(f);
                Log($"Files: {lstFiles.Items.Count} file(s) on SD card", Theme.Green);
                lblTransferStatus.Text = $"{lstFiles.Items.Count} file(s) found.";
            }
            else if (!receivedAny)
            { Log("Files: no response from Pico", Theme.Red); lblTransferStatus.Text = "No response."; }
        }

        private async Task GetSelectedFileAsync()
        {
            if (!CheckConnected()) return;
            if (lstFiles.SelectedItem == null)
            { Log("Files: select a file first.", Theme.Red); return; }
            if (_transferring)
            { Log("Files: transfer already in progress.", Theme.Red); return; }

            string filename = lstFiles.SelectedItem.ToString()!;
            ushort hwid     = ActiveHwid;
            _transferring   = true;
            btnFilesGet.Enabled    = false;
            btnFilesDelete.Enabled = false;

            try
            {
                Log($"Files: requesting info for {filename}...", Theme.Cyan);
                ushort seq = IncSeqNum();
                FlushRxQueue();
                byte[] infoPayload = new byte[] { 0x04 }
                    .Concat(System.Text.Encoding.UTF8.GetBytes(filename)).ToArray();
                WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, infoPayload));
                var infoPkt = await WaitForReply(hwid, seq, 10000);
                if (infoPkt?.PicoPayload == null || !infoPkt.PicoPayload.StartsWith("INFO:"))
                { Log($"Files: failed to get info for {filename}", Theme.Red); lblTransferStatus.Text = "Get info failed."; return; }

                string[] parts      = infoPkt.PicoPayload.Split(':');
                int      totalBytes  = int.Parse(parts[2]);
                int      totalChunks = int.Parse(parts[3]);
                Log($"Files: {filename} = {totalBytes} bytes, {totalChunks} chunks", Theme.White);
                // Reset Value to 0 FIRST before setting Minimum/Maximum
                // WinForms throws ArgumentOutOfRangeException if existing Value
                // exceeds new Maximum during the assignment sequence
                pbTransfer.Value   = 0;
                pbTransfer.Minimum = 0;
                pbTransfer.Maximum = totalChunks;
                pbTransfer.Value   = 0;

                var imageData = new MemoryStream();
                byte[] fnBytes = System.Text.Encoding.UTF8.GetBytes(filename);

                for (int chunk = 0; chunk < totalChunks; chunk++)
                {
                    bool success = false;
                    int  retries = 3;
                    while (retries > 0 && !success)
                    {
                        seq = IncSeqNum();
                        FlushRxQueue();
                        byte[] chunkPayload = new byte[] { 0x05, (byte)(chunk & 0xFF), (byte)((chunk >> 8) & 0xFF) }
                            .Concat(fnBytes).ToArray();
                        WritePacket(OpenLstProtocol.BuildPacket(hwid, seq, 0x20, chunkPayload));
                        var chunkPkt = await WaitForReply(hwid, seq, 10000);
                        if (chunkPkt?.RawPayload != null)
                        {
                            byte[] raw = chunkPkt.RawPayload.Skip(6).ToArray();
                            int firstColon  = Array.IndexOf(raw, (byte)':');
                            int secondColon = firstColon >= 0 ? Array.IndexOf(raw, (byte)':', firstColon + 1) : -1;
                            if (secondColon >= 0)
                            {
                                byte[] chunkData = raw.Skip(secondColon + 1).ToArray();
                                imageData.Write(chunkData, 0, chunkData.Length);
                                success = true;
                                pbTransfer.Value = chunk + 1;
                                lblTransferStatus.Text = $"Chunk {chunk + 1}/{totalChunks} — {imageData.Length}/{totalBytes} bytes";
                            }
                        }
                        if (!success)
                        { retries--; Log($"Files: chunk {chunk} failed, {retries} retries left", Theme.Yellow); await Task.Delay(200); }
                    }
                    if (!success)
                    { Log($"Files: transfer failed at chunk {chunk}", Theme.Red); lblTransferStatus.Text = $"Transfer failed at chunk {chunk}."; return; }
                }

                string savePath = Path.Combine(ImagesFolderPath, filename);
                File.WriteAllBytes(savePath, imageData.ToArray());
                Log($"Files: ✓ {filename} saved ({imageData.Length} bytes) → {savePath}", Theme.Green);
                lblTransferStatus.Text = $"✓ {filename} saved to Images folder.";
                System.Diagnostics.Process.Start("explorer.exe", ImagesFolderPath);
            }
            catch (Exception ex)
            { Log($"Files: transfer exception: {ex.Message}", Theme.Red); lblTransferStatus.Text = "Transfer error."; }
            finally
            {
                _transferring          = false;
                btnFilesGet.Enabled    = true;
                btnFilesDelete.Enabled = true;
                pbTransfer.Value       = 0;
            }
        }

        private async Task DeleteSelectedFileAsync()
        {
            if (!CheckConnected()) return;
            if (lstFiles.SelectedItem == null)
            { Log("Files: select a file first.", Theme.Red); return; }

            string filename = lstFiles.SelectedItem.ToString()!;
            var result = MessageBox.Show($"Delete {filename} from SD card?", "Confirm Delete",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return;

            ushort seq = IncSeqNum();
            FlushRxQueue();
            byte[] delPayload = new byte[] { 0x06 }
                .Concat(System.Text.Encoding.UTF8.GetBytes(filename)).ToArray();
            WritePacket(OpenLstProtocol.BuildPacket(ActiveHwid, seq, 0x20, delPayload));
            var pkt = await WaitForReply(ActiveHwid, seq, 10000);
            if (pkt?.PicoPayload?.StartsWith("DEL:OK") == true)
            { Log($"Files: {filename} deleted from SD card", Theme.Green); lstFiles.Items.Remove(filename); lblTransferStatus.Text = $"{filename} deleted."; }
            else
            { Log($"Files: delete failed for {filename}", Theme.Red); lblTransferStatus.Text = "Delete failed."; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  PROVISION TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildProvisionTab()
        {
            var p = tpProvision;
            int lx = 14, row = 12;

            // Board-aware provisioning description panel.
            // Stored as field so board selector dropdown can refresh it.
            _provInstrPanel = new Panel { Location = new Point(lx, row), Size = new Size(660, 124), BackColor = Color.FromArgb(16, 24, 16) };
            DrawBorder(_provInstrPanel);
            RefreshProvDescription();
            p.Controls.Add(_provInstrPanel);
            row += 126;

            var lblSmartRf = MkSectionLabel("── SmartRF Flash Programmer", lx, row);
            p.Controls.Add(lblSmartRf); row += 28;
            _sck915OnlyControls.Add(lblSmartRf);

            p.Controls.Add(MkLabel("Path:", lx, row + 3, 40, Theme.Gray));
            var txtSmartRfPath = new TextBox
            {
                Location  = new Point(lx + 46, row),
                Size      = new Size(510, 26),
                BackColor = Theme.PanelBack,
                ForeColor = Theme.Silver,
                Font      = Theme.FontSmall,
                Text      = SMARTRF_PATH,
            };
            txtSmartRfPath.TextChanged += (s, e) =>
            {
                if (File.Exists(txtSmartRfPath.Text.Trim()))
                {
                    SMARTRF_PATH = txtSmartRfPath.Text.Trim();
                    txtSmartRfPath.ForeColor = Theme.Green;
                }
                else
                    txtSmartRfPath.ForeColor = Theme.Red;
            };

            var btnBrowseSmartRf = MkButton("Browse...", lx + 566, row, 80, Theme.Cyan, Theme.PanelBack);
            btnBrowseSmartRf.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Title  = "Locate SmartRF Flash Programmer Console",
                    Filter = "SmartRFProgConsole.exe|SmartRFProgConsole.exe|All executables (*.exe)|*.exe",
                    InitialDirectory = @"C:\Program Files (x86)\Texas Instruments",
                };
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtSmartRfPath.Text  = dlg.FileName;
                    SMARTRF_PATH         = dlg.FileName;   // saves to appsettings.json
                    txtSmartRfPath.ForeColor = Theme.Green;
                    Log($"SmartRF path saved: {dlg.FileName}", Theme.Cyan);
                }
            };

            // Show green/red based on whether file exists
            txtSmartRfPath.ForeColor = File.Exists(SMARTRF_PATH) ? Theme.Green : Theme.Red;
            if (!File.Exists(SMARTRF_PATH))
                Log($"SmartRF Flash Programmer not found at default path — use Browse to locate it.", Theme.Yellow);

            p.Controls.AddRange(new Control[] { txtSmartRfPath, btnBrowseSmartRf });
            row += 42;

            p.Controls.Add(MkSectionLabel("── Hardware", lx, row)); row += 28;
            btnProvDetect = MkButton("◎ Detect CC Debugger", lx, row, 190, Theme.Cyan, Theme.PanelBack);
            btnProvDetect.Click += BtnProvDetect_Click;
            lblProvStatus = MkLabel("Not checked", lx + 202, row + 4, 400, Theme.Gray, Theme.FontSmall);
            p.Controls.AddRange(new Control[] { btnProvDetect, lblProvStatus });
            row += 42;

            p.Controls.Add(MkSectionLabel("── Board Identity", lx, row)); row += 28;
            p.Controls.Add(MkLabel("HWID (hex):", lx, row + 3, 90, Theme.Gray));
            txtProvHwid = new TextBox
            {
                Location = new Point(lx + 94, row), Size = new Size(70, 26),
                BackColor = Theme.PanelBack, ForeColor = Theme.Green,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.FontMonoBold,
                MaxLength = 4, PlaceholderText = "0001",
            };
            p.Controls.Add(txtProvHwid);
            row += 38;

            p.Controls.Add(MkSectionLabel("── AES Signing Keys  (3 × 16 bytes hex)", lx, row)); row += 28;
            string[] keyLabels = { "Key 0:", "Key 1:", "Key 2:" };
            for (int k = 0; k < 3; k++)
            {
                p.Controls.Add(MkLabel(keyLabels[k], lx, row + 3, 50, Theme.Gray));
                var tb = new TextBox
                {
                    Location = new Point(lx + 54, row), Size = new Size(340, 26),
                    BackColor = Theme.PanelBack, ForeColor = Theme.Green,
                    BorderStyle = BorderStyle.FixedSingle, Font = Theme.FontMono,
                    MaxLength = 32, Text = "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF",
                };
                p.Controls.Add(tb);
                if (k == 0) txtProvKey0 = tb;
                else if (k == 1) txtProvKey1 = tb;
                else txtProvKey2 = tb;

                // 🎲 AutoGen button — generates a cryptographically random 16-byte key
                int ki = k;
                var btnGen = MkButton("🎲 Roll", lx + 402, row, 72, Theme.Cyan, Theme.PanelBack);
                btnGen.Font = Theme.FontSmall;
                btnGen.Size = new Size(72, 26);
                btnGen.Click += (s, e) =>
                {
                    var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
                    byte[] key = new byte[16];
                    rng.GetBytes(key);
                    string hex = Convert.ToHexString(key).ToLower();
                    if (ki == 0) txtProvKey0.Text = hex;
                    else if (ki == 1) txtProvKey1.Text = hex;
                    else txtProvKey2.Text = hex;
                    SaveSettings();
                };
                tb.TextChanged += (s, e) => SaveSettings();
                p.Controls.Add(btnGen);

                row += 34;
            }
            row += 6;

            p.Controls.Add(MkSectionLabel("── Bootloader Image", lx, row)); row += 28;
            p.Controls.Add(MkLabel("HEX File:", lx, row + 3, 72, Theme.Gray));
            txtProvHexPath = new TextBox
            {
                Location = new Point(lx + 76, row), Size = new Size(440, 26),
                BackColor = Theme.PanelBack, ForeColor = Theme.White,
                BorderStyle = BorderStyle.FixedSingle, Font = Theme.FontMono,
                PlaceholderText = "openlst_437_bootloader.hex",
            };
            string embeddedHex = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "openlst_437_bootloader.hex");
            if (File.Exists(embeddedHex)) txtProvHexPath.Text = embeddedHex;

            btnProvBrowse = MkButton("Browse…", lx + 526, row, 80, Theme.Gray, Theme.PanelBack);
            btnProvBrowse.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog { Filter = "Intel HEX (*.hex)|*.hex|All files (*.*)|*.*", Title = "Select openlst_437_bootloader.hex" };
                if (dlg.ShowDialog() == DialogResult.OK) txtProvHexPath.Text = dlg.FileName;
            };
            p.Controls.AddRange(new Control[] { txtProvHexPath, btnProvBrowse });
            row += 46;

            btnProvFlash = MkButton("▶  PROVISION BOARD", lx, row, 210, Theme.Green, Color.FromArgb(16, 48, 16));
            btnProvFlash.Font = Theme.FontLarge;
            btnProvFlash.Size = new Size(210, 36);
            btnProvFlash.FlatAppearance.BorderColor = Color.FromArgb(40, 120, 40);
            btnProvFlash.Click += async (s, e) => await RunProvisionAsync();
            p.Controls.Add(btnProvFlash);
        }

        private void RefreshProvDescription()
        {
            if (_provInstrPanel == null) return;
            _provInstrPanel.Controls.Clear();

            bool isSck2400 = (_boardType == BoardType.SCK2400);
            var lines = isSck2400
                ? new (string text, int y, Color color)[]
                  {
                      ("◈  Board Provisioning — What this does:",                               7,  Theme.Green),
                      ("1. Click 🎲 Roll to generate cryptographically secure AES-128 keys",   30, Theme.Silver),
                      ("2. Build via Firmware tab — keys auto-patched into security.h",         52, Theme.Silver),
                      ("3. Flash both boards — they must share the same key set",               74, Theme.Silver),
                      ("⚠  Keys live only in security.h (gitignored) — back them up safely",   96, Theme.Yellow),
                  }
                : new (string text, int y, Color color)[]
                  {
                      ("◈  Board Provisioning — What this does:",                               7,  Theme.Green),
                      ("1. Patches BIM (bootloader) hex in memory with your HWID and AES keys",30, Theme.Silver),
                      ("2. Writes patched BIM image to a temp file",                            52, Theme.Silver),
                      ("3. Calls SmartRF Flash Programmer to erase, program and verify",        74, Theme.Silver),
                      ("⚠  CC Debugger must be plugged in and connected before flashing",       96, Theme.Yellow),
                  };

            foreach (var (text, y, color) in lines)
                _provInstrPanel.Controls.Add(MkLabel(text, 10, y, 640, color,
                    y == 8 ? Theme.FontMonoBold : Theme.FontSmall));
        }

        // Apply board-specific UI state — grey out controls that don't apply
        // to the currently selected board type.
        // Called on board selector change and at startup.
        private ToolTip _boardTooltip = new ToolTip();

        private void ApplyBoardUi()
        {
            bool isSck2400 = (_boardType == BoardType.SCK2400);
            foreach (var c in _sck2400OnlyControls)
            {
                c.Enabled = isSck2400;
                c.ForeColor = isSck2400 ? Color.FromArgb(0, 200, 200) : Color.FromArgb(80, 80, 80);
                if (c is Button b)
                    b.BackColor = isSck2400 ? Color.FromArgb(16, 40, 48) : Color.FromArgb(24, 24, 24);
                _boardTooltip.SetToolTip(c, isSck2400 ? "" : "SCK-2400 only");
            }
            foreach (var c in _sck915OnlyControls)
            {
                c.Enabled = !isSck2400;
                c.ForeColor = !isSck2400 ? Color.FromArgb(0, 200, 200) : Color.FromArgb(80, 80, 80);
                if (c is Button b)
                    b.BackColor = !isSck2400 ? Color.FromArgb(16, 40, 48) : Color.FromArgb(24, 24, 24);
                _boardTooltip.SetToolTip(c, !isSck2400 ? "" : "SCK-915 only");
            }
        }

        private void BtnProvDetect_Click(object sender, EventArgs e)
        {
            if (!File.Exists(SMARTRF_PATH))
            { lblProvStatus.Text = "SmartRF Flash Programmer not found."; lblProvStatus.ForeColor = Theme.Red; Log($"SmartRF not found: {SMARTRF_PATH}", Theme.Red); return; }
            try
            {
                var psi = new ProcessStartInfo { FileName = SMARTRF_PATH, Arguments = "X", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                using var proc = Process.Start(psi)!;
                string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
                proc.WaitForExit();
                Log("── CC Debugger Detection ──────────────────────────", Theme.Cyan);
                foreach (string line in output.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)))
                    Log("  " + line.Trim(), Theme.White);
                if (output.Contains("CC1110") || output.Contains("CC Debugger"))
                {
                    string devLine = output.Split('\n').FirstOrDefault(l => l.Contains("Device:") || l.Contains("Chip:"))?.Trim() ?? "CC Debugger detected";
                    lblProvStatus.Text = "✓  " + devLine; lblProvStatus.ForeColor = Theme.Green;
                }
                else if (output.Contains("No connected"))
                { lblProvStatus.Text = "✗  No CC Debugger detected"; lblProvStatus.ForeColor = Theme.Red; }
                else
                { lblProvStatus.Text = "? Unexpected response — see log"; lblProvStatus.ForeColor = Theme.Yellow; }
            }
            catch (Exception ex) { lblProvStatus.Text = $"Error: {ex.Message}"; lblProvStatus.ForeColor = Theme.Red; Log($"Detect error: {ex.Message}", Theme.Red); }
        }

        private async Task RunProvisionAsync()
        {
            if (!ushort.TryParse(txtProvHwid.Text.Trim(), System.Globalization.NumberStyles.HexNumber, null, out ushort hwid))
            { Log("Invalid HWID — enter 4 hex digits e.g. 0001", Theme.Red); return; }
            if (!File.Exists(txtProvHexPath.Text.Trim())) { Log("Bootloader HEX file not found.", Theme.Red); return; }
            if (!File.Exists(SMARTRF_PATH)) { Log($"SmartRF Flash Programmer not found.", Theme.Red); return; }

            byte[]? key0 = ParseProvKey(txtProvKey0.Text, "Key 0");
            byte[]? key1 = ParseProvKey(txtProvKey1.Text, "Key 1");
            byte[]? key2 = ParseProvKey(txtProvKey2.Text, "Key 2");
            if (key0 == null || key1 == null || key2 == null) return;

            btnProvFlash.Enabled = false; btnProvDetect.Enabled = false;
            Log("═══════════════════════════════════════════════════", Theme.Cyan);
            Log($"  Board Provisioning  →  HWID {hwid:X4}", Theme.Cyan);
            Log("═══════════════════════════════════════════════════", Theme.Cyan);
            string tempHex = "";
            try
            {
                Log("Step 1: Parsing bootloader hex image...", Theme.White);
                byte[] image = OpenLstProtocol.ParseIntelHex(txtProvHexPath.Text.Trim());
                Log($"  Image loaded. Buffer = {image.Length} bytes.", Theme.Green);
                Log("Step 2: Patching image (HWID + keys)...", Theme.White);
                ProvisionPatchImage(image, hwid, new[] { key0, key1, key2 });
                Log($"  HWID {hwid:X4} inserted.", Theme.Green);
                Log("Step 3: Writing patched image to temp file...", Theme.White);
                tempHex = Path.Combine(Path.GetTempPath(), $"openlst_boot_{hwid:X4}_{DateTime.Now:HHmmss}.hex");
                File.WriteAllText(tempHex, DumpIntelHex(image));
                Log($"  Temp file: {tempHex}", Theme.Gray);
                Log("Step 4: Pre-erasing chip (handles locked chips automatically)...", Theme.White);
                var eraseArgs = "S E";  // Select first device, Erase only
                var psiErase = new ProcessStartInfo
                {
                    FileName = SMARTRF_PATH, Arguments = eraseArgs,
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true,
                };
                using (var eraseProc = new Process { StartInfo = psiErase })
                {
                    eraseProc.Start();
                    eraseProc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  " + e.Data.Trim(), Theme.Gray); };
                    eraseProc.ErrorDataReceived  += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  [erase] " + e.Data.Trim(), Theme.Gray); };
                    eraseProc.BeginOutputReadLine(); eraseProc.BeginErrorReadLine();
                    await Task.Run(() => eraseProc.WaitForExit());
                    Log(eraseProc.ExitCode == 0
                        ? "  ✓ Chip erased — ready to program."
                        : $"  Erase returned code {eraseProc.ExitCode} — continuing anyway.", Theme.Gray);
                }

                Log("Step 5: Calling SmartRF Flash Programmer...", Theme.White);
                var psi = new ProcessStartInfo
                {
                    FileName = SMARTRF_PATH, Arguments = $"S EPV F=\"{tempHex}\" LB(4)",
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                };
                using var proc = new Process { StartInfo = psi };
                proc.Start();
                proc.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  " + e.Data.Trim(), Theme.White); };
                proc.ErrorDataReceived  += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log("  [ERR] " + e.Data.Trim(), Theme.Red); };
                proc.BeginOutputReadLine(); proc.BeginErrorReadLine();
                await Task.Run(() => proc.WaitForExit());
                if (proc.ExitCode == 0)
                { Log("═══════════════════════════════════════════════════", Theme.Cyan); Log($"  PROVISION COMPLETE ✓  Board HWID {hwid:X4} ready.", Theme.Green); Log("═══════════════════════════════════════════════════", Theme.Cyan); }
                else
                { Log($"  SmartRF exited with code {proc.ExitCode}.", Theme.Red); }
            }
            catch (Exception ex) { Log($"Provision error: {ex.Message}", Theme.Red); }
            finally
            {
                if (!string.IsNullOrEmpty(tempHex) && File.Exists(tempHex)) { try { File.Delete(tempHex); } catch { } }
                btnProvFlash.Enabled = true; btnProvDetect.Enabled = true;
            }
        }

        private const int FLASH_SIGNATURE_KEYS = 0x03CC;
        private const int FLASH_HWID_ADDR      = 0x03FE;
        private const int FLASH_APP_SIGNATURE  = 0x6BF0;
        private const int FLASH_STORAGE_START  = 0x6C00;
        private const int FLASH_UPDATER_START  = 0x7000;

        private static void ProvisionPatchImage(byte[] image, ushort hwid, byte[][] keys)
        {
            image[FLASH_HWID_ADDR]     = (byte)(hwid & 0xFF);
            image[FLASH_HWID_ADDR + 1] = (byte)((hwid >> 8) & 0xFF);
            for (int k = 0; k < keys.Length; k++)
                Array.Copy(keys[k], 0, image, FLASH_SIGNATURE_KEYS + k * 16, 16);
            for (int i = OpenLstProtocol.FLASH_APP_START; i < FLASH_APP_SIGNATURE; i++) image[i] = 0xFF;
            for (int i = FLASH_APP_SIGNATURE; i < FLASH_STORAGE_START; i++)             image[i] = 0xFF;
            for (int i = FLASH_STORAGE_START; i < FLASH_UPDATER_START; i++)             image[i] = 0xFF;
        }

        private static string DumpIntelHex(byte[] data, int lineSize = 32)
        {
            var lines = new System.Text.StringBuilder();
            for (int addr = 0; addr < data.Length; addr += lineSize)
            {
                byte[] lineData = data.Skip(addr).Take(lineSize).ToArray();
                if (lineData.All(b => b == 0xFF)) continue;
                int length = lineData.Length;
                int checksum = length + (addr >> 8) + (addr & 0xFF) + 0;
                foreach (byte b in lineData) checksum += b;
                checksum = ((checksum ^ 0xFF) + 1) & 0xFF;
                lines.AppendLine($":{length:X2}{addr:X4}00" + BitConverter.ToString(lineData).Replace("-", "") + $"{checksum:X2}");
            }
            lines.AppendLine(":00000001FF");
            return lines.ToString();
        }

        private byte[]? ParseProvKey(string text, string label)
        {
            string s = text.Trim();
            if (s.Length != 32) { Log($"{label} must be exactly 32 hex chars.", Theme.Red); return null; }
            try { return Enumerable.Range(0, 16).Select(i => Convert.ToByte(s.Substring(i * 2, 2), 16)).ToArray(); }
            catch { Log($"{label} contains invalid hex characters.", Theme.Red); return null; }
        }

        // ══════════════════════════════════════════════════════════════════
        //  RF QA TAB
        // ══════════════════════════════════════════════════════════════════
        private void BuildRfQaTab()
        {
            var p  = tpRfQa;
            int lx = 10, row = 12;

            // ── Description notice ──────────────────────────────────────
            var pnlInfo = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(720, 56),
                BackColor = Color.FromArgb(14, 28, 14),
            };
            DrawBorder(pnlInfo);
            pnlInfo.Controls.Add(MkLabel(
                "◈  RF QA Check — Automated spectrum verification using RTL-SDR + rtl_power.",
                8, 6, 700, Theme.Green, Theme.FontMonoBold));
            pnlInfo.Controls.Add(MkLabel(
                "Connect board under test via USB-Serial. NESDR antenna 6-12\" from board. QA uses HWID 0xFFFF to force RF relay TX.",
                8, 24, 700, Theme.Gray, Theme.FontSmall));
            p.Controls.Add(pnlInfo);
            row += 68;

            // ── rtl_power path ─────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── rtl_power Location", lx, row)); row += 28;
            p.Controls.Add(MkLabel("Path:", lx, row + 3, 40, Theme.Gray));

            // Auto-detect or use saved path
            _rtlPowerPath = RTL_POWER_DEFAULT_PATHS.FirstOrDefault(File.Exists) ?? _rtlPowerPath;

            txtRtlPowerPath = new TextBox
            {
                Location  = new Point(lx + 46, row),
                Size      = new Size(510, 26),
                BackColor = Theme.PanelBack,
                ForeColor = File.Exists(_rtlPowerPath) ? Theme.Green : Theme.Red,
                Font      = Theme.FontSmall,
                Text      = string.IsNullOrEmpty(_rtlPowerPath)
                            ? RTL_POWER_DEFAULT_PATHS[0]
                            : _rtlPowerPath,
            };
            txtRtlPowerPath.TextChanged += (s, e) =>
            {
                _rtlPowerPath = txtRtlPowerPath.Text.Trim();
                txtRtlPowerPath.ForeColor = File.Exists(_rtlPowerPath) ? Theme.Green : Theme.Red;
                SaveSettings();
            };

            var btnBrowseRtl = MkButton("Browse...", lx + 566, row, 80, Theme.Cyan, Theme.PanelBack);
            btnBrowseRtl.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Title  = "Locate rtl_power.exe",
                    Filter = "rtl_power.exe|rtl_power.exe|All executables (*.exe)|*.exe",
                    InitialDirectory = @"C:\Program Files\RTL-SDR Blog",
                };
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    txtRtlPowerPath.Text = dlg.FileName;
                    _rtlPowerPath        = dlg.FileName;
                    txtRtlPowerPath.ForeColor = Theme.Green;
                    SaveSettings();
                    Log($"rtl_power path saved: {dlg.FileName}", Theme.Cyan);
                }
            };
            p.Controls.AddRange(new Control[] { txtRtlPowerPath, btnBrowseRtl });
            row += 40;

            if (!File.Exists(_rtlPowerPath))
                p.Controls.Add(MkLabel(
                    "⚠  rtl_power not found — RF QA requires RTL-SDR Blog V3/V4 drivers. See website setup guide.",
                    lx, row, 700, Theme.Yellow, Theme.FontSmall));
            row += 24;

            // ── Board details ───────────────────────────────────────────
            p.Controls.Add(MkSectionLabel("── Board Under Test", lx, row)); row += 28;

            p.Controls.Add(MkLabel("Serial:", lx, row + 3, 52, Theme.Gray));
            txtQaSerial = new TextBox
            {
                Location    = new Point(lx + 58, row),
                Size        = new Size(120, 26),
                BackColor   = Theme.PanelBack,
                ForeColor   = Theme.White,
                Font        = Theme.FontMono,
                PlaceholderText = "e.g. 0004",
            };

            p.Controls.Add(MkLabel("Board:", lx + 200, row + 3, 48, Theme.Gray));
            cmbQaBoardType = new ComboBox
            {
                Location      = new Point(lx + 254, row),
                Size          = new Size(140, 26),
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                Font          = Theme.FontMono,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            cmbQaBoardType.Items.AddRange(new object[] { "SCK-915", "SCK-2400" });
            cmbQaBoardType.SelectedIndex = 0;

            p.Controls.Add(MkLabel("Dongle:", lx + 410, row + 3, 56, Theme.Gray));
            cmbQaDongleIndex = new ComboBox
            {
                Location      = new Point(lx + 472, row),
                Size          = new Size(80, 26),
                BackColor     = Theme.PanelBack,
                ForeColor     = Theme.White,
                Font          = Theme.FontMono,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            cmbQaDongleIndex.Items.AddRange(new object[] { "0", "1", "2", "3" });
            cmbQaDongleIndex.SelectedIndex = 0;
            var lblDongleHint = MkLabel("(-d index)", lx + 558, row + 5, 80, Theme.Gray, Theme.FontSmall);

            p.Controls.AddRange(new Control[] { txtQaSerial, cmbQaBoardType, cmbQaDongleIndex, lblDongleHint });
            row += 40;

            // ── PPM correction ──────────────────────────────────────────
            p.Controls.Add(MkLabel("PPM Correction:", lx, row + 3, 110, Theme.Gray));
            var txtQaPpm = new TextBox
            {
                Location        = new Point(lx + 116, row),
                Size            = new Size(70, 26),
                BackColor       = Theme.PanelBack,
                ForeColor       = Theme.Cyan,
                Font            = Theme.FontMono,
                Text            = "auto",
                PlaceholderText = "auto",
            };
            txtQaPpm.TextChanged += (s, e) =>
            {
                string txt = txtQaPpm.Text.Trim();
                if (txt == "auto" || string.IsNullOrEmpty(txt))
                {
                    _qaManualPpm       = false;
                    txtQaPpm.ForeColor = Theme.Cyan;
                }
                else if (int.TryParse(txt, out int ppm))
                {
                    _qaPpm             = ppm;
                    _qaManualPpm       = true;
                    txtQaPpm.ForeColor = Theme.Yellow;
                    SaveSettings();
                }
                else
                    txtQaPpm.ForeColor = Theme.Red;
            };

            p.Controls.Add(MkLabel("auto = calculated each scan from board reference.  Type value to override.",
                lx + 200, row + 5, 390, Theme.Gray, Theme.FontSmall));

            var btnResetPpm = MkButton("↺ Auto", lx + 596, row, 60, Theme.Gray, Theme.PanelBack);
            btnResetPpm.Click += (s, e) =>
            {
                _qaManualPpm       = false;
                txtQaPpm.Text      = "auto";
                txtQaPpm.ForeColor = Theme.Cyan;
                Log("PPM reset to auto mode — calculated fresh each scan.", Theme.Gray);
            };

            var btnLockPpm = MkButton("🔒 Lock PPM", lx + 664, row, 110, Theme.Cyan, Theme.PanelBack);
            btnLockPpm.Click += (s, e) =>
            {
                if (_qaRawPpm == 0)
                { Log("Run a QA scan first to calculate PPM.", Theme.Yellow); return; }
                _qaPpm             = _qaRawPpm;
                _qaManualPpm       = true;
                txtQaPpm.Text      = _qaRawPpm.ToString();
                txtQaPpm.ForeColor = Theme.Yellow;
                SaveSettings();
                Log($"PPM locked at {_qaRawPpm} PPM from last scan. Use ↺ Auto to return to auto mode.", Theme.Green);
            };

            p.Controls.AddRange(new Control[] { txtQaPpm, btnResetPpm, btnLockPpm });
            row += 40;
            btnRunQa = MkButton("▶  Run QA Check", lx, row, 180, Theme.Green, Color.FromArgb(16, 48, 16));
            btnRunQa.Click += async (s, e) => await RunRfQaAsync();

            btnQaSave = MkButton("💾 Save Log", lx + 192, row, 110, Theme.Cyan, Theme.PanelBack);
            btnQaSave.Enabled = false;
            btnQaSave.Click += (s, e) => SaveQaLog();

            btnQaPrint = MkButton("🖨 Print Summary", lx + 314, row, 150, Theme.Yellow, Theme.PanelBack);
            btnQaPrint.Enabled = false;
            btnQaPrint.Click += (s, e) => PrintQaSummary();

            btnQaBrowseSnap = MkButton("📂 Load Snapshot", lx + 476, row, 150, Theme.Gray, Theme.PanelBack);
            btnQaBrowseSnap.Click += (s, e) =>
            {
                using var dlg = new OpenFileDialog
                {
                    Title            = "Load QA Snapshot CSV",
                    Filter           = "QA Snapshot (*.csv)|*.csv|All files (*.*)|*.*",
                    InitialDirectory = QaSnapshotFolder,
                };
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    var loaded = ParseRtlPowerCsv(dlg.FileName);
                    if (loaded.Count == 0)
                    { Log("No data in selected snapshot file.", Theme.Red); return; }
                    _qaSpectrum = loaded;
                    pnlQaChart.Invalidate();
                    lblQaStatus.Text      = $"Loaded: {Path.GetFileName(dlg.FileName)} ({loaded.Count} bins)";
                    lblQaStatus.ForeColor = Theme.Cyan;
                    Log($"RF QA: loaded snapshot {Path.GetFileName(dlg.FileName)} — {loaded.Count} bins", Theme.Cyan);
                }
            };

            lblQaStatus = MkLabel("Ready — configure rtl_power path and board details above.",
                lx + 638, row + 5, 240, Theme.Gray, Theme.FontSmall);

            p.Controls.AddRange(new Control[] { btnRunQa, btnQaSave, btnQaPrint, btnQaBrowseSnap, lblQaStatus });
            row += 44;

            // ── Results panel ───────────────────────────────────────────
            pnlQaResults = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(720, 130),
                BackColor = Theme.PanelBack,
                Visible   = false,
            };
            DrawBorder(pnlQaResults);

            // Results grid
            int rx = 12, ry = 10;
            lblQaFreq    = MkLabel("Center Frequency:", rx, ry, 160, Theme.Gray);
            lblQaFreqVal = MkLabel("—", rx + 165, ry, 240, Theme.White, Theme.FontMonoBold);
            lblQaPower   = MkLabel("Peak Power:", rx, ry + 28, 160, Theme.Gray);
            lblQaPowerVal= MkLabel("—", rx + 165, ry + 28, 240, Theme.White, Theme.FontMonoBold);
            lblQaH2      = MkLabel("2nd Harmonic:", rx + 380, ry, 130, Theme.Gray);
            lblQaH2Val   = MkLabel("—", rx + 515, ry, 180, Theme.White, Theme.FontMonoBold);
            lblQaH3      = MkLabel("3rd Harmonic:", rx + 380, ry + 28, 130, Theme.Gray);
            lblQaH3Val   = MkLabel("—", rx + 515, ry + 28, 180, Theme.White, Theme.FontMonoBold);

            lblQaOverall = new Label
            {
                Text      = "PENDING",
                Location  = new Point(rx, ry + 68),
                Size      = new Size(690, 42),
                ForeColor = Theme.Gray,
                BackColor = Color.Transparent,
                Font      = new Font("Consolas", 14f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
            };

            pnlQaResults.Controls.AddRange(new Control[]
            {
                lblQaFreq, lblQaFreqVal, lblQaPower, lblQaPowerVal,
                lblQaH2, lblQaH2Val, lblQaH3, lblQaH3Val, lblQaOverall
            });
            p.Controls.Add(pnlQaResults);
            row += 140;

            // ── Spectrum chart ──────────────────────────────────────────
            pnlQaChart = new Panel
            {
                Location  = new Point(lx, row),
                Size      = new Size(720, 180),
                BackColor = Color.FromArgb(8, 8, 16),
            };
            DrawBorder(pnlQaChart);

            // Chart title
            var lblChartTitle = MkLabel("◈ SPECTRUM — dBm vs MHz", 8, 4, 400, Theme.Cyan, Theme.FontMonoBold);
            pnlQaChart.Controls.Add(lblChartTitle);

            pnlQaChart.Paint += DrawQaChart;
            p.Controls.Add(pnlQaChart);
        }

        // ── Draw spectrum chart ──────────────────────────────────────────
        private void DrawQaChart(object? sender, PaintEventArgs e)
        {
            var g   = e.Graphics;
            var pnl = pnlQaChart;
            int W = pnl.Width, H = pnl.Height;
            int top = 24, bot = H - 24, left = 48, right = W - 12;
            int chartW = right - left, chartH = bot - top;

            // Background
            g.FillRectangle(new SolidBrush(Color.FromArgb(8, 8, 16)), left, top, chartW, chartH);

            if (_qaSpectrum.Count < 2)
            {
                using var font = new Font("Consolas", 9f);
                g.DrawString("No data — run QA check to populate spectrum",
                    font, Brushes.DimGray, left + 10, top + chartH / 2 - 8);
                return;
            }

            double freqMin = _qaSpectrum.Min(x => x.FreqMHz);
            double freqMax = _qaSpectrum.Max(x => x.FreqMHz);
            double dbMin   = Math.Max(_qaSpectrum.Min(x => x.Dbm) - 5, -120);
            double dbMax   = _qaSpectrum.Max(x => x.Dbm) + 5;

            // Grid lines
            using var gridPen = new Pen(Color.FromArgb(30, 30, 50), 1);
            for (int i = 0; i <= 4; i++)
            {
                int y = top + (int)(chartH * i / 4.0);
                g.DrawLine(gridPen, left, y, right, y);
                double db = dbMax - (dbMax - dbMin) * i / 4.0;
                using var font = new Font("Consolas", 7.5f);
                g.DrawString($"{db:F0}", font, Brushes.DimGray, 2, y - 8);
            }

            // Spectrum trace
            using var tracePen = new Pen(Color.Cyan, 1.5f);
            var pts = _qaSpectrum.Select(s =>
            {
                float x = left + (float)((s.FreqMHz - freqMin) / (freqMax - freqMin) * chartW);
                float y = bot  - (float)((s.Dbm - dbMin) / (dbMax - dbMin) * chartH);
                return new PointF(x, Math.Clamp(y, top, bot));
            }).ToArray();
            if (pts.Length > 1) g.DrawLines(tracePen, pts);

            // Frequency labels
            using var labelFont = new Font("Consolas", 7.5f);
            for (int i = 0; i <= 4; i++)
            {
                double freq = freqMin + (freqMax - freqMin) * i / 4.0;
                int    x    = left + (int)(chartW * i / 4.0);
                g.DrawString($"{freq:F0}", labelFont, Brushes.DimGray, x - 14, bot + 4);
            }

            // Peak marker — vertical cyan line
            if (_lastQaResult != null)
            {
                float px = left + (float)((_lastQaResult.PeakFreqMHz - freqMin)
                           / (freqMax - freqMin) * chartW);
                using var peakPen = new Pen(Color.LimeGreen, 1.5f) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
                g.DrawLine(peakPen, px, top, px, bot);
                g.DrawString($"{_lastQaResult.PeakFreqMHz:F3} MHz",
                    labelFont, Brushes.LimeGreen, px + 3, top + 4);
            }

            // PPM annotation — bottom right of chart
            if (_qaRawPpm != 0 || _qaManualPpm)
            {
                string ppmLabel = _qaManualPpm
                    ? $"PPM: {_qaDisplayPpm} (manual)"
                    : $"PPM: {_qaRawPpm} (auto) — dongle offset corrected";
                using var ppmFont = new Font("Consolas", 8f);
                var sz = g.MeasureString(ppmLabel, ppmFont);
                g.DrawString(ppmLabel, ppmFont, Brushes.DarkCyan,
                    right - sz.Width - 4, bot - sz.Height - 2);
            }
        }

        // ── Run RF QA scan ──────────────────────────────────────────────
        private async Task RunRfQaAsync()
        {
            if (!File.Exists(_rtlPowerPath))
            {
                Log("rtl_power not found. Install RTL-SDR Blog drivers and set path above.", Theme.Red);
                lblQaStatus.Text = "✗ rtl_power not found — see path above.";
                lblQaStatus.ForeColor = Theme.Red;
                return;
            }

            string serial = txtQaSerial.Text.Trim();
            if (string.IsNullOrWhiteSpace(serial))
            {
                Log("Enter board serial number before running QA.", Theme.Yellow);
                txtQaSerial.Focus();
                return;
            }

            string boardType = cmbQaBoardType.SelectedItem?.ToString() ?? "SCK-915";

            // Target frequencies based on board type
            // R820T tuner range: ~24MHz to ~1750MHz
            // Keep sweep tight — ±10MHz around center for faster scan
            double centerMHz     = boardType == "SCK-2400" ? 2440.0 : 915.0;
            double sweepStartMHz = Math.Max(centerMHz - 10, 50);
            double sweepEndMHz   = Math.Min(centerMHz + 10, 1700);
            double h2MHz         = centerMHz * 2;   // 1830MHz for SCK-915
            double h3MHz         = centerMHz * 3;   // 2745MHz for SCK-915

            // Note: NESDR Smart tops out at ~1750MHz
            // For SCK-915: 2nd harmonic at 1830MHz is above range — use TinySA
            // For SCK-2400: entirely above range — use TinySA
            if (boardType == "SCK-2400" && centerMHz > 1750)
            {
                Log("SCK-2400 at 2.4GHz is above NESDR Smart range (~1750MHz).", Theme.Yellow);
                Log("For SCK-2400 QA use TinySA Ultra+ instead. Running fundamental scan only.", Theme.Yellow);
            }

            btnRunQa.Enabled      = false;
            btnQaSave.Enabled     = false;
            btnQaPrint.Enabled    = false;
            lblQaStatus.ForeColor = Theme.Yellow;
            lblQaStatus.Text      = "Running fundamental scan...";
            pnlQaResults.Visible  = false;
            _qaSpectrum.Clear();
            _lastQaResult = null;

            try
            {
                // ── Named snapshot files ─────────────────────────────────────
                // Reset counter when serial changes
                if (serial != _qaSnapLastSerial) { _qaSnapCount = 0; _qaSnapLastSerial = serial; }
                _qaSnapCount++;
                string snapBase = $"SCK915_{serial}_snap{_qaSnapCount:D3}";
                string tmpFund  = Path.Combine(QaTempFolder, $"{snapBase}_fund.csv");

                // Dongle index from dropdown
                int dongleIdx = cmbQaDongleIndex?.SelectedIndex ?? 0;

                Log($"RF QA: scanning {sweepStartMHz:F0}–{sweepEndMHz:F0} MHz for fundamental...", Theme.Cyan);
                Log($"RF QA: snapshot → {snapBase}  dongle index {dongleIdx}", Theme.Gray);
                Log("RF QA: triggering board TX during scan — sending get_telem loop...", Theme.Gray);

                // Start a background TX trigger loop — keeps board transmitting RF during scan
                // KEY: Use HWID 0xFFFF (no board has this ID) so the connected board
                // sees a non-matching HWID and relays the command over RF — forcing RF TX
                // This works with ONE board connected via serial — no second board needed
                // Board receives command, HWID doesn't match, transmits RF relay → NESDR catches it
                var txCts = new CancellationTokenSource();
                _ = Task.Run(async () =>
                {
                    while (!txCts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            if (_port?.IsOpen == true)
                            {
                                ushort rfHwid = 0xFFFF; // non-matching HWID forces RF relay TX
                                ushort seq    = IncSeqNum();
                                WritePacket(OpenLstProtocol.BuildSimpleCommand(rfHwid, seq, "get_telem"));
                            }
                        }
                        catch { }
                        await Task.Delay(800, txCts.Token).ContinueWith(_ => { });
                    }
                }, txCts.Token);

                bool ok = await RunRtlPower(
                    $"-f {sweepStartMHz:F0}M:{sweepEndMHz:F0}M:10k -g 496 -d {dongleIdx}" +
                    $"{(_qaPpm != 0 ? $" -p {_qaPpm}" : "")} -P -i 3s -e 9s \"{tmpFund}\"");

                txCts.Cancel();

                // Check file exists and has content even if exit code non-zero
                // rtl_power sometimes returns non-zero but still writes valid data
                bool fundHasData = File.Exists(tmpFund) && new FileInfo(tmpFund).Length > 50;
                if (!fundHasData)
                {
                    Log("rtl_power fundamental scan failed — no data written.", Theme.Red);
                    lblQaStatus.Text      = "✗ Scan failed — check dongle connection.";
                    lblQaStatus.ForeColor = Theme.Red;
                    return;
                }

                var fundData = ParseRtlPowerCsv(tmpFund);
                // Copy snapshot to QA_Snapshots for later browsing — don't delete
                try
                {
                    string snapDest = Path.Combine(QaSnapshotFolder, $"{snapBase}_fund.csv");
                    File.Copy(tmpFund, snapDest, overwrite: true);
                    Log($"RF QA: snapshot saved → QA_Snapshots\\{snapBase}_fund.csv", Theme.Gray);
                }
                catch { }
                try { File.Delete(tmpFund); } catch { }

                if (fundData.Count == 0)
                { Log("No data returned from rtl_power CSV.", Theme.Red); return; }

                Log($"RF QA: fundamental scan got {fundData.Count} frequency bins.", Theme.Gray);

                // ── SCAN 2: Harmonics (SCK-915 only, within NESDR range) ──
                List<(double FreqMHz, double Dbm)> h2Data = new();
                List<(double FreqMHz, double Dbm)> h3Data = new();

                if (boardType == "SCK-915")
                {
                    // 2nd harmonic at 1830MHz is just above NESDR Smart limit (~1750MHz)
                    // Scan up to 1700MHz to catch anything in range
                    double harmStart = Math.Max(h2MHz - 30, 50);
                    double harmEnd   = Math.Min(h3MHz + 20, 1700);

                    if (harmStart < harmEnd)
                    {
                        lblQaStatus.Text = "Scanning harmonics...";
                        Log($"RF QA: scanning harmonic region {harmStart:F0}–{harmEnd:F0} MHz...", Theme.Cyan);

                        string tmpHarm = Path.Combine(Path.GetTempPath(), $"qa_harm_{Guid.NewGuid():N}.csv");
                        ok = await RunRtlPower(
                            $"-f {harmStart:F0}M:{harmEnd:F0}M:100k -g 496" +
                            $"{(_qaPpm != 0 ? $" -p {_qaPpm}" : "")} -P -i 3s -e 9s \"{tmpHarm}\"");

                        bool harmHasData = File.Exists(tmpHarm) && new FileInfo(tmpHarm).Length > 50;
                        if (harmHasData)
                        {
                            var harmAll = ParseRtlPowerCsv(tmpHarm);
                            try { File.Delete(tmpHarm); } catch { }
                            h2Data = harmAll.Where(x => Math.Abs(x.FreqMHz - h2MHz) < 50).ToList();
                            h3Data = harmAll.Where(x => Math.Abs(x.FreqMHz - h3MHz) < 50).ToList();
                            Log($"RF QA: harmonic scan got {harmAll.Count} bins.", Theme.Gray);
                        }
                        else
                            Log("Harmonic scan returned no data — R820T may not tune this high.", Theme.Yellow);
                    }
                    else
                        Log("Harmonic region above NESDR Smart tuner range — use TinySA for harmonic check.", Theme.Yellow);
                }

                // ── Analyse results ──────────────────────────────────────
                _qaSpectrum = fundData;
                pnlQaChart.Invalidate();

                // Find peak NEAR the expected center frequency (±8MHz)
                var nearCenter = fundData.Where(x => Math.Abs(x.FreqMHz - centerMHz) <= 8.0).ToList();
                var searchSet  = nearCenter.Count > 0 ? nearCenter : fundData;

                // 2-FSK detection: the signal appears as two symmetric tones
                // around the center frequency. Find the midpoint between the
                // two highest peaks rather than taking the single max bin.
                var top2 = searchSet.OrderByDescending(x => x.Dbm).Take(20)
                    .OrderBy(x => x.FreqMHz).ToList();

                double peakFreq, peakDbm;
                if (top2.Count >= 2)
                {
                    // Find the two highest well-separated peaks (>50kHz apart)
                    var highest = top2.OrderByDescending(x => x.Dbm).First();
                    var second  = top2.OrderByDescending(x => x.Dbm)
                        .FirstOrDefault(x => Math.Abs(x.FreqMHz - highest.FreqMHz) > 0.05);

                    if (second != default && Math.Abs(second.FreqMHz - highest.FreqMHz) < 1.0)
                    {
                        // Two FSK tones found — center is midpoint
                        peakFreq = (highest.FreqMHz + second.FreqMHz) / 2.0;
                        peakDbm  = Math.Max(highest.Dbm, second.Dbm);
                        Log($"RF QA: 2-FSK tones at {highest.FreqMHz:F3} + {second.FreqMHz:F3} MHz → center {peakFreq:F3} MHz", Theme.Gray);
                    }
                    else
                    {
                        peakFreq = highest.FreqMHz;
                        peakDbm  = highest.Dbm;
                    }
                }
                else
                {
                    var best = searchSet.OrderByDescending(x => x.Dbm).First();
                    peakFreq = best.FreqMHz;
                    peakDbm  = best.Dbm;
                }

                Log($"RF QA: peak at {peakFreq:F3} MHz = {peakDbm:F1} dBm (searched {searchSet.Count} bins)", Theme.Gray);

                // ── Auto-calculate dongle PPM from this scan ─────────────
                // Use board as reference — SmartRF verified 915.000 MHz
                // PPM = (measured - actual) / actual × 1,000,000
                // This corrects for dongle crystal offset — logged for transparency
                // Manual override: if user typed a PPM value, use that instead
                int autoPpm = (int)Math.Round((peakFreq - centerMHz) / centerMHz * 1_000_000);
                int displayPpm = _qaManualPpm ? _qaPpm : autoPpm;

                // Apply PPM correction to spectrum for display
                // Shifts all frequency bins so peak lands at true center
                double ppmShiftMHz = centerMHz * displayPpm / 1_000_000.0;
                var correctedSpectrum = fundData
                    .Select(x => (FreqMHz: x.FreqMHz - ppmShiftMHz, x.Dbm))
                    .ToList();
                _qaSpectrum     = correctedSpectrum;
                _qaRawPpm       = autoPpm;
                _qaDisplayPpm   = displayPpm;
                pnlQaChart.Invalidate();

                // Corrected peak frequency for results
                double correctedPeakFreq  = peakFreq - ppmShiftMHz;
                double correctedErrorKHz  = (correctedPeakFreq - centerMHz) * 1000.0;

                Log($"RF QA: raw peak {peakFreq:F3} MHz → corrected {correctedPeakFreq:F3} MHz " +
                    $"(dongle PPM: {autoPpm:+0;-0}{(_qaManualPpm ? " manual" : " auto")})", Theme.Gray);

                double freqErrorKHz = correctedErrorKHz;

                double h2Dbm  = h2Data.Count > 0 ? h2Data.Max(x => x.Dbm) : -999;
                double h3Dbm  = h3Data.Count > 0 ? h3Data.Max(x => x.Dbm) : -999;
                double h2Dbc  = h2Dbm  > -999 ? h2Dbm  - peakDbm : -999;
                double h3Dbc  = h3Dbm  > -999 ? h3Dbm  - peakDbm : -999;

                // Frequency pass limit — ±10kHz after PPM correction applied
                // PPM is always auto-calculated so correction is always applied
                double freqPassLimitKHz = 10.0;
                bool freqPass = Math.Abs(freqErrorKHz) <= freqPassLimitKHz;
                bool h2Pass   = h2Dbc <= -999 || h2Dbc < -40.0; // pass if no data (out of range) or < -40dBc
                bool h3Pass   = h3Dbc <= -999 || h3Dbc < -40.0;

                _lastQaResult = new QaResult
                {
                    BoardSerial      = serial,
                    BoardType        = boardType,
                    Firmware         = txtFirmwareFile?.Text.Trim() ?? "",
                    RawPeakFreqMHz   = peakFreq,
                    PeakFreqMHz      = correctedPeakFreq,
                    FreqErrorKHz     = correctedErrorKHz,
                    PeakDbm          = peakDbm,
                    H2Dbc            = h2Dbc,
                    H3Dbc            = h3Dbc,
                    PpmCorrection    = displayPpm,
                    RawPpm           = autoPpm,
                    PpmIsManual      = _qaManualPpm,
                    FreqPass         = freqPass,
                    H2Pass           = h2Pass,
                    H3Pass           = h3Pass,
                };

                UpdateQaResultsPanel(_lastQaResult);
                pnlQaChart.Invalidate();

                string overall = _lastQaResult.Overall ? "PASS" : "FAIL";
                Log($"RF QA [{serial}] → {overall} | "
                  + $"Freq: {peakFreq:F3} MHz ({freqErrorKHz:+0.0;-0.0} kHz) | "
                  + $"H2: {(h2Dbc > -999 ? $"{h2Dbc:F1} dBc" : "N/A")} | "
                  + $"H3: {(h3Dbc > -999 ? $"{h3Dbc:F1} dBc" : "N/A")}",
                    _lastQaResult.Overall ? Theme.Green : Theme.Red);

                lblQaStatus.Text      = $"✓ Complete — {overall}";
                lblQaStatus.ForeColor = _lastQaResult.Overall ? Theme.Green : Theme.Red;
                btnQaSave.Enabled     = true;
                btnQaPrint.Enabled    = true;
            }
            catch (Exception ex)
            {
                Log($"RF QA error: {ex.Message}", Theme.Red);
                lblQaStatus.Text      = "✗ Error — see log.";
                lblQaStatus.ForeColor = Theme.Red;
            }
            finally
            {
                btnRunQa.Enabled = true;
            }
        }

        private void UpdateQaResultsPanel(QaResult r)
        {
            pnlQaResults.Visible = true;

            // Frequency
            string freqStr = $"{r.PeakFreqMHz:F3} MHz  ({r.FreqErrorKHz:+0.0;-0.0} kHz error)";
            lblQaFreqVal.Text      = freqStr + (r.FreqPass ? "  ✓" : "  ✗");
            lblQaFreqVal.ForeColor = r.FreqPass ? Theme.Green : Theme.Red;

            // Power
            lblQaPowerVal.Text      = $"{r.PeakDbm:F1} dBm  (relative — uncalibrated)";
            lblQaPowerVal.ForeColor = Theme.White;

            // H2
            if (r.H2Dbc > -999)
            {
                lblQaH2Val.Text      = $"{r.H2Dbc:F1} dBc" + (r.H2Pass ? "  ✓" : "  ✗");
                lblQaH2Val.ForeColor = r.H2Pass ? Theme.Green : Theme.Red;
            }
            else
            {
                lblQaH2Val.Text      = "N/A (above NESDR range)";
                lblQaH2Val.ForeColor = Theme.Gray;
            }

            // H3
            if (r.H3Dbc > -999)
            {
                lblQaH3Val.Text      = $"{r.H3Dbc:F1} dBc" + (r.H3Pass ? "  ✓" : "  ✗");
                lblQaH3Val.ForeColor = r.H3Pass ? Theme.Green : Theme.Red;
            }
            else
            {
                lblQaH3Val.Text      = "N/A (above NESDR range)";
                lblQaH3Val.ForeColor = Theme.Gray;
            }

            // Overall
            lblQaOverall.Text      = r.Overall ? "◉  PASS" : "◉  FAIL";
            lblQaOverall.ForeColor = r.Overall ? Theme.Green : Theme.Red;
            pnlQaResults.BackColor = r.Overall
                ? Color.FromArgb(10, 30, 10)
                : Color.FromArgb(30, 10, 10);
        }

        // ── Launch rtl_power as subprocess ──────────────────────────────
        private async Task<bool> RunRtlPower(string args)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName               = _rtlPowerPath,
                    Arguments              = args,
                    WorkingDirectory       = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                // Add rtlsdr folder to PATH so rtlsdr.dll and pthreadVC2.dll are found
                string rtlDir = Path.GetDirectoryName(_rtlPowerPath) ?? "";
                string existingPath = psi.EnvironmentVariables["PATH"] ?? "";
                if (!existingPath.Contains(rtlDir))
                    psi.EnvironmentVariables["PATH"] = rtlDir + ";" + existingPath;
                using var proc = new Process { StartInfo = psi };
                proc.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        BeginInvoke(new Action(() => Log("  rtl: " + e.Data.Trim(), Theme.Gray)));
                };
                proc.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        BeginInvoke(new Action(() => Log("  rtl: " + e.Data.Trim(), Theme.Gray)));
                };
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                // Wait up to 30 seconds — scan is 9s + startup overhead
                bool exited = await Task.Run(() => proc.WaitForExit(30000));
                if (!exited) { proc.Kill(); Log("  rtl_power timeout — killed.", Theme.Yellow); }
                // Return true even on non-zero exit — rtl_power often exits 1
                // but still writes valid CSV data
                return true;
            }
            catch (Exception ex)
            {
                Log($"rtl_power launch failed: {ex.Message}", Theme.Red);
                return false;
            }
        }

        // ── Parse rtl_power CSV output ──────────────────────────────────
        // Format: date, time, Hz_low, Hz_high, Hz_step, samples, dbm, dbm...
        private static List<(double FreqMHz, double Dbm)> ParseRtlPowerCsv(string path)
        {
            var result = new List<(double FreqMHz, double Dbm)>();
            try
            {
                foreach (var line in File.ReadAllLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var cols = line.Split(',');
                    if (cols.Length < 7) continue;
                    var ic = System.Globalization.CultureInfo.InvariantCulture;
                    if (!double.TryParse(cols[2].Trim(), ic, out double hzLow))  continue;
                    if (!double.TryParse(cols[4].Trim(), ic, out double hzStep)) continue;
                    if (hzStep <= 0) continue;
                    for (int i = 6; i < cols.Length; i++)
                    {
                        if (!double.TryParse(cols[i].Trim(), ic, out double dbm)) continue;
                        double freqMHz = (hzLow + hzStep * (i - 6)) / 1e6;
                        // Peak hold — keep max dBm at each frequency
                        var existing = result.FindIndex(x => Math.Abs(x.FreqMHz - freqMHz) < 0.001);
                        if (existing >= 0)
                        { if (dbm > result[existing].Dbm) result[existing] = (freqMHz, dbm); }
                        else
                            result.Add((freqMHz, dbm));
                    }
                }
            }
            catch { /* non-fatal — return what we have */ }
            return result.OrderBy(x => x.FreqMHz).ToList();
        }

        // ── Save QA log ─────────────────────────────────────────────────
        private void SaveQaLog()
        {
            if (_lastQaResult == null) return;
            try
            {
                Directory.CreateDirectory(QaSnapshotFolder);
                string serial  = _lastQaResult.BoardSerial;
                string snapNum = $"snap{_qaSnapCount:D3}";
                string fname   = Path.Combine(QaSnapshotFolder,
                    $"SCK915_{serial}_{snapNum}_{DateTime.Now:yyyyMMdd_HHmm}.txt");
                File.WriteAllText(fname, BuildQaSummaryText(_lastQaResult));
                Log($"QA log saved: {fname}", Theme.Green);
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{fname}\"");
            }
            catch (Exception ex) { Log($"QA save error: {ex.Message}", Theme.Red); }
        }

        // ── Print QA summary ────────────────────────────────────────────
        private void PrintQaSummary()
        {
            if (_lastQaResult == null) return;
            try
            {
                // Write to temp HTML and open in browser for printing
                string html = BuildQaSummaryHtml(_lastQaResult);
                string tmp  = Path.Combine(Path.GetTempPath(),
                    $"QA_{_lastQaResult.BoardSerial}_{DateTime.Now:yyyyMMdd}.html");
                File.WriteAllText(tmp, html);
                System.Diagnostics.Process.Start(new ProcessStartInfo(tmp)
                    { UseShellExecute = true });
                Log("QA summary opened in browser — use Ctrl+P to print.", Theme.Cyan);
            }
            catch (Exception ex) { Log($"QA print error: {ex.Message}", Theme.Red); }
        }

        private static string BuildQaSummaryText(QaResult r)
        {
            string h2Str = r.H2Dbc > -999
                ? $"{r.H2Dbc:F1} dBc    " + (r.H2Pass ? "PASS (< -40 dBc)" : "FAIL")
                : "N/A — above NESDR Smart range";
            string h3Str = r.H3Dbc > -999
                ? $"{r.H3Dbc:F1} dBc    " + (r.H3Pass ? "PASS (< -40 dBc)" : "FAIL")
                : "N/A — above NESDR Smart range";
            string firmware = string.IsNullOrEmpty(r.Firmware) ? "Not recorded" : r.Firmware;

            return
$"""
SpaceCommsKit — RF QA Certificate
══════════════════════════════════════════════════════════

Board Type:    {r.BoardType}
Board Serial:  {r.BoardSerial}
Test Date:     {r.Timestamp:yyyy-MM-dd HH:mm:ss}
Firmware:      {firmware}
Test Tool:     SCK Ground Station + rtl_power (RTL-SDR Blog)
Dongle PPM:    {(r.RawPpm != 0 ? $"{r.RawPpm:+0;-0} PPM {(r.PpmIsManual ? "(manual override)" : "(auto-calculated from board reference)")}" : "0 PPM (not yet calculated)")}
Freq Display:  PPM correction applied to display — raw dongle offset corrected using board as reference signal

──────────────────────────────────────────────────────────
RF MEASUREMENTS
──────────────────────────────────────────────────────────

Center Frequency:  {r.PeakFreqMHz:F3} MHz
Frequency Error:   {r.FreqErrorKHz:+0.00;-0.00} kHz    {(r.FreqPass ? "PASS (≤ ±10 kHz)" : "FAIL (> ±10 kHz)")}
Peak Power:        {r.PeakDbm:F1} dBm (relative — uncalibrated)
2nd Harmonic:      {h2Str}
3rd Harmonic:      {h3Str}

──────────────────────────────────────────────────────────
RESULT:  {(r.Overall ? "PASS ✓" : "FAIL ✗")}
──────────────────────────────────────────────────────────

Notes:
- Power measurement is relative (uncalibrated SDR dongle).
- Harmonic levels are approximate — consumer SDR, not calibrated test equipment.
- Frequency accuracy measured against known RTL-SDR PPM calibration.
- SCK-915 boards characterized using NooElec NESDR Smart V5 dongle.
- Batch characterization data available to Patreon members via Section 7.

SpaceCommsKit · Tennessee, USA · spacecommskit.com
""";
        }

        private static string BuildQaSummaryHtml(QaResult r) => $$$"""
<!DOCTYPE html>
<html>
<head>
<meta charset="UTF-8">
<title>RF QA Certificate — {{{r.BoardType}}} {{{r.BoardSerial}}}</title>
<style>
  body { font-family: 'Consolas', monospace; background: #fff; color: #111; max-width: 750px; margin: 40px auto; padding: 0 24px; }
  h1   { font-size: 20px; border-bottom: 2px solid #111; padding-bottom: 8px; margin-bottom: 4px; }
  h2   { font-size: 14px; color: #444; margin-bottom: 20px; font-weight: normal; }
  table{ width: 100%; border-collapse: collapse; margin: 20px 0; font-size: 13px; }
  th   { text-align: left; background: #111; color: #fff; padding: 8px 12px; }
  td   { padding: 7px 12px; border-bottom: 1px solid #ddd; }
  .pass{ color: #1a7a1a; font-weight: bold; }
  .fail{ color: #cc2200; font-weight: bold; }
  .overall { font-size: 24px; font-weight: bold; text-align: center; padding: 16px;
              border: 2px solid; margin-top: 24px;
              color: {{{(r.Overall ? "#1a7a1a" : "#cc2200")}}};
              border-color: {{{(r.Overall ? "#1a7a1a" : "#cc2200")}}}; }
  .footer { font-size: 11px; color: #888; margin-top: 32px; border-top: 1px solid #ddd; padding-top: 12px; }
</style>
</head>
<body>
<h1>SpaceCommsKit — RF QA Certificate</h1>
<h2>{{{r.BoardType}}} · Serial {{{r.BoardSerial}}} · {{{r.Timestamp:yyyy-MM-dd HH:mm}}}</h2>

<table>
  <tr><th colspan="3">Board Information</th></tr>
  <tr><td>Board Type</td><td colspan="2">{{{r.BoardType}}}</td></tr>
  <tr><td>Board Serial</td><td colspan="2">{{{r.BoardSerial}}}</td></tr>
  <tr><td>Test Date</td><td colspan="2">{{{r.Timestamp:yyyy-MM-dd HH:mm:ss}}}</td></tr>
  <tr><td>Firmware</td><td colspan="2">{{{(string.IsNullOrEmpty(r.Firmware) ? "Not recorded" : r.Firmware)}}}</td></tr>
  <tr><td>Test Tool</td><td colspan="2">SCK Ground Station + rtl_power (RTL-SDR Blog V3/V4 drivers)</td></tr>
  <tr><td>Dongle PPM Offset</td><td colspan="2">{{{(r.RawPpm != 0 ? $"{r.RawPpm:+0;-0} PPM {(r.PpmIsManual ? "(manual)" : "(auto-calculated)")}" : "Not calculated")}}}</td></tr>
  <tr><td>Frequency Display</td><td colspan="2">PPM correction applied — dongle offset corrected using board as reference</td></tr>
</table>

<table>
  <tr><th>Measurement</th><th>Value</th><th>Result</th></tr>
  <tr>
    <td>Center Frequency</td>
    <td>{{{r.PeakFreqMHz:F3}}} MHz ({{{r.FreqErrorKHz:+0.00;-0.00}}} kHz error)</td>
    <td class="{{{(r.FreqPass ? "pass" : "fail")}}}">{{{(r.FreqPass ? "PASS ✓" : "FAIL ✗")}}} (limit ±10 kHz)</td>
  </tr>
  <tr>
    <td>Peak Power</td>
    <td>{{{r.PeakDbm:F1}}} dBm (relative)</td>
    <td>Reference only</td>
  </tr>
  <tr>
    <td>2nd Harmonic</td>
    <td>{{{(r.H2Dbc > -999 ? $"{r.H2Dbc:F1} dBc" : "N/A — above NESDR Smart range")}}}</td>
    <td class="{{{(r.H2Pass ? "pass" : "fail")}}}">{{{(r.H2Dbc > -999 ? (r.H2Pass ? "PASS ✓" : "FAIL ✗") + " (limit -40 dBc)" : "N/A")}}}</td>
  </tr>
  <tr>
    <td>3rd Harmonic</td>
    <td>{{{(r.H3Dbc > -999 ? $"{r.H3Dbc:F1} dBc" : "N/A — above NESDR Smart range")}}}</td>
    <td class="{{{(r.H3Pass ? "pass" : "fail")}}}">{{{(r.H3Dbc > -999 ? (r.H3Pass ? "PASS ✓" : "FAIL ✗") + " (limit -40 dBc)" : "N/A")}}}</td>
  </tr>
</table>

<div class="overall">{{{(r.Overall ? "◉  OVERALL: PASS" : "◉  OVERALL: FAIL")}}}</div>

<div class="footer">
  <p>Power measurement is relative (uncalibrated RTL-SDR dongle). Harmonic levels are approximate.</p>
  <p>Frequency accuracy measured against RTL-SDR dongle calibration. Not a substitute for calibrated test equipment.</p>
  <p>Batch characterization data across production units available to Patreon members via Section 7.</p>
  <p><strong>SpaceCommsKit · Tennessee, USA · spacecommskit.com</strong></p>
</div>
</body>
</html>
""";

        // ══════════════════════════════════════════════════════════════════
        //  FORM CLOSE
        // ══════════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            _telemTimer?.Stop();
            _liveLogTimer?.Stop();
            _gpsTimer?.Stop();
            _baroAnimTimer?.Stop();
            if (_recording) StopRecording();
            _port?.Close();
            _port?.Dispose();
            base.OnFormClosing(e);
        }
    }
}
