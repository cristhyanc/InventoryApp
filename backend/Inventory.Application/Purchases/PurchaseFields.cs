namespace Inventory.Application.Purchases;

/// <summary>A purchase's editable, non-item fields, as submitted by the caller.</summary>
public sealed record PurchaseFields(
    string? Title,
    string? Notes,
    decimal? TotalAmount,
    decimal? DeliveryCost,
    decimal? PackageCost,
    DateTime? PurchaseDate,
    int? SupplierId);
