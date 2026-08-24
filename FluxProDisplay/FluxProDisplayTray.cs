using FluxProDisplay.DTOs.AppSettings;
using HidLibrary;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;
using Task = System.Threading.Tasks.Task;

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
    private readonly bool _debug;
    private readonly int _pollingInterval;
    private readonly int _vendorId;
    private readonly int _productId;

    // other UI components for the tab
    private NotifyIcon _appStatusNotifyIcon = null!;
    private ContextMenuStrip _contextMenuStrip = null!;

    private PeriodicTimer? _pollTimer;
    private HidDevice? _device;
    private byte[]? _payload;
    private int _reconnectRequested;

    private readonly Icon _iconConnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_connected.ico"));
    private readonly Icon _iconDisconnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_disconnected.ico"));
    
    public FluxProDisplayTray(RootConfig configuration)
    {
        // check if iUnity is running to prevent conflicts before doing anything else
        PreflightChecks.CheckForIUnity();
        
        // check if PawnIO driver is installed.
        PreflightChecks.CheckForPawnIoDriver();
        
        InitializeComponent();
        
        _monitor = new HardwareMonitor();
        
        // initialize variables from config file for easier changing
        _debug = configuration.AppInfo.Debug;
        _pollingInterval = configuration.AppSettings.PollingInterval;
        _vendorId = configuration.AppSettings.VendorIdInt;
        _productId = configuration.AppSettings.ProductIdInt;
        
        SetUpTrayIcon();
        SystemEvents.PowerModeChanged += SystemEvents_PowerModeChanged;

        _ = WriteToDisplay().ContinueWith(
            t => Logger.LogError(t.Exception!),
            TaskContinuationOptions.OnlyOnFaulted);
    }
    
    private void SetUpTrayIcon()
    {
        _appStatusNotifyIcon = new NotifyIcon(components);
        _appStatusNotifyIcon.Visible = true;

        _contextMenuStrip = new ContextMenuStrip();

        var appNameLabel = new ToolStripLabel(AppMetadata.Name + " " + AppMetadata.Version);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= SystemEvents_PowerModeChanged;
            _pollTimer?.Dispose();
            _device?.Dispose();
            _monitor.Dispose();
            components?.Dispose();
        }

        base.Dispose(disposing);
    }

    private void SystemEvents_PowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;

        // Windows may leave a HID handle valid-looking but unusable after sleep.
        // The update loop owns the handle, so ask it to discard and rediscover it.
        // basically, reconnect on every wake regardless, just to be safe.
        Interlocked.Exchange(ref _reconnectRequested, 1);
        Logger.LogInfo("System resumed; scheduling display HID reconnection.");
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
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));

        do
        {
            try
            {
                if (Interlocked.Exchange(ref _reconnectRequested, 0) == 1)
                {
                    _device?.Dispose();
                    _device = null;
                    _payload = null;
                    Logger.LogInfo("Discarded the previous display HID handle after resume.");
                }

                // sample once per tick and reuse for both payload and debug labels
                var cpuTemp = _monitor.GetCpuTemperature();
                var gpuTemp = _monitor.GetGpuTemperature();

                // drop a stale handle (unplug, sleep/resume) so we re-enumerate
                if (_device is { IsConnected: false })
                {
                    _device.Dispose();
                    _device = null;
                }

                if (_device == null)
                {
                    _device = HidDevices.Enumerate(_vendorId, _productId).FirstOrDefault();
                    _payload = null;
                }

                if (_device != null)
                {
                    var reportLength = _device.Capabilities.OutputReportByteLength;
                    if (_payload == null || _payload.Length != reportLength)
                    {
                        _payload = new byte[reportLength];
                        // constant report header; digits and checksum are rewritten each tick
                        _payload[0] = 0;
                        _payload[1] = 85;
                        _payload[2] = 170;
                        _payload[3] = 1;
                        _payload[4] = 1;
                        _payload[5] = 6;
                    }

                    try
                    {
                        FillPayload(_payload, cpuTemp, gpuTemp);
                        _device.Write(_payload);
                        _connectionStatusLabel!.Text = "Connected";
                        _appStatusNotifyIcon.Icon = _iconConnected;
                        _connectionStatusLabel.ForeColor = Color.Green;
                    }
                    catch
                    {
                        // write failed: drop the handle and fall through to reconnect next tick
                        _device.Dispose();
                        _device = null;
                        _payload = null;
                    }
                }

                if (_device == null)
                {
                    _connectionStatusLabel!.Text = "Not Connected";
                    _appStatusNotifyIcon.Icon = _iconDisconnected;
                    _connectionStatusLabel.ForeColor = Color.Crimson;
                }

                if (_debug)
                {
                    _cpuTempDebugLabel!.Text = "CPU Temp: " + Math.Round(cpuTemp ?? 0, 1) + "°C";
                    _gpuTempDebugLabel!.Text = "GPU Temp: " + Math.Round(gpuTemp ?? 0, 1) + "°C";
                }
            }
            catch (Exception ex)
            {
                // never let a single bad tick kill the update loop
                Logger.LogError(ex);
            }
        } while (await _pollTimer.WaitForNextTickAsync());
    }

    /// <summary>
    /// fills the temperature digits and checksum into a pre-allocated payload buffer.
    /// the constant report header (bytes 0-5) is written once at buffer creation.
    /// </summary>
    private static void FillPayload(byte[] payload, float? cpuTemperature, float? gpuTemperature)
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

        byte checksum = 0;
        for (var i = 0; i < 12; i++) checksum += payload[i];
        payload[12] = checksum;
    }
}
