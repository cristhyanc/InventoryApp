using System.Globalization;

namespace Inventory.Application.Nayax;

/// <summary>
/// One business's Nayax operator id together with its decrypted access token (issue #518).
///
/// This is the only type in the application that carries a Nayax token in plaintext, which is why
/// it is deliberately not a record: a record's generated <c>ToString</c> prints every member, so a
/// single interpolated log line, a structured logging property or an assertion message would put
/// the token into telemetry or a test snapshot. <see cref="ToString"/> is overridden to redact it
/// instead, and the token-free <see cref="NayaxConnection"/> is what every read that does not have
/// to authenticate to Nayax uses.
///
/// Obtained only from <see cref="INayaxConnectionStore.FindCredentialAsync"/>, which decrypts the
/// stored ciphertext for the current business. Never persist, serialise, cache or return this type
/// across an HTTP boundary.
/// </summary>
public sealed class NayaxConnectionCredential
{
    /// <param name="operatorId">The Nayax operator id the token belongs to; not a secret.</param>
    /// <param name="accessToken">
    /// The decrypted Lynx API access token, sent as an <c>Authorization: Bearer</c> credential.
    /// </param>
    /// <param name="credentialRevision">
    /// The revision these credentials were stored as, so a caller that tests them can apply its
    /// result back to the exact revision it tested
    /// (<see cref="INayaxConnectionStore.TryApplyStatusResultAsync"/>).
    /// </param>
    public NayaxConnectionCredential(string operatorId, string accessToken, int credentialRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operatorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        OperatorId = operatorId;
        AccessToken = accessToken;
        CredentialRevision = credentialRevision;
    }

    /// <summary>The Nayax operator id, the Lynx API's <c>OperatorID</c> path parameter.</summary>
    public string OperatorId { get; }

    /// <summary>
    /// The decrypted access token. A secret: never log it, never include it in an exception
    /// message, and never put it in a DTO.
    /// </summary>
    public string AccessToken { get; }

    /// <summary>The credential revision these values were read at.</summary>
    public int CredentialRevision { get; }

    /// <summary>
    /// Redacts the token. This override is the safety net, not documentation: structured logging,
    /// string interpolation and test failure messages all reach for <c>ToString</c>.
    /// </summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"NayaxConnectionCredential {{ OperatorId = {OperatorId}, AccessToken = [redacted], CredentialRevision = {CredentialRevision} }}");
}
