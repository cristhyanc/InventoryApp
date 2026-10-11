using System.Text.Json.Serialization;
using Inventory.Domain.Nayax;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One business's own Nayax Lynx credentials and their connection state (issue #518, a slice of
/// #500). Exactly one row per business, enforced by a unique index on the ownership column.
///
/// The row exists only once credentials have been stored, so
/// <see cref="NayaxConnectionStatus.NotConfigured"/> is the state of a business with *no* row and
/// <see cref="AccessTokenCiphertext"/>/<see cref="EncryptionKeyId"/> are never empty on a stored
/// one: "nothing configured" and "configured but broken" stay distinguishable.
///
/// The token is stored only as ciphertext. Encryption, the key and the key id all belong to
/// <c>Inventory.Infrastructure.Nayax.INayaxTokenProtector</c>; this entity holds the ciphertext and
/// the id of the key that produced it so the key can be rotated without re-encrypting every row at
/// once. Nothing here is ever serialised to a client - there is no DTO over this entity, and the
/// secret-bearing columns carry <see cref="JsonIgnoreAttribute"/> as well, because a credential
/// must not become part of an API response by someone adding a convenient projection later.
///
/// Since issue #520 this record is what every Nayax call authenticates with: the client resolves
/// the operator id and the decrypted token from the current business's own row, per call, and there
/// is no global credential left to fall back to.
/// </summary>
public class BusinessNayaxConnection : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert and
    /// immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }

    /// <summary>
    /// The Nayax operator id these credentials belong to - the Lynx API's <c>OperatorID</c> path
    /// parameter, confirmed against the Nayax developer documentation. A remote identity, never a
    /// local key, and not a secret.
    /// </summary>
    public string OperatorId { get; set; } = string.Empty;

    /// <summary>
    /// The encrypted Lynx API access token. Never a plaintext token, and never logged, projected
    /// into a DTO or exposed by a report.
    /// </summary>
    [JsonIgnore]
    public string AccessTokenCiphertext { get; set; } = string.Empty;

    /// <summary>
    /// The id of the encryption key <see cref="AccessTokenCiphertext"/> was produced with. Stored
    /// with the ciphertext so a rotated key can still decrypt what the previous one wrote, and so
    /// a ciphertext whose key is no longer configured fails closed instead of decrypting to
    /// nonsense.
    /// </summary>
    [JsonIgnore]
    public string EncryptionKeyId { get; set; } = string.Empty;

    /// <summary>How usable the stored credentials currently are.</summary>
    public NayaxConnectionStatus Status { get; set; }

    /// <summary>
    /// Increments on every credential save, starting at 1. A permission test result carries the
    /// revision it was produced for and is applied only while this value still matches it, so a
    /// result for superseded credentials cannot describe the credentials that replaced them.
    /// </summary>
    public int CredentialRevision { get; set; }

    /// <summary>
    /// When the current revision's credentials were last tested, or <see langword="null"/> when
    /// they never were. Cleared by every save: a new token never inherits an older one's result.
    /// </summary>
    public DateTime? LastTestedAtUtc { get; set; }

    /// <summary>When this record last changed, for either reason.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
