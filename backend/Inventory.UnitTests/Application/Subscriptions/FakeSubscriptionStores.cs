using Inventory.Application.Subscriptions;
using Inventory.Domain.Subscriptions;

namespace InventoryApi.Tests.Application.Subscriptions;

/// <summary>
/// The plan-enforcement rollout flag of issue #966, as a test decides it, so a test never has to
/// build an <c>IConfiguration</c> to say "plans are enforced in this deployment".
/// </summary>
public sealed class FakeSubscriptionEnforcementFlag : ISubscriptionEnforcementFlag
{
    private FakeSubscriptionEnforcementFlag(bool isEnabled) => IsEnabled = isEnabled;

    public static FakeSubscriptionEnforcementFlag Enabled { get; } = new(isEnabled: true);

    /// <summary>The shipped state: no deployment has switched plan enforcement on.</summary>
    public static FakeSubscriptionEnforcementFlag Disabled { get; } = new(isEnabled: false);

    public bool IsEnabled { get; }
}

/// <summary>
/// In-memory fake of the grandfathering record's port (issue #966), so the Application tests
/// exercise the gate and the idempotency without EF Core or a database.
///
/// <para>It starts with <em>no record at all</em>, which is the state every installation is in
/// today: that is the state the "enforcement cannot start early" rule exists for, so it is what a
/// test gets unless it asks for another.</para>
/// </summary>
public sealed class FakeSubscriptionGrandfatheringStore : ISubscriptionGrandfatheringStore
{
    private SubscriptionGrandfatheringState? _stored;

    public FakeSubscriptionGrandfatheringStore()
    {
    }

    private FakeSubscriptionGrandfatheringStore(SubscriptionGrandfatheringState stored) =>
        _stored = stored;

    /// <summary>A store whose step has completed, which is what lets enforcement take effect.</summary>
    public static FakeSubscriptionGrandfatheringStore Completed(
        DateTime? cutoffUtc = null,
        int grantedBusinessCount = 1) =>
        new(new SubscriptionGrandfatheringState(
            cutoffUtc ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 11, 1, 0, 5, 0, DateTimeKind.Utc),
            grantedBusinessCount));

    /// <summary>A store whose step started and never finished: the interrupted run.</summary>
    public static FakeSubscriptionGrandfatheringStore Started(DateTime? cutoffUtc = null) =>
        new(new SubscriptionGrandfatheringState(
            cutoffUtc ?? new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc: null,
            GrantedBusinessCount: 0));

    /// <summary>How many times the record was read, so an unnecessary read can be shown not to happen.</summary>
    public int Reads { get; private set; }

    public Task<SubscriptionGrandfatheringState?> FindAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(_stored);
    }

    public Task<SubscriptionGrandfatheringState> StartAsync(
        DateTime cutoffUtc,
        CancellationToken cancellationToken)
    {
        _stored ??= new SubscriptionGrandfatheringState(cutoffUtc, CompletedAtUtc: null, 0);
        return Task.FromResult(_stored);
    }

    public Task<SubscriptionGrandfatheringState> CompleteAsync(
        DateTime completedAtUtc,
        int grantedBusinessCount,
        CancellationToken cancellationToken)
    {
        var record = _stored
            ?? throw new InvalidOperationException("No cutoff has been recorded.");

        _stored = record.CompletedAtUtc is null
            ? record with { CompletedAtUtc = completedAtUtc, GrantedBusinessCount = grantedBusinessCount }
            : record;

        return Task.FromResult(_stored);
    }
}

/// <summary>
/// In-memory fake of the manual-grant port (issue #966), holding one business's grants, so the
/// Application tests and the grandfathering command's own tests can run without a database.
/// </summary>
public sealed class FakeManualPlanGrantStore : IManualPlanGrantStore
{
    private readonly List<ManualPlanGrantState> _grants;

    public FakeManualPlanGrantStore(params ManualPlanGrantState[] grants) => _grants = [.. grants];

    /// <summary>How many times the grants were read, so an unnecessary read can be shown not to happen.</summary>
    public int Reads { get; private set; }

    /// <summary>The grandfathered grants this store was asked to write, in order.</summary>
    public List<(DateTime StartsAtUtc, DateTime GrantedAtUtc)> Writes { get; } = [];

    public Task<IReadOnlyList<ManualPlanGrantState>> ListForCurrentBusinessAsync(
        CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<ManualPlanGrantState>>([.. _grants]);
    }

    public Task<bool> GrantGrandfatheredPlanIfMissingAsync(
        DateTime startsAtUtc,
        DateTime grantedAtUtc,
        CancellationToken cancellationToken)
    {
        if (_grants.Any(grant => grant.Source == ManualPlanGrantSource.Grandfathered))
        {
            return Task.FromResult(false);
        }

        _grants.Add(new ManualPlanGrantState(
            GrandfatheringEligibilityPolicy.GrandfatheredPlan,
            ManualPlanGrantSource.Grandfathered,
            new EntitlementPeriod(startsAtUtc, null)));
        Writes.Add((startsAtUtc, grantedAtUtc));

        return Task.FromResult(true);
    }
}

/// <summary>
/// In-memory fake of the billing-subscription port (issue #966), holding one business's
/// subscription or none.
/// </summary>
public sealed class FakeBillingSubscriptionStore : IBillingSubscriptionStore
{
    private readonly BillingSubscriptionState? _subscription;

    public FakeBillingSubscriptionStore(BillingSubscriptionState? subscription = null) =>
        _subscription = subscription;

    /// <summary>How many times the subscription was read.</summary>
    public int Reads { get; private set; }

    public Task<BillingSubscriptionState?> FindForCurrentBusinessAsync(CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(_subscription);
    }
}
