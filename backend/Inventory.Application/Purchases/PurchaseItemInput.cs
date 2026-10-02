namespace Inventory.Application.Purchases;

/// <summary>A requested purchase line item, as submitted by the caller before validation.</summary>
public sealed record PurchaseItemInput(long ProductId, decimal Quantity, decimal UnitCost);
