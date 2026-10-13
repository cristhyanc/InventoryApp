using Inventory.Application.PlatformAdmin;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// Collects the platform business-lifecycle audit events of issue #545, so a test can assert all
/// three halves: that a committed action is recorded once with the actor, the business, the action
/// and whatever membership or invite count applies; that a refused action records nothing; and that
/// an action which changed nothing records nothing either.
/// </summary>
public sealed class FakePlatformBusinessLifecycleAudit : IPlatformBusinessLifecycleAudit
{
    private readonly List<PlatformBusinessLifecycleAuditEntry> _entries = [];

    public IReadOnlyList<PlatformBusinessLifecycleAuditEntry> Entries => _entries;

    public void Record(PlatformBusinessLifecycleAuditEntry entry) => _entries.Add(entry);
}

/// <summary>
/// Stands in for whatever issue #512 will hang on the deactivation hook (issue #545): it records
/// that it ran, and can refuse.
///
/// <para>Both halves are the point. A hook that ran is proof the transition calls it at all, and the
/// order it was recorded in proves it runs <em>inside</em> the write transaction, after the status
/// and the invites. A hook that throws is how a test asserts the direction this has to fail in: the
/// deactivation is refused and rolled back rather than committed with its hook unfinished.</para>
/// </summary>
public sealed class FakeBusinessDeactivationHook : IBusinessDeactivationHook
{
    public const string Hook = "deactivation-hook";

    /// <summary>Where the call is reported, so one test can see the whole order.</summary>
    public Action<string>? Observer { get; set; }

    /// <summary>What the hook raises instead of completing, when a test asks it to refuse.</summary>
    public Exception? Refusal { get; set; }

    /// <summary>What the hook was told, so a test can assert it is the business and the instant.</summary>
    public BusinessDeactivated? Deactivated { get; private set; }

    public Task OnBusinessDeactivatedAsync(
        BusinessDeactivated deactivated,
        CancellationToken cancellationToken)
    {
        Observer?.Invoke(Hook);
        Deactivated = deactivated;

        return Refusal is null ? Task.CompletedTask : Task.FromException(Refusal);
    }
}
