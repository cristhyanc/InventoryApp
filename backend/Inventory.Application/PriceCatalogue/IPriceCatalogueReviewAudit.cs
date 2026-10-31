namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Which decision one price-catalogue review audit event is about (issue #928).
///
/// <para>Nine values, because each is a different thing a reviewer did and an audit trail that merged
/// any two of them could not answer "who decided this, and what did they decide". Numbered from 1.
/// </para>
/// </summary>
public enum PriceCatalogueReviewAction
{
    /// <summary>A supplier listing was confirmed, at the identity the event records.</summary>
    ListingConfirmed = 1,

    /// <summary>A supplier listing was refused.</summary>
    ListingRejected = 2,

    /// <summary>
    /// A pending price outside the band was accepted because the reviewer explicitly approved the
    /// exception. Emitted <strong>beside</strong> <see cref="ListingConfirmed"/> and never instead of
    /// it, which is the issue's own requirement that the exception is audited separately from the
    /// listing decision: the two are different decisions and one of them overrides a financial
    /// safeguard.
    /// </summary>
    PriceExceptionApproved = 3,

    /// <summary>A waiting key was matched to a master product by the reviewer.</summary>
    ProductMatched = 4,

    /// <summary>
    /// A master product was created. Emitted beside <see cref="ProductMatched"/>, because creating
    /// one and matching a key to it are two facts and this surface is the only writer of that table.
    /// </summary>
    MasterProductCreated = 5,

    /// <summary>A match suggestion was refused, which keeps that pair from ever being auto-applied.</summary>
    MatchSuggestionRejected = 6,

    /// <summary>A waiting key was recorded as matching nothing in the catalogue.</summary>
    WaitingProductNotFound = 7,

    /// <summary>An automatic match was confirmed.</summary>
    AutomaticMatchConfirmed = 8,

    /// <summary>An automatic match was undone.</summary>
    AutomaticMatchUndone = 9,
}

/// <summary>
/// One audit event about one committed decision in the Super Admin's price-catalogue review (issue
/// #928): who decided, what they decided, and when.
///
/// <para>Emitted only after the write is committed, exactly as the platform-admin audits beside it
/// are (<c>SupplierPresetAuditEntry</c>, <c>PlatformBusinessDecisionAuditEntry</c>): a refused
/// decision did not happen.</para>
///
/// <para><strong>What it carries, and why each thing is safe.</strong> The acting administrator's
/// Entra <c>(tid, oid)</c> pair and the row keys of the listing, the master product and the suggestion
/// are identifiers. <see cref="WaitingKey"/> is a <em>folded product description</em> and the only
/// identifier a key-level decision has at all - a "not found" names no listing, no suggestion and no
/// master product - and it carries no business identity by construction, which is the same reason
/// <c>ProductMatchSuggestions</c> is a platform table. <see cref="ConfirmedUnitsPerPack"/> is the one
/// value this review decides that a price is then divided by, so an audit trail that could not say
/// which pack quantity was confirmed would not be an audit trail of this surface.</para>
///
/// <para><strong>What it deliberately does not carry.</strong> No business id, business name or count
/// of businesses; no member's name or address; no supplier name, listing title or URL; and no
/// accepted price. The first group is what the shared catalogue exists to keep apart from anybody
/// else's view of it, and the rest is content rather than identity - the listing's key is what an
/// investigation follows to the row, exactly as the supplier-preset audit records a preset's key and
/// never its name. There is no field for any of them, so a sink that printed the whole entry could not
/// emit one.</para>
/// </summary>
/// <param name="ActorDirectoryTenantId">The acting platform administrator's Entra <c>tid</c>.</param>
/// <param name="ActorObjectId">Their Entra <c>oid</c>.</param>
/// <param name="Action">Which decision it was.</param>
/// <param name="SupplierListingId">The listing decided, where one was.</param>
/// <param name="MasterProductId">The master product involved, where one was.</param>
/// <param name="SuggestionId">The match suggestion row involved, where one was.</param>
/// <param name="WaitingKey">The waiting key decided, where one was.</param>
/// <param name="PriceAccepted">
/// Whether a held reading became the listing's accepted price, for a listing confirmation. False for
/// every other action, and for a confirmation that left the listing with no current price.
/// </param>
/// <param name="ConfirmedUnitsPerPack">
/// The pack quantity as confirmed, for a listing confirmation, or <see langword="null"/> when the
/// reviewer confirmed that it is unknown.
/// </param>
/// <param name="OccurredAtUtc">When the decision was committed, from the application clock.</param>
public sealed record PriceCatalogueReviewAuditEntry(
    string ActorDirectoryTenantId,
    string ActorObjectId,
    PriceCatalogueReviewAction Action,
    long? SupplierListingId,
    long? MasterProductId,
    long? SuggestionId,
    string? WaitingKey,
    bool PriceAccepted,
    int? ConfirmedUnitsPerPack,
    DateTime OccurredAtUtc);

/// <summary>
/// The audit sink for the Super Admin's price-catalogue review decisions, implemented at the
/// InventoryApi boundary over <c>ILogger</c> (issue #928) for the same reasons as the platform-admin
/// audits beside it: the event carries the request's correlation id, which is a transport fact, and an
/// audit row in the same database the decision was made in would be evidence kept inside the thing it
/// is evidence about.
/// </summary>
public interface IPriceCatalogueReviewAudit
{
    void Record(PriceCatalogueReviewAuditEntry entry);
}
