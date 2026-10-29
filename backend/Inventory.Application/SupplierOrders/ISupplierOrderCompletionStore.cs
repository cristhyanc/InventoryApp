using Inventory.Domain.SupplierOrders;

namespace Inventory.Application.SupplierOrders;

/// <summary>
/// Narrow persistence port behind the "complete this order with undelivered quantities" preview
/// (issue #974). It reads only what the decision is made from, and writes nothing: the write is the
/// purchase-save transaction's, through <c>IPurchaseStore.CreateAsync</c>, so there is no second
/// client request that could close an order after a purchase was created.
///
/// <para>Both facts it returns are the purchase write's own: the order's stored lines, and the
/// candidate outstanding lines the allocation would consider for the submitted products. The EF
/// adapter builds the second with the identical query
/// <c>EfSupplierOrderFulfillmentQueries.CandidateLinesByProductAsync</c> that the purchase write
/// uses, so the preview cannot project against a different set of candidates than the save
/// allocates over.</para>
/// </summary>
public interface ISupplierOrderCompletionStore
{
    /// <summary>
    /// The order and the allocation candidates, or <c>null</c> when
    /// <paramref name="supplierOrderId"/> is not an order of the caller's business - which is how a
    /// cross-business or unknown order reference becomes a not-found rather than a projection.
    /// </summary>
    Task<SupplierOrderCompletionFacts?> FindFactsAsync(
        int supplierOrderId,
        int? purchaseSupplierId,
        DateTime purchaseDate,
        IReadOnlyCollection<long> productIds,
        CancellationToken cancellationToken);
}

/// <summary>Everything the completion decision is projected from, as the Application layer sees it.</summary>
public sealed record SupplierOrderCompletionFacts(
    int SupplierOrderId,
    int BusinessId,
    int? SupplierId,
    SupplierOrderStatus Status,
    IReadOnlyList<SupplierOrderCompletionLineFacts> Lines,
    IReadOnlyDictionary<long, IReadOnlyList<CandidateOrderLine>> CandidateLinesByProduct);

/// <summary>
/// One of the order's own lines, with the product detail the confirmation names it by. The unit is
/// the product's own <c>Unit</c> label, so an outstanding quantity is shown in the units it was
/// ordered in rather than as a bare number.
/// </summary>
public sealed record SupplierOrderCompletionLineFacts(
    int LineId,
    long ProductId,
    string ProductName,
    string? Unit,
    decimal QuantityOrdered,
    decimal QuantityReceived);
