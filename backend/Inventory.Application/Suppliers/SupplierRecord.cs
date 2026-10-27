namespace Inventory.Application.Suppliers;

/// <summary>
/// A persisted supplier as seen by the Application layer. <paramref name="Url"/> is the optional
/// website (issue #847): <c>null</c> when the business has not recorded one, and otherwise an
/// absolute http(s) address <see cref="SupplierUrlPolicy"/> already accepted.
///
/// <para><paramref name="SupplierPresetId"/> is the platform supplier master row this supplier was
/// copied from (issue #934), or <c>null</c> for a supplier the business created itself. It is the
/// single fact <c>Inventory.Domain.Suppliers.SupplierPresetLinkPolicy</c> reads, and it is carried
/// here rather than published as a derived "is linked" boolean so the one policy stays the only
/// thing that interprets it.</para>
///
/// <para><paramref name="IsHidden"/> is the business's "Not used" switch on a linked copy: the row
/// stays, its purchase and order history still resolves it, and the supplier pickers leave it
/// out.</para>
/// </summary>
public sealed record SupplierRecord(
    int Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Email,
    string? Address,
    string? Url,
    int? SupplierPresetId,
    bool IsHidden);
