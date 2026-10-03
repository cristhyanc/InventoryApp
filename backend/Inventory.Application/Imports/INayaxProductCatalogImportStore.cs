namespace Inventory.Application.Imports;

/// <summary>
/// Narrow persistence port for the Nayax product catalogue import (issue #300), owned by the
/// Application layer.
///
/// The Products slice ports from issue #240 deliberately do not serve this import and are not
/// widened to: <c>IProductStore.CreateAsync</c> creates a locally keyed product (with its initial
/// stock adjustment and cost rebuild) from operator input, while this import upserts a product
/// whose primary key *is* the remote Nayax product identifier and touches only the four
/// Nayax-managed catalogue fields; <c>IProductCatalogStore</c> reads the enriched catalogue graph an
/// API response needs, which the import neither needs nor should load.
/// </summary>
public interface INayaxProductCatalogImportStore
{
    /// <summary>
    /// Applies one remote catalogue snapshot to the caller's business in a single unit of work:
    /// creates a category for every <see cref="NayaxProductCatalogImport.Categories"/> entry the
    /// business does not already own (an existing category is left exactly as it is, including its
    /// name), creates a product for every <see cref="NayaxProductCatalogImport.Products"/> entry
    /// with no local row, and overwrites the Nayax-managed catalogue fields on the rest.
    ///
    /// A local product Nayax stopped returning is never removed - the drift is reported by the
    /// Nayax catalog source-state reconciliation instead (docs/architecture.md § Nayax catalog
    /// source-state reconciliation).
    /// </summary>
    Task ApplyAsync(NayaxProductCatalogImport import, CancellationToken cancellationToken);
}

/// <summary>
/// One Nayax product catalogue snapshot, already projected onto the fields the import owns.
/// <paramref name="ImportedAtUtc"/> timestamps every row the snapshot creates or updates.
/// </summary>
public sealed record NayaxProductCatalogImport(
    IReadOnlyList<ImportedProductCategory> Categories,
    IReadOnlyList<ImportedProductCatalogEntry> Products,
    DateTime ImportedAtUtc);

/// <summary>
/// A Nayax product group the import may create as a local category. <paramref name="Id"/> is the
/// remote <c>ProductGroupID</c>, which the local category is keyed by.
/// </summary>
public sealed record ImportedProductCategory(long Id, string Name, string? Description);

/// <summary>
/// One catalogue product. <paramref name="Id"/> is the remote <c>NayaxProductID</c>, which the
/// local product is keyed by, and <paramref name="UnitPrice"/> is the catalogue retail selling
/// price - never the Nayax <c>ProductCostPrice</c> cost field (AGENTS.md § Inventory and historical
/// costing invariants, issue #57).
/// </summary>
public sealed record ImportedProductCatalogEntry(
    long Id,
    string Name,
    string? Description,
    decimal UnitPrice,
    long? CategoryId);
