namespace Inventory.Application.Purchases;

/// <summary>
/// One product's last and cheapest recorded actual purchase cost, with the supplier each one was
/// paid to (issue #788, B of #786). It is the bulk form of the two entries
/// <see cref="ProductPriceComparisonDto.Latest"/>/<see cref="ProductPriceComparisonDto.Lowest"/>
/// already carry for a single product, derived by the same
/// <c>Inventory.Domain.Purchases.SupplierPriceComparisonPolicy</c>, so the product screens and a
/// product's own price history can never disagree about what was last paid or about who it was
/// cheapest from.
///
/// <para>A summary exists only for a product with <b>at least one</b> recorded purchase line, which
/// is why every cost and date here is non-nullable: a product with no purchase history is absent
/// from the answer rather than present with nulls or zeros.</para>
///
/// <para><see cref="LastSupplierName"/>/<see cref="LowestSupplierName"/> are
/// <see langword="null"/> exactly when the purchase they come from has no supplier recorded.
/// A client must present that explicitly (for example "None"); it is never replaced here with
/// another purchase's supplier or with a guess, and the purchase is never dropped to avoid saying
/// so - dropping it would quietly move the last cost to an older purchase.</para>
///
/// <para>A unit cost of <c>0</c> is a real recorded cost (a free sample, a replacement delivery)
/// and stays one: it is neither filtered out nor reported as a missing figure.</para>
/// </summary>
public sealed record ProductPurchaseCostSummaryDto(
    long ProductId,
    decimal LastUnitCost,
    string? LastSupplierName,
    DateTime LastPurchaseDate,
    decimal LowestUnitCost,
    string? LowestSupplierName,
    DateTime LowestPurchaseDate);
