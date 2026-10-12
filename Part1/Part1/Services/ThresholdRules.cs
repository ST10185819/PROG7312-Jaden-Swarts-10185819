using System;
using System.Collections.Generic;
using Part1.Models;

namespace Part1.Services
{
    /// <summary>
    /// Per-metric warning/critical limits. Decides which queue a packet goes to.
    /// LowIsBad = true means values BELOW the limit are dangerous (e.g. moisture).
    /// </summary>
    public static class ThresholdRules
    {
        private class Rule
        {
            public double Warn, Crit;
            public bool LowIsBad;
            public int CritPriority;   // 0 = most urgent
        }

        private static readonly Dictionary<string, Rule> Rules =
            new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase)
        {
            { "Moisture",    new Rule { Warn = 30,   Crit = 20,   LowIsBad = true,  CritPriority = 1 } },
            { "Power",       new Rule { Warn = 2000, Crit = 2500, LowIsBad = false, CritPriority = 0 } },
            { "Temperature", new Rule { Warn = 35,   Crit = 40,   LowIsBad = false, CritPriority = 2 } },
            { "Humidity",    new Rule { Warn = 80,   Crit = 90,   LowIsBad = false, CritPriority = 2 } },
            { "Pressure",    new Rule { Warn = 380,  Crit = 450,  LowIsBad = false, CritPriority = 1 } },
        };

        public static AlertSeverity Evaluate(string metric, double value, out int priority)
        {
            priority = 10;
            Rule r;
            if (metric == null || !Rules.TryGetValue(metric, out r)) return AlertSeverity.Normal;

            bool crit = r.LowIsBad ? value < r.Crit : value > r.Crit;
            bool warn = r.LowIsBad ? value < r.Warn : value > r.Warn;

            if (crit) { priority = r.CritPriority; return AlertSeverity.Critical; }
            return warn ? AlertSeverity.Warning : AlertSeverity.Normal;
        }

        public static bool TryGetCritical(string metric, out double crit, out bool lowIsBad)
        {
            crit = 0; lowIsBad = false;
            Rule r;
            if (metric == null || !Rules.TryGetValue(metric, out r)) return false;
            crit = r.Crit; lowIsBad = r.LowIsBad;
            return true;
        }
    }
}