using Inventory.Application.Imports;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Adapters.ReimbursementImport;

/// <summary>
/// <see cref="IReimbursementImportJobScopes"/> over the host's own service scopes (issue #1149): the
/// one place the import worker's two scopes are opened, and the one place a business becomes the
/// current business for a job. It is
/// <c>Adapters/NayaxReconciliation/ServiceScopeNayaxSalesReconciliationScopes</c>'s shape exactly
/// (issue #1104).
///
/// <para><strong>It is hosting glue and holds no rule.</strong> It opens a scope, publishes a
/// business into it or deliberately does not, resolves a port or a use case and disposes the scope.
/// Nothing here decides which job is next or what importing one does.</para>
///
/// <para><strong>The discovery scope publishes no business, on purpose.</strong> A fresh
/// <c>BusinessScope</c> starts denied, so the scope <see cref="FindNextDueJobAsync"/> opens reads
/// nothing tenant-owned through the ordinary filters - which is exactly the state the reviewed
/// discovery statement is written for, and the reason it may ignore them: the business is the answer
/// rather than the input. Nothing else in that scope can reach a business's data either.</para>
///
/// <para><strong>The work scope publishes exactly one business, once.</strong> It resolves that
/// scope's <c>BusinessScope</c> to the business id the discovery read answered with - never to
/// anything configured or passed in from outside - and <c>BusinessScope</c> itself refuses a second
/// resolution, so nothing later in the job can repoint it. From that point the context, the tenant
/// query filters, the ownership stamp and every write the import makes are the ones a member of that
/// business would get.</para>
///
/// <para><strong>It is resolved as hosted work.</strong> The job has no request and no signed-in
/// user, so there is no membership for <c>CurrentBusinessProvider</c> to resolve the business from.
/// The scope is therefore published with <c>BusinessScope.ResolveForHostedWork</c>, which is the same
/// <c>Resolved</c> scope for every read and write and only additionally lets
/// <c>RequireBusinessIdAsync</c> answer this business.</para>
/// </summary>
public sealed class ServiceScopeReimbursementImportJobScopes : IReimbursementImportJobScopes
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ServiceScopeReimbursementImportJobScopes(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<DueReimbursementImportJob?> FindNextDueJobAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // No Resolve call: this scope's BusinessScope stays denied, because whose job is waiting is
        // what the read is for rather than something it already knows.
        return await scope.ServiceProvider
            .GetRequiredService<IReimbursementImportJobQueue>()
            .FindNextDueAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReimbursementImportJobScope CreateForBusiness(int businessId)
    {
        var scope = _scopeFactory.CreateAsyncScope();

        try
        {
            scope.ServiceProvider
                .GetRequiredService<BusinessScope>()
                .ResolveForHostedWork(BusinessId.From(businessId));

            return new WorkScope(scope);
        }
        catch
        {
            // A scope that could not be made current must not be left open: the business id came
            // from a stored row, so this is unreachable, and leaking a context would be the worse
            // failure.
            scope.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One business's scope, which owns both the service scope and the one use case invoked in it.
    /// </summary>
    private sealed class WorkScope : IReimbursementImportJobScope
    {
        private readonly AsyncServiceScope _scope;

        public WorkScope(AsyncServiceScope scope)
        {
            _scope = scope;
        }

        public Task<ReimbursementImportJobRunResult> ProcessAsync(
            int jobId, CancellationToken cancellationToken) =>
            _scope.ServiceProvider
                .GetRequiredService<ProcessReimbursementImportJob>()
                .Handle(jobId, cancellationToken);

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}
