namespace Inventory.Application.Products;

/// <summary>
/// A product's editable fields, as seen by the Application layer.
///
/// <para><see cref="Name"/>, <see cref="UnitPrice"/> and <see cref="CategoryId"/> joined them in
/// issue #736 (parent #329 decision P2): a business without a sales provider maintains its own
/// catalogue, so a product's name, selling price and category have to be editable somewhere, and
/// this is that place. They were deliberately absent before, when every product arrived through the
/// Nayax catalogue import and those three fields were the import's to write.</para>
///
/// <para><strong>A product the provider names still keeps its name and category.</strong> For a
/// product carrying an <c>ExternalProductId</c> the store refuses a change to either, with
/// <c>Inventory.Domain.Providers.ProviderManagedCatalogueFields</c>'s message, because the next
/// catalogue import would overwrite it; the price and every other field here stay editable for every
/// product. Submitting the values the product already has is not a change and is accepted, which is
/// what lets a client send the whole product back.</para>
///
/// <para><see cref="UnitPrice"/> is the catalogue default selling price and never a cost: no
/// calculation in <c>Inventory.Application</c>/<c>Inventory.Domain</c> reads it, and it must never be
/// conflated with <c>AverageUnitCost</c>, a historical sale cost or a machine's live price (AGENTS.md
/// § Inventory and historical costing invariants; docs/architecture.md § Product selling price).</para>
/// </summary>
public sealed record ProductUpdateFields(
    string? Name,
    string? Sku,
    string? Description,
    decimal UnitPrice,
    int LowStockThreshold,
    int RestockTo,
    string? Unit,
    long? CategoryId,
    int? SupplierId,
    bool IsActive);
