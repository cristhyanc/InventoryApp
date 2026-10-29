using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// One business's stored Nayax connection, without its token (issue #518).
///
/// This is the read every status display, use case and later Owner wizard step needs, and it
/// deliberately carries no token and no ciphertext: a type that cannot hold the secret cannot leak
/// it into a response body, a log line or a test snapshot. The one read that does decrypt is
/// <see cref="INayaxConnectionStore.FindCredentialAsync"/>, which returns
/// <see cref="NayaxConnectionCredential"/> instead.
///
/// It also carries no business identifier. The owning business is resolved from the authenticated
/// actor's membership, never from a request and never from a response the client could send back.
/// </summary>
/// <param name="OperatorId">
/// The Nayax operator id the stored token belongs to. It is a remote identity - the
/// <c>OperatorID</c> path parameter of the Lynx API - not a local key, and it is not a secret.
/// </param>
/// <param name="Status">How usable the stored credentials currently are.</param>
/// <param name="CredentialRevision">
/// Increments on every credential save, starting at 1 for the first one. A permission test result
/// is only applied back to the revision it was produced for; see
/// <see cref="INayaxConnectionStore.TryApplyStatusResultAsync"/>.
/// </param>
/// <param name="LastTestedAtUtc">
/// When the stored credentials were last tested, or <see langword="null"/> when the current
/// revision has never been tested. A save clears it, because an untested token must never keep an
/// older token's test result.
/// </param>
/// <param name="UpdatedAtUtc">When this record last changed, for either reason.</param>
public sealed record NayaxConnection(
    string OperatorId,
    NayaxConnectionStatus Status,
    int CredentialRevision,
    DateTime? LastTestedAtUtc,
    DateTime UpdatedAtUtc)
{
    /// <summary>
    /// Whether the last completed connection check left the machine-scoped checks unverified
    /// (issue #537): the business had no machine in Nayax to run them against.
    ///
    /// It is an additive member rather than a sixth positional one so that every existing caller
    /// keeps saying exactly what it said before. <see langword="false"/> is also what a connection
    /// that no check has ever run against reports - including the one the human-run
    /// <c>migrate-nayax-connection</c> command stored - because this flag exists to ask for a
    /// re-test once machines are set up, not to make a connection that works look incomplete.
    /// </summary>
    public bool MachineChecksPending { get; init; }

    /// <summary>
    /// Which Nayax <em>account</em> the stored credentials belong to, as this business's own internal
    /// connection series (issue #978): <c>1</c> for the first account whose credentials were stored,
    /// and one higher each time a <strong>different</strong> operator id is saved. Re-saving or
    /// re-testing the same account keeps it.
    ///
    /// <para>It is deliberately not <see cref="CredentialRevision"/>, which counts every save: a new
    /// token for the same Nayax account is the same fleet, and billing evidence about that fleet must
    /// not be split by an operator pasting a fresh token. And it is deliberately not the operator id
    /// either - the series identifier carries no part of a credential, so a connected-machine
    /// snapshot can name the account it belongs to without naming the account.</para>
    ///
    /// <para>It is an additive member rather than a seventh positional one, like
    /// <see cref="MachineChecksPending"/>, so every existing caller keeps saying what it said before.
    /// <c>1</c> is its default because a stored connection always belongs to a series and the first
    /// one is <c>1</c>.</para>
    /// </summary>
    public int AccountSeries { get; init; } = 1;
}

/// <summary>
/// The outcome of testing one business's stored Nayax credentials, ready to be applied back to the
/// connection record (issue #518).
///
/// <see cref="CredentialRevision"/> is the revision the test ran against, and it is what makes the
/// write conditional: by the time a test finishes, an operator may already have saved a different
/// token, and applying a result produced for the previous credentials would describe a credential
/// pair that no longer exists. The store discards such a result instead of writing it.
/// </summary>
/// <param name="CredentialRevision">The credential revision the test was produced for.</param>
/// <param name="Status">The status the test concluded.</param>
/// <param name="TestedAtUtc">When the test ran.</param>
public sealed record NayaxConnectionStatusResult(
    int CredentialRevision,
    NayaxConnectionStatus Status,
    DateTime TestedAtUtc)
{
    /// <summary>
    /// What this result says about the machine-scoped checks (issue #537), or
    /// <see langword="null"/> when it says nothing about them and the stored value is to be left
    /// alone.
    ///
    /// Only a completed connection check knows: a 401 reported by an ordinary Nayax call
    /// (<see cref="INayaxRequestCredentialProvider.ReportUnauthorizedAsync"/>) and the human-run
    /// credential migration both pass <see langword="null"/>, because neither ran the machine
    /// checks, and overwriting the flag with "not pending" would claim they had.
    /// </summary>
    public bool? MachineChecksPending { get; init; }
}
