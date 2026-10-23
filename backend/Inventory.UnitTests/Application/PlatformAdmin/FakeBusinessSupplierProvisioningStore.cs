using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// In-memory fake of the provisioning port an approval writes the new business's supplier copies
/// through (issue #849).
///
/// <para>It records the business id and the instant it was handed, because those are the two things
/// the writer has to get right - the id of the row its own transaction read rather than the route
/// value, and the decision instant rather than some later clock reading - and it can be told to
/// fail, because "the copy failed, so nothing was approved" is a rule of its own.</para>
///
/// <para><see cref="Recorder"/> lets a test place the copy in the surrounding order of operations
/// (<c>FakeBusinessMembershipWriteStore.Record</c>), which is how "the copy happened inside the
/// write transaction, before the commit" is asserted rather than assumed.</para>
/// </summary>
public sealed class FakeBusinessSupplierProvisioningStore : IBusinessSupplierProvisioningStore
{
    public const string Copy = "copy-supplier-presets";

    /// <summary>Every copy this store was asked for, in order.</summary>
    public List<(int BusinessId, DateTime CopiedAtUtc)> Copies { get; } = [];

    /// <summary>
    /// What the copy throws instead of succeeding, or <see langword="null"/> when it succeeds. The
    /// call is still recorded: a test needs to see that the writer tried before it failed.
    /// </summary>
    public Exception? FailWith { get; set; }

    /// <summary>Where the call is announced, so a test can see it in the overall order.</summary>
    public Action<string>? Recorder { get; set; }

    public Task CopyEnabledSupplierPresetsAsync(
        int businessId,
        DateTime copiedAtUtc,
        CancellationToken cancellationToken)
    {
        Recorder?.Invoke(Copy);
        Copies.Add((businessId, copiedAtUtc));

        return FailWith is null ? Task.CompletedTask : Task.FromException(FailWith);
    }
}

/// <summary>
/// A provisioning port that refuses every copy, for the integration tests that prove an approval
/// rolls back when the copy fails. It writes nothing at all, so what the database is left holding is
/// entirely the transaction's own answer.
/// </summary>
public sealed class FailingBusinessSupplierProvisioningStore : IBusinessSupplierProvisioningStore
{
    public const string Message = "The supplier preset copy failed.";

    public Task CopyEnabledSupplierPresetsAsync(
        int businessId,
        DateTime copiedAtUtc,
        CancellationToken cancellationToken) =>
        Task.FromException(new InvalidOperationException(Message));
}
