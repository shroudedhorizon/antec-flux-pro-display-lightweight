namespace FluxProDisplay.DTOs.AppSettings;

public class AppSettings
{
    public int PollingInterval { get; set; }
    private string VendorId { get; set; } = null!;
    private string ProductId { get; set; } = null!;
    public int VendorIdInt => ParseHexString(VendorId);
    public int ProductIdInt => ParseHexString(ProductId);

    // parse our hex string
    private static int ParseHexString(string hex)
    {
        if (string.IsNullOrEmpty(hex))
            return 0;

        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            hex = hex.Substring(2);

        return Convert.ToInt32(hex, 16);
    }
}