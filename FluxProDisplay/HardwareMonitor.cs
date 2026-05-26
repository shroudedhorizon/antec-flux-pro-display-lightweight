using System;
using System.Linq;
using FluxProDisplay.Enum;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Cpu;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private readonly object _sync = new object();
    private bool _disposed;

    public HardwareMonitor()
    {
        _computer = new Computer()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };

        _computer.Open();
        _computer.Accept(new UpdateVisitor());
    }


    /// <summary>
    /// Reads CPU and GPU sensors in a single pass and returns temperatures based on display mode.
    /// displayMode:
    /// 0 = Cpu + Gpu Package
    /// 1 = Cpu + Gpu Hotspot (fallback to package)
    /// 2 = Gpu Hotspot + Package
    /// Returns tuple: (cpuTemp, gpuForPayload, gpuHotspot, gpuPackage, resultingDisplayMode, didFallback)
    /// </summary>
    // returns payload1, payload2 and an optional fallback message (non-null when a fallback occurred)
    internal (float? payload1, float? payload2, string? fallbackMessage) GetTemperatures(DisplayModeEnum displayMode)
    {
        float? cpuTemp = null;
        float? gpuHotspot = null;
        float? gpuPackage = null;
        string? fallbackMessage = null;

        // determine which sensors are required for the requested display mode
        bool needCpu = displayMode == DisplayModeEnum.CPU_GPU_PACKAGE
                       || displayMode == DisplayModeEnum.CPU_GPU_HOTSPOT;
        bool needGpuHotspot = displayMode == DisplayModeEnum.CPU_GPU_HOTSPOT
                              || displayMode == DisplayModeEnum.GPU_PACKAGE_GPU_HOTSPOT;
        bool needGpuPackage = displayMode == DisplayModeEnum.CPU_GPU_PACKAGE
                              || displayMode == DisplayModeEnum.GPU_PACKAGE_GPU_HOTSPOT;

        lock (_sync)
        {
            if (_disposed)
                return (null, null, null);

            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu &&
                    hardware.HardwareType != HardwareType.GpuNvidia &&
                    hardware.HardwareType != HardwareType.GpuAmd &&
                    hardware.HardwareType != HardwareType.GpuIntel)
                    continue;

                hardware.Update();
                var sensors = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature).ToList();
                foreach (var sensor in sensors)
                {
                    if (!sensor.Value.HasValue)
                        continue;
                    var val = sensor.Value.Value;
                    if (float.IsNaN(val) || float.IsInfinity(val))
                        continue;

                    var name = sensor.Name ?? string.Empty;

                    if (hardware.HardwareType == HardwareType.Cpu && cpuTemp == null)
                    {
                        if (name.Contains("Tctl/Tdie") || name.Contains("CPU Package"))
                        {
                            cpuTemp = val;
                        }
                    }
                    else
                    {
                        if (gpuHotspot == null && name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase))
                            gpuHotspot = val;

                        if (gpuPackage == null && name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                            gpuPackage = val;
                    }

                    // stop early only when all sensors required by the display mode were found
                    if ((!needCpu || cpuTemp != null) &&
                        (!needGpuHotspot || gpuHotspot != null) &&
                        (!needGpuPackage || gpuPackage != null))
                    {
                        break;
                    }

                }
            }
        }
        // map to opaque payloads according to displayMode
        float? payload1 = null;
        float? payload2 = null;

        switch (displayMode)
        {
            case DisplayModeEnum.CPU_GPU_PACKAGE:
                payload1 = cpuTemp;
                if (gpuPackage != null)
                {
                    payload2 = gpuPackage;
                    break;
                }
                fallbackMessage = "GPU Package not found, using hotspot GPU temperature as fallback.";
                payload2 = gpuHotspot;
                break;
            case DisplayModeEnum.CPU_GPU_HOTSPOT:
                payload1 = cpuTemp;
                if (gpuHotspot != null)
                {
                    payload2 = gpuHotspot;
                    break;
                }
                fallbackMessage = "GPU Hot Spot found, using  GPU Package as fallback.";
                payload2 = gpuPackage;
                break;        
            case DisplayModeEnum.GPU_PACKAGE_GPU_HOTSPOT:
                payload1 = gpuPackage;
                payload2 = gpuHotspot;
                // local helper to append with newline only when needed
                static string AppendMsg(string? existing, string addition) =>
                    string.IsNullOrEmpty(existing) ? addition : existing + Environment.NewLine + addition;
                if (gpuPackage == null)
                {
                    fallbackMessage = "GPU gpuPackage temperature not found.";
                }
                if (gpuHotspot == null)
                {
                    fallbackMessage = AppendMsg(fallbackMessage, "GPU Hot Spot not found.");
                }
                break;
            default:
                payload1 = payload2 = 0;
                fallbackMessage = "Unsupported or invalid display mode";
                break;
        }

        return (payload1, payload2, fallbackMessage);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            try
            {
                _computer.Close();
            }
            catch
            {
                // ignore
            }

            if (_computer is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }

            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }
}
