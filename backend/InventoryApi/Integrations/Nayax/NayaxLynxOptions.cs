namespace InventoryApi.Integrations.Nayax;

public class NayaxLynxOptions
{
    public const string SectionName = "NayaxLynx";

    // Use https://lynx.nayax.com for production, https://qa-lynx.nayax.com for the QA sandbox.
    public string BaseUrl { get; set; } = "https://lynx.nayax.com";

    // Bearer token retrieved from Nayax Core. Store the real value in
    // appsettings.Development.json (gitignored), an environment variable, or a secret store —
    // never commit it to source control.
    public string AccessToken { get; set; } = string.Empty;
    public string OperatorId { get; set; } = string.Empty;
}
