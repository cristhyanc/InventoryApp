namespace Inventory.Domain.Nayax;

/// <summary>
/// How usable one business's own Nayax Lynx connection currently is (issue #518, a slice of #500).
///
/// It describes the stored credential pair, not a single API call: a status is only ever written
/// when credentials are saved, or when a permission test result is applied for the exact credential
/// revision it was produced for. A transient Nayax outage is an upstream error
/// (<c>Inventory.Infrastructure.Nayax.NayaxUpstreamException</c>), never a status change.
///
/// <see cref="NotConfigured"/> is the shipped state and the state of a business with no connection
/// record at all, so "nothing stored" and "stored but unusable" can never be confused. Nothing
/// reads the connection yet - the Nayax client still uses the single configured operator/token
/// until issue #520 - so no status currently changes application behaviour.
/// </summary>
public enum NayaxConnectionStatus
{
    /// <summary>No operator id and no token have been stored for this business.</summary>
    NotConfigured = 0,

    /// <summary>
    /// Credentials are stored but their permissions have not been verified since they were last
    /// saved. Every save lands here: a freshly stored token is never assumed to work.
    /// </summary>
    PendingPermissions = 1,

    /// <summary>The stored credentials were tested and carried the permissions the application needs.</summary>
    Ready = 2,

    /// <summary>
    /// The stored credentials were tested and did not work - revoked, expired, or missing a
    /// required permission - so an operator has to look at them.
    /// </summary>
    NeedsAttention = 3,
}
