using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One product as one supplier publishes it (issue #923, parent #817): the supplier's own id for it,
/// the page it is on, how many come in a pack, and the barcode it carries.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> A
/// supplier's listing is a fact about that supplier's shelf, not about anybody's business, so this
/// row carries no <c>BusinessId</c>, has no query filter and takes no ownership stamp. The supplier
/// it belongs to is a <see cref="SupplierPreset"/> - the platform's own supplier master since issue
/// #931 - referenced with <c>Restrict</c>, because a supplier record whose listings would be
/// orphaned is a record that must not quietly disappear from under them.</para>
///
/// <para><strong>It belongs to exactly one of two owners, and the database says so.</strong> Either
/// <see cref="MasterProductId"/> - the normal state - or <see cref="WaitingKey"/>, the search key the
/// listing was discovered under while nothing had matched it yet. Both at once is a listing claiming
/// to be two things; neither is a listing nothing can find again. The rule is a check constraint as
/// well as <c>SupplierListingPolicy.HasExactlyOneOwner</c>, because a row in either condition is not
/// something an application-level check can be trusted to have prevented.</para>
///
/// <para><strong><see cref="OriginWaitingKey"/> is a separate column for exactly that reason.</strong>
/// When a match moves a listing to a master product (#941), the active waiting key has to be cleared
/// or the constraint above is violated - and the provenance, which search found this listing, is
/// precisely what a reviewer of a wrong match needs afterwards. Keeping discovery provenance and
/// current ownership in one column would have forced a choice between losing the first and breaking
/// the second.</para>
///
/// <para><strong>Nothing here is a price.</strong> What this listing costs on a given day is the
/// price-observation work of #925; this row is what a price would later be observed <em>for</em>.</para>
/// </summary>
public class SupplierListing
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The supplier this listing is published by: a platform <see cref="SupplierPreset"/>, which issue
    /// #931 made the supplier master for price checks. Required, and restricted on delete.
    /// </summary>
    public int SupplierPresetId { get; set; }

    /// <summary>The supplier master record itself.</summary>
    public virtual SupplierPreset? SupplierPreset { get; set; }

    /// <summary>
    /// The master product this listing is a listing of, or <see langword="null"/> while it is still
    /// waiting on a match. Exactly one of this and <see cref="WaitingKey"/> is set; restricted on
    /// delete, so a master product with listings cannot be removed from under them.
    /// </summary>
    public long? MasterProductId { get; set; }

    /// <summary>The master product itself, while there is one.</summary>
    public virtual MasterProduct? MasterProduct { get; set; }

    /// <summary>
    /// The waiting key this listing is currently held under, or <see langword="null"/> once it has a
    /// master product. The <em>active</em> half of the exclusive-ownership rule above.
    /// </summary>
    public string? WaitingKey { get; set; }

    /// <summary>
    /// The waiting key whose search originally found this listing, kept after the listing moves to a
    /// master product (#941), or <see langword="null"/> for a listing that was never discovered by a
    /// search. Provenance only: it is deliberately outside the exclusive-ownership check, which is
    /// what lets a matched listing still say where it came from.
    /// </summary>
    public string? OriginWaitingKey { get; set; }

    /// <summary>
    /// The supplier's own product id or SKU, as written. Opaque text, compared exactly and never
    /// parsed, for the reason every external identifier in this application is
    /// (AGENTS.md § Nayax integration): a retailer that issues a code or a slug needs no schema
    /// change. Unique per supplier master record, because one supplier lists one product once.
    /// </summary>
    public string SupplierProductId { get; set; } = string.Empty;

    /// <summary>The page this listing is on, or <see langword="null"/>. Nothing fetches or resolves it.</summary>
    public string? Url { get; set; }

    /// <summary>
    /// The supplier's own wording for the product, or <see langword="null"/>. Kept as published,
    /// because it is the evidence a reviewer reads when deciding whether a match is right - never a
    /// value anything derives a size, a pack count or a brand from.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// How many individual items this listing sells together, or <see langword="null"/> when the
    /// supplier does not say.
    ///
    /// <para><strong>Unknown stays unknown</strong> (issue #923). Not 1, which would make a carton's
    /// price read as a single's, and not 0, which divides: a listing whose pack size nobody published
    /// has no per-unit price, and that is an answer rather than a gap. A positive integer when it is
    /// known - the database checks that too.</para>
    /// </summary>
    public int? UnitsPerPack { get; set; }

    /// <summary>
    /// The barcode the supplier published, or <see langword="null"/>. <strong>Text, never a
    /// number</strong>: <c>0012345678905</c> and <c>12345678905</c> are different barcodes and the
    /// leading zeros are not recoverable once parsed away, so nothing on this path converts,
    /// re-formats or pads it.
    /// </summary>
    public string? Barcode { get; set; }

    /// <summary>
    /// Whether <see cref="Barcode"/> is the individual item's code or the carton's, or
    /// <see langword="null"/> when there is no barcode. The two travel together - the database checks
    /// that - because a carton code read as a unit code prices one can at the price of thirty.
    /// </summary>
    public BarcodeScope? BarcodeScope { get; set; }

    /// <summary>
    /// How confident the catalogue is that this listing really is the product it hangs from:
    /// proposed by the collector, confirmed, or flagged for a person to look at.
    /// </summary>
    public SupplierListingMatchStatus MatchStatus { get; set; } = SupplierListingMatchStatus.Proposed;

    /// <summary>When the listing was first recorded, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was last changed; equal to <see cref="CreatedAtUtc"/> until the first edit.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
