using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Tenancy;

/// <summary>
/// In-memory fake of the membership write port.
///
/// It records what happened and in which order, because the order is the point of
/// <see cref="MembershipWriteGuard"/>: the transaction opens, the eligibility read happens inside
/// it, the write runs, the owner read sees what the write left, and only then is anything
/// committed. A fake that only returned rows could not tell a correct guard from one that checked
/// a rule after committing.
/// </summary>
public sealed class FakeBusinessMembershipWriteStore : IBusinessMembershipWriteStore
{
    public const string Begin = "begin";
    public const string IdentityRead = "identity-read";
    public const string BusinessRead = "business-read";
    public const string Commit = "commit";
    public const string Dispose = "dispose";

    private readonly List<string> _operations = [];

    /// <summary>Every call this store saw, in order, including the caller's own write.</summary>
    public IReadOnlyList<string> Operations => _operations;

    /// <summary>What <see cref="FindIdentityMembershipsAsync"/> answers.</summary>
    public List<IdentityMembership> IdentityMemberships { get; } = [];

    /// <summary>
    /// What <see cref="FindBusinessMembershipRolesAsync"/> answers. A test that needs the read to
    /// reflect the write mutates this list from inside its write delegate, which is exactly what
    /// a database read after a save would do.
    /// </summary>
    public List<BusinessMembershipRole> BusinessMembershipRoles { get; } = [];

    public ActorIdentity? LastQueriedIdentity { get; private set; }

    public BusinessId? LastQueriedBusiness { get; private set; }

    /// <summary>How the fake answers "is this the unique index refusing a second active row?".</summary>
    public Func<Exception, bool> DuplicateActiveMembership { get; set; } = _ => false;

    /// <summary>Records an operation the test itself performed, so it appears in order.</summary>
    public void Record(string operation) => _operations.Add(operation);

    public Task<IBusinessMembershipWriteTransaction> BeginWriteTransactionAsync(
        CancellationToken cancellationToken)
    {
        _operations.Add(Begin);
        return Task.FromResult<IBusinessMembershipWriteTransaction>(new Transaction(_operations));
    }

    public Task<IReadOnlyList<IdentityMembership>> FindIdentityMembershipsAsync(
        ActorIdentity identity,
        CancellationToken cancellationToken)
    {
        _operations.Add(IdentityRead);
        LastQueriedIdentity = identity;
        return Task.FromResult<IReadOnlyList<IdentityMembership>>(IdentityMemberships.ToList());
    }

    public Task<IReadOnlyList<BusinessMembershipRole>> FindBusinessMembershipRolesAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        _operations.Add(BusinessRead);
        LastQueriedBusiness = businessId;
        return Task.FromResult<IReadOnlyList<BusinessMembershipRole>>(BusinessMembershipRoles.ToList());
    }

    public bool IsDuplicateActiveMembership(Exception exception) => DuplicateActiveMembership(exception);

    private sealed class Transaction(List<string> operations) : IBusinessMembershipWriteTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken)
        {
            operations.Add(Commit);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            operations.Add(Dispose);
            return ValueTask.CompletedTask;
        }
    }
}
