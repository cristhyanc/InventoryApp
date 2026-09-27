namespace Inventory.Application.Suppliers;

/// <summary>
/// A persisted supplier as seen by the Application layer.
/// </summary>
public sealed record SupplierRecord(int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address);
