using System.Diagnostics.CodeAnalysis;

namespace InventoryApi.Auth.ExternalIdentity;

/// <summary>
/// The one configured External ID tenant, resolved once at startup from
/// <see cref="ExternalIdOptions"/> (issue #710, design § 5.1 rules 2-4).
///
/// It holds a single tenant id, a single metadata authority and a single audience pair, because
/// the External ID scheme is deliberately single-tenant and pinned: one issuer, taken from that
/// tenant's own metadata document, and one audience. Nothing here is templated, wildcarded or
/// pattern-matched, and nothing here is ever derived from a token.
/// </summary>
public sealed class ExternalIdAuthority
{
    private ExternalIdAuthority(Guid tenantId, string metadataAuthority, Guid clientId)
    {
        TenantId = tenantId;
        MetadataAuthority = metadataAuthority;
        ClientId = clientId;
        ValidAudiences = PinnedAudiences.For(clientId.ToString("D"));
    }

    /// <summary>The tenant every accepted External ID token must claim in <c>tid</c>.</summary>
    public Guid TenantId { get; }

    /// <summary>
    /// The authority whose OpenID Connect metadata document publishes the issuer and the signing
    /// keys this scheme accepts. The issuer is read from that document rather than built here:
    /// Microsoft documents the v2 form as
    /// <c>https://&lt;tenant-id&gt;.ciamlogin.com/&lt;tenant-id&gt;/v2.0</c> and says to take it
    /// from the metadata (design § 13 item 2), so this application pins what the tenant publishes
    /// instead of a string of its own.
    /// </summary>
    public string MetadataAuthority { get; }

    /// <summary>The API app registration in that tenant.</summary>
    public Guid ClientId { get; }

    /// <summary>
    /// The two spellings of that registration's audience, pinned explicitly rather than inferred.
    /// </summary>
    public IReadOnlyList<string> ValidAudiences { get; }

    /// <summary>
    /// Whether an <em>unvalidated</em> issuer string names this tenant, used by
    /// <see cref="IssuerSelectingPolicyScheme"/> to choose which scheme performs the real
    /// validation and for nothing else.
    ///
    /// Choosing a scheme is not trusting a claim: the scheme chosen here still validates the
    /// signature, the issuer, the audience and the tenant binding completely, so a token routed
    /// here that this tenant did not issue ends in <c>401</c>. The match is deliberately on the
    /// tenant id rather than on the issuer's exact shape - Microsoft's documented endpoint host
    /// uses the tenant's subdomain while its issuer uses the tenant id, and a routing rule that
    /// guessed wrong would refuse the <em>workforce</em> tokens this task must preserve.
    /// </summary>
    public bool NamesThisTenant([NotNullWhen(true)] string? unvalidatedIssuer)
    {
        if (!Uri.TryCreate(unvalidatedIssuer, UriKind.Absolute, out var issuer))
        {
            return false;
        }

        var firstHostLabel = issuer.Host.Split('.', 2)[0];
        var firstPathSegment = issuer.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return IsThisTenant(firstHostLabel) || IsThisTenant(firstPathSegment);
    }

    /// <summary>
    /// Builds the authority from configuration, or answers why it cannot. Every rule is a
    /// fail-closed one: a setting that is absent, blank, or not the kind of value the setting
    /// means leaves this API with no customer identity tenant rather than with a loosely
    /// configured one.
    /// </summary>
    internal static bool TryCreate(
        ExternalIdOptions options,
        [NotNullWhen(true)] out ExternalIdAuthority? authority,
        [NotNullWhen(false)] out string? problem)
    {
        authority = null;
        problem = null;

        var instance = options.Instance?.Trim();
        var tenantId = options.TenantId?.Trim();
        var clientId = options.ClientId?.Trim();

        if (!Uri.TryCreate(instance, UriKind.Absolute, out var instanceUri)
            || !string.Equals(instanceUri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
            || !string.IsNullOrEmpty(instanceUri.Query)
            || !string.IsNullOrEmpty(instanceUri.Fragment)
            || instanceUri.AbsolutePath.Trim('/').Length > 0)
        {
            problem = $"{ExternalIdOptions.SectionName}:{nameof(ExternalIdOptions.Instance)} must be an absolute HTTPS authority host with no path, query or fragment";
            return false;
        }

        if (!Guid.TryParse(tenantId, out var tenant) || tenant == Guid.Empty)
        {
            problem = $"{ExternalIdOptions.SectionName}:{nameof(ExternalIdOptions.TenantId)} must be the External ID tenant's GUID";
            return false;
        }

        if (!Guid.TryParse(clientId, out var client) || client == Guid.Empty)
        {
            problem = $"{ExternalIdOptions.SectionName}:{nameof(ExternalIdOptions.ClientId)} must be the GUID application id of the API registration in that tenant";
            return false;
        }

        authority = new ExternalIdAuthority(
            tenant,
            $"{instanceUri.GetLeftPart(UriPartial.Authority)}/{tenant:D}/v2.0",
            client);
        return true;
    }

    private bool IsThisTenant(string? candidate) =>
        Guid.TryParse(candidate, out var parsed) && parsed == TenantId;
}
