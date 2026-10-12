using Inventory.Application.Onboarding;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Onboarding;

/// <summary>
/// In-memory fake of the business-application persistence port (issue #541).
///
/// It records what it was asked and in which order, because the order is part of what
/// <c>ApplyForBusiness</c> has to get right: the creation happens inside the write transaction, and
/// a resubmission reads the business the caller's own membership named and writes nothing at all.
/// </summary>
public sealed class FakeBusinessApplicationStore : IBusinessApplicationStore
{
    public const string Find = "find";
    public const string Create = "create";

    private readonly List<string> _operations = [];

    /// <summary>Every call this store saw, in order.</summary>
    public IReadOnlyList<string> Operations => _operations;

    /// <summary>What <see cref="FindAsync"/> answers, keyed by the business it is asked about.</summary>
    public Dictionary<BusinessId, BusinessApplication> Applications { get; } = [];

    /// <summary>The business id the next creation reports.</summary>
    public BusinessId CreatedBusinessId { get; set; } = BusinessId.From(99);

    public BusinessId? LastFoundBusinessId { get; private set; }

    public BusinessSettings? CreatedSettings { get; private set; }

    public ActorIdentity? CreatedIdentity { get; private set; }

    public MemberProfile? CreatedProfile { get; private set; }

    public DateTime? CreatedAtUtc { get; private set; }

    /// <summary>
    /// Runs inside <see cref="CreateAsync"/>, which is how a test stands in for anything that can
    /// happen while the write is in flight.
    /// </summary>
    public Action? WhileCreating { get; set; }

    public Task<BusinessApplication?> FindAsync(BusinessId businessId, CancellationToken cancellationToken)
    {
        _operations.Add(Find);
        LastFoundBusinessId = businessId;

        return Task.FromResult(
            Applications.TryGetValue(businessId, out var application) ? application : null);
    }

    public Task<BusinessApplication> CreateAsync(
        BusinessSettings settings,
        ActorIdentity identity,
        MemberProfile profile,
        DateTime appliedAtUtc,
        CancellationToken cancellationToken)
    {
        _operations.Add(Create);
        WhileCreating?.Invoke();

        CreatedSettings = settings;
        CreatedIdentity = identity;
        CreatedProfile = profile;
        CreatedAtUtc = appliedAtUtc;

        var created = new BusinessApplication(
            CreatedBusinessId,
            settings,
            BusinessStatus.Pending,
            appliedAtUtc);
        Applications[CreatedBusinessId] = created;

        return Task.FromResult(created);
    }

    /// <summary>Records an operation the test itself performed, so it appears in order.</summary>
    public void Record(string operation) => _operations.Add(operation);
}
