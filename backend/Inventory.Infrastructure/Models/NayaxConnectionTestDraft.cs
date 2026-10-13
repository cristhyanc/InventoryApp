using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One business's short-lived, encrypted Nayax connection-test draft (issue #536, a slice of #506).
/// At most one row per business, enforced by a unique index on the ownership column.
///
/// <para>It is <strong>not</strong> <see cref="BusinessNayaxConnection"/> and must never be confused
/// with it. That record is the business's accepted connection and is what every Nayax call
/// authenticates with; this one holds a pair an Owner has pasted and that nothing has accepted. No
/// ordinary Nayax call reads it, it carries no <c>NayaxConnectionStatus</c> and no credential
/// revision, and it expires on its own. Promoting a draft into the connection is a separate,
/// reviewed step (#537).</para>
///
/// <para>The token is stored only as ciphertext, with the id of the key that produced it beside it,
/// exactly as on the connection: the test exists so a token can be checked before it is accepted,
/// and holding it in plaintext for fifteen minutes to achieve that would defeat the point. Nothing
/// here is ever serialised to a client - there is no DTO over this entity, and the secret-bearing
/// columns carry <see cref="JsonIgnoreAttribute"/> as well, because a credential must not become
/// part of an API response by someone adding a convenient projection later.</para>
///
/// <para>The draft is bound to the Owner who created it as well as to the business, by the validated
/// Entra <c>(tid, oid)</c> pair that is this application's identity for an actor. An email address or
/// display name is deliberately not stored, following <see cref="BusinessMembership"/>: both are
/// mutable, reassignable personal data.</para>
/// </summary>
public class NayaxConnectionTestDraft : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The opaque draft identifier returned to the caller. A random GUID rather than a sequence, so
    /// the identifier of a short-lived credential holder cannot be guessed or walked.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The Nayax operator id that was tested - the Lynx API's <c>OperatorID</c> path parameter. A
    /// remote identity, never a local key, and not a secret.
    /// </summary>
    public string OperatorId { get; set; } = string.Empty;

    /// <summary>
    /// The encrypted Lynx API access token that was tested. Never a plaintext token, and never
    /// logged, projected into a DTO or exposed by a report.
    /// </summary>
    [JsonIgnore]
    public string AccessTokenCiphertext { get; set; } = string.Empty;

    /// <summary>
    /// The id of the encryption key <see cref="AccessTokenCiphertext"/> was produced with, stored
    /// with the ciphertext so a rotated key can still decrypt what the previous one wrote.
    /// </summary>
    [JsonIgnore]
    public string EncryptionKeyId { get; set; } = string.Empty;

    /// <summary>
    /// The serialized check report, as the server produced it. It carries only which checks ran and
    /// how each answered: no token, no ciphertext and no Nayax response body.
    /// </summary>
    public string ResultsJson { get; set; } = string.Empty;

    /// <summary>The Entra directory tenant id (<c>tid</c>) of the Owner who ran the test.</summary>
    public string CreatedByDirectoryTenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (<c>oid</c>) of the Owner who ran the test.</summary>
    public string CreatedByObjectId { get; set; } = string.Empty;

    /// <summary>When the test ran.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// When the draft stops existing. A read past this instant reports no draft at all, so an
    /// abandoned test leaves nothing usable behind even before the row itself is replaced.
    /// </summary>
    public DateTime ExpiresAt { get; set; }
}
