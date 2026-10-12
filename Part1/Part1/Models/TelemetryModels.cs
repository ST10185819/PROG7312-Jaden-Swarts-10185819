using System;
using System.Collections.Generic;

namespace Part1.Models
{
    public enum AlertSeverity { Normal = 0, Warning = 1, Critical = 2 }

    /// <summary>Result of offering a packet to the ingestion pipeline.</summary>
    public enum IngestResult { Accepted, Invalid, Overloaded }

    /// <summary>
    /// The JSON body a device (real or simulated) POSTs to /api/telemetry.
    /// Timestamp and Severity are set by the SERVER, because many ESP32 boards
    /// have no real-time clock and a device must not decide its own priority.
    /// </summary>
    public class TelemetryPacket
    {
        public string MacAddress { get; set; } = "";   // unique node id = Dictionary key
        public string DeviceName { get; set; } = "";
        public string Category { get; set; } = "";     // Hydroponics / Power / Environment ...
        public string Metric { get; set; } = "";       // Moisture, Power, Temperature ...
        public double Value { get; set; }
        public string Unit { get; set; } = "";
        public bool HasActuator { get; set; }          // pump / relay / valve present?
        public int Battery { get; set; } = 100;

        // ---- server-side fields ----
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public AlertSeverity Severity { get; set; }
        public int Priority { get; set; }              // lower number = processed first
    }

    /// <summary>Live state of one registered device (value stored in the Dictionary).</summary>
    public class LiveDevice
    {
        public string MacAddress { get; set; } = "";
        public string Name { get; set; } = "";
        public string Category { get; set; } = "";
        public string Metric { get; set; } = "";
        public string Unit { get; set; } = "";
        public double LastValue { get; set; }
        public int Battery { get; set; }
        public DateTime LastSeen { get; set; }
        public AlertSeverity Severity { get; set; }
        public bool IsOnline { get; set; } = true;
        public bool HasActuator { get; set; }
        public bool IsActuatorOn { get; set; }
        public long PacketCount { get; set; }

        public string StatusText
        {
            get { return !IsOnline ? "OFFLINE" : Severity.ToString().ToUpperInvariant(); }
        }

        /// <summary>Copy handed to the UI so it never touches live engine objects.</summary>
        public LiveDevice Clone() { return (LiveDevice)MemberwiseClone(); }
    }

    /// <summary>
    /// One operator command. PreviousStates lets the Stack-based Undo restore every
    /// device the command touched (works for single and "shut down all" commands).
    /// </summary>
    public class CommandRecord
    {
        public string Action { get; set; } = "";       // "ON" or "OFF"
        public string Description { get; set; } = "";
        public string Source { get; set; } = "Operator";
        public DateTime IssuedAt { get; set; } = DateTime.Now;
        public List<KeyValuePair<string, bool>> PreviousStates { get; } =
            new List<KeyValuePair<string, bool>>();

        public override string ToString()
        {
            return $"{IssuedAt:HH:mm:ss} [{Source}] {Description}";
        }
    }

    /// <summary>A "Suggested Action" / "Automated Insight" shown in the UI.</summary>
    public class Insight
    {
        public string Kind { get; set; } = "";         // Pattern, Trend, Anomaly, Alert, Offline
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string MacAddress { get; set; }         // device to highlight (may be null)
        public string SuggestedSearch { get; set; }    // text to drop into the search box
        public double Confidence { get; set; }         // 0..1
        public AlertSeverity Severity { get; set; }
    }

    public class EngineStats
    {
        public int StandardQueueDepth { get; set; }
        public int PriorityQueueDepth { get; set; }
        public long Received { get; set; }
        public long Processed { get; set; }
        public long Rejected { get; set; }
        public int ActiveAlertSetSize { get; set; }
        public int UndoDepth { get; set; }
    }
}