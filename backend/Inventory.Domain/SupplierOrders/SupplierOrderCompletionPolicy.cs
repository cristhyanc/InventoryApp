using System.Globalization;

namespace Inventory.Domain.SupplierOrders;

/// <summary>One stored supplier-order line's fulfillment position: what was ordered and what has been received.</summary>
public readonly record struct SupplierOrderLineFulfillment(int LineId, decimal QuantityOrdered, decimal QuantityReceived);

/// <summary>
/// One supplier-order line's outstanding balance once the purchase being saved is applied to it.
/// <see cref="QuantityBeingReceived"/> is what that purchase delivers against this line, decided by
/// <see cref="SupplierOrderFulfillmentAllocationPolicy"/> rather than by anything a client submits.
/// </summary>
public readonly record struct ProjectedLineOutstanding(
    int LineId,
    decimal QuantityOrdered,
    decimal QuantityAlreadyReceived,
    decimal QuantityBeingReceived,
    decimal OutstandingQuantity);

/// <summary>
/// Whether a supplier order may be completed with part of it undelivered, and what balance would be
/// left undelivered if it were (issue #974).
///
/// <para>It is the one rule behind both halves of that decision: the preview an operator sees before
/// saving a linked purchase, and the re-validation the purchase-save transaction performs before it
/// writes the status. Both call <see cref="Project"/> over the same already-decided allocation, so
/// what the operator was shown and what the server acts on cannot be two different calculations -
/// and neither of them is a second receipt rule: the quantities applied to each line come from
/// <see cref="SupplierOrderFulfillmentAllocationPolicy"/>, exactly as the purchase write's own
/// allocation does, and the remaining balance from
/// <see cref="SupplierOrderLineOutstandingPolicy"/>.</para>
///
/// <para>Completing an order changes nothing that was received. It records no quantity, no stock
/// movement, no cost and no GST component for goods that never arrived: the only thing it writes is
/// that nobody is waiting for them any more.</para>
/// </summary>
public static class SupplierOrderCompletionPolicy
{
    /// <summary>
    /// The refusal when the submitted decision no longer describes the order - another receipt, a
    /// cancellation or a completion landed in between - which is the one answer the purchase save
    /// gives instead of applying a decision nobody saw. It names the action, because the operator's
    /// entered purchase is still on screen and re-previewing is what makes it savable.
    /// </summary>
    public const string StaleDecisionMessage =
        "This supplier order changed after the completion choice was shown, so nothing was saved. " +
        "Reload the order, review the outstanding quantities and save the purchase again.";

    /// <summary>
    /// The refusal when the order is no longer one that can be completed at all: it is already
    /// cancelled, already fully received, or already completed with an undelivered balance.
    /// </summary>
    public const string NotEligibleMessage =
        "This supplier order is no longer open, so it cannot be completed with undelivered quantities. " +
        "Reload the order and save the purchase again.";

    /// <summary>
    /// The refusal when the purchase leaves nothing outstanding. There is no undelivered balance to
    /// close, and the order's own fully-received state is the truthful one, so the request is
    /// refused rather than quietly recording a completion over a complete delivery.
    /// </summary>
    public const string NoOutstandingBalanceMessage =
        "This purchase leaves nothing outstanding on the supplier order, so there is no undelivered " +
        "balance to close. Reload the order and save the purchase again.";

    /// <summary>The refusal when the submitted order id is not an order of the caller's business.</summary>
    public const string UnknownOrderMessage = "The supplier order to complete does not exist.";

    /// <summary>
    /// The refusal when a purchase that was linked to an order completed with undelivered quantities
    /// would be edited or deleted (issue #974). Reopening a completed order is out of scope, and
    /// rewriting or removing the receipts it was closed on would either reopen it silently or leave
    /// it recording deliveries that no purchase holds any more, so the correction is refused whole.
    /// </summary>
    public static string LinkedToCompletedOrderMessage(IEnumerable<int> orderIds)
    {
        ArgumentNullException.ThrowIfNull(orderIds);

        var ids = string.Join(", ", orderIds.Distinct().OrderBy(id => id).Select(id => $"#{id.ToString(CultureInfo.InvariantCulture)}"));
        return $"This purchase records the final delivery of supplier order {ids}, which was completed with " +
            "undelivered quantities. Reopening a completed order is not supported, so this purchase can no " +
            "longer be changed or deleted.";
    }

    /// <summary>The status an explicitly completed order carries. Never inferred from quantities.</summary>
    public const SupplierOrderStatus CompletedStatus = SupplierOrderStatus.ClosedPartiallyDelivered;

    /// <summary>An order a person may still choose to complete: one that is open, in either of its two open states.</summary>
    public static bool IsEligibleForCompletion(SupplierOrderStatus status) =>
        status is SupplierOrderStatus.Ordered or SupplierOrderStatus.PartiallyReceived;

    /// <summary>Whether the order carries the explicit completion decision.</summary>
    public static bool IsExplicitlyCompleted(SupplierOrderStatus status) => status == CompletedStatus;

    /// <summary>
    /// Whether the receipt rollup must leave this order's status exactly as it is. A cancelled order
    /// has always been excluded from it; an explicitly completed one is excluded for the same
    /// reason - the status is a person's decision, not a function of the quantities.
    /// </summary>
    public static bool PreservesStatus(SupplierOrderStatus status) =>
        status is SupplierOrderStatus.Cancelled or SupplierOrderStatus.ClosedPartiallyDelivered;

    /// <summary>
    /// Each line's balance after <paramref name="quantityAppliedByLineId"/> - the purchase's own
    /// allocation - is applied, with the lines that would be left outstanding first. The order's
    /// <paramref name="status"/> is the one it is in <em>now</em>: an order that is already closed
    /// owes nothing, so projecting one reports no outstanding balance at all.
    /// </summary>
    public static IReadOnlyList<ProjectedLineOutstanding> Project(
        SupplierOrderStatus status,
        IEnumerable<SupplierOrderLineFulfillment> lines,
        IReadOnlyDictionary<int, decimal> quantityAppliedByLineId)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(quantityAppliedByLineId);

        return lines
            .Select(line =>
            {
                var applied = quantityAppliedByLineId.GetValueOrDefault(line.LineId);
                return new ProjectedLineOutstanding(
                    line.LineId,
                    line.QuantityOrdered,
                    line.QuantityReceived,
                    applied,
                    SupplierOrderLineOutstandingPolicy.Outstanding(
                        status, line.QuantityOrdered, line.QuantityReceived + applied));
            })
            .OrderBy(line => line.LineId)
            .ToList();
    }

    /// <summary>
    /// Whether any projected line would still be owed something. This is the question the
    /// confirmation is offered on, and the one the save re-asks before it writes the status.
    /// </summary>
    public static bool HasOutstandingBalance(IEnumerable<ProjectedLineOutstanding> projected)
    {
        ArgumentNullException.ThrowIfNull(projected);
        return projected.Any(line => line.OutstandingQuantity > 0m);
    }
}
