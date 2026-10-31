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
/// <para><strong>The prices themselves are not here.</strong> What this listing cost on a given day
/// is a <see cref="SupplierPriceObservation"/> (issue #925); what this row keeps is <em>which</em>
/// observation is current, when the listing was last checked successfully and when its price last
/// changed - three facts, deliberately not one, and none of them ever cleared by a failed or missing
/// check. See <see cref="LatestObservationId"/>.</para>
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
    /// The supplier's own wording for <em>which</em> one this is - "Classic", "Zero Sugar" - or
    /// <see langword="null"/> when it publishes none (issue #937).
    ///
    /// <para>Read off the listing and never derived from <see cref="Title"/>, for the reason the
    /// title says it is evidence rather than a source of fields. It is part of the listing's
    /// <strong>identity</strong>: a confirmed listing whose variant moves is flagged rather than
    /// re-priced, because the pack on the shelf is no longer the pack somebody confirmed. See
    /// <c>PriceCollectorResultPolicy.HasIdentityChanged</c>.</para>
    /// </summary>
    public string? Variant { get; set; }

    /// <summary>
    /// The size of one individual item as the supplier publishes it - the 375 of a 375 ml can - or
    /// <see langword="null"/> when it does not (issue #937).
    ///
    /// <para>It is the listing's own reading and deliberately not the master product's size: the two
    /// disagreeing is precisely the evidence a reviewer of a wrong match needs, so copying the
    /// master product's down here would destroy it. Travels with <see cref="SizeUnit"/>, which the
    /// database checks, because a number with no unit cannot be compared with anything.</para>
    /// </summary>
    public decimal? SizeValue { get; set; }

    /// <summary>
    /// What <see cref="SizeValue"/> is measured in, from the same closed unit list a master
    /// product's own size uses, or <see langword="null"/> when there is no size.
    /// </summary>
    public MasterProductSizeUnit? SizeUnit { get; set; }

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

    /// <summary>
    /// The latest accepted observation of this listing's price (issue #925), or
    /// <see langword="null"/> while it has never been checked - which is every listing as issue #923
    /// shipped them. Restricted on delete, so the observation a listing points at cannot be removed
    /// from under it.
    ///
    /// <para><strong>It is replaced only by a later accepted observation, never by an absence.</strong>
    /// A collector run that fails, or that never reaches this listing, leaves this column and
    /// <see cref="LastCheckedAtUtc"/> exactly as they were - the owner's constraint of 27 October
    /// 2026 - and <c>SupplierPriceFreshnessPolicy</c> is what eventually stops that price being
    /// compared. Emptying it on a failure would turn a missed check into "this supplier has no
    /// price", which is the same mistake the report availability model exists to prevent.</para>
    ///
    /// <para>There is deliberately no navigation property in either direction: the pointer is a
    /// cursor into an append-only table, not a parent-child graph, and no <c>Include</c> may pull a
    /// listing's whole price history along with it.</para>
    /// </summary>
    public long? LatestObservationId { get; set; }

    /// <summary>
    /// When this listing was last checked <em>successfully</em>, or <see langword="null"/> while it
    /// never has been. Recorded together with <see cref="LatestObservationId"/> -
    /// <c>SupplierListingPriceStatePolicy</c> and the database both hold that - because each half is
    /// the evidence about the other.
    /// </summary>
    public DateTime? LastCheckedAtUtc { get; set; }

    /// <summary>
    /// When this listing's price last changed, or <see langword="null"/> while it holds no
    /// observation.
    ///
    /// <para>Separate from <see cref="LastCheckedAtUtc"/> on purpose: a daily re-read that finds the
    /// same price moves the check instant and leaves this one alone. A single "last updated" column
    /// would answer neither "how old is this price?" nor "when did this supplier last move it?".</para>
    /// </summary>
    public DateTime? LastPriceChangeAtUtc { get; set; }

    /// <summary>
    /// When a <strong>complete</strong> search last failed to find this listing at all, or
    /// <see langword="null"/> while none ever has (issue #937).
    ///
    /// <para><strong>Only a complete search may write it.</strong> A search that was truncated, that
    /// failed, or that was never run stopped before it had seen everything, so a listing missing
    /// from its results is a listing nobody looked for -
    /// <c>PriceCollectorWorkItemPolicy.RecordsListingsNotFound</c> is that rule, and it is the same
    /// restraint the report availability model shows about a missing provider figure: an absence is
    /// evidence only when the thing that produced it was complete.</para>
    ///
    /// <para>It changes nothing by itself. The listing keeps its latest accepted observation, its
    /// last successful check and its match status exactly as they were - this column records that a
    /// search looked and did not see it, which is what #928 and #930 read.</para>
    /// </summary>
    public DateTime? LastNotFoundAtUtc { get; set; }

    /// <summary>When the listing was first recorded, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When it was last changed; equal to <see cref="CreatedAtUtc"/> until the first edit.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
