namespace Inventory.Application.Suppliers;

/// <summary>
/// A persisted supplier as seen by the Application layer. <paramref name="Url"/> is the optional
/// website (issue #847): <c>null</c> when the business has not recorded one, and otherwise an
/// absolute http(s) address <see cref="SupplierUrlPolicy"/> already accepted.
/// </summary>
public sealed record SupplierRecord(
    int Id, string Name, string? ContactName, string? Phone, string? Email, string? Address, string? Url);
