using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.PlatformAdmin;

/// <summary>
/// In-memory fake of the Super Admin's business port (issue #544).
///
/// <para>It records every call in order, because the order is what
/// <see cref="Inventory.Application.PlatformAdmin.BusinessApplicationDecisionWriter"/> has to get
/// right: the decision is read again <em>inside</em> the write transaction, and only then written.
/// A fake that answered reads without recording them could not tell a correct writer from one that
/// decided on the view it was handed before the lock was taken.</para>
///
/// <para>A test stands in for another decision committing between the two reads by mutating
/// <see cref="Targets"/> from <c>FakeBusinessMembershipWriteStore.WhileWaitingForTheWriteLock</c>,
/// which runs in exactly that window - with no threads and no wall clock.</para>
/// </summary>
public sealed class FakePlatformBusinessDirectoryStore : IPlatformBusinessDirectoryStore
{
    public const string List = "list";
    public const string CountPending = "count-pending";
    public const string DecisionRead = "decision-read";
    public const string Approve = "approve";
    public const string Reject = "reject";

    private readonly List<string> _operations = [];

    /// <summary>Every call this store saw, in order.</summary>
    public IReadOnlyList<string> Operations => _operations;

    /// <summary>What <see cref="ListAsync"/> answers.</summary>
    public List<PlatformBusinessSummary> Businesses { get; } = [];

    /// <summary>What <see cref="FindDecisionTargetAsync"/> answers, by business id.</summary>
    public Dictionary<int, PlatformBusinessDecisionTarget> Targets { get; } = [];

    public DateTime? ApprovedAtUtc { get; private set; }

    public DateTime? RejectedAtUtc { get; private set; }

    public string? RejectedReason { get; private set; }

    public int? DeactivatedMembershipId { get; private set; }

    public Task<IReadOnlyList<PlatformBusinessSummary>> ListAsync(CancellationToken cancellationToken)
    {
        _operations.Add(List);
        return Task.FromResult<IReadOnlyList<PlatformBusinessSummary>>(Businesses.ToList());
    }

    public Task<int> CountPendingAsync(CancellationToken cancellationToken)
    {
        _operations.Add(CountPending);
        return Task.FromResult(Businesses.Count(business => business.Status == BusinessStatus.Pending));
    }

    public Task<PlatformBusinessDecisionTarget?> FindDecisionTargetAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        _operations.Add(DecisionRead);

        return Task.FromResult(Targets.TryGetValue(businessId, out var target) ? target : null);
    }

    public Task ApproveAsync(int businessId, DateTime decidedAtUtc, CancellationToken cancellationToken)
    {
        _operations.Add(Approve);
        ApprovedAtUtc = decidedAtUtc;

        if (Targets.TryGetValue(businessId, out var target))
        {
            Targets[businessId] = target with { Status = BusinessStatus.Active };
        }

        return Task.CompletedTask;
    }

    public Task RejectAsync(
        int businessId,
        string? reason,
        DateTime decidedAtUtc,
        int? deactivateMembershipId,
        CancellationToken cancellationToken)
    {
        _operations.Add(Reject);
        RejectedAtUtc = decidedAtUtc;
        RejectedReason = reason;
        DeactivatedMembershipId = deactivateMembershipId;

        if (Targets.TryGetValue(businessId, out var target))
        {
            Targets[businessId] = target with { Status = BusinessStatus.Rejected };
        }

        return Task.CompletedTask;
    }
}
