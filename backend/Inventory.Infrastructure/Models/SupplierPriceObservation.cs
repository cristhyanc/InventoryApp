using Inventory.Domain.PriceCatalogue;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// One price one supplier published for one listing at one instant (issue #925, parent #817), stored
/// as it was published.
///
/// <para><strong>A platform table, on the terms <see cref="MasterProduct"/> states.</strong> What a
/// supplier charges for a pack is a fact about that supplier's shelf and about no business, so this
/// row carries no <c>BusinessId</c>, has no query filter and takes no ownership stamp. The listing it
/// belongs to is a <see cref="SupplierListing"/> and the run that read it is a
/// <see cref="PriceCollectorRun"/>; both references restrict, because an observation whose listing or
/// whose provenance had been deleted from under it would be a price nobody could place.</para>
///
/// <para><strong>Append-only, and raw.</strong> A row is written once and never edited - there is no
/// <c>UpdatedAtUtc</c>, because an observation is what was read at an instant and a correction is a
/// later observation. The amounts, the basis and the stock answer are stored exactly as published,
/// which is the same separation AGENTS.md § Nayax integration requires of imported facts: the raw
/// fact and the derived comparison stay apart so the comparison can be rerun and audited.</para>
///
/// <para><strong>None of it is a cost.</strong> An observed supplier price never becomes a purchase, a
/// unit cost, an average cost, an inventory valuation, a GST accounting figure or a line of any
/// report. No purchase, cost, inventory, GST-accounting or reporting code reads this table, and
/// <c>SupplierPriceObservationBoundaryTests</c> pins that.</para>
///
/// <para>Nothing writes this table yet: the collector import of #937 is its writer, and the summary
/// endpoint and UI are #930.</para>
/// </summary>
public class SupplierPriceObservation
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public long Id { get; set; }

    /// <summary>The listing this is a price of. Required, and restricted on delete.</summary>
    public long SupplierListingId { get; set; }

    /// <summary>The listing itself.</summary>
    public virtual SupplierListing? SupplierListing { get; set; }

    /// <summary>
    /// The collector run that read this price - the first half of its source. Required, and
    /// restricted on delete, because provenance that can disappear is not provenance.
    /// </summary>
    public long PriceCollectorRunId { get; set; }

    /// <summary>The run itself.</summary>
    public virtual PriceCollectorRun? PriceCollectorRun { get; set; }

    /// <summary>
    /// What the whole pack cost, on <see cref="PriceBasis"/>. Above zero -
    /// <c>SupplierPriceObservationPolicy</c> and the database both refuse zero, which would divide
    /// into a unit price of zero and compare as cheaper than every real offer.
    ///
    /// <para>It is the <em>pack</em> price deliberately: the per-unit figure is
    /// <c>pack price ÷ units per pack</c>, derived by <see cref="CheapestUnitOfferPolicy"/> at full
    /// precision and rounded only for display, so nothing stored here has been divided or rounded
    /// already.</para>
    /// </summary>
    public decimal PackPrice { get; set; }

    /// <summary>
    /// What the pack normally costs when <see cref="IsPromotion"/> is set and the supplier publishes
    /// it, or <see langword="null"/>.
    ///
    /// <para>Never filled in from <see cref="PackPrice"/>: "this is the normal price" and "we do not
    /// know the normal price" are different facts, and a promotion's saving cannot be read off a
    /// column somebody completed with the promotional price.</para>
    /// </summary>
    public decimal? RegularPackPrice { get; set; }

    /// <summary>
    /// The currency, which is <c>SupplierPriceObservationPolicy.Currency</c> - <c>AUD</c> - and which
    /// the database checks.
    ///
    /// <para>It is a stored column rather than an assumption so that a price in anything else can be
    /// refused on arrival instead of being compared as though it were dollars. A second currency is a
    /// decision about conversion rates nobody has made.</para>
    /// </summary>
    public string Currency { get; set; } = SupplierPriceObservationPolicy.Currency;

    /// <summary>
    /// Whether <see cref="PackPrice"/> includes GST, <strong>as observed</strong>: the basis of the
    /// supplier master record that published it (<c>SupplierPreset.PricesIncludeGst</c>, issue #931)
    /// at the time of the reading, copied onto the row rather than read back through the supplier.
    ///
    /// <para>Copied deliberately. A supplier that changes how it publishes prices must not silently
    /// restate every price this application already observed on the other basis - which is exactly
    /// what reading the basis through the supplier would do.</para>
    /// </summary>
    public SupplierPriceBasis PriceBasis { get; set; }

    /// <summary>
    /// The supplier's own GST rate for this product when it publishes one - the JB Metro <c>gst</c>
    /// field - as a proportion between 0 and 1, or <see langword="null"/> when it does not.
    ///
    /// <para>Unknown stays unknown. It is not defaulted to the standard rate and not to zero: an
    /// offer whose rate neither the supplier nor the product states is shown and not compared
    /// (<see cref="CheapestUnitOfferPolicy"/>), which is a real answer, while either default would be
    /// an invented tax basis.</para>
    /// </summary>
    public decimal? SupplierGstRate { get; set; }

    /// <summary>Whether this price was a promotion rather than the supplier's ordinary price.</summary>
    public bool IsPromotion { get; set; }

    /// <summary>
    /// When the promotion finishes, or <see langword="null"/> for an ordinary price and for a
    /// promotion whose end the supplier does not state. An end date belongs to a promotion - the
    /// database checks that - because "promotion ended" withdraws an offer from the comparison, and a
    /// stray date would withdraw a real price.
    /// </summary>
    public DateTime? PromotionEndsAtUtc { get; set; }

    /// <summary>
    /// What the supplier said about availability. <see cref="SupplierStockStatus.Unknown"/> is a real
    /// answer and is not <see cref="SupplierStockStatus.InStock"/>: only a stated "out of stock"
    /// withdraws an offer.
    /// </summary>
    public SupplierStockStatus StockStatus { get; set; }

    /// <summary>
    /// When the price was observed. This is the instant the freshness limit is measured from, not
    /// <see cref="CreatedAtUtc"/>: an import that lands hours after the read must not make a price
    /// look newer than it is.
    /// </summary>
    public DateTime ObservedAtUtc { get; set; }

    /// <summary>
    /// The collector's own reference for the result it read - the second half of its source - or
    /// <see langword="null"/>. Opaque text, compared exactly and never parsed.
    /// </summary>
    public string? SourceReference { get; set; }

    /// <summary>
    /// The page this price was read from, or <see langword="null"/>. It is what a person follows to
    /// see the price for themselves; nothing fetches or resolves it.
    /// </summary>
    public string? SourceUrl { get; set; }

    /// <summary>When the row was stored, from the application clock. There is no updated instant: a
    /// correction is a later observation, never an edit of this one.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
