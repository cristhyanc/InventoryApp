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

    /// <summary>
    /// Whether the last completed connection check left the machine-scoped checks unverified
    /// (issue #537): the business had no machine in Nayax to run <c>GET machines/{id}</c> and its
    /// siblings against, so those permissions are neither granted nor refused as far as this
    /// application knows.
    ///
    /// It is written only by a save from a tested draft and by a re-test, and only together with the
    /// status those checks concluded. A 401 reported by an ordinary Nayax call leaves it alone,
    /// because such a call never ran the machine checks. <see langword="false"/> is therefore both
    /// "the machine checks passed" and "no check has ever run", which is deliberate: the flag asks
    /// an Owner to re-test once their machines are set up, and a connection that is working must not
    /// be made to look incomplete by a check nobody has run yet.
    /// </summary>
    public bool MachineChecksPending { get; set; }

    /// <summary>
    /// Which Nayax <strong>account</strong> the stored credentials belong to, as this business's own
    /// internal series (issue #978): <c>1</c> for the first account stored, and one higher each time
    /// a <em>different</em> operator id is saved. Re-saving or re-testing the same account leaves it
    /// alone.
    ///
    /// <para>It is deliberately not <see cref="CredentialRevision"/>, which counts every save: a new
    /// token for the same Nayax account is the same fleet, and the connected-machine evidence of
    /// issue #978 must not be split by an operator pasting a fresh token. And it is deliberately not
    /// the operator id: a snapshot names the series, so billing evidence can say which account it
    /// belongs to while carrying no part of a credential.</para>
    /// </summary>
    public int AccountSeries { get; set; } = 1;

    /// <summary>
    /// When <see cref="AccountSeries"/> last changed, or <see langword="null"/> when it never has -
    /// which is every connection stored before issue #978 and every one that has only ever held one
    /// account. It records the account change with its time, which is what lets a day holding two
    /// series' snapshots be explained.
    /// </summary>
    public DateTime? AccountSeriesChangedAtUtc { get; set; }

    /// <summary>When this record last changed, for either reason.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
