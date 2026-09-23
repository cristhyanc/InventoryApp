using System.Text.Json.Serialization;

namespace InventoryApi.Models;

/// <summary>
/// Approves one authenticated Microsoft Entra actor, identified by the validated
/// <c>(tid, oid)</c> claim pair, for one <see cref="Models.Business"/> (issue #64).
///
/// This table is the application's authorization boundary for data ownership: an actor with no
/// active row here reads no business data, whatever Entra directory issued a valid token. Rows
/// are created by an explicit, human-supplied bootstrap, never by self-service sign-up.
///
/// It deliberately stores no email address, display name, or other personal identifier: those
/// are mutable and reassignable, so they must not decide ownership, and keeping them out avoids
/// putting personal data in the repository's schema.
/// </summary>
public class BusinessMembership
{
    public int Id { get; set; }

    public int BusinessId { get; set; }

    [JsonIgnore]
    public virtual Business? Business { get; set; }

    /// <summary>The Entra directory tenant id (<c>tid</c>) the actor signs in from.</summary>
    public string DirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The actor's stable Entra object id (<c>oid</c>) within that directory.</summary>
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>
    /// Revoking a membership sets this to false rather than deleting the row, so the revoked
    /// approval stays visible and access stops on the next request.
    ///
    /// This is a current-state flag, not an audit trail: the row records only that the
    /// membership is now inactive, not who revoked it or when. A historical audit log of
    /// membership changes is not part of this change.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; }
}
