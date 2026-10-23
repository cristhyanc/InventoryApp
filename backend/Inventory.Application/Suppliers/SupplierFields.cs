namespace Inventory.Application.Suppliers;

/// <summary>
/// A supplier's editable fields, as seen by the Application layer. <see cref="Url"/> is the
/// optional website (issue #847) as submitted, before <see cref="SupplierUrlPolicy"/> normalizes
/// it; <see cref="ISupplierStore.AddAsync"/> and <see cref="ISupplierStore.UpdateAsync"/> only ever
/// see the normalized value.
/// </summary>
public sealed record SupplierFields(
    string Name, string? ContactName, string? Phone, string? Email, string? Address, string? Url);
