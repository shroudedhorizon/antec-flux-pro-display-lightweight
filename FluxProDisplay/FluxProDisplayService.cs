using FluxProDisplay.DTOs.AppSettings;
using HidLibrary;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32.TaskScheduler;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using Task = System.Threading.Tasks.Task;

namespace FluxProDisplay;

public class FluxProDisplayService : IDisposable
{
    private readonly HardwareMonitor _monitor;
    private ToolStripLabel? _connectionStatusLabel;
    private ToolStripLabel? _cpuTempDebugLabel;
    private ToolStripLabel? _gpuTempDebugLabel;
    private ToolStripMenuItem? _startupToggleMenuItem;
    private const string ElevatedTaskName = "FluxProDisplayElevatedTask";

    // minimum version needed for pawnIO so it runs fine.
    private const string PawnIoLatestVersion = "2.1.0.0";

    // Lazily cache the PawnIO installer bytes to avoid repeated extraction
    private static readonly Lazy<byte[]?> CachedPawnIoInstaller = new(ExtractPawnIoInstallerBytes, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly bool _debug;
    private readonly int _pollingInterval;
    private readonly int _vendorId;
    private readonly int _productId;

    private NotifyIcon _appStatusNotifyIcon = null!;
    private ContextMenuStrip _contextMenuStrip = null!;

    private PeriodicTimer? _pollTimer;
    private HidDevice? _device;
    private byte[]? _payload;

    private readonly Icon _iconConnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_connected.ico"));
    private readonly Icon _iconDisconnected = new Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon_disconnected.ico"));

    public FluxProDisplayService(RootConfig configuration)
    {
        CheckForIUnity();
        CheckForPawnIoDriver();

        _monitor = new HardwareMonitor();

        _debug = configuration.AppInfo.Debug;
        _pollingInterval = configuration.AppSettings.PollingInterval;
        _vendorId = configuration.AppSettings.VendorIdInt;
        _productId = configuration.AppSettings.ProductIdInt;

        SetUpTrayIcon();

        _ = WriteToDisplay().ContinueWith(
            t => Logger.LogError(t.Exception!),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private void SetUpTrayIcon()
    {
        _appStatusNotifyIcon = new NotifyIcon()
        {
            Visible = true
        };

        _contextMenuStrip = new ContextMenuStrip();

        var appNameLabel = new ToolStripLabel(AppMetadata.Name + " " + AppMetadata.Version);
        appNameLabel.ForeColor = Color.Gray;
        appNameLabel.Enabled = false;
        _contextMenuStrip.Items.Add(appNameLabel);

        if (_debug)
        {
            AddDebugMenuItems();
        }

        _contextMenuStrip.Items.Add(new ToolStripSeparator());

        _connectionStatusLabel = new ToolStripLabel();
        _connectionStatusLabel.ForeColor = Color.Crimson;
        _connectionStatusLabel.Enabled = true;
        _contextMenuStrip.Items.Add(_connectionStatusLabel);

        _startupToggleMenuItem = new ToolStripMenuItem();
        _startupToggleMenuItem.Click += StartupToggleMenuItemClicked;

        var quitMenuItem = new ToolStripMenuItem("Quit");
        quitMenuItem.Click += QuitMenuItem_Click!;

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

    public void Dispose()
    {
        _pollTimer?.Dispose();
        _device?.Dispose();
        _monitor.Dispose();
        _appStatusNotifyIcon?.Dispose();
        _contextMenuStrip?.Dispose();
        _iconConnected?.Dispose();
        _iconDisconnected?.Dispose();
    }

    private async Task WriteToDisplay()
    {
        _pollTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(_pollingInterval));

        do
        {
            try
            {
                var cpuTemp = _monitor.GetCpuTemperature();
                var gpuTemp = _monitor.GetGpuTemperature();

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
                Logger.LogError(ex);
            }
        } while (await _pollTimer.WaitForNextTickAsync());
    }

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

    /// <summary>
    /// check if IUnity is running on the user's system.
    /// </summary>
    private static void CheckForIUnity()
    {
        var isRunning =
            Process.GetProcessesByName("iunity").Length > 0 ||
            Process.GetProcessesByName("AntecHardwareMonitorWindowsService").Length > 0;

        if (!isRunning) return;

        MessageBox.Show("iUnity is running, please end the iUnity program and its related processes from task manager and try again.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
    }

    /// <summary>
    /// checks for the PawnIO driver. If it's not installed, then install it.
    /// </summary>
    private static void CheckForPawnIoDriver()
    {
        if (PawnIo.IsInstalled)
        {
            if (PawnIo.Version < new Version(PawnIoLatestVersion))
            {
                var result = MessageBox.Show("PawnIO driver is outdated, do you want to update it?", nameof(FluxProDisplay), MessageBoxButtons.OKCancel);
                if (result == DialogResult.OK)
                {
                    InstallPawnIoDriver();
                }
                else
                {
                    Environment.Exit(1);
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
                Environment.Exit(1);
            }
        }
    }

    /// <summary>
    /// installs the PawnIO driver.
    /// </summary>
    /// <exception cref="Exception"></exception>
    private static void InstallPawnIoDriver()
    {
        var destination = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

        try
        {
            var installerBytes = CachedPawnIoInstaller.Value;
            if (installerBytes == null)
                throw new Exception("Embedded installer not found");

            // Write cached installer bytes to temp file
            File.WriteAllBytes(destination, installerBytes);

            // run uninstaller, always uninstall if an install is needed.
            var uninstallProcess = Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                Arguments = "-uninstall -silent",
                UseShellExecute = true
            });

            uninstallProcess?.WaitForExit();

            // run installer
            var installProcess = Process.Start(new ProcessStartInfo
            {
                FileName = destination,
                Arguments = "-install",
                UseShellExecute = true
            });

            installProcess?.WaitForExit();

            File.Delete(destination);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message);
        }
    }

    /// <summary>
    /// Extracts the embedded PawnIO installer bytes from the assembly.
    /// This is called lazily on first access and cached thereafter.
    /// </summary>
    private static byte[]? ExtractPawnIoInstallerBytes()
    {
        try
        {
            using (var resourceStream = typeof(FluxProDisplayService).Assembly
                       .GetManifestResourceStream("FluxProDisplay.Assets.PawnIO_setup.exe"))
            {
                if (resourceStream == null)
                    return null;

                using (var memoryStream = new MemoryStream())
                {
                    resourceStream.CopyTo(memoryStream);
                    return memoryStream.ToArray();
                }
            }
        }
        catch
        {
            return null;
        }
    }
}