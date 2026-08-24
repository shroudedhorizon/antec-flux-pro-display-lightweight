using FluxProDisplay.DTOs.AppSettings;
using Microsoft.Extensions.Configuration;

namespace FluxProDisplay;

internal static class Program
{
    // name of the mutex
    private const string MutexName = "FluxProDisplay_SingleInstance_Mutex";

    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        try
        {
            using (new Mutex(true, MutexName, out var createdNew))
            {
                if (!createdNew)
                {
                    MessageBox.Show(
                        "Another instance of FluxProDisplay is already running.",
                        "Instance Running",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }
                
                // set up appsettings configuration
                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
                    .Build();

                var rootConfig = configuration.Get<RootConfig>();

                if (rootConfig?.AppInfo == null || rootConfig.AppSettings == null)
                {
                    MessageBox.Show(
                        "appsettings.json is missing or malformed. Expected AppInfo and AppSettings sections.",
                        "Configuration Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    Environment.Exit(1);
                }

                ApplicationConfiguration.Initialize();
                Logger.LogInfo($"Application starting. Version: {AppMetadata.Version}; PID: {Environment.ProcessId}.");
                Application.Run(new FluxProDisplayTray(rootConfig!));
                Logger.LogInfo("Application exited cleanly.");
            }
        }
        catch (Exception ex)
        {
            // debug logs for this are located in %APPDATA%\FluxProDisplay\error.log
            Logger.LogError(ex);
            throw;
        }
    }
}

internal static class Logger
{
    public static void LogError(Exception ex)
    {
        Write($"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
    }

    public static void LogInfo(string message)
    {
        Write($"[{DateTime.Now:O}] {message}{Environment.NewLine}");
    }

    private static void Write(string message)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FluxProDisplay",
                "error.log");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, message);
        }
        catch
        {
            // a failure to log must not crash the app
        }
    }
}
