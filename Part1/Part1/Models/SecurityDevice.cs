using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Part1.Models
{
    internal class SecurityDevice
    {
        public string DeviceID { get; set; } = "";
        public string DeviceName { get; set; } = "";
        public string SensorType { get; set; } = "";
        public string Location { get; set; } = "";
        public int BatteryLevel { get; set; }
        public bool IsOnline { get; set; }
    }
}
