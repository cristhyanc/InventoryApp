namespace Inventory.Application.Purchases;

/// <summary>
/// One raw persisted <c>PurchaseItem</c> for a product, joined to its owning Purchase's date,
/// title/reference, and supplier - exactly the projection <see cref="GetProductPriceComparison"/>
/// needs before applying the Domain policy. Not a generic repository row: it is this one feature's
/// fact shape.
/// </summary>
public sealed record SupplierPriceHistoryFact(
    int PurchaseItemId,
    int PurchaseId,
    string PurchaseTitle,
    DateTime PurchaseDate,
    int? SupplierId,
    string? SupplierName,
    decimal UnitCost);
