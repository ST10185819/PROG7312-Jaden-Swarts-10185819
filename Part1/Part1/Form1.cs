using Part1.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace Part1
{
    public partial class Form1 : Form
    {
        // Stores all registered IoT security devices.
        private List<SecurityDevice> devices = new List<SecurityDevice>();
        public Form1()
        {
            InitializeComponent();
        }
        private void DashboardForm_Load(object sender, EventArgs e)
        {
            // Add some sample IoT devices.
            devices.Add(new SecurityDevice
            {
                DeviceID = "Device-001",
                DeviceName = "Front Door Sensor",
                SensorType = "Door Sensor",
                Location = "Main Entrance",
                BatteryLevel = 92,
                IsOnline = true
            });

            devices.Add(new SecurityDevice
            {
                DeviceID = "Device-002",
                DeviceName = "Warehouse Motion",
                SensorType = "PIR Motion",
                Location = "Warehouse",
                BatteryLevel = 76,
                IsOnline = true
            });

            devices.Add(new SecurityDevice
            {
                DeviceID = "Device-003",
                DeviceName = "Server Room Smoke",
                SensorType = "Smoke Sensor",
                Location = "Server Room",
                BatteryLevel = 64,
                IsOnline = true
            });

            // Display the devices in the ListBox.
            LoadDevices();

            // Display sample events.
            LoadEvents();

            // Update dashboard statistics.
            UpdateStatistics();

            // Show that the application is currently running.
            lblAPIStatus.Text = "API Status: Connected";

            lblLastUpdate.Text =
                $"Last Update: {DateTime.Now:HH:mm:ss}";
        }

        private void LoadDevices()
        {
            // Clear the ListBox before loading the devices.
            lstDevices.Items.Clear();

            // Add every registered device to the ListBox.
            foreach (SecurityDevice device in devices)
            {
                string status = device.IsOnline
                    ? "ONLINE"
                    : "OFFLINE";

                lstDevices.Items.Add(
                    $"{device.DeviceID} | {device.DeviceName} | {status}");
            }
        }

        private void lstDevices_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Make sure the user has actually selected a device.
            if (lstDevices.SelectedIndex == -1)
                return;

            // Get the selected device from our list.
            SecurityDevice selectedDevice =
                devices[lstDevices.SelectedIndex];

            // Display the selected device's information.
            txtDeviceID.Text =
                selectedDevice.DeviceID;

            txtDeviceName.Text =
                selectedDevice.DeviceName;

            txtSensorType.Text =
                selectedDevice.SensorType;

            txtLocation.Text =
                selectedDevice.Location;

            lblBattery.Text =
                $"Battery: {selectedDevice.BatteryLevel}%";

            lblStatus.Text =
                selectedDevice.IsOnline
                    ? "Status: ONLINE"
                    : "Status: OFFLINE";

            // Change the status colour.
            lblStatus.ForeColor =
                selectedDevice.IsOnline
                    ? Color.Green
                    : Color.Red;
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            // Refresh the device list.
            LoadDevices();

            // Recalculate dashboard statistics.
            UpdateStatistics();

            // Update the last refresh time.
            lblLastUpdate.Text =
                $"Last Update: {DateTime.Now:HH:mm:ss}";

            // Tell the user that the refresh was successful.
            MessageBox.Show(
                "IoT device data has been refreshed.",
                "Smart-X SecureSense",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        private void UpdateStatistics()
        {
            // Count devices that are currently online.
            int onlineDevices =
                devices.Count(d => d.IsOnline);

            // Display the number of online devices.
            lblDevicesOnline.Text =
                $"Devices Online: {onlineDevices}";

            // For now these are simulated values.
            // Later they will come from the API/database.
            lblCriticalAlerts.Text =
                "Critical Alerts: 2";

            lblWarnings.Text =
                "Warnings: 4";

            lblSystemHealth.Text =
                "System Health: 96%";
        }
        private void LoadEvents()
        {
            // Clear existing events.
            lstEvents.Items.Clear();

            // Add simulated IoT security events.
            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Motion detected - Warehouse");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Door opened - Main Entrance");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Smoke level normal - Server Room");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Device connected - Device-003");
        }

        private void lstEvents_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Make sure an event has been selected.
            if (lstEvents.SelectedIndex == -1)
                return;

            // Get the selected event.
            string selectedEvent =
                lstEvents.SelectedItem?.ToString() ?? "";

            // Display more information about the event.
            MessageBox.Show(
                $"Security Event:\n\n{selectedEvent}\n\n" +
                "The event can be investigated further from the telemetry section.",
                "Security Event Details",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }


        private void btnRegisterDevic_Click(object sender, EventArgs e)
        {
            // Get the information entered by the user.
            string deviceID = txtDeviceID.Text.Trim();
            string deviceName = txtDeviceName.Text.Trim();
            string sensorType = txtSensorType.Text.Trim();
            string location = txtLocation.Text.Trim();

            // Make sure all required fields have been completed.
            if (string.IsNullOrWhiteSpace(deviceID) ||
                string.IsNullOrWhiteSpace(deviceName) ||
                string.IsNullOrWhiteSpace(sensorType) ||
                string.IsNullOrWhiteSpace(location))
            {
                MessageBox.Show(
                    "Please enter the Device ID, Device Name, Sensor Type and Location.",
                    "Missing Information",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            // Check if the Device ID already exists.
            if (devices.Any(d => d.DeviceID.Equals(deviceID, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show(
                    "A device with this Device ID already exists.",
                    "Duplicate Device ID",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);

                return;
            }

            // Create a new security device using the user's input.
            SecurityDevice newDevice = new SecurityDevice
            {
                DeviceID = deviceID,
                DeviceName = deviceName,
                SensorType = sensorType,
                Location = location,

                // New devices start with a full battery.
                BatteryLevel = 100,

                // New devices are initially online.
                IsOnline = true
            };

            // Add the new device to the device list.
            devices.Add(newDevice);

            // Refresh the device ListBox.
            LoadDevices();

            // Update the dashboard statistics.
            UpdateStatistics();

            // Clear the input fields so another device can be registered.
            txtDeviceID.Clear();
            txtDeviceName.Clear();
            txtSensorType.Clear();
            txtLocation.Clear();

            // Tell the user that registration was successful.
            MessageBox.Show(
                "IoT device registered successfully!",
                "Smart-X SecureSense",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }


        private void btnDashboard_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "Dashboard";

            // Reload the registered devices.
            LoadDevices();

            // Reload the security events.
            LoadEvents();

            // Recalculate the dashboard statistics.
            UpdateStatistics();

            // Update the time.
            lblLastUpdate.Text =
                $"Last Update: {DateTime.Now:HH:mm:ss}";

            MessageBox.Show(
                "Dashboard loaded successfully.",
                "Smart-X SecureSense",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        private void btnDevices_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "IoT Devices";

            LoadDevices();

            if (devices.Count == 0)
            {
                MessageBox.Show(
                    "No IoT security devices are currently registered.",
                    "IoT Devices",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);

                return;
            }

            MessageBox.Show(
                $"{devices.Count} IoT security device(s) are currently registered.",
                "IoT Devices",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        private void btnTelemetry_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "Live Telemetry";

            lstEvents.Items.Clear();

            Random random = new Random();

            int temperature = random.Next(18, 31);
            int motion = random.Next(0, 2);
            int signal = random.Next(70, 101);

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Temperature: {temperature}°C");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Motion Sensor: {(motion == 1 ? "MOTION DETECTED" : "No Motion")}");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Signal Strength: {signal}%");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - Security Monitoring: ACTIVE");

            MessageBox.Show(
                "Live telemetry data loaded.",
                "Live Telemetry",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        private void btnAlerts_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "Security Alerts";

            lstEvents.Items.Clear();

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - CRITICAL - Motion detected - Warehouse");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - WARNING - Door opened - Main Entrance");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - NORMAL - Smoke level normal - Server Room");

            lstEvents.Items.Add(
                $"{DateTime.Now:HH:mm:ss} - INFO - Security device connected");

            MessageBox.Show(
                "Security alerts have been loaded.",
                "Security Alerts",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        private void btnReports_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "Security Reports";

            int totalDevices = devices.Count;
            int onlineDevices = devices.Count(d => d.IsOnline);
            int offlineDevices = devices.Count(d => !d.IsOnline);

            string report =
                "SMART-X SECURESENSE SECURITY REPORT\n" +
                "====================================\n\n" +
                $"Report Time: {DateTime.Now}\n\n" +
                $"Total Devices: {totalDevices}\n" +
                $"Devices Online: {onlineDevices}\n" +
                $"Devices Offline: {offlineDevices}\n\n" +
                "REGISTERED DEVICES\n" +
                "------------------\n";

            foreach (SecurityDevice device in devices)
            {
                report +=
                    $"\nDevice ID: {device.DeviceID}" +
                    $"\nName: {device.DeviceName}" +
                    $"\nSensor: {device.SensorType}" +
                    $"\nLocation: {device.Location}" +
                    $"\nBattery: {device.BatteryLevel}%" +
                    $"\nStatus: {(device.IsOnline ? "ONLINE" : "OFFLINE")}" +
                    "\n------------------";
            }

            MessageBox.Show(
                report,
                "Security Report",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        private void btnSettings_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "System Settings";

            string settings =
                "SMART-X SECURESENSE SETTINGS\n\n" +
                "System Status: Active\n" +
                "API Status: Connected\n" +
                "Monitoring: Enabled\n" +
                "Security Monitoring: Enabled\n" +
                "Device Monitoring: Enabled\n" +
                "Automatic Updates: Enabled";

            MessageBox.Show(
                settings,
                "System Settings",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void btnMedia_Click(object sender, EventArgs e)
        {
            lblPageTitle.Text = "Security Media";

            OpenFileDialog dialog = new OpenFileDialog();

            dialog.Filter =
                "Supported Files|*.jpg;*.jpeg;*.png;*.json;*.txt;*.xml|" +
                "Images|*.jpg;*.jpeg;*.png|" +
                "Configuration Files|*.json;*.xml|" +
                "Text Files|*.txt";

            dialog.Multiselect = true;

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                MessageBox.Show(
                    $"{dialog.FileNames.Length} file(s) selected.",
                    "Security Media",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private void label5_Click(object sender, EventArgs e)
        {

        }

        private void txtDeviceID_TextChanged(object sender, EventArgs e)
        {

        }

        private void txtDeviceName_TextChanged(object sender, EventArgs e)
        {

        }

        private void txtSensorType_TextChanged(object sender, EventArgs e)
        {

        }

        private void txtLocation_TextChanged(object sender, EventArgs e)
        {

        }

        private void lblDeviceName_Click(object sender, EventArgs e)
        {

        }
    }
}

