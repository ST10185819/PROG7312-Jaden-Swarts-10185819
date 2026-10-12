using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace Part1.Services
{
    /// <summary>
    /// Simulates ESP32 nodes, smart plugs and sensors. Every reading is sent as a real
    /// HTTP POST to /api/telemetry, so the API is hit exactly like a physical device would.
    /// One task per device = independent timing. Ref: Microsoft (n.d.) HttpClient.
    /// </summary>
    public sealed class DeviceSimulator
    {
        private class SimDevice
        {
            public string Mac, Name, Category, Metric, Unit;
            public bool HasActuator;
            public double BaseValue, Noise, Value;
            public volatile bool Placeholder;         // unused, keeps class simple
            public double Drift;                      // forced change per tick (fault injection)
            public int SpikeTicks;                    // ticks left in a power spike
            public DateTime SilentUntil = DateTime.MinValue;   // simulates a dead node
        }

        // one shared HttpClient is the recommended usage (avoids socket exhaustion)
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        private readonly string _url;
        private readonly List<SimDevice> _devices = new List<SimDevice>();
        private CancellationTokenSource _cts;

        public DeviceSimulator(string apiBaseUrl)
        {
            _url = apiBaseUrl + "api/telemetry";

            Add("AA:BB:CC:00:00:03", "Laptop Plug 1", "Power", "Power", "W", true, 900, 60);
            Add("AA:BB:CC:00:00:04", "Server Plug 2", "Power", "Power", "W", true, 600, 40);
            Add("AA:BB:CC:00:00:05", "Laptop Room Temp", "Environment", "Temperature", "°C", false, 27, 0.5);
            Add("AA:BB:CC:00:00:06", "Laptop Room Humidity", "Environment", "Humidity", "%", false, 30, 1.5);
            
        }

        private void Add(string mac, string name, string cat, string metric, string unit,
                         bool actuator, double baseValue, double noise)
        {
            _devices.Add(new SimDevice
            {
                Mac = mac,
                Name = name,
                Category = cat,
                Metric = metric,
                Unit = unit,
                HasActuator = actuator,
                BaseValue = baseValue,
                Noise = noise,
                Value = baseValue
            });
        }

        public void Start()
        {
            Stop();
            _cts = new CancellationTokenSource();
            foreach (SimDevice d in _devices)
            {
                SimDevice dev = d;
                Task.Run(() => RunDevice(dev, _cts.Token));
            }
        }

        public void Stop()
        {
            if (_cts != null) { _cts.Cancel(); _cts = null; }
        }

        // ---- fault injection for demos (fields are simple, benign races are acceptable) ----
        public void InjectMoistureDrop()
        {
            SimDevice d = _devices[0];
            d.Value = 26; d.Drift = -1.3;            // slides below 20% = critical
        }

        public void InjectPowerSpike()
        {
            _devices[2].SpikeTicks = 6;              // ~6 s above 2500 W
        }

        public void SimulateDisconnect()
        {
            _devices[3].SilentUntil = DateTime.Now.AddSeconds(30);   // Smart Plug 2 goes quiet
        }

        // ---------------------------------------------------------------- device loop
        private async Task RunDevice(SimDevice d, CancellationToken ct)
        {
            var rnd = new Random(Environment.TickCount ^ d.Mac.GetHashCode()); // Random isn't thread-safe: one per task
            var serializer = new JavaScriptSerializer();

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (DateTime.Now >= d.SilentUntil)
                    {
                        d.Value = NextValue(d, rnd);
                        string json = serializer.Serialize(new
                        {
                            MacAddress = d.Mac,
                            DeviceName = d.Name,
                            Category = d.Category,
                            Metric = d.Metric,
                            Value = Math.Round(d.Value, 1),
                            Unit = d.Unit,
                            HasActuator = d.HasActuator,
                            Battery = 60 + rnd.Next(0, 40)
                        });
                        using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                        {
                            await Http.PostAsync(_url, content, ct).ConfigureAwait(false);
                        }
                    }
                    await Task.Delay(900 + rnd.Next(0, 300), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception)
                {
                    // API unreachable: wait and retry, as firmware would
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private static double NextValue(SimDevice d, Random rnd)
        {
            double noise = (rnd.NextDouble() - 0.5) * 2 * d.Noise;
            double v = d.Value;

            if (d.SpikeTicks > 0) { d.SpikeTicks--; v = 2800 + noise; }
            else if (d.Drift != 0)
            {
                v += d.Drift + noise;
                if (v < 12) d.Drift = 0;               // bottomed out, then recover
            }
            else
            {
                v += (d.BaseValue - v) * 0.1 + noise;  // mean-reverting random walk
            }
            return Math.Max(0, v);
        }
    }
}