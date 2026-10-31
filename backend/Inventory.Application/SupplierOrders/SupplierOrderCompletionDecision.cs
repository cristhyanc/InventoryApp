namespace Inventory.Application.SupplierOrders;

/// <summary>
/// What a client may ask for when it saves a purchase linked to an open supplier order (issue
/// #974): that the order be completed with whatever stays undelivered, and the fingerprint the
/// preview issued for the order that choice was made over.
///
/// <para><strong>That is deliberately all it carries.</strong> No receipt total, no outstanding
/// quantity, no status and no line list: every one of those is read by the purchase-save transaction
/// from the order's own rows, so there is nothing here a client could use to assert what was
/// delivered. The fingerprint is not trusted either - it is only ever compared against one the
/// server recomputes, and anything else is refused.</para>
/// </summary>
public sealed record SupplierOrderCompletionRequest(int SupplierOrderId, string DecisionFingerprint);

/// <summary>
/// The same request with the server's own attribution attached, as the purchase store receives it.
/// The actor and the instant come from <c>IAuthenticatedActorAccessor</c> and <c>IClock</c> in
/// <see cref="Inventory.Application.Purchases.UploadPurchase"/>, never from the request, so a
/// completion that cannot name who made it is refused rather than recorded as blank - the same rule
/// <c>ApplyInventoryCostRepair</c> follows for a costing repair.
/// </summary>
public sealed record SupplierOrderCompletionDecision(
    int SupplierOrderId,
    string DecisionFingerprint,
    string DecidedByDirectoryTenantId,
    string DecidedByObjectId,
    DateTime DecidedAtUtc);
