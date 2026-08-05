using LibreHardwareMonitor.Hardware;

namespace FluxProDisplay;

public class HardwareMonitor : IDisposable
{
    private readonly Computer _computer;
    private IHardware? _cpuHardware;
    private ISensor? _cpuSensor;
    private IHardware? _gpuHardware;
    private ISensor? _gpuSensor;

    // Track if we've already tried to resolve sensors to avoid repeated failed searches
    private bool _cpuSensorResolved;
    private bool _gpuSensorResolved;

    // Cache for temperature values to return sensible defaults if read fails
    private float _lastCpuTemp;
    private float _lastGpuTemp;

    public HardwareMonitor()
    {
        _computer = new Computer()
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true
        };

        try
        {
            _computer.Open();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }
    }

    public void Dispose()
    {
        try
        {
            _computer.Close();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
        finally
        {
            GC.SuppressFinalize(this);
        }
    }

    public float? GetCpuTemperature()
    {
        try
        {
            // Resolve sensor on first call only
            if (!_cpuSensorResolved)
            {
                ResolveCpuSensor();
                _cpuSensorResolved = true;
            }

            if (_cpuSensor == null)
                return null;

            _cpuHardware!.Update();
            var temp = _cpuSensor.Value;

            // Only cache if we got a valid reading
            if (temp.HasValue && temp > 0)
            {
                _lastCpuTemp = temp.Value;
            }

            return temp;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            // Return last known good value, or null if never set
            return _lastCpuTemp > 0 ? _lastCpuTemp : null;
        }
    }

    public float? GetGpuTemperature()
    {
        try
        {
            if (!_gpuSensorResolved)
            {
                ResolveGpuSensor();
                _gpuSensorResolved = true;
            }

            if (_gpuSensor == null)
                return null;

            _gpuHardware!.Update();
            var temp = _gpuSensor.Value;

            if (temp.HasValue && temp > 0)
            {
                _lastGpuTemp = temp.Value;
            }

            return temp;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            return _lastGpuTemp > 0 ? _lastGpuTemp : null;
        }
    }

    private void ResolveCpuSensor()
    {
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType != HardwareType.Cpu)
                    continue;

                hardware.Update();

                ISensor? fallbackSensor = null;
                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType != SensorType.Temperature)
                        continue;

                    // Prefer specific sensor names (AMD & newer Intel)
                    if (sensor.Name.Contains("Tctl/Tdie", StringComparison.OrdinalIgnoreCase) ||
                        sensor.Name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase))
                    {
                        _cpuHardware = hardware;
                        _cpuSensor = sensor;
                        return;
                    }

                    // Keep first temperature sensor as fallback
                    fallbackSensor ??= sensor;
                }

                // Fallback to any available CPU temp (laptops, Intel E-cores, older AMD)
                if (fallbackSensor != null)
                {
                    _cpuHardware = hardware;
                    _cpuSensor = fallbackSensor;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }

    private void ResolveGpuSensor()
    {
        try
        {
            foreach (var hardware in _computer.Hardware)
            {
                if (hardware.HardwareType is not (HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel))
                    continue;

                hardware.Update();

                foreach (var sensor in hardware.Sensors)
                {
                    if (sensor.SensorType == SensorType.Temperature &&
                        sensor.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase))
                    {
                        _gpuHardware = hardware;
                        _gpuSensor = sensor;
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
        }
    }
}