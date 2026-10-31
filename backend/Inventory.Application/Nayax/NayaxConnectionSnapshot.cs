using System.Globalization;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// One business's stored Nayax connection as it was at a single instant: the status, and the
/// credential read from the same row in the same statement (issue #520).
///
/// It exists because the status and the token have to describe the *same* state of the record. Read
/// separately, through <see cref="INayaxConnectionStore.FindAsync"/> and then
/// <see cref="INayaxConnectionStore.FindCredentialAsync"/>, a credential save landing between the
/// two reads produces a pair that never existed - the previous status beside the new token - and
/// that pair decides both whether the call may run at all and whether a Nayax 403 is a per-feature
/// permission answer or an ordinary upstream failure. A snapshot is what makes those two decisions
/// answerable from one consistent read.
///
/// <see cref="Credential"/> is <see langword="null"/> when the caller's own gate refused
/// <see cref="Status"/>, because the store stops before the ciphertext in that case: a refused state
/// never handles the secret at all. It is never <see langword="null"/> for a status the gate allowed
/// while the row holds credentials.
///
/// <see cref="AccountSeries"/> is read in that same statement, for the same reason and one more
/// (issue #978): it is the non-secret provenance of <em>which Nayax account</em> this snapshot's
/// credentials belong to, so a call made with them can report what it was authenticated as instead of
/// a later, separate read of the connection having to guess. A second read is not a weaker version of
/// this - it is a different fact, because an Owner can save a different account in between.
/// </summary>
public sealed class NayaxConnectionSnapshot
{
    /// <param name="status">The connection status in the row this snapshot was read from.</param>
    /// <param name="credential">
    /// The operator id, decrypted token and revision from the same row, or <see langword="null"/>
    /// when the caller's gate refused <paramref name="status"/> and the token was therefore never
    /// decrypted.
    /// </param>
    /// <param name="accountSeries">
    /// The account series stored on that same row (issue #978), or <see langword="null"/> when the
    /// snapshot's source cannot say. It carries no part of a credential.
    /// </param>
    public NayaxConnectionSnapshot(
        NayaxConnectionStatus status,
        NayaxConnectionCredential? credential,
        int? accountSeries = null)
    {
        Status = status;
        Credential = credential;
        AccountSeries = accountSeries;
    }

    /// <summary>The connection status this snapshot's credential belongs to.</summary>
    public NayaxConnectionStatus Status { get; }

    /// <summary>
    /// Which Nayax account the row this snapshot came from holds credentials for, as the business's
    /// own internal series (issue #978), or <see langword="null"/> when the source cannot say. Never
    /// the operator id, a token or any part of a credential.
    /// </summary>
    public int? AccountSeries { get; }

    /// <summary>
    /// The credential read with <see cref="Status"/>, or <see langword="null"/> when the status was
    /// refused before the token was decrypted. Carries a secret; never serialise it.
    /// </summary>
    public NayaxConnectionCredential? Credential { get; }

    /// <summary>Redacts the token, by rendering the wrapped credential's redacting form.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NayaxConnectionSnapshot {{ Status = {Status}, Credential = {Credential?.ToString() ?? "none"} }}");
}
