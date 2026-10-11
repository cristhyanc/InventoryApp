namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The single typed configuration contract for the Nayax Lynx integration (issue #49): bound once
/// instead of each reader separately reaching into <c>IConfiguration</c>. See
/// <see cref="NayaxLynxConfiguration"/> for validation, for the base address built from
/// <see cref="BaseUrl"/>, and for how <see cref="AccessToken"/> is resolved.
///
/// Since issue #520 only <see cref="BaseUrl"/> is still global configuration the HTTP client uses.
/// <see cref="OperatorId"/> and <see cref="AccessToken"/> are each business's own, held encrypted in
/// its connection record and resolved per call from the trusted current business, so the two members
/// below are read only by the human-run <c>migrate-nayax-connection</c> command (issue #519), which
/// is what moves a still-configured credential into the business that owns it. A human removes them
/// from the environment afterwards; see docs/tenant-rollout.md.
/// </summary>
public sealed class NayaxLynxOptions
{
    public const string SectionName = "NayaxLynx";

    // Use https://lynx.nayax.com for production, https://qa-lynx.nayax.com for the QA sandbox.
    public string BaseUrl { get; set; } = "https://lynx.nayax.com";

    /// <summary>
    /// The globally configured Nayax operator id, if one is still configured. Not used by the Nayax
    /// client and not required at startup (issue #520): the operator id a call uses is the current
    /// business's own.
    /// </summary>
    public string OperatorId { get; set; } = string.Empty;

    /// <summary>
    /// The globally configured Nayax Core bearer token, if one is still configured. A secret: never
    /// logged and never included in a validation error message. Not used by the Nayax client
    /// (issue #520) - a call authenticates with the current business's own stored token.
    /// </summary>
    public string? AccessToken { get; set; }
}
