using Inventory.Domain.Tenancy;

namespace InventoryApi.Auth.PlatformAdmin;

/// <summary>
/// The non-secret configuration that names the platform super-administrator (issue #336).
///
/// It is an Entra <c>(tid, oid)</c> pair and nothing else - the same stable claim pair ownership
/// already resolves membership from. Deliberately not an email address or a display name, both of
/// which are mutable and reassignable, and deliberately not a <c>BusinessMembership</c> row or any
/// other tenant-owned data: the whole point is that this authority lives outside the rows the
/// diagnostics path exists to investigate, so a database mistake cannot create or destroy it.
///
/// Neither value is a secret. An Entra object id is an identifier, not a credential; it grants
/// nothing without a validated token carrying it, so the pair is ordinary App Service
/// configuration (<c>PlatformAdmin__DirectoryTenantId</c>, <c>PlatformAdmin__ObjectId</c>) rather
/// than a Key Vault secret - and no real identifier may be committed to this repository.
///
/// Both empty is the shipped state and means <em>nobody</em> is a platform administrator. That is
/// the fail-closed default: the policy denies every caller, including a legitimate business member.
/// </summary>
public sealed class PlatformAdminOptions
{
    public const string SectionName = "PlatformAdmin";

    /// <summary>The Entra directory tenant id (<c>tid</c>) of the platform administrator.</summary>
    public string? DirectoryTenantId { get; set; }

    /// <summary>The Entra object id (<c>oid</c>) of the platform administrator.</summary>
    public string? ObjectId { get; set; }
}

/// <summary>
/// The configured platform administrator, resolved once at startup (issue #336).
///
/// Resolution goes through <see cref="ActorIdentity.TryCreate"/>, which is the same normalisation
/// the membership lookup uses: both halves are required, both must be well-formed GUIDs, and the
/// stored form is canonical, so a configuration value written in braced, dashless or differently
/// cased form still matches the token's claims - and a malformed or half-filled setting identifies
/// nobody rather than matching something unintended.
/// </summary>
public sealed class ConfiguredPlatformAdmin
{
    private ConfiguredPlatformAdmin(ActorIdentity? identity)
    {
        Identity = identity;
    }

    /// <summary>The configured administrator, or <c>null</c> when none is configured.</summary>
    public ActorIdentity? Identity { get; }

    public bool IsConfigured => Identity is not null;

    public static ConfiguredPlatformAdmin From(PlatformAdminOptions? options) =>
        new(ActorIdentity.TryCreate(options?.DirectoryTenantId, options?.ObjectId, out var identity)
            ? identity
            : null);

    /// <summary>
    /// Whether this actor is the configured platform administrator. A missing configuration and a
    /// missing actor both answer <c>false</c>: there is no "no administrator configured, so let
    /// everyone in" branch, and no "no actor, so skip the check" one.
    /// </summary>
    public bool Matches(ActorIdentity? actor) =>
        Identity is not null && actor is not null && Identity == actor;
}
