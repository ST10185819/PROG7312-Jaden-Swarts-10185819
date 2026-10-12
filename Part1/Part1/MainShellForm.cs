using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using Part1.Services;
using Part1.Views;

namespace Part1
{
    /// <summary>
    /// Main menu + navigation shell. It owns the engine, API gateway and simulator
    /// for the whole app lifetime. Each module view is created ONCE (lazily) and then
    /// only shown/hidden, so navigating never loses state and never restarts the backend.
    ///   1. Sensor Data Ingestion and Telemetry  -> your Part 1 Form1, embedded
    ///   2. Real-Time Command Stream and History -> CommandStreamView (Part 2)
    ///   3. Network Topology and Mesh Routing    -> placeholder (final PoE)
    /// </summary>
    public class MainShellForm : Form
    {
        private readonly TelemetryEngine _engine = new TelemetryEngine();
        private readonly ApiGateway _gateway;
        private readonly DeviceSimulator _simulator;

        private readonly Panel _content = new Panel { Dock = DockStyle.Fill };
        private readonly Dictionary<string, Control> _views = new Dictionary<string, Control>();
        private readonly Dictionary<string, Button> _navButtons = new Dictionary<string, Button>();

        public MainShellForm()
        {
            Text = "Smart-X SecureSense";
            ClientSize = new Size(1280, 780);
            MinimumSize = new Size(1000, 650);
            StartPosition = FormStartPosition.CenterScreen;

            _gateway = new ApiGateway(_engine, 5080);
            _simulator = new DeviceSimulator(_gateway.BaseUrl);

            BuildNavigation();
            Load += OnShellLoad;
            FormClosing += OnShellClosing;
        }

        private void BuildNavigation()
        {
            var nav = new FlowLayoutPanel
            {
                Dock = DockStyle.Left,
                Width = 210,
                BackColor = Color.FromArgb(24, 32, 48),
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8)
            };
            nav.Controls.Add(new Label
            {
                Text = "SMART-X",
                ForeColor = Color.White,
                AutoSize = false,
                Width = 190,
                Height = 50,
                Font = new Font("Segoe UI", 15f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            });

            AddNavButton(nav, "telemetry", "Sensor Data && Telemetry");
            AddNavButton(nav, "stream", "Real-Time Command Stream");
            AddNavButton(nav, "topology", "Network Topology && Mesh");

            // Add the Fill control first, then the Left one, so docking resolves correctly.
            Controls.Add(_content);
            Controls.Add(nav);
        }

        private void AddNavButton(FlowLayoutPanel nav, string key, string text)
        {
            var b = new Button
            {
                Text = text,
                Width = 190,
                Height = 46,
                FlatStyle = FlatStyle.Flat,
                ForeColor = Color.White,
                BackColor = Color.FromArgb(40, 52, 76),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0, 0, 0, 6)
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (s, e) => Navigate(key);
            _navButtons[key] = b;
            nav.Controls.Add(b);
        }

        private void OnShellLoad(object sender, EventArgs e)
        {
            _engine.Start();

            string error;
            if (_gateway.Start(out error))
            {
                _simulator.Start();
            }
            else
            {
                MessageBox.Show(
                    "The API gateway could not start on " + _gateway.BaseUrl + "\n\n" + error +
                    "\n\nClose whatever uses port 5080 or change the port in MainShellForm.",
                    "API gateway", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            Navigate("telemetry");
        }

        private void OnShellClosing(object sender, FormClosingEventArgs e)
        {
            _simulator.Stop();
            _gateway.Stop();
            _engine.Stop();
        }

        /// <summary>Show one module and hide the others. Views are cached, never rebuilt.</summary>
        private void Navigate(string key)
        {
            Control view;
            if (!_views.TryGetValue(key, out view))
            {
                view = CreateView(key);
                _views[key] = view;
                _content.Controls.Add(view);
                if (view is Form) ((Form)view).Show();
            }

            foreach (var kv in _views) kv.Value.Visible = ReferenceEquals(kv.Value, view);
            view.BringToFront();

            foreach (var kv in _navButtons)
                kv.Value.BackColor = kv.Key == key ? Color.FromArgb(0, 120, 215) : Color.FromArgb(40, 52, 76);
        }

        private Control CreateView(string key)
        {
            switch (key)
            {
                case "telemetry":
                    // Part 1 form embedded as a child so it keeps its own state.
                    return new Form1 { TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill };

                case "stream":
                    return new CommandStreamView(_engine, _simulator) { Dock = DockStyle.Fill };

                default:
                    var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
                    p.Controls.Add(new Label
                    {
                        Dock = DockStyle.Fill,
                        TextAlign = ContentAlignment.MiddleCenter,
                        Font = new Font("Segoe UI", 14f),
                        Text = "Network Topology and Mesh Routing\n\nTo be implemented in the final PoE."
                    });
                    return p;
            }
        }
    }
}