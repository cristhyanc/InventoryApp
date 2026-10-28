using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One product in the shared catalogue (issue #923, parent #817): the thing that is looked up at a
/// supplier <em>once</em>, however many businesses want its price.
///
/// <para><strong>A platform table, on the terms <see cref="SupplierPreset"/> states.</strong> It is
/// about no business at all: a brand, a name and a size are facts about a product, and none of them
/// says whose product it is. So it carries no <c>BusinessId</c>, implements no
/// <see cref="IBusinessOwned"/>, has no query filter and takes no ownership stamp - and it is
/// declared in <c>BusinessOwnershipCoverageTests.PlatformEntities</c> beside the supplier presets and
/// the option-visibility row, never in <c>DeliberatelyGlobalEntities</c>, because each of those five
/// <em>is</em> about one business and names its key in every statement.</para>
///
/// <para><strong>What a business has instead is a mapping.</strong> A business says "this product of
/// mine is that master product" through its own tenant-owned <see cref="ProductPriceTracking"/> row,
/// and nothing about that row reaches this table: no business can learn which master products another
/// business tracks, and nothing here is copied into or out of anybody's catalogue. A business's own
/// <see cref="Product"/> is completely unchanged by this issue.</para>
///
/// <para><strong>The size is the individual item's size</strong> - 375 ml of a 375 ml can - and how
/// many come in a pack is a fact about a <em>supplier's</em> listing, not about the product, so it
/// lives on <see cref="SupplierListing.UnitsPerPack"/>. That separation is what lets one master
/// product be sold as a single, a 10-pack and a carton of 30 without being three products.</para>
///
/// <para><strong>Nothing here is a price.</strong> This row says what a product <em>is</em>; what it
/// costs at a supplier on a given day is the price-observation work of #925.</para>
/// </summary>
public class MasterProduct
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The brand, as <c>MasterProductPolicy.NormaliseBrand</c> stores it: trimmed, never empty. It is
    /// displayed with the capitalisation that was typed; <see cref="NormalisedKey"/> beside it is what
    /// decides whether two descriptions are the same product.
    /// </summary>
    public string Brand { get; set; } = string.Empty;

    /// <summary>The product's name or variant - "Classic", "Zero Sugar" - trimmed and never empty.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The size of one item, a positive decimal of at most
    /// <c>MasterProductPolicy.SizeDecimals</c> decimal places. Stored at that scale exactly, which is
    /// why a finer value is refused rather than rounded: a per-unit price divides by this number.
    /// </summary>
    public decimal SizeValue { get; set; }

    /// <summary>
    /// What <see cref="SizeValue"/> is measured in, from the closed list <c>ml</c>, <c>l</c>, <c>g</c>,
    /// <c>kg</c>, <c>each</c>. A required enum rather than free text, because a size is only
    /// comparable against a size in the same unit.
    /// </summary>
    public MasterProductSizeUnit SizeUnit { get; set; }

    /// <summary>
    /// <c>MasterProductPolicy.NormalisedKey</c> of the four values above - the brand, name, size and
    /// unit code folded so case and spacing do not make two products of one - stored so that "one
    /// description is one master product" is a unique index rather than a <c>WHERE</c> clause two
    /// concurrent inserts can both pass.
    ///
    /// <para>It is a column for the reason <see cref="SupplierPreset.NormalisedName"/> is: the
    /// comparison has to be the database's, and SQLite's <c>NOCASE</c> collation folds ASCII only.</para>
    /// </summary>
    public string NormalisedKey { get; set; } = string.Empty;

    /// <summary>When the master product was created, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// When it was last changed. Equal to <see cref="CreatedAtUtc"/> until the first edit, so "never
    /// edited" is readable without a nullable column every reader would have to interpret.
    /// </summary>
    public DateTime UpdatedAtUtc { get; set; }
}
