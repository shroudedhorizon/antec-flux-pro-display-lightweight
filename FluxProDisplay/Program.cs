using FluxProDisplay.DTOs.AppSettings;
using Microsoft.Extensions.Configuration;

namespace FluxProDisplay;

internal static class Program
{
    private const string MutexName = "FluxProDisplay_SingleInstance_Mutex";

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

                // Run the tray service (no Form window)
                using (var service = new FluxProDisplayService(rootConfig!))
                {
                    Application.Run();
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex);
            throw;
        }
    }
}

internal static class Logger
{
    public static void LogError(Exception ex)
    {
        try
        {
            var logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "FluxProDisplay",
                "error.log");

            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(
                logPath,
                $"[{DateTime.Now:O}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // a failure to log must not crash the app
        }
    }
}