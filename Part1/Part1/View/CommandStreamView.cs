using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Part1.Models;
using Part1.Services;

namespace Part1.Views
{
    /// <summary>
    /// Real-Time Command Stream dashboard: live device list with search + filters,
    /// manual override buttons with Undo, "Suggested Actions" panel, a timeline
    /// chart (from the SortedDictionary history), command history and event log.
    /// It refreshes from SNAPSHOTS on a UI timer; all heavy work stays on background threads.
    /// Ref: Microsoft (n.d.) Windows Forms Timer / ListView.
    /// </summary>
    public class CommandStreamView : UserControl
    {
        private readonly TelemetryEngine _engine;
        private readonly DeviceSimulator _simulator;
        private readonly Timer _refreshTimer = new Timer { Interval = 500 };
        private readonly Timer _searchDebounce = new Timer { Interval = 1200 };
        private Font _boldFont;

        private TextBox txtSearch;
        private ComboBox cboCategory, cboAlert;
        private ListView lvDevices;
        private Label lblStats, lblSelected;
        private Button btnOn, btnOff, btnUndo, btnShutdownAll;
        private ListBox lstInsights, lstHistory, lstLog;
        private Panel pnlChart;
        private GroupBox grpChart;

        private string _selectedMac;
        private bool _refreshing;
        private List<LiveDevice> _lastDevices = new List<LiveDevice>();
        private List<Insight> _insights = new List<Insight>();
        private List<KeyValuePair<DateTime, double>> _chartData = new List<KeyValuePair<DateTime, double>>();
        private string _chartMetric = "";

        public CommandStreamView(TelemetryEngine engine, DeviceSimulator simulator)
        {
            _engine = engine;
            _simulator = simulator;
            BuildUi();

            _refreshTimer.Tick += (s, e) => RefreshView();
            _searchDebounce.Tick += OnSearchDebounce;

            // Only poll while visible. State lives in the engine, so switching
            // pages never loses data and costs no CPU while hidden.
            VisibleChanged += (s, e) =>
            {
                _refreshTimer.Enabled = Visible;
                if (Visible) RefreshView();
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _refreshTimer.Dispose();
                _searchDebounce.Dispose();
                if (_boldFont != null) _boldFont.Dispose();
            }
            base.Dispose(disposing);
        }

        // =================================================================== UI BUILD
        private void BuildUi()
        {
            Dock = DockStyle.Fill;
            _boldFont = new Font(Font, FontStyle.Bold);

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));

            lblStats = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Color.FromArgb(32, 40, 58),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Padding = new Padding(8, 0, 0, 0)
            };
            root.Controls.Add(lblStats, 0, 0);

            var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            main.Controls.Add(BuildLeftPanel(), 0, 0);
            main.Controls.Add(BuildRightPanel(), 1, 0);
            root.Controls.Add(main, 0, 1);

            root.Controls.Add(BuildBottomPanel(), 0, 2);
            Controls.Add(root);
        }

        private Control BuildLeftPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var filters = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                Padding = new Padding(4, 6, 0, 0)
            };

            txtSearch = new TextBox { Width = 170 };
            txtSearch.TextChanged += (s, e) =>
            {
                _searchDebounce.Stop(); _searchDebounce.Start();   // wait until typing pauses
                RefreshView();
            };

            cboCategory = new ComboBox { Width = 120, DropDownStyle = ComboBoxStyle.DropDownList };
            cboCategory.Items.Add("All categories");
            cboCategory.SelectedIndex = 0;
            cboCategory.SelectedIndexChanged += (s, e) => RefreshView();

            cboAlert = new ComboBox { Width = 100, DropDownStyle = ComboBoxStyle.DropDownList };
            cboAlert.Items.AddRange(new object[] { "All states", "Any alert", "Critical", "Warning", "Normal", "Offline" });
            cboAlert.SelectedIndex = 0;
            cboAlert.SelectedIndexChanged += (s, e) => RefreshView();

            filters.Controls.Add(new Label { Text = "Search:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            filters.Controls.Add(txtSearch);
            filters.Controls.Add(new Label { Text = "Category:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            filters.Controls.Add(cboCategory);
            filters.Controls.Add(new Label { Text = "State:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) });
            filters.Controls.Add(cboAlert);
            panel.Controls.Add(filters, 0, 0);

            lvDevices = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                MultiSelect = false
            };
            lvDevices.Columns.Add("Device", 150);
            lvDevices.Columns.Add("Category", 85);
            lvDevices.Columns.Add("Metric", 80);
            lvDevices.Columns.Add("Value", 80);
            lvDevices.Columns.Add("Status", 70);
            lvDevices.Columns.Add("Output", 55);
            lvDevices.Columns.Add("Last seen", 70);
            lvDevices.Columns.Add("MAC", 125);
            EnableDoubleBuffer(lvDevices);
            lvDevices.SelectedIndexChanged += (s, e) =>
            {
                if (_refreshing) return;
                if (lvDevices.SelectedItems.Count > 0)
                {
                    _selectedMac = (string)lvDevices.SelectedItems[0].Tag;
                    UpdateDetails();
                }
            };
            panel.Controls.Add(lvDevices, 0, 1);
            return panel;
        }

        private Control BuildRightPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            // ---- manual override commands ----
            var grpCmd = new GroupBox { Text = "Manual override commands", Dock = DockStyle.Fill };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true };

            lblSelected = new Label { Width = 450, Height = 22, Text = "Select a device from the list." };
            flow.Controls.Add(lblSelected);
            flow.SetFlowBreak(lblSelected, true);

            btnOn = MakeButton("Turn ON", (s, e) => SendCommand(true));
            btnOff = MakeButton("Turn OFF", (s, e) => SendCommand(false));
            btnUndo = MakeButton("Undo last command (0)", (s, e) => UndoCommand());
            btnShutdownAll = MakeButton("SHUT DOWN ALL", (s, e) => ShutdownAll());
            btnShutdownAll.BackColor = Color.FromArgb(220, 80, 80);
            btnShutdownAll.ForeColor = Color.White;
            btnShutdownAll.FlatStyle = FlatStyle.Flat;

            flow.Controls.AddRange(new Control[] { btnOn, btnOff, btnUndo, btnShutdownAll });
            flow.SetFlowBreak(btnShutdownAll, true);

            // simulator controls: send a fault through the real API for demos
            flow.Controls.Add(new Label { Text = "Demo:", AutoSize = true, Padding = new Padding(0, 8, 0, 0) });
            flow.Controls.Add(MakeButton("Moisture drop", (s, e) => _simulator.InjectMoistureDrop()));
            flow.Controls.Add(MakeButton("Power spike", (s, e) => _simulator.InjectPowerSpike()));
            flow.Controls.Add(MakeButton("Disconnect node", (s, e) => _simulator.SimulateDisconnect()));
            grpCmd.Controls.Add(flow);
            panel.Controls.Add(grpCmd, 0, 0);

            // ---- suggested actions ----
            var grpIns = new GroupBox { Text = "Suggested Actions / Automated Insights (double-click to apply)", Dock = DockStyle.Fill };
            lstInsights = new ListBox { Dock = DockStyle.Fill, HorizontalScrollbar = true, IntegralHeight = false };
            lstInsights.DoubleClick += OnInsightDoubleClick;
            grpIns.Controls.Add(lstInsights);
            panel.Controls.Add(grpIns, 0, 1);

            // ---- timeline chart ----
            grpChart = new GroupBox { Text = "Timeline", Dock = DockStyle.Fill };
            pnlChart = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
            EnableDoubleBuffer(pnlChart);
            pnlChart.Paint += PaintChart;
            pnlChart.Resize += (s, e) => pnlChart.Invalidate();
            grpChart.Controls.Add(pnlChart);
            panel.Controls.Add(grpChart, 0, 2);
            return panel;
        }

        private Control BuildBottomPanel()
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            var g1 = new GroupBox { Text = "Command history (newest first)", Dock = DockStyle.Fill };
            lstHistory = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            g1.Controls.Add(lstHistory);

            var g2 = new GroupBox { Text = "Event stream", Dock = DockStyle.Fill };
            lstLog = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
            g2.Controls.Add(lstLog);

            panel.Controls.Add(g1, 0, 0);
            panel.Controls.Add(g2, 1, 0);
            return panel;
        }

        private static Button MakeButton(string text, EventHandler onClick)
        {
            var b = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(4, 2, 4, 2) };
            b.Click += onClick;
            return b;
        }

        // Stops ListView / Panel flicker (DoubleBuffered is protected, so use reflection).
        private static void EnableDoubleBuffer(Control c)
        {
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
                           .SetValue(c, true, null);
        }

        // ================================================================== REFRESH
        private void RefreshView()
        {
            if (_refreshing) return;     // ignore events fired by our own updates
            _refreshing = true;
            try
            {
                EngineStats st = _engine.GetStats();
                _lastDevices = _engine.GetDevices();
                _insights = _engine.GetInsights();

                lblStats.Text =
                    $"Devices: {_lastDevices.Count}   Online: {_lastDevices.Count(d => d.IsOnline)}   " +
                    $"Critical: {_lastDevices.Count(d => d.IsOnline && d.Severity == AlertSeverity.Critical)}   " +
                    $"Offline: {_lastDevices.Count(d => !d.IsOnline)}   |   " +
                    $"FIFO queue: {st.StandardQueueDepth}   Priority queue: {st.PriorityQueueDepth}   " +
                    $"Received: {st.Received}   Processed: {st.Processed}   Rejected: {st.Rejected}   " +
                    $"Active alerts: {st.ActiveAlertSetSize}";

                RebuildCategoryList();
                var flagged = new HashSet<string>(
                    _insights.Where(i => i.MacAddress != null).Select(i => i.MacAddress),
                    StringComparer.OrdinalIgnoreCase);
                PopulateDevices(ApplyFilters(_lastDevices), flagged);

                SetItems(lstInsights, _insights.Select(i =>
                    "[" + i.Kind + "] " + i.Title + " - " + i.Detail).ToList());
                SetItems(lstHistory, _engine.GetCommandHistory());
                SetItems(lstLog, _engine.GetEventLog());

                btnUndo.Text = "Undo last command (" + st.UndoDepth + ")";
                btnUndo.Enabled = st.UndoDepth > 0;

                UpdateDetails();
            }
            finally { _refreshing = false; }
        }

        /// <summary>Search text + category + alert-state filter, problem devices first.</summary>
        private List<LiveDevice> ApplyFilters(List<LiveDevice> all)
        {
            string term = txtSearch.Text.Trim();
            string cat = cboCategory.SelectedItem as string;
            string state = cboAlert.SelectedItem as string;

            IEnumerable<LiveDevice> q = all;
            if (term.Length > 0)
                q = q.Where(d => d.Name.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 d.MacAddress.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 d.Metric.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0);
            if (cat != null && cat != "All categories")
                q = q.Where(d => d.Category == cat);

            switch (state)
            {
                case "Any alert": q = q.Where(d => !d.IsOnline || d.Severity != AlertSeverity.Normal); break;
                case "Critical": q = q.Where(d => d.IsOnline && d.Severity == AlertSeverity.Critical); break;
                case "Warning": q = q.Where(d => d.IsOnline && d.Severity == AlertSeverity.Warning); break;
                case "Normal": q = q.Where(d => d.IsOnline && d.Severity == AlertSeverity.Normal); break;
                case "Offline": q = q.Where(d => !d.IsOnline); break;
            }

            return q.OrderByDescending(d => d.IsOnline ? (int)d.Severity : 3)   // offline first
                    .ThenBy(d => d.Name).ToList();
        }

        private void RebuildCategoryList()
        {
            var cats = _lastDevices.Select(d => d.Category).Distinct().OrderBy(c => c).ToList();
            if (cboCategory.Items.Count == cats.Count + 1) return;

            object selected = cboCategory.SelectedItem;
            cboCategory.Items.Clear();
            cboCategory.Items.Add("All categories");
            foreach (string c in cats) cboCategory.Items.Add(c);
            cboCategory.SelectedItem = selected ?? "All categories";
            if (cboCategory.SelectedIndex < 0) cboCategory.SelectedIndex = 0;
        }

        /// <summary>Updates rows in place when the order is unchanged (no flicker, keeps scroll).</summary>
        private void PopulateDevices(List<LiveDevice> rows, HashSet<string> flagged)
        {
            bool sameOrder = lvDevices.Items.Count == rows.Count;
            for (int i = 0; sameOrder && i < rows.Count; i++)
                if ((string)lvDevices.Items[i].Tag != rows[i].MacAddress) sameOrder = false;

            lvDevices.BeginUpdate();
            if (!sameOrder)
            {
                lvDevices.Items.Clear();
                foreach (LiveDevice d in rows)
                    lvDevices.Items.Add(new ListViewItem(new string[8]) { Tag = d.MacAddress });
            }
            for (int i = 0; i < rows.Count; i++)
            {
                LiveDevice d = rows[i];
                ListViewItem item = lvDevices.Items[i];
                bool isFlagged = flagged.Contains(d.MacAddress);

                item.Text = (isFlagged ? "[!] " : "") + d.Name;     // [!] = predicted / suspected problem
                item.SubItems[1].Text = d.Category;
                item.SubItems[2].Text = d.Metric;
                item.SubItems[3].Text = d.LastValue.ToString("0.#") + " " + d.Unit;
                item.SubItems[4].Text = d.StatusText;
                item.SubItems[5].Text = d.HasActuator ? (d.IsActuatorOn ? "ON" : "OFF") : "-";
                item.SubItems[6].Text = d.LastSeen.ToString("HH:mm:ss");
                item.SubItems[7].Text = d.MacAddress;
                item.Font = isFlagged ? _boldFont : lvDevices.Font;
                item.BackColor = !d.IsOnline ? Color.LightGray
                    : d.Severity == AlertSeverity.Critical ? Color.FromArgb(255, 205, 205)
                    : d.Severity == AlertSeverity.Warning ? Color.FromArgb(255, 240, 200)
                    : Color.White;

                bool sel = d.MacAddress == _selectedMac;
                if (item.Selected != sel) item.Selected = sel;
            }
            lvDevices.EndUpdate();
        }

        private static void SetItems(ListBox lb, IList<string> items)
        {
            bool same = lb.Items.Count == items.Count;
            for (int i = 0; same && i < items.Count; i++)
                if ((string)lb.Items[i] != items[i]) same = false;
            if (same) return;                                    // avoid needless repaint

            lb.BeginUpdate();
            lb.Items.Clear();
            foreach (string s in items) lb.Items.Add(s);
            lb.EndUpdate();
        }

        private void UpdateDetails()
        {
            LiveDevice dev = _selectedMac == null ? null
                : _lastDevices.FirstOrDefault(d => d.MacAddress == _selectedMac);

            if (dev == null)
            {
                lblSelected.Text = "Select a device from the list.";
                btnOn.Enabled = btnOff.Enabled = false;
                _chartData = new List<KeyValuePair<DateTime, double>>();
                pnlChart.Invalidate();
                return;
            }

            lblSelected.Text = dev.Name + " | " + dev.MacAddress + " | " + dev.StatusText + " | " +
                (dev.HasActuator ? "Output: " + (dev.IsActuatorOn ? "ON" : "OFF") : "sensor only");
            btnOn.Enabled = btnOff.Enabled = dev.HasActuator && dev.IsOnline;

            // history comes out of the SortedDictionary already ordered by timestamp
            var all = _engine.GetHistory(dev.MacAddress);
            _chartData = all.Skip(Math.Max(0, all.Count - 200)).ToList();
            _chartMetric = dev.Metric;
            grpChart.Text = "Timeline - " + dev.Name + " (" + dev.Metric + ", " + dev.Unit + ")";
            pnlChart.Invalidate();
        }

        // ================================================================== COMMANDS
        private void SendCommand(bool turnOn)
        {
            if (_selectedMac == null) return;
            if (_engine.IssueCommand(_selectedMac, turnOn, "Operator") == null)
                MessageBox.Show("This device has no controllable output.", "Command",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshView();
        }

        private void UndoCommand()
        {
            if (_engine.UndoLastCommand() == null)
                MessageBox.Show("There is no command to undo.", "Undo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
            RefreshView();
        }

        private void ShutdownAll()
        {
            var answer = MessageBox.Show(
                "Turn OFF every online pump, valve and relay?\n(You can revert this with Undo.)",
                "Shut down all", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (answer != DialogResult.Yes) return;
            _engine.IssueBulkCommand(null, false, "Operator");
            RefreshView();
        }

        // ================================================================== SEARCH + INSIGHTS
        private void OnSearchDebounce(object sender, EventArgs e)
        {
            _searchDebounce.Stop();
            string term = txtSearch.Text.Trim();
            // Only learn from searches that were long enough and actually found something.
            if (term.Length >= 3 && lvDevices.Items.Count > 0) _engine.RecordSearch(term);
        }

        private void OnInsightDoubleClick(object sender, EventArgs e)
        {
            int i = lstInsights.SelectedIndex;
            if (i < 0 || i >= _insights.Count) return;

            Insight ins = _insights[i];
            if (ins.MacAddress != null) _selectedMac = ins.MacAddress;
            if (!string.IsNullOrEmpty(ins.SuggestedSearch))
                txtSearch.Text = ins.SuggestedSearch;    // accepting a suggestion also reinforces the pattern
            RefreshView();
        }

        // ================================================================== CHART
        private void PaintChart(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_chartData.Count < 2)
            {
                g.DrawString("Select a device to see its timeline.", Font, Brushes.Gray, 10, 10);
                return;
            }

            Rectangle plot = new Rectangle(50, 10, pnlChart.Width - 65, pnlChart.Height - 32);
            if (plot.Width < 30 || plot.Height < 30) return;

            double min = _chartData.Min(p => p.Value), max = _chartData.Max(p => p.Value);
            if (max - min < 1e-9) { max += 1; min -= 1; }
            double pad = (max - min) * 0.08;
            min -= pad; max += pad;

            DateTime t0 = _chartData[0].Key, t1 = _chartData[_chartData.Count - 1].Key;
            double span = Math.Max(1, (t1 - t0).TotalSeconds);

            var pts = new PointF[_chartData.Count];
            for (int i = 0; i < pts.Length; i++)
            {
                float x = plot.Left + (float)((_chartData[i].Key - t0).TotalSeconds / span * plot.Width);
                float y = plot.Bottom - (float)((_chartData[i].Value - min) / (max - min) * plot.Height);
                pts[i] = new PointF(x, y);
            }

            g.DrawRectangle(Pens.LightGray, plot);
            g.DrawString(max.ToString("0.#"), Font, Brushes.DimGray, 2, plot.Top - 6);
            g.DrawString(min.ToString("0.#"), Font, Brushes.DimGray, 2, plot.Bottom - 12);
            g.DrawString(t0.ToString("HH:mm:ss"), Font, Brushes.DimGray, plot.Left, plot.Bottom + 4);
            g.DrawString(t1.ToString("HH:mm:ss"), Font, Brushes.DimGray, plot.Right - 55, plot.Bottom + 4);

            // dashed red line at the critical threshold when it is inside the visible range
            double crit; bool lowIsBad;
            if (ThresholdRules.TryGetCritical(_chartMetric, out crit, out lowIsBad) && crit > min && crit < max)
            {
                float cy = plot.Bottom - (float)((crit - min) / (max - min) * plot.Height);
                using (var dash = new Pen(Color.Firebrick, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawLine(dash, plot.Left, cy, plot.Right, cy);
            }

            using (var pen = new Pen(Color.SteelBlue, 2f))
                g.DrawLines(pen, pts);
        }
    }
}