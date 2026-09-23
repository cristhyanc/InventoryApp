namespace Inventory.Domain.Tenancy;

/// <summary>
/// The stable identity of an authenticated actor, as the validated Microsoft Entra
/// <c>(tid, oid)</c> claim pair (issue #64).
///
/// Neither half is a business identifier on its own: <see cref="DirectoryTenantId"/> is the
/// Entra directory the actor signed in from, and <see cref="ObjectId"/> is the actor's stable
/// object id inside that directory. Email address and display name are deliberately not part of
/// this identity - they are mutable, reassignable, and personal data.
///
/// This type is pure domain language. Parsing a <c>ClaimsPrincipal</c> into one stays at the
/// InventoryApi boundary; Domain and Application never see ASP.NET claims.
/// </summary>
public sealed record ActorIdentity
{
    private ActorIdentity(string directoryTenantId, string objectId)
    {
        DirectoryTenantId = directoryTenantId;
        ObjectId = objectId;
    }

    /// <summary>The Entra directory tenant id (<c>tid</c>), normalized for comparison.</summary>
    public string DirectoryTenantId { get; }

    /// <summary>The Entra object id (<c>oid</c>) of the actor, normalized for comparison.</summary>
    public string ObjectId { get; }

    /// <summary>
    /// Builds an actor identity from raw claim values. Both halves are required: a token missing
    /// either one cannot identify an actor, so the caller must fail closed rather than fall back
    /// to a weaker identifier such as email or the directory tenant alone.
    /// </summary>
    public static bool TryCreate(string? directoryTenantId, string? objectId, out ActorIdentity? identity)
    {
        var tenant = Normalize(directoryTenantId);
        var oid = Normalize(objectId);

        if (tenant is null || oid is null)
        {
            identity = null;
            return false;
        }

        identity = new ActorIdentity(tenant, oid);
        return true;
    }

    /// <summary>
    /// Trims and upper-cases the value so a lookup is not defeated by the casing a token or a
    /// hand-entered membership row happens to use. Upper- rather than lower-casing is the
    /// analyzer-approved normalization form (CA1308); these values are opaque comparison keys
    /// and are never displayed.
    /// </summary>
    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant();
    }

    public override string ToString() => $"{DirectoryTenantId}/{ObjectId}";
}
