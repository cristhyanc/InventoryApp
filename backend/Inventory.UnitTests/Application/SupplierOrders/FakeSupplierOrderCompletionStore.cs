using Inventory.Application.SupplierOrders;
using Inventory.Domain.SupplierOrders;

namespace InventoryApi.Tests.Application.SupplierOrders;

/// <summary>
/// In-memory fake of the completion-preview read port, so the use case's projection is exercised
/// without EF Core or SQLite. The candidate lines it hands back are what the purchase write's own
/// query would return; the relational behaviour of that query is
/// <c>SupplierOrderPartialCompletionTests</c>.
/// </summary>
public sealed class FakeSupplierOrderCompletionStore : ISupplierOrderCompletionStore
{
    private readonly SupplierOrderCompletionFacts? _facts;

    public FakeSupplierOrderCompletionStore(SupplierOrderCompletionFacts? facts)
    {
        _facts = facts;
    }

    public int? LastRequestedOrderId { get; private set; }

    public int? LastRequestedSupplierId { get; private set; }

    public DateTime? LastRequestedPurchaseDate { get; private set; }

    public IReadOnlyCollection<long>? LastRequestedProductIds { get; private set; }

    public Task<SupplierOrderCompletionFacts?> FindFactsAsync(
        int supplierOrderId,
        int? purchaseSupplierId,
        DateTime purchaseDate,
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken)
    {
        LastRequestedOrderId = supplierOrderId;
        LastRequestedSupplierId = purchaseSupplierId;
        LastRequestedPurchaseDate = purchaseDate;
        LastRequestedProductIds = productIds;
        return Task.FromResult(_facts);
    }

    /// <summary>One order with one line per product, and that order's own lines as the only candidates.</summary>
    public static SupplierOrderCompletionFacts Facts(
        SupplierOrderStatus status,
        params (int LineId, long ProductId, decimal Ordered, decimal Received)[] lines)
    {
        var lineFacts = lines
            .Select(line => new SupplierOrderCompletionLineFacts(
                line.LineId, line.ProductId, $"Product {line.ProductId}", "can", line.Ordered, line.Received))
            .ToList();

        return new SupplierOrderCompletionFacts(
            9,
            BusinessId: 1,
            SupplierId: 4,
            status,
            lineFacts,
            lineFacts
                .Where(line => line.QuantityReceived < line.QuantityOrdered)
                .GroupBy(line => line.ProductId)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<CandidateOrderLine>)group
                        .Select(line => new CandidateOrderLine(line.LineId, 9, line.QuantityOrdered - line.QuantityReceived))
                        .ToList()));
    }
}
