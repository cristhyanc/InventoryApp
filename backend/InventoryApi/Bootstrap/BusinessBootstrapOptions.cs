namespace InventoryApi.Bootstrap;

/// <summary>
/// The human-supplied mapping from real Microsoft Entra identities to the one existing vending
/// business (issue #64, checkpoint 3).
///
/// Nothing here has a usable default and nothing here is committed. The repository ships the
/// section empty; the real values are supplied per environment through user secrets locally or
/// application settings in Azure, because <see cref="BusinessBootstrapMemberOptions.ObjectId"/>
/// identifies a real person. An absent or incomplete section makes the bootstrap refuse to run
/// rather than guess an identity - see docs/tenant-rollout.md.
/// </summary>
public sealed class BusinessBootstrapOptions
{
    public const string SectionName = "BusinessBootstrap";

    /// <summary>
    /// Display name for the Business record created on first bootstrap. It is not an identifier
    /// and is ignored when a business already exists.
    /// </summary>
    public string BusinessName { get; set; } = string.Empty;

    /// <summary>
    /// The Entra actors approved for that business. At least one is required: a business nobody
    /// can sign in to would leave the data unreachable.
    /// </summary>
    public IList<BusinessBootstrapMemberOptions> Members { get; } = [];
}

/// <summary>
/// One approved actor, as the validated Entra <c>(tid, oid)</c> claim pair. No email address or
/// display name: those are mutable and must never decide data ownership.
/// </summary>
public sealed class BusinessBootstrapMemberOptions
{
    /// <summary>The Entra directory tenant id (<c>tid</c>) claim value.</summary>
    public string DirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) claim value.</summary>
    public string ObjectId { get; set; } = string.Empty;
}
