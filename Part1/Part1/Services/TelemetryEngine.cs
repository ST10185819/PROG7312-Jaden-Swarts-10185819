using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Part1.DataStructures;
using Part1.Models;

namespace Part1.Services
{
    /// <summary>
    /// The real-time processing core. Data structures used:
    ///   Queue&lt;T&gt;            - FIFO buffer for normal telemetry
    ///   MinHeapPriorityQueue - critical packets jump ahead of the FIFO queue
    ///   Stack&lt;T&gt;            - undo history of operator commands (LIFO)
    ///   Dictionary           - live devices keyed by MAC, O(1) lookup per packet
    ///   SortedDictionary     - per-device history ordered by timestamp
    ///   HashSet&lt;T&gt;          - active alert keys; Add() returns false for duplicates
    /// API threads only ENQUEUE (fast); one worker thread drains the queues, so HTTP
    /// requests are never blocked by processing and the backend can't be overwhelmed.
    /// Ref: Microsoft (n.d.) collections documentation.
    /// </summary>
    public sealed class TelemetryEngine : IDisposable
    {
        private const int MaxStandardQueue = 5000;      // back-pressure limit
        private const int MaxHistoryPerDevice = 600;    // ~10 min at 1 packet/s
        private const int BatchSize = 100;              // packets per worker pass
        private static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);

        private readonly object _lock = new object();

        // --- the data structures ---
        private readonly Queue<TelemetryPacket> _standardQueue = new Queue<TelemetryPacket>();
        private readonly MinHeapPriorityQueue<TelemetryPacket> _criticalQueue =
            new MinHeapPriorityQueue<TelemetryPacket>();
        private readonly Stack<CommandRecord> _undoStack = new Stack<CommandRecord>();
        private readonly Dictionary<string, LiveDevice> _devices =
            new Dictionary<string, LiveDevice>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SortedDictionary<DateTime, double>> _history =
            new Dictionary<string, SortedDictionary<DateTime, double>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _activeAlerts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly Queue<string> _eventLog = new Queue<string>();
        private readonly List<string> _commandHistory = new List<string>();
        private readonly RecommendationEngine _recommender = new RecommendationEngine();

        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _worker;
        private volatile bool _running;
        private long _received, _processed, _rejected;

        public TelemetryEngine()
        {
            _recommender.SeedDemoHistory();
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "TelemetryEngine" };
            _worker.Start();
        }

        public void Stop()
        {
            _running = false;
            _signal.Set();
            if (_worker != null && _worker.IsAlive) _worker.Join(1000);
        }

        public void Dispose() { Stop(); _signal.Dispose(); }

        // =============================================================== INGEST
        /// <summary>Called by API threads. Validates, classifies, enqueues; returns fast.</summary>
        public IngestResult Ingest(TelemetryPacket p)
        {
            if (p == null ||
                string.IsNullOrWhiteSpace(p.MacAddress) || p.MacAddress.Length > 32 ||
                string.IsNullOrWhiteSpace(p.Metric) ||
                double.IsNaN(p.Value) || double.IsInfinity(p.Value))
                return IngestResult.Invalid;

            p.Timestamp = DateTime.Now;                         // server-side clock
            int priority;
            p.Severity = ThresholdRules.Evaluate(p.Metric, p.Value, out priority);
            p.Priority = priority;

            lock (_lock)
            {
                if (p.Severity == AlertSeverity.Critical)
                {
                    _criticalQueue.Enqueue(p, priority);        // bypasses the FIFO queue
                }
                else
                {
                    if (_standardQueue.Count >= MaxStandardQueue)
                    {
                        _rejected++;
                        return IngestResult.Overloaded;         // API answers 503
                    }
                    _standardQueue.Enqueue(p);
                }
                _received++;
            }
            _signal.Set();
            return IngestResult.Accepted;
        }

        // =============================================================== WORKER
        private void WorkerLoop()
        {
            DateTime nextWatchdog = DateTime.Now;
            while (_running)
            {
                bool more = false;
                try
                {
                    more = ProcessPending();
                    if (DateTime.Now >= nextWatchdog)
                    {
                        CheckOfflineDevices();
                        nextWatchdog = DateTime.Now.AddSeconds(1);
                    }
                }
                catch (Exception ex)
                {
                    lock (_lock) { AddLog("ENGINE ERROR: " + ex.Message); }
                }
                if (!more) _signal.WaitOne(250);
            }
        }

        /// <summary>Drains ALL critical packets first, then a bounded batch of normal ones.</summary>
        private bool ProcessPending()
        {
            lock (_lock)
            {
                TelemetryPacket pkt;
                while (_criticalQueue.TryDequeue(out pkt)) Apply(pkt);

                int n = 0;
                while (n < BatchSize && _standardQueue.Count > 0)
                {
                    Apply(_standardQueue.Dequeue());
                    n++;
                    // a critical packet that arrived mid-batch still jumps the line
                    while (_criticalQueue.TryDequeue(out pkt)) Apply(pkt);
                }
                return _standardQueue.Count > 0;
            }
        }

        /// <summary>Applies one packet to device state. Caller MUST hold _lock.</summary>
        private void Apply(TelemetryPacket p)
        {
            LiveDevice d;
            if (!_devices.TryGetValue(p.MacAddress, out d))     // O(1) Dictionary lookup
            {
                d = new LiveDevice { MacAddress = p.MacAddress };
                _devices.Add(p.MacAddress, d);
                AddLog("New device registered: " + p.DeviceName + " (" + p.MacAddress + ")");
            }

            d.Name = p.DeviceName; d.Category = p.Category; d.Metric = p.Metric;
            d.Unit = p.Unit; d.LastValue = p.Value; d.Battery = p.Battery;
            d.HasActuator = p.HasActuator; d.LastSeen = p.Timestamp;
            d.Severity = p.Severity; d.PacketCount++;

            // device came back after being offline -> clear its OFFLINE entry in the set
            if (!d.IsOnline)
            {
                d.IsOnline = true;
                _activeAlerts.Remove("OFFLINE|" + d.MacAddress);
                AddLog("Device back online: " + d.Name);
            }

            // SortedDictionary keeps readings ordered by time (graph-ready)
            SortedDictionary<DateTime, double> h;
            if (!_history.TryGetValue(d.MacAddress, out h))
            {
                h = new SortedDictionary<DateTime, double>();
                _history[d.MacAddress] = h;
            }
            DateTime key = p.Timestamp;
            while (h.ContainsKey(key)) key = key.AddTicks(1);   // keys must be unique
            h.Add(key, p.Value);
            if (h.Count > MaxHistoryPerDevice) h.Remove(h.Keys.First());

            // HashSet: Add() is true only for a NEW alert, so duplicates are ignored
            string critKey = "CRITICAL|" + d.MacAddress;
            string warnKey = "WARNING|" + d.MacAddress;
            if (p.Severity == AlertSeverity.Critical)
            {
                _activeAlerts.Remove(warnKey);
                if (_activeAlerts.Add(critKey))
                {
                    AddLog("CRITICAL: " + d.Name + " " + p.Metric + "=" + p.Value.ToString("0.#") + p.Unit);
                    _recommender.NoteContext("ALERT:" + d.Name, DateTime.Now);
                }
            }
            else if (p.Severity == AlertSeverity.Warning)
            {
                _activeAlerts.Remove(critKey);
                if (_activeAlerts.Add(warnKey))
                {
                    AddLog("WARNING: " + d.Name + " " + p.Metric + "=" + p.Value.ToString("0.#") + p.Unit);
                    _recommender.NoteContext("ALERT:" + d.Name, DateTime.Now);
                }
            }
            else
            {
                _activeAlerts.Remove(critKey);
                _activeAlerts.Remove(warnKey);
            }

            _recommender.Observe(p);
            _processed++;
        }

        private void CheckOfflineDevices()
        {
            lock (_lock)
            {
                DateTime now = DateTime.Now;
                foreach (LiveDevice d in _devices.Values)
                {
                    if (d.IsOnline && now - d.LastSeen > OfflineAfter)
                    {
                        d.IsOnline = false;
                        // duplicate disconnect alerts for the same node are ignored by the set
                        if (_activeAlerts.Add("OFFLINE|" + d.MacAddress))
                        {
                            AddLog("OFFLINE: " + d.Name + " stopped reporting");
                            _recommender.NoteContext("ALERT:" + d.Name, now);
                        }
                    }
                }
            }
        }

        // ============================================================== COMMANDS
        /// <summary>Single-device override. Returns null if the device has no actuator.</summary>
        public CommandRecord IssueCommand(string mac, bool turnOn, string source)
        {
            lock (_lock)
            {
                LiveDevice d;
                if (mac == null || !_devices.TryGetValue(mac, out d) || !d.HasActuator) return null;

                var rec = new CommandRecord { Action = turnOn ? "ON" : "OFF", Source = source };
                rec.PreviousStates.Add(new KeyValuePair<string, bool>(d.MacAddress, d.IsActuatorOn));
                d.IsActuatorOn = turnOn;
                rec.Description = "Turn " + rec.Action + " -> " + d.Name;
                Commit(rec);

                _recommender.RecordUserAction("CMD|" + rec.Action + "|" + d.Name, DateTime.Now);
                return rec;
            }
        }

        /// <summary>Bulk override, e.g. "shut down all valves". One Undo reverts them all.</summary>
        public CommandRecord IssueBulkCommand(string category, bool turnOn, string source)
        {
            lock (_lock)
            {
                var targets = _devices.Values.Where(d => d.HasActuator && d.IsOnline &&
                    (category == null || d.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (targets.Count == 0) return null;

                var rec = new CommandRecord { Action = turnOn ? "ON" : "OFF", Source = source };
                foreach (LiveDevice d in targets)
                {
                    rec.PreviousStates.Add(new KeyValuePair<string, bool>(d.MacAddress, d.IsActuatorOn));
                    d.IsActuatorOn = turnOn;
                }
                rec.Description = "Turn " + rec.Action + " -> " + targets.Count + " actuator(s)" +
                                  (category == null ? "" : " in " + category);
                Commit(rec);
                return rec;
            }
        }

        /// <summary>Stack pop = revert the most recent command (LIFO).</summary>
        public CommandRecord UndoLastCommand()
        {
            lock (_lock)
            {
                if (_undoStack.Count == 0) return null;
                CommandRecord rec = _undoStack.Pop();
                foreach (var prev in rec.PreviousStates)
                {
                    LiveDevice d;
                    if (_devices.TryGetValue(prev.Key, out d)) d.IsActuatorOn = prev.Value;
                }
                string line = DateTime.Now.ToString("HH:mm:ss") + " [Undo] reverted: " + rec.Description;
                _commandHistory.Add(line);
                AddLog(line);
                return rec;
            }
        }

        private void Commit(CommandRecord rec)       // caller holds _lock
        {
            _undoStack.Push(rec);
            _commandHistory.Add(rec.ToString());
            if (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
            AddLog("Command: " + rec.Description);
        }

        public void RecordSearch(string term)
        {
            lock (_lock) { _recommender.RecordUserAction("SEARCH|" + term.ToLowerInvariant(), DateTime.Now); }
        }

        // ============================================================== SNAPSHOTS
        // The UI always receives COPIES, so it never races the worker thread.
        public List<LiveDevice> GetDevices()
        {
            lock (_lock) { return _devices.Values.Select(d => d.Clone()).ToList(); }
        }

        public List<KeyValuePair<DateTime, double>> GetHistory(string mac)
        {
            lock (_lock)
            {
                SortedDictionary<DateTime, double> h;
                return _history.TryGetValue(mac, out h)
                    ? h.ToList()                               // already in timestamp order
                    : new List<KeyValuePair<DateTime, double>>();
            }
        }

        public List<Insight> GetInsights()
        {
            lock (_lock) { return _recommender.BuildInsights(_devices.Values); }
        }

        public List<string> GetCommandHistory()
        {
            lock (_lock) { return Enumerable.Reverse(_commandHistory).ToList(); }
        }

        public List<string> GetEventLog()
        {
            lock (_lock) { return Enumerable.Reverse(_eventLog).ToList(); }
        }

        public EngineStats GetStats()
        {
            lock (_lock)
            {
                return new EngineStats
                {
                    StandardQueueDepth = _standardQueue.Count,
                    PriorityQueueDepth = _criticalQueue.Count,
                    Received = _received,
                    Processed = _processed,
                    Rejected = _rejected,
                    ActiveAlertSetSize = _activeAlerts.Count,
                    UndoDepth = _undoStack.Count
                };
            }
        }

        private void AddLog(string msg)               // caller holds _lock
        {
            _eventLog.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + msg);
            while (_eventLog.Count > 200) _eventLog.Dequeue();
        }
    }
}