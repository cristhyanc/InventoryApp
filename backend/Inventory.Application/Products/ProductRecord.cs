using Inventory.Domain.Products;
using Inventory.Domain.Stock;

namespace Inventory.Application.Products;

/// <summary>
/// One catalogue product as the Application layer sees it (issue #240): the persisted catalogue and
/// costing fields, its category detail, its stock-adjustment history, and the two reorder
/// inputs (<see cref="MachineReplenishmentNeed"/>/<see cref="OnOrderQuantity"/>) that
/// <see cref="ListLowStockProducts"/> resolves live rather than reading from the database.
///
/// Derived reorder values are deliberately absent: <see cref="Inventory.Domain.Products.ProductReorderPolicy"/>
/// owns those formulas and both this layer and the API's response mapping call it, so there is one
/// implementation rather than a cached copy that can disagree with it.
/// </summary>
public sealed record ProductRecord
{
    public required long Id { get; init; }
    public required string Name { get; init; }
    public string? Sku { get; init; }
    public string? Description { get; init; }

    // No selling price since issue #792 (A2 of #786): a product carries none. AverageUnitCost is a
    // cost and is unaffected.
    public required decimal AverageUnitCost { get; init; }
    public int? CostingQuantity { get; init; }
    public decimal? InventoryValue { get; init; }
    public required int QuantityInStock { get; init; }
    public required int LowStockThreshold { get; init; }
    public required int RestockTo { get; init; }
    public string? Unit { get; init; }
    public required bool IsActive { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }
    public long? CategoryId { get; init; }
    public ProductCategoryRecord? Category { get; init; }
    public IReadOnlyList<ProductStockAdjustmentRecord> StockAdjustments { get; init; } = [];

    /// <summary>
    /// Whether the business's sales provider names this product - that is, whether the row carries an
    /// <c>ExternalProductId</c> (issue #736). It is the fact, not the identifier: a provider's id is
    /// integration state that no reader of a product needs, and
    /// <c>Inventory.Domain.Providers.ProviderManagedCatalogueFields</c> turns this one bit into the
    /// fields a client must lock and the refusal a local edit of one gets.
    ///
    /// <para>Not <c>required</c>: a read path that does not resolve it leaves it <c>false</c>, which
    /// is what every product of a business with no provider is.</para>
    /// </summary>
    public bool IsProviderManaged { get; init; }

    /// <summary>
    /// Whether this product may be deleted and, when it may not, the refusal that says why
    /// (issue #737): the deletion rule's own answer over the rows that refer to the product, resolved
    /// on the reads a person deletes from so a screen can offer <strong>Delete</strong> only where the
    /// API allows it.
    ///
    /// <para><see langword="null"/> means <em>not resolved on this read path</em> - not "deletable".
    /// Only the catalogue listing and the single-product read resolve it
    /// (<see cref="IProductCatalogStore"/>); the listings that exist to rank or enrich rows - the
    /// reorder alerts, the home Dashboard summary and the machine product views - do not pay for six
    /// reference counts to answer a question their callers never ask. A caller that is told nothing
    /// must not read that as permission.</para>
    ///
    /// <para>Advisory in every case: the delete counts again inside its own transaction and decides
    /// there (<see cref="Inventory.Domain.Products.ProductDeletionEligibility"/>).</para>
    /// </summary>
    public ProductDeletionEligibility? DeletionEligibility { get; init; }

    /// <summary>
    /// Whether anything recorded ever put inventory into this product, took it out, or valued it
    /// (issue #861) - the history half of the evidence
    /// <see cref="Inventory.Domain.Reporting.Dashboard.ProductInventoryValuationFact"/> defines, which
    /// is what lets the Dashboard's valuation tell a product that has never held inventory from one
    /// whose inventory is genuinely unknown.
    ///
    /// <para><see langword="null"/> means <em>not resolved on this read path</em> - never "no
    /// history". Only <see cref="IProductCatalogStore.ListUnorderedAsync"/> can resolve it, and only
    /// when its caller asks for it (its <c>resolveInventoryHistory</c> parameter): the home Dashboard
    /// summary is the only caller that aggregates a valuation from these records and asks for it; the
    /// catalogue listing and the single-product read answer the deletion question instead
    /// (<see cref="DeletionEligibility"/>), and the other <c>ListUnorderedAsync</c> callers ask
    /// <see langword="false"/> and do not pay for rows nothing on those screens reads. A caller handed
    /// <see langword="null"/> must treat the product as having history, which keeps an unknown value
    /// unavailable rather than silently dropping it from a total.</para>
    /// </summary>
    public bool? HasInventoryHistory { get; init; }

    /// <summary>
    /// Units the machines currently need refilled from storage. Zero unless a caller resolved the
    /// live machine fleet (<see cref="ListLowStockProducts"/>), exactly as the former
    /// <c>ProductService.GetAll</c>/<c>Get</c> left it unset.
    /// </summary>
    public int MachineReplenishmentNeed { get; init; }

    /// <summary>Units on an outstanding supplier order, resolved on the same path.</summary>
    public decimal OnOrderQuantity { get; init; }

    /// <summary>The reorder quantity for this product, from the one authoritative Domain formula.</summary>
    public decimal NeedToOrder => ProductReorderPolicy.NeedToOrder(
        QuantityInStock, OnOrderQuantity, MachineReplenishmentNeed, LowStockThreshold, RestockTo);

    /// <summary>Whether this product belongs on the reorder-alert list, from the same Domain formula.</summary>
    public bool IsReorderAlert => ProductReorderPolicy.IsReorderAlert(IsActive, NeedToOrder);
}

/// <summary>The category detail carried on a product, as the Application layer sees it.</summary>
public sealed record ProductCategoryRecord(long Id, string Name, string? Description);

/// <summary>
/// One persisted stock movement in a product's history. <paramref name="Reason"/> and
/// <paramref name="Source"/> use the authoritative Stock vocabulary
/// (<see cref="Inventory.Domain.Stock.StockAdjustmentReason"/>/<see cref="Inventory.Domain.Stock.StockAdjustmentSource"/>,
/// issue #282's #240 handoff); no product read orchestration branches on them, so this record only
/// carries them through unchanged.
/// </summary>
public sealed record ProductStockAdjustmentRecord(
    int Id,
    long ProductId,
    int? ReceiptItemId,
    int QuantityChange,
    int QuantityAfter,
    decimal? UnitCost,
    decimal? TotalCost,
    int? CostingQuantityAfter,
    decimal? AverageUnitCostAfter,
    decimal? InventoryValueAfter,
    StockAdjustmentReason Reason,
    StockAdjustmentSource Source,
    long? MachineId,
    string? Notes,
    DateTime? EatBefore,
    DateTime CreatedAt,
    DateTime EffectiveAt);
