namespace Inventory.Application.Nayax;

/// <summary>
/// The one gate-free read of the current business's stored Nayax credentials, for the Owner-only
/// re-test of issue #506 (issue #520, a slice of #500).
///
/// It is a separate port from <see cref="INayaxRequestCredentialProvider"/>, not a second method on
/// it, because the difference between the two is a security boundary rather than a convenience:
/// this one deliberately ignores the status gate, so credentials in
/// <see cref="Domain.Nayax.NayaxConnectionStatus.NeedsAttention"/> can be tested again and
/// recovered. An ordinary consumer that could reach it would have a way to use credentials the gate
/// refuses, so no ordinary consumer may depend on this type at all - which
/// <c>NayaxRetestCredentialBoundaryTests</c> enforces against the compiled assemblies rather than
/// leaving it to review.
///
/// It only reads. Applying a test result stays with #506, through
/// <see cref="INayaxConnectionStore.TryApplyStatusResultAsync"/> and the revision this read
/// returned.
/// </summary>
public interface INayaxConnectionRetestCredentialProvider
{
    /// <summary>
    /// The current business's operator id, decrypted access token and the credential revision the
    /// token was read at, whatever the connection status is, or <see langword="null"/> when the
    /// business has stored no credentials at all.
    ///
    /// The revision is the point of returning it: a re-test's result must be applied back to the
    /// exact revision it tested, so a token saved while the test was running keeps its own status.
    /// A stored credential that cannot be decrypted still fails closed, exactly as it does for an
    /// ordinary call - that is <see cref="INayaxConnectionStore.FindCredentialAsync"/>'s guarantee,
    /// and this port does not soften it.
    /// </summary>
    Task<NayaxConnectionCredential?> GetForRetestAsync(CancellationToken cancellationToken);
}
