using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// The latest reading of one supplier listing that was <strong>not</strong> accepted as its price
/// (issue #937, parent #817): what the collector read, kept where a person reviewing the listing can
/// see it.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> It is a
/// reading of a supplier's shelf, so it carries no <c>BusinessId</c>, has no query filter and takes
/// no ownership stamp. It is declared in <c>BusinessOwnershipCoverageTests.PlatformEntities</c>
/// beside the eight tables of #848, #873, #923, #924 and #925.</para>
///
/// <para><strong>It is a separate table precisely so <see cref="SupplierPriceObservation"/> can stay
/// what it says it is.</strong> Every row of that table is an accepted price: the listing's
/// <see cref="SupplierListing.LatestObservationId"/> points into it, the comparison of #930 reads
/// it, and <c>SupplierPriceObservationBoundaryTests</c> pins who may. A reading that was held back -
/// because the listing is not confirmed, because its identity moved, or because the price is outside
/// its band - is not a price, and storing it in the same table behind a flag would have made every
/// reader of that table responsible for remembering the flag. Here it cannot be read as a price by
/// accident.</para>
///
/// <para><strong>One row per listing, replaced rather than appended.</strong> The issue's own
/// wording: a row is "that listing's <em>latest</em> pending observation (replacing the previous
/// pending one)". A reviewer needs the current disagreement, not a history of disagreements, and an
/// append-only pending store would grow by one row per listing per run for every listing nobody has
/// confirmed yet. A unique index on the listing is what makes that the database's rule.</para>
///
/// <para><strong>Accepting a price clears it.</strong> A listing whose next reading is accepted has
/// nothing pending any more, so the row is removed - leaving it would show a reviewer a
/// disagreement that has already been resolved.</para>
/// </summary>
public class SupplierListingPendingObservation
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>
    /// The listing this reading is of. <strong>Unique</strong>: one listing holds at most one pending
    /// reading, which is the "replacing the previous pending one" rule expressed as an index rather
    /// than as a delete-then-insert nothing enforces.
    /// </summary>
    public long SupplierListingId { get; set; }

    /// <summary>The listing itself.</summary>
    public virtual SupplierListing? SupplierListing { get; set; }

    /// <summary>
    /// The collector run that read it - the first half of its provenance. Required, and restricted
    /// on delete, exactly as an accepted observation's is.
    /// </summary>
    public long PriceCollectorRunId { get; set; }

    /// <summary>The run itself.</summary>
    public virtual PriceCollectorRun? PriceCollectorRun { get; set; }

    /// <summary>
    /// The issued work item the reading answered - the second half. It is here and not on an accepted
    /// observation because a pending reading is evidence a person reads: "which search produced
    /// this?" is the first question about a disagreement. Restricted on delete.
    /// </summary>
    public long PriceCollectorWorkItemId { get; set; }

    /// <summary>The work item itself.</summary>
    public virtual PriceCollectorWorkItem? PriceCollectorWorkItem { get; set; }

    /// <summary>
    /// Why this reading was held rather than accepted - one of
    /// <c>PriceCollectorResultPolicy</c>'s reasons, stored as the sentence the collector was given,
    /// so the response and the review screen say the same thing.
    /// </summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>What the whole pack cost, on <see cref="PriceBasis"/>. Above zero, as an accepted price is.</summary>
    public decimal PackPrice { get; set; }

    /// <summary>What the pack normally costs, when the supplier publishes it beside a promotion.</summary>
    public decimal? RegularPackPrice { get; set; }

    /// <summary>The currency, which is <c>SupplierPriceObservationPolicy.Currency</c>.</summary>
    public string Currency { get; set; } = SupplierPriceObservationPolicy.Currency;

    /// <summary>Whether <see cref="PackPrice"/> includes GST, as observed.</summary>
    public SupplierPriceBasis PriceBasis { get; set; }

    /// <summary>The supplier's own GST rate for this product when it publishes one, as a proportion.</summary>
    public decimal? SupplierGstRate { get; set; }

    /// <summary>Whether this price was a promotion rather than the supplier's ordinary price.</summary>
    public bool IsPromotion { get; set; }

    /// <summary>When the promotion finishes, or <see langword="null"/>.</summary>
    public DateTime? PromotionEndsAtUtc { get; set; }

    /// <summary>What the supplier said about availability.</summary>
    public SupplierStockStatus StockStatus { get; set; }

    /// <summary>When the price was read.</summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>The collector's own reference for the result it read, or <see langword="null"/>.</summary>
    public string? SourceReference { get; set; }

    /// <summary>The page it was read from, or <see langword="null"/>. Nothing fetches or resolves it.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The identity the collector read, kept beside the price because that is usually what the
    /// disagreement is <em>about</em>: a reviewer comparing a flagged listing with the reading that
    /// flagged it needs both halves. The supplier's own wording for which variant this is.
    /// </summary>
    public string? Variant { get; set; }

    /// <summary>The individual size the collector read, or <see langword="null"/>.</summary>
    public decimal? SizeValue { get; set; }

    /// <summary>What that size is measured in, or <see langword="null"/>.</summary>
    public MasterProductSizeUnit? SizeUnit { get; set; }

    /// <summary>The barcode the collector read, as text and never parsed.</summary>
    public string? Barcode { get; set; }

    /// <summary>Whether that barcode is the unit's or the carton's.</summary>
    public BarcodeScope? BarcodeScope { get; set; }

    /// <summary>How many individual items the collector read in the pack, or <see langword="null"/>.</summary>
    public int? UnitsPerPack { get; set; }

    /// <summary>The supplier's own wording for the product, as published.</summary>
    public string? Title { get; set; }

    /// <summary>When this row was written, from the application clock. It is replaced, never edited.</summary>
    public DateTime RecordedAtUtc { get; set; }
}
