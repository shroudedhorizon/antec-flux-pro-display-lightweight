namespace FluxProDisplay.DTOs.AppSettings;

public class RootConfig
{
    public AppInfo AppInfo { get; set; } = null!;
    public AppSettings AppSettings { get; set; } = null!;
    public Git Git { get; set; } = null!;
}