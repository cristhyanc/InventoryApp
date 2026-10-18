namespace Inventory.Application.Imports;

/// <summary>
/// Narrow persistence port for the Nayax product catalogue import (issue #300), owned by the
/// Application layer.
///
/// The Products slice ports from issue #240 deliberately do not serve this import and are not
/// widened to: <c>IProductStore.CreateAsync</c> creates a product from operator input, with its
/// initial stock adjustment and cost rebuild, while this import upserts a product identified by the
/// remote Nayax product id recorded on it and touches only the four Nayax-managed catalogue fields;
/// <c>IProductCatalogStore</c> reads the enriched catalogue graph an API response needs, which the
/// import neither needs nor should load.
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
    /// A row the import creates gets a database-generated id and records the remote id it came from
    /// in its external id column (issue #735): the provider's id identifies the row, and never keys
    /// it, so two businesses may import the same catalogue and a local row that merely holds that
    /// number is left alone.
    ///
    /// A local product Nayax stopped returning is never removed - the drift is reported by the
    /// Nayax catalog source-state reconciliation instead (docs/architecture.md § Nayax catalog
    /// source-state reconciliation).
    /// </summary>
    Task ApplyAsync(NayaxProductCatalogImport import, CancellationToken cancellationToken);

    /// <summary>
    /// How many of the business's products carry no <c>ExternalProductId</c> - the products this
    /// import cannot recognise, because it identifies a product only by the provider's own id
    /// (issue #736, parent #329 decision P2).
    ///
    /// <para>It is a count rather than a yes/no because the refusal says how many rows are in the
    /// way, and it is read before the remote catalogue, so a business the import would duplicate
    /// never reaches Nayax at all.</para>
    /// </summary>
    Task<int> CountProductsWithNoProviderIdAsync(CancellationToken cancellationToken);
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
/// A Nayax product group the import may create as a local category.
/// <paramref name="NayaxProductGroupId"/> is the remote <c>ProductGroupID</c>, which the local
/// category records in <c>Category.ExternalCategoryId</c> - never as its own key (issue #735,
/// parent #329 decision A4).
/// </summary>
public sealed record ImportedProductCategory(long NayaxProductGroupId, string Name, string? Description);

/// <summary>
/// One catalogue product. <paramref name="NayaxProductId"/> is the remote <c>NayaxProductID</c> and
/// <paramref name="NayaxProductGroupId"/> the remote <c>ProductGroupID</c> of its group: both are
/// provider identities, resolved to local rows through the external id columns and never read as
/// local keys (issue #735). <paramref name="UnitPrice"/> is the catalogue retail selling price -
/// never the Nayax <c>ProductCostPrice</c> cost field (AGENTS.md § Inventory and historical costing
/// invariants, issue #57).
///
/// The names say which space each id is in, because both sides of the resolution are a
/// <c>long</c>: <c>ProviderIdIsNeverALocalKeyTests</c> is what holds the two apart, and it can only
/// do that over identifiers that are named for what they are.
/// </summary>
public sealed record ImportedProductCatalogEntry(
    long NayaxProductId,
    string Name,
    string? Description,
    decimal UnitPrice,
    long? NayaxProductGroupId);
