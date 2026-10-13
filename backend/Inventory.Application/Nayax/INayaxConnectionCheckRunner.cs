namespace Inventory.Application.Nayax;

/// <summary>
/// Runs the read-only Nayax connection checks against one <em>candidate</em> credential pair
/// (issue #536, a slice of #506).
///
/// It is a separate port from <see cref="INayaxLynxClient"/> on purpose, and the difference is the
/// credential. Every ordinary Nayax call authenticates with the current business's <em>stored</em>
/// connection, resolved per call through <see cref="INayaxRequestCredentialProvider"/> and gated by
/// <c>NayaxConnectionStatusGate</c>. A connection test authenticates with an operator id and token
/// an Owner has just pasted and that nothing has stored yet, so there is no stored connection to
/// resolve, no revision to attribute an answer to, and no status to gate on or to write. Hiding that
/// behind the ordinary client would mean a request body could choose the credentials an ordinary
/// Nayax call is made with, which is precisely what issue #520 removed.
///
/// What it must not do is equally part of the contract: it makes <strong>only</strong> the
/// <c>GET</c> checks of <c>NayaxConnectionCheckPolicy.All</c>, never
/// <c>POST machines/{id}/machineProducts</c> or any other write, and it writes nothing locally -
/// no connection status, no last-tested instant, no imported row. Testing a connection changes
/// neither this application's data nor the operator's Nayax data.
/// </summary>
public interface INayaxConnectionCheckRunner
{
    /// <summary>
    /// Makes each check in turn with <paramref name="candidate"/>'s operator id and token, and
    /// reports every line.
    ///
    /// It does not throw for a Nayax refusal: a <c>401</c>, a <c>403</c>, a <c>404</c>, an outage
    /// and a timeout are all results of the test and are reported as lines, because reporting which
    /// permission is missing <em>is</em> the feature. Cancellation still propagates, and so does a
    /// genuine programming error.
    /// </summary>
    /// <param name="candidate">
    /// The pasted operator id and token to test. Carries a secret: it is sent to Nayax and is never
    /// logged, echoed into a result, or stored by this port.
    /// </param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    Task<NayaxConnectionCheckReport> RunAsync(
        NayaxConnectionCredential candidate,
        CancellationToken cancellationToken);
}
