using Inventory.Domain.SupplierOrders;

namespace Inventory.Application.SupplierOrders;

/// <summary>One line of the purchase being entered, as the completion preview is asked about it.</summary>
public sealed record PurchaseLineToReceive(long ProductId, decimal Quantity);

/// <summary>
/// What the operator is shown before saving a purchase linked to an open supplier order (issue
/// #974): the balance each of the order's lines would still be owed once this purchase is applied,
/// and a fingerprint of the order it was projected over.
///
/// <see cref="EligibleForCompletion"/> and <see cref="OutstandingLines"/> together are what decide
/// whether the confirmation is offered at all: an order that is already closed, or one this purchase
/// completes in full, has no undelivered balance to close and is never asked about.
/// </summary>
public sealed record SupplierOrderCompletionPreview(
    int SupplierOrderId,
    SupplierOrderStatus Status,
    bool EligibleForCompletion,
    string DecisionFingerprint,
    IReadOnlyList<SupplierOrderOutstandingLine> OutstandingLines);

/// <summary>One product the order would still be owed, with the quantity and the unit it was ordered in.</summary>
public sealed record SupplierOrderOutstandingLine(
    int SupplierOrderLineId,
    long ProductId,
    string ProductName,
    string? Unit,
    decimal QuantityOrdered,
    decimal QuantityAlreadyReceived,
    decimal QuantityOnThisPurchase,
    decimal OutstandingQuantity);

/// <summary>
/// Projects a supplier order's outstanding balance against the purchase an operator has entered but
/// not yet saved (issue #974).
///
/// <para><strong>It writes nothing, and it is not the authority the save trusts.</strong> It exists
/// so the person deciding sees the server's own figures rather than a second calculation in the
/// browser: the quantities it applies to each line come from
/// <see cref="SupplierOrderFulfillmentAllocationPolicy"/> - the same algorithm the purchase write
/// allocates with, over the same candidate lines - and the remaining balances from
/// <see cref="SupplierOrderCompletionPolicy"/>. The purchase save then recomputes all of it inside
/// its own transaction and compares <see cref="SupplierOrderCompletionPreview.DecisionFingerprint"/>
/// against what this reported, so a competing receipt between the preview and the save is refused
/// rather than applied against a balance nobody saw.</para>
/// </summary>
public sealed class PreviewSupplierOrderCompletion
{
    private readonly ISupplierOrderCompletionStore _store;

    public PreviewSupplierOrderCompletion(ISupplierOrderCompletionStore store)
    {
        _store = store;
    }

    /// <summary>
    /// <c>null</c> when the order is not one of the caller's business's, which the endpoint reports
    /// as a not-found.
    /// </summary>
    public async Task<SupplierOrderCompletionPreview?> Handle(
        int supplierOrderId,
        int? purchaseSupplierId,
        DateTime purchaseDate,
        IReadOnlyList<PurchaseLineToReceive> purchaseLines,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(purchaseLines);

        var productIds = purchaseLines.Select(line => line.ProductId).Distinct().ToList();
        var facts = await _store.FindFactsAsync(
            supplierOrderId, purchaseSupplierId, purchaseDate, productIds, cancellationToken);
        if (facts is null) return null;

        var fulfillments = facts.Lines
            .Select(line => new SupplierOrderLineFulfillment(line.LineId, line.QuantityOrdered, line.QuantityReceived))
            .ToList();
        var fingerprint = SupplierOrderCompletionFingerprint.Compute(
            facts.BusinessId, facts.SupplierOrderId, facts.Status, fulfillments);

        var projected = SupplierOrderCompletionPolicy.Project(
            facts.Status, fulfillments, AppliedByLineId(purchaseLines, facts.CandidateLinesByProduct));
        var linesById = facts.Lines.ToDictionary(line => line.LineId);

        return new SupplierOrderCompletionPreview(
            facts.SupplierOrderId,
            facts.Status,
            SupplierOrderCompletionPolicy.IsEligibleForCompletion(facts.Status),
            fingerprint,
            projected
                .Where(line => line.OutstandingQuantity > 0m)
                .Select(line => ToOutstandingLine(line, linesById[line.LineId]))
                .ToList());
    }

    /// <summary>
    /// How much of the entered purchase the authoritative allocation would apply to each of this
    /// order's lines. The purchase lines are handed to the policy with their position as the
    /// stand-in purchase-item id - nothing is persisted, and the policy only needs the ids to be
    /// distinct - and the allocation runs over every candidate the purchase write would consider,
    /// including the supplier's older orders, so a line this order would lose to an earlier order is
    /// projected as still outstanding here too.
    /// </summary>
    private static IReadOnlyDictionary<int, decimal> AppliedByLineId(
        IReadOnlyList<PurchaseLineToReceive> purchaseLines,
        IReadOnlyDictionary<long, IReadOnlyList<CandidateOrderLine>> candidateLinesByProduct)
    {
        var allocations = SupplierOrderFulfillmentAllocationPolicy.Allocate(
            purchaseLines.Select((line, index) => new PurchaseItemToAllocate(index, line.ProductId, line.Quantity)),
            candidateLinesByProduct);

        var applied = new Dictionary<int, decimal>();
        foreach (var allocation in allocations)
        {
            applied[allocation.SupplierOrderLineId] =
                applied.GetValueOrDefault(allocation.SupplierOrderLineId) + allocation.QuantityApplied;
        }

        return applied;
    }

    private static SupplierOrderOutstandingLine ToOutstandingLine(
        ProjectedLineOutstanding projected, SupplierOrderCompletionLineFacts line) => new(
        projected.LineId,
        line.ProductId,
        line.ProductName,
        line.Unit,
        projected.QuantityOrdered,
        projected.QuantityAlreadyReceived,
        projected.QuantityBeingReceived,
        projected.OutstandingQuantity);
}
