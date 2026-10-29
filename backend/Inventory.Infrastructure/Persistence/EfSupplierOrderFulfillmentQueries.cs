using Inventory.Domain.SupplierOrders;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
// Inventory.Domain.SupplierOrders declares its own SupplierOrderStatus, so the persisted enum the
// EF query compares against is named explicitly rather than relying on which using wins.
using PersistenceModels = Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The one query that says which outstanding supplier-order lines a purchase's quantities could
/// fulfil, shared the way <see cref="EfProductInventoryHistoryQueries"/> and
/// <c>EfReportingSharedQueries</c> are shared.
///
/// <para>It was <see cref="EfPurchaseStore"/>'s private query until issue #974 gave the purchase
/// form a server-side completion preview. Both callers need the <em>same</em> candidates - the same
/// supplier, the same "ordered on or before the purchase date" bound, the same exclusion of closed
/// orders, and the same oldest-order-first ordering - because the preview's projected outstanding
/// balance and the save's allocation have to be one answer. Extracting it is what makes that
/// structural rather than a coincidence; the query itself is unchanged apart from also excluding an
/// order that was explicitly completed with undelivered quantities, which is closed and must not
/// receive anything more.</para>
/// </summary>
internal static class EfSupplierOrderFulfillmentQueries
{
    /// <summary>
    /// The candidate lines per product, already filtered and ordered exactly as
    /// <see cref="SupplierOrderFulfillmentAllocationPolicy.Allocate"/> requires. A purchase with no
    /// supplier can fulfil nothing - which order would it be against? - so it gets no candidates at
    /// all, and a product with no outstanding line gets an empty list rather than being absent.
    /// </summary>
    public static async Task<Dictionary<long, IReadOnlyList<CandidateOrderLine>>> CandidateLinesByProductAsync(
        AppDbContext db,
        int? supplierId,
        DateTime purchaseDate,
        IEnumerable<long> productIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(productIds);

        var candidateLinesByProduct = new Dictionary<long, IReadOnlyList<CandidateOrderLine>>();
        if (!supplierId.HasValue) return candidateLinesByProduct;

        var purchaseDayEnd = purchaseDate.Date.AddDays(1);
        foreach (var productId in productIds.Distinct())
        {
            var lines = await db.SupplierOrderLines
                .Include(line => line.SupplierOrder)
                .Where(line => line.ProductId == productId &&
                    line.SupplierOrder.SupplierId == supplierId &&
                    line.SupplierOrder.OrderDate < purchaseDayEnd &&
                    line.SupplierOrder.Status != PersistenceModels.SupplierOrderStatus.Cancelled &&
                    line.SupplierOrder.Status != PersistenceModels.SupplierOrderStatus.Received &&
                    line.SupplierOrder.Status != PersistenceModels.SupplierOrderStatus.ClosedPartiallyDelivered &&
                    line.QuantityReceived < line.QuantityOrdered)
                .OrderBy(line => line.SupplierOrder.OrderDate)
                .ThenBy(line => line.SupplierOrder.Id)
                .ThenBy(line => line.Id)
                .ToListAsync(cancellationToken);

            candidateLinesByProduct[productId] = lines
                .Select(line => new CandidateOrderLine(line.Id, line.SupplierOrderId, line.QuantityOrdered - line.QuantityReceived))
                .ToList();
        }

        return candidateLinesByProduct;
    }
}
