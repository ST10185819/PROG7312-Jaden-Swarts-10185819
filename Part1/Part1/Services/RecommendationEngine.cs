using System;
using System.Collections.Generic;
using System.Linq;
using Part1.Models;

namespace Part1.Services
{
    /// <summary>
    /// Predictive Action and Recommendation Engine. Three techniques:
    ///  1. PATTERN ANALYSIS - a first-order "context -> next operator action" frequency
    ///     table (Dictionary of Dictionaries). Support (count) and confidence
    ///     (count / total for that context) are the association-rule measures.
    ///     Ref: Agrawal and Srikant (1994).
    ///  2. ANOMALY DETECTION - z-score of the newest reading against a rolling window.
    ///     Ref: Montgomery (2012).
    ///  3. TREND PREDICTION - least-squares line through the window, extrapolated to
    ///     the critical threshold to estimate time-to-breach.
    ///     Ref: Montgomery, Peck and Vining (2012).
    /// NOT thread-safe by itself: TelemetryEngine only calls it inside its lock.
    /// </summary>
    public sealed class RecommendationEngine
    {
        private const int WindowSize = 30;
        private const int MinSamples = 15;
        private const int MinSupport = 2;                       // pattern must be seen 2+ times
        private const double ZThreshold = 3.0;
        private const double BreachHorizonSeconds = 300;        // warn if breach < 5 min away
        private static readonly TimeSpan ContextWindow = TimeSpan.FromSeconds(90);

        // device MAC -> last N readings (time, value)
        private readonly Dictionary<string, Queue<KeyValuePair<DateTime, double>>> _windows =
            new Dictionary<string, Queue<KeyValuePair<DateTime, double>>>(StringComparer.OrdinalIgnoreCase);

        // context ("ALERT:Moisture Sensor 8") -> { action -> times seen }
        private readonly Dictionary<string, Dictionary<string, int>> _transitions =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        // alerts raised in the last 90 s (what the operator might be reacting to)
        private readonly List<KeyValuePair<string, DateTime>> _recentContexts =
            new List<KeyValuePair<string, DateTime>>();

        // ------------------------------------------------------------ learning
        public void Observe(TelemetryPacket p)
        {
            Queue<KeyValuePair<DateTime, double>> w;
            if (!_windows.TryGetValue(p.MacAddress, out w))
            {
                w = new Queue<KeyValuePair<DateTime, double>>();
                _windows[p.MacAddress] = w;
            }
            w.Enqueue(new KeyValuePair<DateTime, double>(p.Timestamp, p.Value));
            while (w.Count > WindowSize) w.Dequeue();
        }

        /// <summary>Called when a NEW alert appears (e.g. "ALERT:Moisture Sensor 8").</summary>
        public void NoteContext(string context, DateTime now)
        {
            Prune(now);
            _recentContexts.Add(new KeyValuePair<string, DateTime>(context, now));
        }

        /// <summary>Called for each search/command. Links it to alerts seen in the last 90 s.</summary>
        public void RecordUserAction(string action, DateTime now)
        {
            Prune(now);
            foreach (var ctx in _recentContexts) Learn(ctx.Key, action);
        }

        private void Prune(DateTime now)
        {
            _recentContexts.RemoveAll(c => now - c.Value > ContextWindow);
        }

        private void Learn(string context, string action)
        {
            Dictionary<string, int> next;
            if (!_transitions.TryGetValue(context, out next))
            {
                next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                _transitions[context] = next;
            }
            int count;
            next.TryGetValue(action, out count);
            next[action] = count + 1;
        }

        /// <summary>
        /// SIMULATED operator history so the recommender has something to suggest on
        /// first launch (the brief allows simulated data). Real use keeps adding to it.
        /// </summary>
        public void SeedDemoHistory()
        {
            for (int i = 0; i < 4; i++) Learn("ALERT:Moisture Sensor 8", "SEARCH|pump a");
            for (int i = 0; i < 3; i++) Learn("ALERT:Moisture Sensor 8", "CMD|ON|Pump A");
            for (int i = 0; i < 3; i++) Learn("ALERT:Smart Plug 1", "CMD|OFF|Smart Plug 1");
        }

        // ------------------------------------------------------------ insights
        public List<Insight> BuildInsights(IEnumerable<LiveDevice> devices)
        {
            var result = new List<Insight>();

            foreach (LiveDevice d in devices)
            {
                // 1. Disconnected node
                if (!d.IsOnline)
                {
                    result.Add(new Insight
                    {
                        Kind = "Offline",
                        Title = d.Name + " stopped reporting",
                        Detail = "Last seen " + d.LastSeen.ToString("HH:mm:ss") + ". Check power / Wi-Fi.",
                        MacAddress = d.MacAddress,
                        SuggestedSearch = d.Name,
                        Confidence = 1.0,
                        Severity = AlertSeverity.Critical
                    });
                }
                else
                {
                    // 2. Out-of-range reading
                    if (d.Severity != AlertSeverity.Normal)
                    {
                        result.Add(new Insight
                        {
                            Kind = "Alert",
                            Title = d.Name + " is " + d.Severity.ToString().ToUpperInvariant(),
                            Detail = d.Metric + " = " + d.LastValue.ToString("0.#") + " " + d.Unit,
                            MacAddress = d.MacAddress,
                            SuggestedSearch = d.Name,
                            Confidence = 1.0,
                            Severity = d.Severity
                        });
                    }

                    // 3. Statistical outlier
                    double z;
                    if (IsOutlier(d.MacAddress, out z))
                    {
                        result.Add(new Insight
                        {
                            Kind = "Anomaly",
                            Title = d.Name + " reading is unusual",
                            Detail = "Latest value is " + Math.Abs(z).ToString("0.0") + " std-devs from its recent average.",
                            MacAddress = d.MacAddress,
                            SuggestedSearch = d.Name,
                            Confidence = Math.Min(1.0, Math.Abs(z) / 6.0),
                            Severity = AlertSeverity.Warning
                        });
                    }

                    // 4. Predicted breach
                    double secs, slopePerMin;
                    if (TryPredictBreach(d.MacAddress, d.Metric, d.LastValue, out secs, out slopePerMin))
                    {
                        result.Add(new Insight
                        {
                            Kind = "Trend",
                            Title = d.Name + " will hit its critical limit in ~" + FormatSeconds(secs),
                            Detail = d.Metric + " is changing " + slopePerMin.ToString("+0.0;-0.0") + " " + d.Unit + "/min.",
                            MacAddress = d.MacAddress,
                            SuggestedSearch = d.Name,
                            Confidence = 0.7,
                            Severity = AlertSeverity.Warning
                        });
                    }
                }

                // 5. Learned operator pattern for this device's alert
                if (!d.IsOnline || d.Severity != AlertSeverity.Normal)
                {
                    Dictionary<string, int> next;
                    if (_transitions.TryGetValue("ALERT:" + d.Name, out next))
                    {
                        int total = next.Values.Sum();
                        foreach (var kv in next.Where(k => k.Value >= MinSupport)
                                               .OrderByDescending(k => k.Value).Take(2))
                        {
                            result.Add(DescribePattern(kv.Key, kv.Value, total, d));
                        }
                    }
                }
            }

            return result.OrderByDescending(i => (int)i.Severity)
                         .ThenByDescending(i => i.Confidence)
                         .Take(10).ToList();
        }

        private static Insight DescribePattern(string action, int count, int total, LiveDevice d)
        {
            string[] p = action.Split('|');
            string title = p[0] == "SEARCH"
                ? "Suggested: search for '" + p[1] + "'"
                : "Suggested: turn " + p[1] + " '" + p[2] + "'";
            string target = p[0] == "SEARCH" ? p[1] : p[2];
            double confidence = (double)count / total;

            return new Insight
            {
                Kind = "Pattern",
                Title = title,
                Detail = "After '" + d.Name + "' alerts, operators did this " + count + " of " +
                         total + " times (" + confidence.ToString("P0") + ").",
                MacAddress = d.MacAddress,
                SuggestedSearch = target,
                Confidence = confidence,
                Severity = d.IsOnline ? d.Severity : AlertSeverity.Critical
            };
        }

        // ------------------------------------------------------------ maths
        private bool IsOutlier(string mac, out double z)
        {
            z = 0;
            Queue<KeyValuePair<DateTime, double>> w;
            if (!_windows.TryGetValue(mac, out w) || w.Count < MinSamples) return false;

            double[] v = w.Select(x => x.Value).ToArray();
            double last = v[v.Length - 1];
            double[] hist = v.Take(v.Length - 1).ToArray();   // compare newest vs the rest
            double mean = hist.Average();
            double std = Math.Sqrt(hist.Sum(x => (x - mean) * (x - mean)) / (hist.Length - 1));
            if (std < 1e-6) return false;

            z = (last - mean) / std;
            return Math.Abs(z) > ZThreshold;
        }

        private bool TryPredictBreach(string mac, string metric, double current,
                                      out double secondsToBreach, out double slopePerMinute)
        {
            secondsToBreach = 0; slopePerMinute = 0;
            Queue<KeyValuePair<DateTime, double>> w;
            if (!_windows.TryGetValue(mac, out w) || w.Count < MinSamples) return false;

            double crit; bool lowIsBad;
            if (!ThresholdRules.TryGetCritical(metric, out crit, out lowIsBad)) return false;

            var pts = w.ToArray();
            DateTime t0 = pts[0].Key;
            int n = pts.Length;
            double sx = 0, sy = 0, sxx = 0, sxy = 0;
            foreach (var p in pts)
            {
                double x = (p.Key - t0).TotalSeconds, y = p.Value;
                sx += x; sy += y; sxx += x * x; sxy += x * y;
            }
            double denom = n * sxx - sx * sx;
            if (Math.Abs(denom) < 1e-9) return false;

            double slope = (n * sxy - sx * sy) / denom;       // units per second
            slopePerMinute = slope * 60;

            if (lowIsBad)
            {
                if (slope >= -1e-4 || current <= crit) return false;
                secondsToBreach = (current - crit) / (-slope);
            }
            else
            {
                if (slope <= 1e-4 || current >= crit) return false;
                secondsToBreach = (crit - current) / slope;
            }
            return secondsToBreach <= BreachHorizonSeconds;
        }

        private static string FormatSeconds(double s)
        {
            return s < 90 ? ((int)s) + " s" : (s / 60).ToString("0.0") + " min";
        }
    }
}