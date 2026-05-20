using System;
using System.Linq;
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
    public (float? payload1, float? payload2, string? fallbackMessage) GetTemperatures(int displayMode)
    {
        float? cpuTemp = null;
        float? gpuHotspot = null;
        float? gpuPackage = null;
        float? gpuFallbackTemp = null;
        string? fallbackMessage = null;

        lock (_sync)
        {
            if (_disposed)
                return (null, null, null);

            // single pass through hardware; iterate each hardware's sensors only once
            foreach (var hardware in _computer.Hardware)
            {
                // only process CPU and GPU hardware
                if (hardware.HardwareType != HardwareType.Cpu &&
                    hardware.HardwareType != HardwareType.GpuNvidia &&
                    hardware.HardwareType != HardwareType.GpuAmd &&
                    hardware.HardwareType != HardwareType.GpuIntel)
                    continue;

                hardware.Update();
                var sensors = hardware.Sensors.Where(sensor => sensor.SensorType == SensorType.Temperature).ToList();
                foreach (var sensor in sensors)
                {

                    // ensure sensor has valid value
                    if (!sensor.Value.HasValue)
                        continue;
                    var val = sensor.Value.Value;
                    if (float.IsNaN(val) || float.IsInfinity(val))
                        continue;

                    var name = sensor.Name ?? string.Empty;

                    // CPU temperature sensors
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
                        {
                            gpuHotspot = val;
                        }

                        if (gpuPackage == null && name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        {
                            gpuPackage = val;
                        }

                        if (gpuFallbackTemp == null)
                            gpuFallbackTemp = val;
                    }
                }

                if (cpuTemp == null || gpuHotspot == null || gpuPackage == null)
                    continue;
                break; // if we've found all relevant temps, no need to continue iterating through hardware
            }
        }
        // map to opaque payloads according to displayMode
        float? payload1 = null;
        float? payload2 = null;

        switch (displayMode)
        {
            case 0: // payload1 = CPU, payload2 = GPU package
                payload1 = cpuTemp;
                payload2 = gpuPackage ?? gpuFallbackTemp;
                break;
            case 1: // payload1 = CPU, payload2 = GPU hotspot (fallback to package)
                payload1 = cpuTemp;
                if (gpuHotspot != null)
                {
                    payload2 = gpuHotspot;
                }
                else
                {
                    payload2 = gpuPackage ?? gpuFallbackTemp;
                    fallbackMessage = "GPU Hot Spot not found, switching to GPU Package temperature.";
                }
                break;
            case 2:
                payload1 = gpuPackage ?? gpuFallbackTemp;
                payload2 = gpuHotspot ?? payload1;
                if (gpuHotspot == null && (gpuPackage != null || gpuFallbackTemp != null))
                {
                    fallbackMessage = "GPU Hot Spot not found, using GPU Package temperature.";
                }
                break;
            default:
                payload1 = cpuTemp;
                payload2 = gpuPackage ?? gpuFallbackTemp;
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
