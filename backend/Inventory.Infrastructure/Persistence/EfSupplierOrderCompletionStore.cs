using Inventory.Application.SupplierOrders;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISupplierOrderCompletionStore"/> (issue #974), beside
/// <see cref="EfSupplierOrderStore"/> for the same reason every other <c>Ef*</c> adapter lives here.
///
/// <para>It is read-only. The order it reads is reached through the ordinary tenant query filters,
/// so an order of another business - or one that does not exist - simply is not found, and no
/// per-call business predicate is added here (AGENTS.md § Tenant ownership and data isolation). The
/// allocation candidates come from <see cref="EfSupplierOrderFulfillmentQueries"/>, which is the
/// purchase write's own query.</para>
/// </summary>
public sealed class EfSupplierOrderCompletionStore : ISupplierOrderCompletionStore
{
    private readonly AppDbContext _db;

    public EfSupplierOrderCompletionStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<SupplierOrderCompletionFacts?> FindFactsAsync(
        int supplierOrderId,
        int? purchaseSupplierId,
        DateTime purchaseDate,
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        var order = await _db.SupplierOrders
            .AsNoTracking()
            .Include(candidate => candidate.Lines)
                .ThenInclude(line => line.Product)
            .FirstOrDefaultAsync(candidate => candidate.Id == supplierOrderId, cancellationToken);
        if (order is null) return null;

        var candidateLines = await EfSupplierOrderFulfillmentQueries.CandidateLinesByProductAsync(
            _db, purchaseSupplierId, purchaseDate, productIds, cancellationToken);

        return new SupplierOrderCompletionFacts(
            order.Id,
            order.BusinessId,
            order.SupplierId,
            (Inventory.Domain.SupplierOrders.SupplierOrderStatus)order.Status,
            order.Lines
                .Select(line => new SupplierOrderCompletionLineFacts(
                    line.Id,
                    line.ProductId,
                    line.Product.Name,
                    line.Product.Unit,
                    line.QuantityOrdered,
                    line.QuantityReceived))
                .ToList(),
            candidateLines);
    }
}
