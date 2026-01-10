using System.Diagnostics;
using LibreHardwareMonitor.PawnIo;

namespace FluxProDisplay;

public static class PreflightChecks
{
    /// <summary>
    /// check if IUnity is running on the user's system.
    /// </summary>
    public static void CheckForIUnity()
    {
        var isRunning =
            Process.GetProcessesByName("iunity").Length > 0 ||
            Process.GetProcessesByName("AntecHardwareMonitorWindowsService").Length > 0;

        if (!isRunning) return;

        MessageBox.Show("iUnity is running, please end the iUnity program and its related processes from task manager and try again.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(0);
    }

    /// <summary>
    ///  checks for the PawnIO driver. If it's not installed, then install it.
    /// </summary>
    public static void CheckForPawnIoDriver()
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
    
    /// <summary>
    /// installs the PawnIO driver.
    /// </summary>
    /// <exception cref="Exception"></exception>
    private static void InstallPawnIoDriver()
    {
        var destination = Path.Combine(Path.GetTempPath(), "PawnIO_setup.exe");

        try
        {
            using (var resourceStream = typeof(FluxProDisplayTray).Assembly
                       .GetManifestResourceStream("FluxProDisplay.Assets.PawnIO_setup.exe"))
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

}