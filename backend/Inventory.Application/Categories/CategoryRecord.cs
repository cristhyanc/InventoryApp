namespace Inventory.Application.Categories;

/// <summary>
/// A persisted product category as seen by the Application layer.
/// </summary>
public sealed record CategoryRecord(long Id, string Name, string? Description);
