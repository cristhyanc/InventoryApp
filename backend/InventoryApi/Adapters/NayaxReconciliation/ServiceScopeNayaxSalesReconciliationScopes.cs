using Inventory.Application.NayaxReconciliation;
using Inventory.Application.NayaxSqs;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Adapters.NayaxReconciliation;

/// <summary>
/// <see cref="INayaxSalesReconciliationScopes"/> over the host's own service scopes (issue #1104):
/// the one place the hourly reconciliation's two scopes are opened, and the one place a business
/// becomes the current business for a run. It is
/// <c>Adapters/NayaxSqs/ServiceScopeNayaxSqsReaderScopes</c>'s shape exactly (issue #1103).
///
/// <para><strong>It is hosting glue and holds no rule.</strong> It opens a scope, publishes a
/// business into it or deliberately does not, resolves a port or a use case and disposes the scope -
/// the same three steps <c>Auth/MemberProfileRefreshWorker</c> and
/// <c>Bootstrap/ConnectedMachineSnapshotCommand</c> take. Nothing here decides which businesses are
/// eligible or what a reconciliation does.</para>
///
/// <para><strong>The eligibility scope publishes no business, on purpose.</strong> A fresh
/// <c>BusinessScope</c> starts denied, so the scope <see cref="ListEligibleBusinessIdsAsync"/> opens
/// reads nothing tenant-owned through the ordinary filters - which is exactly the state the reviewed
/// eligibility statement is written for, and the reason it may ignore them: the businesses are the
/// answer rather than the input. Nothing else in that scope can reach a business's data either.</para>
///
/// <para><strong>The work scope publishes exactly one business, once.</strong>
/// <see cref="INayaxSalesReconciliationBusinessScope"/> resolves that scope's <c>BusinessScope</c> to
/// the business id the eligibility read answered with - never to anything configured or passed in -
/// and <c>BusinessScope</c> itself refuses a second resolution, so nothing later in the run can
/// repoint it. From that point the context, the tenant query filters, the ownership stamp, the
/// credential read, the plan check and the Nayax call are all the ones a member of that business would
/// get.</para>
/// </summary>
public sealed class ServiceScopeNayaxSalesReconciliationScopes : INayaxSalesReconciliationScopes
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ServiceScopeNayaxSalesReconciliationScopes(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListEligibleBusinessIdsAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // No Resolve call: this scope's BusinessScope stays denied, because which businesses are
        // eligible is what the read is for rather than something it already knows.
        return await scope.ServiceProvider
            .GetRequiredService<INayaxSqsSenderDirectory>()
            .ListEligibleBusinessIdsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public INayaxSalesReconciliationBusinessScope CreateForBusiness(int businessId)
    {
        var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            scope.ServiceProvider
                .GetRequiredService<BusinessScope>()
                .Resolve(BusinessId.From(businessId));

            return new WorkScope(scope);
        }
        catch
        {
            // A scope that could not be made current must not be left open: the business id came from
            // a stored row, so this is unreachable, and leaking a context would be the worse failure.
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One business's scope, which owns both the service scope and the one use case invoked in it.
    /// </summary>
    private sealed class WorkScope : INayaxSalesReconciliationBusinessScope
    {
        private readonly AsyncServiceScope _scope;

        public WorkScope(AsyncServiceScope scope)
        {
            _scope = scope;
        }

        public Task<NayaxSalesReconciliationResult> ReconcileAsync(CancellationToken cancellationToken) =>
            _scope.ServiceProvider
                .GetRequiredService<ReconcileNayaxSalesWithProvider>()
                .Handle(cancellationToken);

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}
