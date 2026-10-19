using Inventory.Application.CatalogueReview;
using Microsoft.EntityFrameworkCore.Storage;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The relational transaction behind the catalogue review's two
/// <c>BeginTransactionAsync</c> methods (issue #738), or a no-op when there is none to hold - a
/// non-relational provider, or a transaction an outer operation already owns and must commit itself.
/// Exactly the shape <see cref="EfNayaxProductCatalogImportStore"/> uses for the same reason.
/// </summary>
internal sealed class CatalogueReviewTransaction(IDbContextTransaction? transaction)
    : ICatalogueReviewTransaction
{
    public Task CommitAsync(CancellationToken cancellationToken) =>
        transaction?.CommitAsync(cancellationToken) ?? Task.CompletedTask;

    public ValueTask DisposeAsync() => transaction?.DisposeAsync() ?? ValueTask.CompletedTask;
}
