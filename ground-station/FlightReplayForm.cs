// FlightReplayForm.cs
// OpenLST Ground Station — Flight Replay
// Separate form for post-flight analysis and playback.
//
// Opens a .sckflight JSON file, parses all packets, and replays the
// flight with animated Google Maps track, live altitude chart,
// real-time data panels, and event timeline.
//
// Add to project:
//   1. Add this file to the VS2022 project
//   2. Add NuGet: System.Windows.Forms.DataVisualization (if not already present)
//      — or use the built-in chart: right-click References → Add Reference
//        → Assemblies → System.Windows.Forms.DataVisualization
//   3. In MainForm.cs add a "Flight Replay" button or menu item that calls:
//        new FlightReplayForm().Show();
//
// To open from MainForm — add this to BuildGpsTab() button row or a menu:
//   var btnReplay = MkButton("▶ Replay", x, y, 110, Theme.Yellow, Theme.PanelBack);
//   btnReplay.Click += (s,e) => new FlightReplayForm().Show();

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using GMap.NET;
using GMap.NET.MapProviders;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;

namespace OpenLstGroundStation
{
    // ══════════════════════════════════════════════════════════════════════
    //  FLIGHT PACKET MODEL
    // ══════════════════════════════════════════════════════════════════════
    public class FlightPacket
    {
        public double   T          { get; set; }  // elapsed seconds
        public string   Time       { get; set; } = "";
        public double   Lat        { get; set; }
        public double   Lon        { get; set; }
        public double   GpsAlt     { get; set; }
        public int      Sats       { get; set; }
        public int      Fix        { get; set; }
        public double   BaroHpa    { get; set; }
        public double   BaroAlt    { get; set; }
        public double   BaroTemp   { get; set; }
        public double   AscentRate { get; set; }
        public double   MaxAlt     { get; set; }
        public string   Event      { get; set; } = "";

        public bool IsValid => Fix == 1 && (Lat != 0.0 || Lon != 0.0);
    }

    public class FlightSummary
    {
        public int    TotalPackets    { get; set; }
        public double MaxAltitudeM    { get; set; }
        public double FlightDurationS { get; set; }
        public bool   BurstDetected   { get; set; }
        public double LandingLat      { get; set; }
        public double LandingLon      { get; set; }
    }

    public class FlightFile
    {
        public string          FlightId    { get; set; } = "";
        public string          Date        { get; set; } = "";
        public string          LaunchTime  { get; set; } = "";
        public string          Hardware    { get; set; } = "";
        public string          Callsign    { get; set; } = "";
        public List<FlightPacket> Packets  { get; set; } = new();
        public FlightSummary   Summary     { get; set; } = new();
    }

    // ══════════════════════════════════════════════════════════════════════
    //  FLIGHT REPLAY FORM
    // ══════════════════════════════════════════════════════════════════════
    public class FlightReplayForm : Form
    {
        // ── Theme (matches MainForm) ───────────────────────────────────────
        private static class T
        {
            public static readonly Color FormBack  = Color.FromArgb(14, 14, 20);
            public static readonly Color PanelBack = Color.FromArgb(22, 22, 32);
            public static readonly Color HeaderBack= Color.FromArgb(10, 10, 16);
            public static readonly Color LogBack   = Color.FromArgb(8,  8,  12);
            public static readonly Color Border    = Color.FromArgb(50, 50, 80);
            public static readonly Color Green     = Color.LimeGreen;
            public static readonly Color Yellow    = Color.Gold;
            public static readonly Color Red       = Color.OrangeRed;
            public static readonly Color Cyan      = Color.Cyan;
            public static readonly Color White     = Color.WhiteSmoke;
            public static readonly Color Gray      = Color.DimGray;
            public static readonly Color Silver    = Color.Silver;
            public static readonly Color Magenta   = Color.MediumOrchid;
            public static readonly Color ChartBack = Color.FromArgb(8, 12, 18);
            public static readonly Font  Mono      = new Font("Consolas", 9.5f);
            public static readonly Font  MonoBold  = new Font("Consolas", 9.5f, FontStyle.Bold);
            public static readonly Font  Small     = new Font("Consolas", 8.5f);
            public static readonly Font  Large     = new Font("Consolas", 13f, FontStyle.Bold);
            public static readonly Font  Title     = new Font("Consolas", 11f, FontStyle.Bold);
        }

        // ── State ─────────────────────────────────────────────────────────
        private FlightFile?   _flight;
        private int           _playIndex    = 0;
        private bool          _playing      = false;
        private bool          _playbackComplete = false;
        private int           _speedMulti   = 1;
        private System.Windows.Forms.Timer _playTimer = new();

        // ── Controls ──────────────────────────────────────────────────────
        private Label       lblFlightId    = null!;
        private Label       lblFlightDate  = null!;
        private Label       lblFlightHw    = null!;
        private Button      btnOpen        = null!;
        private Button      btnPlay        = null!;
        private Button      btnStop        = null!;
        private Button      btnRewind      = null!;
        private ComboBox    cmbSpeed       = null!;
        private Label       lblTimeCode    = null!;
        private Label       lblPacketNum   = null!;
        private TrackBar    tbScrub        = null!;
        private ProgressBar pbProgress     = null!;

        // Data panels
        private Label lblLat         = null!;
        private Label lblLon         = null!;
        private Label lblGpsAlt      = null!;
        private Label lblBaroAlt     = null!;
        private Label lblPressure    = null!;
        private Label lblTemp        = null!;
        private Label lblAscentRate  = null!;
        private Label lblSats        = null!;
        private Label lblMaxAlt      = null!;
        private Label lblDuration    = null!;
        private Label lblBurstStatus = null!;

        // Map
        private GMapControl  _map          = null!;
        private GMapOverlay  _trackOverlay = null!;
        private GMapOverlay  _markerOverlay= null!;

        // Chart
        private Chart        _chart        = null!;

        // Event timeline
        private RichTextBox  _rtbEvents    = null!;

        // ── Constructor ───────────────────────────────────────────────────
        public FlightReplayForm()
        {
            BuildForm();
            BuildHeader();
            BuildControls();
            BuildDataPanels();
            BuildMapAndChart();
            BuildEventTimeline();
            WireTimer();
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM SHELL
        // ══════════════════════════════════════════════════════════════════
        private void BuildForm()
        {
            Text           = "◈ Flight Replay — OpenLST Ground Station";
            Size           = new Size(1400, 900);
            MinimumSize    = new Size(1100, 750);
            BackColor      = T.FormBack;
            ForeColor      = T.Silver;
            Font           = T.Mono;
            StartPosition  = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
        }

        // ══════════════════════════════════════════════════════════════════
        //  HEADER
        // ══════════════════════════════════════════════════════════════════
        private void BuildHeader()
        {
            var header = new Panel
            {
                Dock      = DockStyle.Top,
                Height    = 52,
                BackColor = T.HeaderBack,
            };

            var lblTitle = Lbl("◈ FLIGHT REPLAY", 10, 14, 220, T.Cyan, T.Large);

            btnOpen = Btn("Open Flight", 240, 12, 120, T.Yellow, T.PanelBack);
            btnOpen.Click += BtnOpen_Click;

            btnRewind = Btn("◀◀", 370, 12, 50, T.Gray, T.PanelBack);
            btnRewind.Click += (s, e) => Rewind();

            btnPlay = Btn("▶ Play", 428, 12, 90, T.Green, T.PanelBack);
            btnPlay.BackColor = Color.FromArgb(20, 60, 20);
            btnPlay.Click += BtnPlay_Click;

            btnStop = Btn("■ Stop", 526, 12, 80, T.Red, T.PanelBack);
            btnStop.BackColor = Color.FromArgb(60, 20, 20);
            btnStop.Click += (s, e) => StopPlayback();

            var lblSpeed = Lbl("Speed:", 616, 17, 56, T.Gray);
            cmbSpeed = new ComboBox
            {
                Location      = new Point(676, 13),
                Size          = new Size(70, 26),
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor     = T.PanelBack,
                ForeColor     = T.White,
                Font          = T.Mono,
            };
            cmbSpeed.Items.AddRange(new object[] { "1x", "5x", "10x", "30x", "60x" });
            cmbSpeed.SelectedIndex = 0;
            cmbSpeed.SelectedIndexChanged += (s, e) =>
            {
                string sel = cmbSpeed.SelectedItem?.ToString() ?? "1x";
                _speedMulti = int.Parse(sel.Replace("x", ""));
                if (_playing) _playTimer.Interval = Math.Max(50, 1000 / _speedMulti);
            };

            lblTimeCode = Lbl("t: --:--:--", 760, 17, 120, T.Cyan, T.MonoBold);
            lblPacketNum = Lbl("Packet: 0/0", 890, 17, 150, T.Gray, T.Small);

            // Flight info labels
            lblFlightId   = Lbl("No flight loaded", 1050, 8,  300, T.Gray, T.Small);
            lblFlightDate = Lbl("",                 1050, 22, 300, T.Gray, T.Small);
            lblFlightHw   = Lbl("",                 1050, 36, 300, T.Gray, T.Small);

            header.Controls.AddRange(new Control[]
            {
                lblTitle, btnOpen, btnRewind, btnPlay, btnStop,
                lblSpeed, cmbSpeed, lblTimeCode, lblPacketNum,
                lblFlightId, lblFlightDate, lblFlightHw,
            });
            Controls.Add(header);
        }

        // ══════════════════════════════════════════════════════════════════
        //  SCRUB BAR + PROGRESS
        // ══════════════════════════════════════════════════════════════════
        private void BuildControls()
        {
            var ctrlPanel = new Panel
            {
                Location  = new Point(0, 52),
                Height    = 32,
                BackColor = Color.FromArgb(12, 12, 18),
                Anchor    = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            SizeChanged += (s, e) =>
                ctrlPanel.SetBounds(0, 52, ClientSize.Width, 32);

            tbScrub = new TrackBar
            {
                Location     = new Point(8, 4),
                Size         = new Size(ClientSize.Width - 16, 24),
                Minimum      = 0,
                Maximum      = 100,
                Value        = 0,
                TickFrequency= 10,
                BackColor    = Color.FromArgb(12, 12, 18),
                Anchor       = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
            };
            tbScrub.Scroll += TbScrub_Scroll;
            SizeChanged += (s, e) =>
                tbScrub.SetBounds(8, 4, ClientSize.Width - 16, 24);

            ctrlPanel.Controls.Add(tbScrub);
            Controls.Add(ctrlPanel);
        }

        // ══════════════════════════════════════════════════════════════════
        //  DATA PANELS  (bottom strip)
        // ══════════════════════════════════════════════════════════════════
        private void BuildDataPanels()
        {
            var strip = new Panel
            {
                Height    = 130,
                BackColor = T.FormBack,
                Anchor    = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            SizeChanged += (s, e) =>
                strip.SetBounds(0, ClientSize.Height - 130, ClientSize.Width, 130);

            int pw = 148, gap = 6, row1 = 8, row2 = 70;
            int x = 8;

            strip.Controls.Add(DataPanel("LATITUDE",     x,              row1, ref lblLat,        "—"));
            strip.Controls.Add(DataPanel("LONGITUDE",    x+(pw+gap),     row1, ref lblLon,        "—"));
            strip.Controls.Add(DataPanel("GPS ALT (m)",  x+(pw+gap)*2,   row1, ref lblGpsAlt,     "—"));
            strip.Controls.Add(DataPanel("BARO ALT (m)", x+(pw+gap)*3,   row1, ref lblBaroAlt,    "—"));
            strip.Controls.Add(DataPanel("PRESSURE hPa", x+(pw+gap)*4,   row1, ref lblPressure,   "—"));
            strip.Controls.Add(DataPanel("BARO TEMP °C", x+(pw+gap)*5,   row1, ref lblTemp,       "—"));
            strip.Controls.Add(DataPanel("ASCENT m/s",   x+(pw+gap)*6,   row1, ref lblAscentRate, "—"));
            strip.Controls.Add(DataPanel("SATELLITES",   x+(pw+gap)*7,   row1, ref lblSats,       "—"));

            // Second row — summary stats
            strip.Controls.Add(DataPanel("MAX ALTITUDE",  x,            row2, ref lblMaxAlt,     "—"));
            strip.Controls.Add(DataPanel("FLIGHT TIME",   x+(pw+gap),   row2, ref lblDuration,   "—"));
            strip.Controls.Add(DataPanel("BURST STATUS",  x+(pw+gap)*2, row2, ref lblBurstStatus,"—"));

            Controls.Add(strip);
            strip.SetBounds(0, ClientSize.Height - 130, ClientSize.Width, 130);
        }

        private Panel DataPanel(string title, int x, int y, ref Label val, string def)
        {
            var p = new Panel
            {
                Location  = new Point(x, y),
                Size      = new Size(148, 56),
                BackColor = T.PanelBack,
            };
            p.Paint += (s, e) =>
            {
                using var pen = new Pen(T.Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, p.Width-1, p.Height-1);
            };
            p.Controls.Add(new Label
            {
                Text = title, Location = new Point(6, 4), Size = new Size(138, 16),
                ForeColor = T.Gray, BackColor = Color.Transparent, Font = T.Small,
            });
            val = new Label
            {
                Text = def, Location = new Point(6, 22), Size = new Size(138, 26),
                ForeColor = T.Green, BackColor = Color.Transparent,
                Font = new Font("Consolas", 14f, FontStyle.Bold),
            };
            p.Controls.Add(val);
            return p;
        }

        // ══════════════════════════════════════════════════════════════════
        //  MAP + CHART  (main body)
        // ══════════════════════════════════════════════════════════════════
        private void BuildMapAndChart()
        {
            int topY   = 84;   // below header + scrub bar
            int bottomH = 130; // data panels height

            // ── Left: Google Map ──────────────────────────────────────────
            _map = new GMapControl
            {
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
                Zoom                  = 13,
                BackColor             = Color.FromArgb(22, 22, 32),
            };

            SizeChanged += (s, e) =>
            {
                int h    = ClientSize.Height - topY - bottomH;
                int mapW = ClientSize.Width / 2 - 4;
                int chartX = ClientSize.Width / 2 + 2;
                int chartW = ClientSize.Width / 2 - 200;
                _map.SetBounds(0, topY, mapW, h);
                _chart.SetBounds(chartX, topY, chartW, h);
                _rtbEvents.SetBounds(ClientSize.Width - 196, topY, 194, h);
            };

            _map.Position = new PointLatLng(36.058952, -87.384060);
            GMaps.Instance.Mode = AccessMode.ServerAndCache;
            GMaps.Instance.UseMemoryCache = true;
            try   { _map.MapProvider = GMapProviders.GoogleMap; }
            catch { _map.MapProvider = GMapProviders.OpenStreetMap; }

            _trackOverlay  = new GMapOverlay("track");
            _markerOverlay = new GMapOverlay("markers");
            _map.Overlays.Add(_trackOverlay);
            _map.Overlays.Add(_markerOverlay);
            Controls.Add(_map);

            // ── Right: Altitude Chart ─────────────────────────────────────
            _chart = new Chart
            {
                BackColor      = T.ChartBack,
                BorderlineColor= T.Border,
                Palette        = ChartColorPalette.None,
            };

            var ca = new ChartArea("AltArea")
            {
                BackColor        = T.ChartBack,
                BorderColor      = T.Border,
                BorderDashStyle  = ChartDashStyle.Solid,
            };

            // X axis — elapsed time
            ca.AxisX.Title          = "TIME (s)";
            ca.AxisX.TitleForeColor = T.Gray;
            ca.AxisX.LabelStyle.ForeColor = T.Gray;
            ca.AxisX.LabelStyle.Font      = T.Small;
            ca.AxisX.LineColor      = T.Border;
            ca.AxisX.MajorGrid.LineColor  = Color.FromArgb(30, 50, 60);
            ca.AxisX.MajorGrid.LineDashStyle = ChartDashStyle.Dot;

            // Y axis — altitude
            ca.AxisY.Title          = "ALTITUDE (m)";
            ca.AxisY.TitleForeColor = T.Gray;
            ca.AxisY.LabelStyle.ForeColor = T.Gray;
            ca.AxisY.LabelStyle.Font      = T.Small;
            ca.AxisY.LineColor      = T.Border;
            ca.AxisY.MajorGrid.LineColor  = Color.FromArgb(30, 50, 60);
            ca.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dot;

            // Secondary Y axis — temperature
            ca.AxisY2.Enabled       = AxisEnabled.True;
            ca.AxisY2.Title         = "TEMP (°C)";
            ca.AxisY2.TitleForeColor= T.Gray;
            ca.AxisY2.LabelStyle.ForeColor = T.Gray;
            ca.AxisY2.LabelStyle.Font      = T.Small;
            ca.AxisY2.LineColor     = T.Border;
            ca.AxisY2.MajorGrid.Enabled    = false;

            _chart.ChartAreas.Add(ca);

            // Legend
            var legend = new Legend
            {
                BackColor = T.ChartBack,
                ForeColor = T.Gray,
                Font      = T.Small,
                Docking   = Docking.Bottom,
            };
            _chart.Legends.Add(legend);

            // Series: Baro Altitude
            var serBaro = new Series("BARO ALT")
            {
                ChartType       = SeriesChartType.Line,
                Color           = Color.Cyan,
                BorderWidth     = 2,
                ChartArea       = "AltArea",
                Legend          = legend.Name,
                XValueType      = ChartValueType.Double,
                YValueType      = ChartValueType.Double,
            };

            // Series: GPS Altitude
            var serGps = new Series("GPS ALT")
            {
                ChartType       = SeriesChartType.Line,
                Color           = Color.FromArgb(80, 140, 220),
                BorderWidth     = 1,
                BorderDashStyle = ChartDashStyle.Dash,
                ChartArea       = "AltArea",
                Legend          = legend.Name,
                XValueType      = ChartValueType.Double,
                YValueType      = ChartValueType.Double,
            };

            // Series: Temperature (secondary axis)
            var serTemp = new Series("TEMP °C")
            {
                ChartType       = SeriesChartType.Line,
                Color           = Color.FromArgb(255, 140, 0),
                BorderWidth     = 1,
                BorderDashStyle = ChartDashStyle.Dot,
                ChartArea       = "AltArea",
                Legend          = legend.Name,
                YAxisType       = AxisType.Secondary,
                XValueType      = ChartValueType.Double,
                YValueType      = ChartValueType.Double,
            };

            _chart.Series.Add(serBaro);
            _chart.Series.Add(serGps);
            _chart.Series.Add(serTemp);

            // Title
            _chart.Titles.Add(new Title
            {
                Text      = "ALTITUDE / TEMPERATURE PROFILE",
                ForeColor = T.Cyan,
                Font      = T.MonoBold,
                Docking   = Docking.Top,
            });

            Controls.Add(_chart);

            // ── Event Timeline ────────────────────────────────────────────
            _rtbEvents = new RichTextBox
            {
                BackColor   = T.LogBack,
                ForeColor   = T.Silver,
                Font        = T.Small,
                BorderStyle = BorderStyle.None,
                ReadOnly    = true,
                ScrollBars  = RichTextBoxScrollBars.Vertical,
                WordWrap    = false,
            };
            Controls.Add(_rtbEvents);

            // Trigger initial layout
            OnSizeChanged(EventArgs.Empty);
        }

        // ══════════════════════════════════════════════════════════════════
        //  EVENT TIMELINE PANEL
        // ══════════════════════════════════════════════════════════════════
        private void BuildEventTimeline()
        {
            EventLog("◈ EVENT TIMELINE", Color.Cyan);
            EventLog("──────────────────", Color.FromArgb(40, 60, 80));
            EventLog("Open a .sckflight", Color.DimGray);
            EventLog("file to begin.", Color.DimGray);
        }

        private void EventLog(string msg, Color color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => EventLog(msg, color))); return; }
            _rtbEvents.SelectionStart  = _rtbEvents.TextLength;
            _rtbEvents.SelectionLength = 0;
            _rtbEvents.SelectionColor  = color;
            _rtbEvents.AppendText(msg + Environment.NewLine);
            _rtbEvents.ScrollToCaret();
        }

        // ══════════════════════════════════════════════════════════════════
        //  OPEN FLIGHT FILE
        // ══════════════════════════════════════════════════════════════════
        private void BtnOpen_Click(object? sender, EventArgs e)
        {
            string flightsPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Flights");

            using var dlg = new OpenFileDialog
            {
                Filter      = "SCK Flight files (*.sckflight)|*.sckflight|All files (*.*)|*.*",
                Title       = "Open Flight Recording",
                InitialDirectory = Directory.Exists(flightsPath) ? flightsPath : "",
            };

            if (dlg.ShowDialog() != DialogResult.OK) return;
            LoadFlight(dlg.FileName);
        }

        private void LoadFlight(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                _flight = ParseFlightFile(json);

                if (_flight == null || _flight.Packets.Count == 0)
                {
                    MessageBox.Show("No packets found in flight file.", "Load Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Update header info
                lblFlightId.Text   = $"ID: {_flight.FlightId}";
                lblFlightDate.Text = $"Date: {_flight.Date}  Launch: {_flight.LaunchTime}";
                lblFlightHw.Text   = $"HW: {_flight.Hardware}";

                // Reset playback
                _playIndex = 0;
                _playbackComplete = false;
                tbScrub.Minimum = 0;
                tbScrub.Maximum = Math.Max(1, _flight.Packets.Count - 1);
                tbScrub.Value   = 0;

                // Clear chart series
                _chart.Series["BARO ALT"].Points.Clear();
                _chart.Series["GPS ALT"].Points.Clear();
                _chart.Series["TEMP °C"].Points.Clear();

                // Pre-load all data into chart (full flight visible immediately)
                foreach (var pkt in _flight.Packets)
                {
                    _chart.Series["BARO ALT"].Points.AddXY(pkt.T, pkt.BaroAlt);
                    _chart.Series["GPS ALT"].Points.AddXY(pkt.T, pkt.GpsAlt);
                    _chart.Series["TEMP °C"].Points.AddXY(pkt.T, pkt.BaroTemp);
                }

                // Clear map
                _trackOverlay.Routes.Clear();
                _markerOverlay.Markers.Clear();

                // Add launch pin
                var firstValid = _flight.Packets.FirstOrDefault(p => p.IsValid);
                if (firstValid != null)
                {
                    var launchPos = new PointLatLng(firstValid.Lat, firstValid.Lon);
                    var launchMarker = new GMarkerGoogle(launchPos, GMarkerGoogleType.green_dot);
                    launchMarker.ToolTipText = $"LAUNCH\n{_flight.LaunchTime}";
                    launchMarker.ToolTipMode = MarkerTooltipMode.OnMouseOver;
                    _markerOverlay.Markers.Add(launchMarker);
                    _map.Position = launchPos;
                }

                // Add apogee pin
                var apogee = _flight.Packets.OrderByDescending(p => p.BaroAlt).FirstOrDefault();
                if (apogee != null && apogee.IsValid)
                {
                    var apogeePos = new PointLatLng(apogee.Lat, apogee.Lon);
                    var apogeeMarker = new GMarkerGoogle(apogeePos, GMarkerGoogleType.yellow_dot);
                    apogeeMarker.ToolTipText =
                        $"APOGEE\n{apogee.BaroAlt:F0}m @ t={apogee.T:F0}s";
                    apogeeMarker.ToolTipMode = MarkerTooltipMode.OnMouseOver;
                    _markerOverlay.Markers.Add(apogeeMarker);
                }

                // Add landing pin
                var lastValid = _flight.Packets.LastOrDefault(p => p.IsValid);
                if (lastValid != null && lastValid != firstValid)
                {
                    var landPos = new PointLatLng(lastValid.Lat, lastValid.Lon);
                    var landMarker = new GMarkerGoogle(landPos, GMarkerGoogleType.red_dot);
                    landMarker.ToolTipText = "LANDING";
                    landMarker.ToolTipMode = MarkerTooltipMode.OnMouseOver;
                    _markerOverlay.Markers.Add(landMarker);
                }

                // Draw complete flight track faintly
                var allPoints = _flight.Packets
                    .Where(p => p.IsValid)
                    .Select(p => new PointLatLng(p.Lat, p.Lon))
                    .ToList();
                if (allPoints.Count >= 2)
                {
                    var fullRoute = new GMapRoute(allPoints, "full_track")
                    {
                        Stroke = new System.Drawing.Pen(
                            Color.FromArgb(60, 0, 207, 255), 1)
                    };
                    _trackOverlay.Routes.Add(fullRoute);
                }

                _map.ZoomAndCenterMarkers("markers");
                _map.Refresh();

                // Build event timeline
                BuildEventLog();

                // Update summary panels
                UpdateSummaryPanels();

                // Reset position display
                UpdateDisplayForPacket(0);

                lblTimeCode.Text  = "t: 00:00:00";
                lblPacketNum.Text = $"Packet: 1/{_flight.Packets.Count}";
                btnPlay.Enabled   = true;

                Text = $"◈ Flight Replay — {_flight.FlightId}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load flight file:\n{ex.Message}",
                    "Load Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  FLIGHT FILE PARSER
        // ══════════════════════════════════════════════════════════════════
        private FlightFile? ParseFlightFile(string json)
        {
            var result = new FlightFile();
            try
            {
                // Handle incomplete JSON from SD black box recorder
                // The file ends with the last packet but missing ]} closing
                // Detect by checking if "packets":[ array is properly closed
                string repaired = json.TrimEnd();

                // File is incomplete if it doesn't contain the summary block
                // A complete file ends with ...,"event":""}]} or similar
                // An incomplete file ends with ...,"event":""}  (no ]})
                bool hasClosingArray = repaired.EndsWith("]}") ||
                                       repaired.EndsWith("]}")  ||
                                       repaired.Contains("\"summary\"");

                if (!hasClosingArray)
                {
                    // Remove trailing comma if present
                    if (repaired.EndsWith(","))
                        repaired = repaired.TrimEnd(',', '\n', '\r', ' ');
                    // Close the packets array and root object
                    if (!repaired.EndsWith("]"))
                        repaired += "\n]";
                    repaired += "\n}";
                }
                using var doc = JsonDocument.Parse(repaired);
                var root = doc.RootElement;

                if (root.TryGetProperty("flight_id",   out var fid))  result.FlightId   = fid.GetString()  ?? "";
                if (root.TryGetProperty("date",        out var d))    result.Date       = d.GetString()    ?? "";
                if (root.TryGetProperty("launch_time", out var lt))   result.LaunchTime = lt.GetString()   ?? "";
                if (root.TryGetProperty("hardware",    out var hw))   result.Hardware   = hw.GetString()   ?? "";

                if (root.TryGetProperty("packets", out var pkts) &&
                    pkts.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in pkts.EnumerateArray())
                    {
                        var pkt = new FlightPacket();
                        if (el.TryGetProperty("t",           out var t))   pkt.T          = t.GetDouble();
                        if (el.TryGetProperty("time",        out var tm))  pkt.Time       = tm.GetString() ?? "";
                        if (el.TryGetProperty("lat",         out var la))  pkt.Lat        = la.GetDouble();
                        if (el.TryGetProperty("lon",         out var lo))  pkt.Lon        = lo.GetDouble();
                        if (el.TryGetProperty("gps_alt",     out var ga))  pkt.GpsAlt     = ga.GetDouble();
                        if (el.TryGetProperty("sats",        out var sa))  pkt.Sats       = sa.GetInt32();
                        if (el.TryGetProperty("fix",         out var fx))  pkt.Fix        = fx.GetInt32();
                        if (el.TryGetProperty("baro_hpa",    out var bh))  pkt.BaroHpa    = bh.GetDouble();
                        if (el.TryGetProperty("baro_alt",    out var ba))  pkt.BaroAlt    = ba.GetDouble();
                        if (el.TryGetProperty("baro_temp",   out var bt))  pkt.BaroTemp   = bt.GetDouble();
                        if (el.TryGetProperty("ascent_rate", out var ar))  pkt.AscentRate = ar.GetDouble();
                        if (el.TryGetProperty("max_alt",     out var ma))  pkt.MaxAlt     = ma.GetDouble();
                        if (el.TryGetProperty("event",       out var ev))  pkt.Event      = ev.GetString() ?? "";
                        result.Packets.Add(pkt);
                    }
                }

                if (root.TryGetProperty("summary", out var sum))
                {
                    if (sum.TryGetProperty("total_packets",    out var tp)) result.Summary.TotalPackets    = tp.GetInt32();
                    if (sum.TryGetProperty("max_altitude_m",   out var ma)) result.Summary.MaxAltitudeM    = ma.GetDouble();
                    if (sum.TryGetProperty("flight_duration_s",out var fd)) result.Summary.FlightDurationS = fd.GetDouble();
                    if (sum.TryGetProperty("burst_detected",   out var bd)) result.Summary.BurstDetected   = bd.GetBoolean();
                    if (sum.TryGetProperty("landing_lat",      out var ll)) result.Summary.LandingLat      = ll.GetDouble();
                    if (sum.TryGetProperty("landing_lon",      out var lo)) result.Summary.LandingLon      = lo.GetDouble();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"JSON parse error: {ex.Message}", "Parse Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        //  EVENT LOG BUILDER
        // ══════════════════════════════════════════════════════════════════
        private void BuildEventLog()
        {
            if (_flight == null) return;
            _rtbEvents.Clear();

            EventLog("◈ EVENT TIMELINE", Color.Cyan);
            EventLog($"  {_flight.FlightId}", Color.FromArgb(0, 180, 180));
            EventLog("──────────────────", Color.FromArgb(40, 60, 80));

            var packets = _flight.Packets;
            if (packets.Count == 0) return;

            // Launch
            var first = packets.FirstOrDefault(p => p.IsValid);
            if (first != null)
                EventLog($"t+{first.T:F0}s  ● LAUNCH", Color.LimeGreen);

            // Apogee
            var apogee = packets.OrderByDescending(p => p.BaroAlt).First();
            EventLog($"t+{apogee.T:F0}s  ● APOGEE {apogee.BaroAlt:F0}m", Color.Cyan);

            // Burst detection — first point where ascent rate goes strongly negative
            // after reaching significant altitude
            double maxAlt = packets.Max(p => p.BaroAlt);
            if (maxAlt > 500)
            {
                var burst = packets.FirstOrDefault(p =>
                    p.BaroAlt > maxAlt * 0.8 && p.AscentRate < -3.0);
                if (burst != null)
                    EventLog($"t+{burst.T:F0}s  ⚡ BURST", Color.OrangeRed);
            }

            // Landing
            var last = packets.LastOrDefault(p => p.IsValid);
            if (last != null && last != first)
                EventLog($"t+{last.T:F0}s  ● LANDING", Color.Gold);

            EventLog("──────────────────", Color.FromArgb(40, 60, 80));

            // Stats
            EventLog($"Packets: {packets.Count}", Color.DimGray);
            EventLog($"Max alt: {maxAlt:F0}m", Color.DimGray);
            TimeSpan dur = TimeSpan.FromSeconds(_flight.Summary.FlightDurationS);
            EventLog($"Duration: {dur:hh\\:mm\\:ss}", Color.DimGray);
            EventLog($"Min temp: {packets.Min(p => p.BaroTemp):F1}°C", Color.FromArgb(0, 160, 220));
            EventLog($"Max rate: {packets.Max(p => p.AscentRate):F1} m/s", Color.DimGray);
        }

        // ══════════════════════════════════════════════════════════════════
        //  SUMMARY PANELS UPDATE
        // ══════════════════════════════════════════════════════════════════
        private void UpdateSummaryPanels()
        {
            if (_flight == null) return;
            double maxAlt = _flight.Packets.Any() ? _flight.Packets.Max(p => p.BaroAlt) : 0;
            lblMaxAlt.Text     = $"{maxAlt:F0}m";
            lblMaxAlt.ForeColor = T.Cyan;
            TimeSpan dur = TimeSpan.FromSeconds(_flight.Summary.FlightDurationS);
            lblDuration.Text   = $"{dur:hh\\:mm\\:ss}";
            lblBurstStatus.Text = _flight.Summary.BurstDetected ? "BURST ⚡" : "NO BURST";
            lblBurstStatus.ForeColor = _flight.Summary.BurstDetected ? T.Red : T.Green;
        }

        // ══════════════════════════════════════════════════════════════════
        //  PLAYBACK ENGINE
        // ══════════════════════════════════════════════════════════════════
        private void WireTimer()
        {
            _playTimer.Interval = 1000;
            _playTimer.Tick    += PlayTimer_Tick;
        }

        private void BtnPlay_Click(object? sender, EventArgs e)
        {
            if (_flight == null) return;
            if (_playing) { PausePlayback(); return; }
            StartPlayback();
        }

        private void StartPlayback()
        {
            if (_flight == null || _flight.Packets.Count == 0) return;
            _playing = true;
            btnPlay.Text      = "⏸ Pause";
            btnPlay.ForeColor = T.Yellow;
            string sel = cmbSpeed.SelectedItem?.ToString() ?? "1x";
            _speedMulti = int.Parse(sel.Replace("x", ""));
            _playTimer.Interval = Math.Max(50, 1000 / _speedMulti);
            _playTimer.Start();

            // Clear the animated track — will redraw as playback runs
            _trackOverlay.Routes.Clear();
        }

        private void PausePlayback()
        {
            _playing = false;
            _playTimer.Stop();
            btnPlay.Text      = "▶ Play";
            btnPlay.ForeColor = T.Green;
        }

        private void StopPlayback()
        {
            PausePlayback();
            _playIndex = 0;
            _playbackComplete = false;
            tbScrub.Value = 0;
            if (_flight != null)
            {
                UpdateDisplayForPacket(0);
                lblTimeCode.Text  = "t: 00:00:00";
                lblPacketNum.Text = $"Packet: 1/{_flight.Packets.Count}";
                _trackOverlay.Routes.Clear();
                _map.Refresh();
            }
        }

        private void Rewind()
        {
            StopPlayback();
        }

        private void PlayTimer_Tick(object? sender, EventArgs e)
        {
            if (_flight == null || _playIndex >= _flight.Packets.Count)
            {
                PausePlayback();
                if (!_playbackComplete)
                {
                    _playbackComplete = true;
                    EventLog("── PLAYBACK COMPLETE ──", Color.Cyan);
                }
                return;
            }

            UpdateDisplayForPacket(_playIndex);
            _playIndex++;

            // Update scrub bar
            if (tbScrub.Maximum > 0)
                tbScrub.Value = Math.Min(_playIndex, tbScrub.Maximum);
        }

        private void TbScrub_Scroll(object? sender, EventArgs e)
        {
            if (_flight == null) return;
            _playIndex = tbScrub.Value;
            UpdateDisplayForPacket(_playIndex);
        }

        // ══════════════════════════════════════════════════════════════════
        //  UPDATE DISPLAY FOR PACKET INDEX
        // ══════════════════════════════════════════════════════════════════
        private void UpdateDisplayForPacket(int index)
        {
            if (_flight == null || index >= _flight.Packets.Count) return;
            var pkt = _flight.Packets[index];

            // ── Data panels ───────────────────────────────────────────────
            lblLat.Text        = pkt.IsValid ? $"{pkt.Lat:F6}°" : "NO FIX";
            lblLon.Text        = pkt.IsValid ? $"{pkt.Lon:F6}°" : "NO FIX";
            lblGpsAlt.Text     = $"{pkt.GpsAlt:F1}";
            lblBaroAlt.Text    = $"{pkt.BaroAlt:F1}";
            lblPressure.Text   = $"{pkt.BaroHpa:F2}";
            lblTemp.Text       = $"{pkt.BaroTemp:F2}";
            lblSats.Text       = pkt.Sats.ToString();

            // Ascent rate with color
            lblAscentRate.Text      = $"{pkt.AscentRate:+0.0;-0.0;0.0}";
            lblAscentRate.ForeColor = pkt.AscentRate > 0.5 ? T.Green :
                                      (pkt.AscentRate < -0.5 ? T.Red : T.Gray);

            // Time display
            TimeSpan elapsed = TimeSpan.FromSeconds(pkt.T);
            lblTimeCode.Text  = $"t: {elapsed:hh\\:mm\\:ss}";
            lblPacketNum.Text = $"Packet: {index+1}/{_flight.Packets.Count}";

            // Temperature color
            lblTemp.ForeColor = pkt.BaroTemp < 0
                ? Color.FromArgb(0, 180, 255)
                : T.Green;

            // ── Animated track up to current index ────────────────────────
            var trackPts = _flight.Packets
                .Take(index + 1)
                .Where(p => p.IsValid)
                .Select(p => new PointLatLng(p.Lat, p.Lon))
                .ToList();

            _trackOverlay.Routes.Clear();
            if (trackPts.Count >= 2)
            {
                var route = new GMapRoute(trackPts, "anim_track")
                {
                    Stroke = new System.Drawing.Pen(
                        Color.FromArgb(220, 0, 207, 255), 2)
                };
                _trackOverlay.Routes.Add(route);
            }

            // ── Current position marker ───────────────────────────────────
            // Remove only the current position marker (keep launch/apogee/landing)
            var toRemove = _markerOverlay.Markers
                .Where(m => m.Tag?.ToString() == "current")
                .ToList();
            foreach (var m in toRemove)
                _markerOverlay.Markers.Remove(m);

            if (pkt.IsValid)
            {
                var pos = new PointLatLng(pkt.Lat, pkt.Lon);
                var cur = new GMarkerCross(pos)
                {
                    Pen = new System.Drawing.Pen(Color.Cyan, 2),
                    Tag = "current",
                };
                _markerOverlay.Markers.Add(cur);
                _map.Position = pos;
            }
            _map.Refresh();

            // ── Chart — highlight current time with vertical line ─────────
            // Remove previous strip line
            _chart.ChartAreas["AltArea"].AxisX.StripLines.Clear();
            var strip = new StripLine
            {
                IntervalOffset   = pkt.T,
                StripWidth       = 0.5,
                BackColor        = Color.FromArgb(40, 0, 207, 255),
                BorderColor      = Color.FromArgb(100, 0, 207, 255),
                BorderWidth      = 1,
            };
            _chart.ChartAreas["AltArea"].AxisX.StripLines.Add(strip);
        }

        // ══════════════════════════════════════════════════════════════════
        //  FORM CLOSE CLEANUP
        // ══════════════════════════════════════════════════════════════════
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _playTimer.Stop();
            _playTimer.Dispose();
            base.OnFormClosing(e);
        }

        // ══════════════════════════════════════════════════════════════════
        //  CONTROL FACTORY HELPERS
        // ══════════════════════════════════════════════════════════════════
        private static Label Lbl(string text, int x, int y, int w,
            Color? color = null, Font? font = null)
            => new Label
            {
                Text      = text,
                Location  = new Point(x, y),
                Size      = new Size(w, 22),
                ForeColor = color ?? T.Gray,
                BackColor = Color.Transparent,
                Font      = font ?? T.Mono,
            };

        private static Button Btn(string text, int x, int y, int w,
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
                Font      = T.MonoBold,
                Cursor    = Cursors.Hand,
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(50, 50, 80);
            return btn;
        }
    }
}
