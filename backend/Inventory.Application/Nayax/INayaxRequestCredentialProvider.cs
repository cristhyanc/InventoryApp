namespace Inventory.Application.Nayax;

/// <summary>
/// The credentials every ordinary Nayax call is made with, resolved per call from the trusted
/// current business (issue #520, a slice of #500).
///
/// This is the only way <c>Inventory.Infrastructure.Nayax.NayaxLynxClient</c> learns an operator id
/// or a token: nothing is baked into the shared <c>HttpClient</c> at construction any more, and
/// there is no global operator id or token to fall back to. No method takes a business id, for the
/// same reason <see cref="INayaxConnectionStore"/> takes none - the owning business is resolved
/// from the authenticated actor's membership, and an id a method accepted would be an id a later
/// caller could supply.
///
/// The status gate is part of this contract rather than of each of the ~27 consumers: a consumer
/// keeps depending on <c>INayaxLynxClient</c> and never learns that a status exists.
/// </summary>
public interface INayaxRequestCredentialProvider
{
    /// <summary>
    /// The current business's operator id, decrypted token and credential revision for one ordinary
    /// operation, with the status gate applied
    /// (<see cref="Domain.Nayax.NayaxConnectionStatusGate"/>).
    ///
    /// The status, the operator id, the token and the revision all come from one consistent
    /// snapshot of the connection (<see cref="INayaxConnectionStore.FindForOperationAsync"/>), so
    /// the status the gate accepted is the status the returned token is stored with - which is what
    /// the 403 classification and the 401 revision write both depend on.
    ///
    /// It fails closed rather than returning a usable-looking result: a business with no stored
    /// connection, a connection in <see cref="Domain.Nayax.NayaxConnectionStatus.NotConfigured"/>
    /// or <see cref="Domain.Nayax.NayaxConnectionStatus.NeedsAttention"/>, and a caller whose
    /// business could not be resolved all raise <see cref="NayaxNotConnectedException"/>, and the
    /// refused path never decrypts the stored token at all.
    /// </summary>
    /// <exception cref="NayaxNotConnectedException">
    /// The current business has no usable Nayax connection.
    /// </exception>
    Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that Nayax answered an ordinary operation with <c>401 Unauthorized</c> - the token is
    /// invalid or expired, per the Lynx API's documented meaning of that status - by moving the
    /// connection to <see cref="Domain.Nayax.NayaxConnectionStatus.NeedsAttention"/>.
    ///
    /// <paramref name="credentialRevision"/> must be the revision read when the call
    /// <em>started</em>, which is what makes the write safe: it reaches
    /// <see cref="INayaxConnectionStore.TryApplyStatusResultAsync"/>, whose conditional update
    /// discards a result produced for credentials that have since been replaced. A 401 answering a
    /// call that used revision N can therefore never mark revision N+1's token as needing
    /// attention.
    ///
    /// It reports nothing back: a discarded result is the store's own structured log event, and a
    /// caller that is already failing the operation has no decision left to make.
    /// </summary>
    Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken);
}
