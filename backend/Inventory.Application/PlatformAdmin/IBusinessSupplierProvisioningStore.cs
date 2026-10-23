namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// The provisioning write a business application's approval makes into the business it is approving
/// (issue #849, child C of #835): one <c>Supplier</c> row per enabled platform supplier preset, so a
/// business that starts trading does not start with an empty supplier list.
///
/// <para><strong>Why it is a port of its own rather than a method on
/// <see cref="IPlatformBusinessDirectoryStore"/>.</strong> That port is the whole of what the Super
/// Admin surface may reach of a <em>business</em>, and its stated boundary is that no method on it
/// reaches a tenant-owned row (AGENTS.md § Super Admin businesses API). A supplier is tenant-owned.
/// Widening that port would make the sentence that bounds it untrue; a second, one-method port keeps
/// both surfaces exactly as reviewable as they were, and keeps this one small enough to read in
/// full.</para>
///
/// <para><strong>One method, no caller-supplied anything.</strong> There is no read here, no filter,
/// no predicate, no supplier field and no preset id: what is copied is decided entirely by the
/// stored presets, and which business receives it is decided entirely by the approval that is
/// already holding the write lock on that business's row. The request a decision arrives in carries
/// a <em>denied</em> business scope and no supplier data whatsoever.</para>
///
/// <para><strong>It is reachable from exactly one place.</strong>
/// <c>BusinessApplicationDecisionWriter</c> calls it on the approval branch, inside issue #522's
/// shared write transaction, with the business id of the row that transaction itself read - never
/// the route value. Nothing else calls it: a rejection, a deactivation, a reactivation,
/// <c>bootstrap-business</c> and the E2E fixture all leave the presets alone, and no business
/// endpoint can reach it at all.</para>
/// </summary>
public interface IBusinessSupplierProvisioningStore
{
    /// <summary>
    /// Copies every <em>enabled</em> supplier preset into <paramref name="businessId"/> as that
    /// business's own supplier, once, and records <paramref name="copiedAtUtc"/> as the instant it
    /// happened.
    ///
    /// <para><strong>The marker is the whole of the idempotency rule.</strong> It copies only while
    /// the business's recorded copy instant is <c>null</c>, and it sets that instant in the same
    /// save as the suppliers, so a second call - a second approval attempt, a reactivation, anything
    /// - copies nothing. Supplier <em>names</em> are deliberately not consulted: a business may
    /// rename, add and delete its own suppliers freely the moment it starts trading, so a name is
    /// not evidence about whether a copy was made.</para>
    ///
    /// <para><strong>Zero enabled presets is a normal outcome</strong>, not a failure and not a
    /// reason to leave the marker unset: the copy was made, and what it contained was nothing.</para>
    ///
    /// <para>It must be called inside the approval's own write transaction, and it saves its own
    /// change before returning, exactly as the approval's status write does - so a failure anywhere
    /// after it rolls the suppliers, the marker and the approval back together.</para>
    /// </summary>
    /// <param name="businessId">
    /// The business being approved, as the key of the row the caller's own write transaction read.
    /// It is never a value a request supplied.
    /// </param>
    /// <param name="copiedAtUtc">The decision instant, recorded as the copy instant.</param>
    /// <param name="cancellationToken">Cancellation for the whole operation.</param>
    Task CopyEnabledSupplierPresetsAsync(
        int businessId,
        DateTime copiedAtUtc,
        CancellationToken cancellationToken);
}
