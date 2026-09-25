namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The single typed configuration contract for the Nayax Lynx HTTP client (issue #49): every
/// setting the concrete adapter needs, bound once instead of the client separately reading
/// <c>IConfiguration</c> for the token. See <see cref="NayaxLynxConfiguration"/> for validation
/// and for how <see cref="AccessToken"/> is resolved.
/// </summary>
public sealed class NayaxLynxOptions
{
    public const string SectionName = "NayaxLynx";

    // Use https://lynx.nayax.com for production, https://qa-lynx.nayax.com for the QA sandbox.
    public string BaseUrl { get; set; } = "https://lynx.nayax.com";

    public string OperatorId { get; set; } = string.Empty;

    /// <summary>
    /// The Nayax Core bearer token. A secret: never logged and never included in a validation
    /// error message. Absent means requests are sent unauthenticated, matching prior behaviour.
    /// </summary>
    public string? AccessToken { get; set; }
}
