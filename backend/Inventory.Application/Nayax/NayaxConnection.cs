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
    DateTime UpdatedAtUtc);

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
    DateTime TestedAtUtc);
