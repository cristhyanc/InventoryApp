namespace InventoryApi.Auth.PriceCollector;

/// <summary>
/// The non-secret configuration that names the one scheduled price collector this API trusts (issue
/// #924, parent #817).
///
/// <para>Every value here is an identifier, not a credential - the same classification
/// <see cref="PlatformAdmin.PlatformAdminOptions"/> and
/// <see cref="ExternalIdentity.ExternalIdOptions"/> already record. The collector authenticates with
/// a short-lived Microsoft Entra token it obtains through GitHub OIDC workload identity federation,
/// so there is no Restockroo password and no client secret on either side: nothing in this section
/// belongs in a Key Vault reference, and no real identifier may be committed to this repository.
/// </para>
///
/// <para><strong>Both identifiers empty is the shipped state, and it means this API trusts no
/// collector at all</strong>: the collector bearer scheme refuses every token it is handed, the
/// collector authorization policy denies everybody, and <c>GET /api/price-collector/work</c> answers
/// <c>401</c> to every request. See <see cref="PriceCollectorAuthenticationDecision"/> for what a
/// half-filled section means.</para>
///
/// <para><strong>The tenant is pinned because the workforce scheme is multi-tenant.</strong> The API
/// registration is reachable from <c>TenantId: common</c>, so another Entra tenant could consent to
/// it and assign the collector app role to an app of its own; a token from that tenant would carry a
/// genuine Microsoft signature and the right role. Pinning <c>tid</c> and the client application id
/// is what makes that token somebody else's - it is mandatory, not defence in depth.</para>
/// </summary>
public sealed class PriceCollectorOptions
{
    public const string SectionName = "PriceCollector";

    /// <summary>
    /// The Entra tenant the collector's app registration lives in: one fixed GUID, never a template
    /// or a pattern, and the only value an accepted token's <c>tid</c> may be.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The public application id of the collector's own app registration in that tenant, which is the
    /// only value an accepted token's <c>azp</c> or <c>appid</c> may be. It is a different value from
    /// the API registration's <c>AzureAd:ClientId</c>: one is who is calling, the other is who is
    /// being called.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// How many hours an issued run stays open unless the collector reports back. Blank,
    /// unparseable, or outside the range <c>PriceCollectorRunPolicy</c> accepts all mean the owner's
    /// default of six hours; it is read through
    /// <c>Inventory.Application.PriceCatalogue.IPriceCollectorRunSettings</c> so the Application
    /// layer owns the decision and this layer owns the source.
    /// </summary>
    public string? RunExpiryHours { get; set; }
}
