namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The non-secret configuration that names the one Microsoft Entra External ID tenant this API
/// trusts (issue #710, under the approved design in docs/identity-provider-decision.md § 5.3).
///
/// Every value here is an identifier, not a credential - the same classification
/// <see cref="PlatformAdmin.PlatformAdminOptions"/> already records. The API validates tokens with
/// the authority's published signing keys and the SPA is a public client using authorization code
/// with PKCE, so no client secret exists on either side and nothing in this section belongs in a
/// Key Vault reference. No real identifier may be committed to this repository.
///
/// All three identifiers empty is the shipped state and means this API trusts no customer identity
/// tenant at all: the second bearer scheme is not registered, every External ID token is
/// <c>401</c>, and workforce Microsoft sign-in is untouched. See
/// <see cref="ExternalIdAuthenticationDecision"/> for what a half-filled section means.
/// </summary>
public sealed class ExternalIdOptions
{
    public const string SectionName = "ExternalId";

    /// <summary>
    /// The External ID authority host, as an absolute HTTPS origin - for example
    /// <c>https://&lt;tenant-subdomain&gt;.ciamlogin.com/</c>. The tenant's OpenID Connect
    /// metadata document is discovered under it, and it is the only place this API ever reads
    /// identity metadata from; a token's own contents never choose an authority.
    /// </summary>
    public string? Instance { get; set; }

    /// <summary>The External ID tenant id: one fixed GUID, and never a template or a pattern.</summary>
    public string? TenantId { get; set; }

    /// <summary>
    /// The public application id of the API app registration <em>in that tenant</em>. It is a
    /// different value from the workforce <c>AzureAd:ClientId</c>, because an app registration
    /// belongs to one tenant, and each scheme validates only its own audience.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// The delegated scope that registration exposes, recorded here so the configured surface is
    /// discoverable and reviewable beside the workforce section. Token validation does not read it:
    /// the required scope is enforced by <c>[RequiredScope("access_as_user")]</c> on every
    /// controller, identically for both schemes, and that is deliberately the one place it is
    /// stated.
    /// </summary>
    public string? Scopes { get; set; }
}
