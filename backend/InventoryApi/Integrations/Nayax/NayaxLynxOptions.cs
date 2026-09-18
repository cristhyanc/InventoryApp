namespace InventoryApi.Integrations.Nayax;

public class NayaxLynxOptions
{
    public const string SectionName = "NayaxLynx";

    // Use https://lynx.nayax.com for production, https://qa-lynx.nayax.com for the QA sandbox.
    public string BaseUrl { get; set; } = "https://lynx.nayax.com";
 
    public string OperatorId { get; set; } = string.Empty;
}
