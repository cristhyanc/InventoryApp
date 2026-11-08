using Inventory.Application.NayaxSqs;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Adapters.NayaxSqs;

/// <summary>
/// <see cref="INayaxSqsReaderScopes"/> over the host's own service scopes (issue #1103): the one
/// place the Nayax SQS sales reader's two scopes are opened, and the one place a business becomes the
/// current business for a message.
///
/// <para><strong>It is hosting glue and holds no rule.</strong> It opens a scope, publishes a
/// business into it or deliberately does not, resolves a use case and disposes the scope - the same
/// three steps <c>Auth/MemberProfileRefreshWorker</c> and
/// <c>Bootstrap/ConnectedMachineSnapshotCommand</c> take. Nothing here decides what a message means,
/// whose it is or what happens to it.</para>
///
/// <para><strong>The lookup scope publishes no business, on purpose.</strong> A fresh
/// <c>BusinessScope</c> starts denied, so the scope
/// <see cref="FindBusinessBySenderAsync"/> opens reads nothing tenant-owned through the ordinary
/// filters - which is exactly the state the reviewed sender lookup is written for, and the reason it
/// is the one statement in this application that may ignore them. Nothing else in that scope can
/// reach a business's data either.</para>
///
/// <para><strong>The work scope publishes exactly one business, once.</strong>
/// <see cref="INayaxSqsBusinessWorkScope"/> resolves that scope's <c>BusinessScope</c> and resolves
/// it to the business id the lookup answered with - never to anything a message carried -
/// and <c>BusinessScope</c> itself refuses a second resolution, so nothing later in the message can
/// repoint it. From that point the context, the tenant query filters, the ownership stamp, the
/// credential read, the plan check and the Nayax call are all the ones a member of that business
/// would get.</para>
/// </summary>
public sealed class ServiceScopeNayaxSqsReaderScopes : INayaxSqsReaderScopes
{
    private readonly IServiceScopeFactory _scopeFactory;

    public ServiceScopeNayaxSqsReaderScopes(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<NayaxSqsSenderAttribution?> FindBusinessBySenderAsync(
        string senderId, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        // No Resolve call: this scope's BusinessScope stays denied, because the business is what the
        // lookup is for rather than something it already knows.
        return await scope.ServiceProvider
            .GetRequiredService<INayaxSqsSenderDirectory>()
            .FindBusinessBySenderAsync(senderId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public INayaxSqsBusinessWorkScope CreateForBusiness(int businessId)
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
    /// One message's scope, which owns both the service scope and the one use case invoked in it.
    /// </summary>
    private sealed class WorkScope : INayaxSqsBusinessWorkScope
    {
        private readonly AsyncServiceScope _scope;

        public WorkScope(AsyncServiceScope scope)
        {
            _scope = scope;
        }

        public Task<NayaxSqsProcessingOutcome> ProcessAsync(
            string messageId, string? messageBody, CancellationToken cancellationToken) =>
            _scope.ServiceProvider
                .GetRequiredService<ProcessNayaxSqsSale>()
                .Handle(messageId, messageBody, cancellationToken);

        public ValueTask DisposeAsync() => _scope.DisposeAsync();
    }
}
