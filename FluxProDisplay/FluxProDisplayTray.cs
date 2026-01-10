using System.ComponentModel;
using System.Diagnostics;
using FluxProDisplay.DTOs.AppSettings;
using HidLibrary;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;
using LibreHardwareMonitor.PawnIo;

namespace FluxProDisplay;

public partial class FluxProDisplayTray : Form
{
    private readonly HardwareMonitor _monitor;
    private ToolStripLabel? _connectionStatusLabel;
    private ToolStripLabel? _cpuTempDebugLabel;
    private ToolStripLabel? _gpuTempDebugLabel;
    private ToolStripMenuItem? _startupToggleMenuItem;
    private const string ElevatedTaskName = "FluxProDisplayElevatedTask";
    
    // app settings
    private readonly string _appName;
    private readonly string _version;
    private readonly bool _debug;
    private readonly int _pollingInterval;
    private readonly int _vendorId;
    private readonly int _productId;

    // other UI components for the tab
    private NotifyIcon _appStatusNotifyIcon = null!;
    private Container _component = null!;
    private ContextMenuStrip _contextMenuStrip = null!;
    
    private readonly Icon _iconConnected = new Icon("assets/icon_connected.ico");
    private readonly Icon _iconDisconnected = new Icon("assets/icon_disconnected.ico");
    
    public FluxProDisplayTray(RootConfig configuration)
    {
        // check if iUnity is running to prevent conflicts before doing anything else
        CheckForIUnity();
        
        // check if PawnIO driver is installed.
        CheckForPawnIoDriver();
        
        InitializeComponent();
        
        _monitor = new HardwareMonitor();
        
        // initialize variables from config file for easier changing
        _appName = configuration.AppInfo.Info;
        _version = configuration.AppInfo.Version;
        _debug = configuration.AppInfo.Debug;
        _pollingInterval = configuration.AppSettings.PollingInterval;
        _vendorId = configuration.AppSettings.VendorIdInt;
        _productId = configuration.AppSettings.ProductIdInt;
        
        SetUpTrayIcon();

        _ = WriteToDisplay();
    }

    private static void CheckForPawnIoDriver()
    {
        if (PawnIo.IsInstalled)
        {
            if (PawnIo.Version < new Version(2, 0, 0, 0))
            {
                var result = MessageBox.Show("PawnIO driver is outdated, do you want to update it?", nameof(FluxProDisplay), MessageBoxButtons.OKCancel);
                if (result == DialogResult.OK)
                {
                    InstallPawnIoDriver();
                }
                else
                {
                    Environment.Exit(0);
                }
            }
        }
        else
        {
            var result = MessageBox.Show("PawnIO driver is not installed, do you want to install it?", nameof(FluxProDisplay), MessageBoxButtons.OKCancel);
            if (result == DialogResult.OK)
            {
                InstallPawnIoDriver();
            }
            else
            {
                Environment.Exit(0);
            }
        }
    }

    private static void InstallPawnIoDriver()
    {
        var destination = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

        try
        {
            using (var resourceStream = typeof(FluxProDisplayTray).Assembly
                       .GetManifestResourceStream("FluxProDisplay.assets.PawnIO_setup.exe"))
            {
                if (resourceStream == null)
                    throw new Exception("Embedded installer not found");

                using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    resourceStream.CopyTo(fileStream);
                }
            }

            // run installer
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                Arguments = "-install",
                UseShellExecute = true
            });

            process?.WaitForExit();

            File.Delete(destination);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }
    
    private static void CheckForIUnity()
    {
        var isRunning =
            Process.GetProcessesByName("iunity").Length > 0 ||
            Process.GetProcessesByName("AntecHardwareMonitorWindowsService").Length > 0;

        if (!isRunning) return;

        MessageBox.Show("iUnity is running, please end the iUnity program and its related processes from task manager and try again.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(0);
    }

    private void SetUpTrayIcon()
    {
        _component = new Container();
        _appStatusNotifyIcon = new NotifyIcon(_component);
        _appStatusNotifyIcon.Visible = true;

        _contextMenuStrip = new ContextMenuStrip();

        var appNameLabel = new ToolStripLabel(_appName + " " + _version);
        appNameLabel.ForeColor = Color.Gray;
        appNameLabel.Enabled = false;
        _contextMenuStrip.Items.Add(appNameLabel);
        
        // debug item that shows current temperature in menu strip
        if (_debug)
        {
            AddDebugMenuItems();
        }

        _contextMenuStrip.Items.Add(new ToolStripSeparator());

        _connectionStatusLabel = new ToolStripLabel();
        _connectionStatusLabel.ForeColor = Color.Crimson;
        _connectionStatusLabel.Enabled = true;
        _contextMenuStrip.Items.Add(_connectionStatusLabel);

        // menu items
        _startupToggleMenuItem = new ToolStripMenuItem();
        _startupToggleMenuItem.Click += StartupToggleMenuItemClicked;

        var quitMenuItem = new ToolStripMenuItem("Quit");
        quitMenuItem.Click += QuitMenuItem_Click!;

        // separator to separate
        _contextMenuStrip.Items.Add(new ToolStripSeparator());
        _contextMenuStrip.Items.Add(_startupToggleMenuItem);
        _contextMenuStrip.Items.Add(quitMenuItem);

        _appStatusNotifyIcon.ContextMenuStrip = _contextMenuStrip;

        UpdateStartupMenuItemText();

        _appStatusNotifyIcon.Icon = _iconDisconnected;
    }

    private void AddDebugMenuItems()
    {
        _contextMenuStrip.Items.Add(new ToolStripSeparator());
        var debugModeLabel = new ToolStripLabel("Debug Mode Active");
        debugModeLabel.ForeColor = Color.Gray;
        debugModeLabel.Enabled = false;
        _contextMenuStrip.Items.Add(debugModeLabel);
            
        _cpuTempDebugLabel = new ToolStripLabel("CPU Temp: 0°C");
        _cpuTempDebugLabel.ForeColor = Color.Gray;
        _cpuTempDebugLabel.Enabled = false;
        _contextMenuStrip.Items.Add(_cpuTempDebugLabel);
            
        _gpuTempDebugLabel = new ToolStripLabel("GPU Temp: 0°C");
        _gpuTempDebugLabel.ForeColor = Color.Gray;
        _gpuTempDebugLabel.Enabled = false;
        _contextMenuStrip.Items.Add(_gpuTempDebugLabel);
    }

    private void StartupToggleMenuItemClicked(object? sender, EventArgs e)
    {
        var exePath = Application.ExecutablePath;

        using (var taskService = new TaskService())
        {
            var existingTask = taskService.FindTask(ElevatedTaskName);

            if (existingTask != null)
            {
                taskService.RootFolder.DeleteTask(ElevatedTaskName);
            }
            else
            {
                var newStartupTask = taskService.NewTask();

                newStartupTask.RegistrationInfo.Description = "Flux Pro Display Service Task with Admin Privileges";
                newStartupTask.Principal.RunLevel = TaskRunLevel.Highest;
                newStartupTask.Principal.LogonType = TaskLogonType.InteractiveToken;

                newStartupTask.Triggers.Add(new LogonTrigger());
                newStartupTask.Actions.Add(new ExecAction(exePath, null, Path.GetDirectoryName(exePath)));

                taskService.RootFolder.RegisterTaskDefinition(ElevatedTaskName, newStartupTask);
            }
        }

        UpdateStartupMenuItemText();
    }

    private void UpdateStartupMenuItemText()
    {
        using var ts = new TaskService();
        var taskEnabled = ts.FindTask(ElevatedTaskName) != null;
        _startupToggleMenuItem!.Text = taskEnabled ? "✓ Start with Windows" : "Start with Windows";
    }

    private void QuitMenuItem_Click(object sender, EventArgs e)
    {
        Application.Exit();
    }

    /// <summary>
    /// Hides the main window on startup.
    /// </summary>
    /// <param name="value"></param>
    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated) {
            value = false;
            CreateHandle();
        }
        base.SetVisibleCore(value);
    }

    private async Task WriteToDisplay()
    {
        // interval is in ms, set in appsettings.json
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));

        do
        {
            var device = HidDevices.Enumerate(_vendorId, _productId).FirstOrDefault();

            if (device != null)
            {
                _connectionStatusLabel!.Text = "Connected";
                _appStatusNotifyIcon.Icon = _iconConnected;
                _connectionStatusLabel.ForeColor = Color.Green;
                
                // write data to the screen
                device?.Write(GeneratePayload(device));
            }
            else
            {
                _connectionStatusLabel!.Text = "Not Connected";
                _appStatusNotifyIcon.Icon = _iconDisconnected;
                _connectionStatusLabel.ForeColor = Color.Crimson;
            }

            if (!_debug) continue;
            _cpuTempDebugLabel!.Text = "CPU Temp: " + Math.Round(_monitor.GetCpuTemperature() ?? 0, 1) + "°C";
            _gpuTempDebugLabel!.Text = "GPU Temp: " + Math.Round(_monitor.GetGpuTemperature() ?? 0, 1) + "°C";

        } while (await timer.WaitForNextTickAsync());
    }

    /// <summary>
    /// generates the encoded payload to the display
    /// </summary>
    /// <param name="device"></param>
    /// <returns></returns>
    private byte[] GeneratePayload(HidDevice device)
    {
        var reportLength = device.Capabilities.OutputReportByteLength;
        var payload = new byte[reportLength];

        // reporting number, and other information needed to send to the display
        payload[0] = 0;
        payload[1] = 85;
        payload[2] = 170;
        payload[3] = 1;
        payload[4] = 1;
        payload[5] = 6;

        return FormatDisplayPayload(payload, _monitor.GetCpuTemperature(), _monitor.GetGpuTemperature());
    }

    /// <summary>
    /// formats the payload correctly to send information to the antec flux pro display.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="cpuTemperature"></param>
    /// <param name="gpuTemperature"></param>
    /// <returns></returns>
    private static byte[] FormatDisplayPayload(byte[] payload, float? cpuTemperature, float? gpuTemperature)
    {
        var roundedCpuTemp = Math.Round(cpuTemperature ?? 0, 1);
        var roundedGpuTemp = Math.Round(gpuTemperature ?? 0, 1);

        var wholeNumCpuTemp = (int)roundedCpuTemp;
        var tensPlaceCpuTemp = wholeNumCpuTemp / 10;
        var onesPlaceCpuTemp = wholeNumCpuTemp % 10;
        var tenthsPlaceCpuTemp = (int)((roundedCpuTemp - wholeNumCpuTemp) * 10);

        var wholeNumGpuTemp = (int)roundedGpuTemp;
        var tensPlaceGpuTemp = wholeNumGpuTemp / 10;
        var onesPlaceGpuTemp = wholeNumGpuTemp % 10;
        var tenthsPlaceGpuTemp = (int)((roundedGpuTemp - wholeNumGpuTemp) * 10);

        payload[6] = (byte)tensPlaceCpuTemp;
        payload[7] = (byte)onesPlaceCpuTemp;
        payload[8] = (byte)tenthsPlaceCpuTemp;

        payload[9] = (byte)tensPlaceGpuTemp;
        payload[10] = (byte)onesPlaceGpuTemp;
        payload[11] = (byte)tenthsPlaceGpuTemp;

        // generate checksum per item that is sent to the display
        var checksum = payload.Aggregate<byte, byte>(0, (current, b) => (byte)(current + b));
        payload[12] = checksum;

        return payload;
    }
}