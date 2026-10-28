using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// One business's price-tracking mapping as the Application layer sees it (issue #923, parent #817).
///
/// <para>It carries no business id, and that is deliberate rather than incidental: the owner is
/// resolved from the authenticated actor's membership and stamped centrally, never read from or
/// published to a caller (AGENTS.md § Tenant ownership and data isolation). A record that carried one
/// would be a business key travelling through the Application layer for no reader to use.</para>
/// </summary>
/// <param name="Id">The mapping's own key.</param>
/// <param name="ProductId">The business's own product the mapping is about.</param>
/// <param name="MasterProductId">
/// The shared master product it has been matched to, or <see langword="null"/> while it is waiting.
/// </param>
/// <param name="Status">Where the mapping has got to.</param>
/// <param name="MatchSource">How the match was made, or <see langword="null"/> while there is none.</param>
/// <param name="WaitingKey">
/// The key this product is searched for and grouped by - <c>PriceCatalogueWaitingKey.For</c> of the
/// two values below.
/// </param>
/// <param name="SearchName">The product's own name, as it was when tracking was turned on.</param>
/// <param name="UnitLabel">
/// The product's own unit label, as it was then. Never blank since issue #938, whose switch refuses a
/// product that has no real one.
/// </param>
/// <param name="CreatedAtUtc">When tracking was turned on.</param>
/// <param name="UpdatedAtUtc">When the mapping last changed.</param>
public sealed record ProductPriceTrackingRecord(
    long Id,
    long ProductId,
    long? MasterProductId,
    ProductPriceTrackingStatus Status,
    ProductPriceTrackingMatchSource? MatchSource,
    string WaitingKey,
    string SearchName,
    string UnitLabel,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

/// <summary>
/// The matched master product's description, as a business endpoint is allowed to publish it
/// (issue #938): its name and its size, and nothing else at all.
///
/// <para><strong>This is a member-facing read of a platform table, and it is the third one in the
/// application.</strong> The limit is the review itself - issue #938's own known constraint says
/// "only the matched master product's name and size are returned" - so the brand, the row key, the
/// normalised key, the instants and every supplier listing beside it stay unreachable from a business
/// endpoint. A wider projection is a tenancy decision needing its own reviewed issue. See AGENTS.md
/// § Tenant ownership and data isolation and docs/architecture.md § Tenant ownership.</para>
///
/// <para>It says nothing whatever about any other business, because a master product is about no
/// business: it is a brand's product, looked up once for everybody, so there is no business identity
/// on it to leak and no way to learn who else is tracking it.</para>
/// </summary>
/// <param name="Name">The master product's name or variant.</param>
/// <param name="SizeValue">The size of one item.</param>
/// <param name="SizeUnit">What that size is measured in.</param>
public sealed record MatchedMasterProduct(
    string Name,
    decimal SizeValue,
    MasterProductSizeUnit SizeUnit);

/// <summary>
/// One of the caller's products and the mapping it has, exactly as the persistence adapter can read
/// them in one statement (issue #938): the product's own current name and unit label, and the mapping
/// beside them when there is one.
///
/// <para>It is the adapter's answer rather than the endpoint's: the product's name and unit label are
/// here so the <em>Application</em> layer can ask <c>ProductPriceTrackingPolicy</c> whether the switch
/// may be turned on, instead of an adapter deciding that or an endpoint reading the product a second
/// time. Neither field leaves the Application layer on this path.</para>
/// </summary>
/// <param name="ProductId">The product, which is always one of the caller's own.</param>
/// <param name="ProductName">Its name as it now stands.</param>
/// <param name="ProductUnit">Its unit label as it now stands, which may be blank or the generic one.</param>
/// <param name="Tracking">Its mapping, or <see langword="null"/> when it is not tracked.</param>
/// <param name="MasterProduct">
/// The matched master product's name and size, or <see langword="null"/> when the mapping is not
/// matched or there is no mapping.
/// </param>
public sealed record ProductPriceTrackingView(
    long ProductId,
    string ProductName,
    string? ProductUnit,
    ProductPriceTrackingRecord? Tracking,
    MatchedMasterProduct? MasterProduct);

/// <summary>How a request to turn tracking on or off for one product ended (issue #938).</summary>
public enum ProductPriceTrackingWriteOutcome
{
    /// <summary>The mapping was created, awaiting a match, with the product's search text captured.</summary>
    Tracked = 1,

    /// <summary>
    /// This product is already tracked, so the state the caller asked for is already the case. It is
    /// a success rather than a conflict because the switch is a statement of intent - "track this" -
    /// and a double click, a second tab or a retried request must not need a different answer.
    /// </summary>
    AlreadyTracked = 2,

    /// <summary>The mapping was removed. Nothing shared was touched.</summary>
    Removed = 3,

    /// <summary>This product was not tracked, so there was nothing to remove. A success, for the same reason.</summary>
    NotTracked = 4,

    /// <summary>
    /// There is no such product in the caller's own business - which is the same answer another
    /// business's product gives, because the central query filters match none of its rows.
    /// </summary>
    ProductNotFound = 5,

    /// <summary>
    /// The product may not be tracked and <c>ProductPriceTrackingPolicy.TrackingRefusal</c> says why -
    /// today a blank or generic unit label, or a name the search text cannot hold. Nothing is written.
    /// </summary>
    Refused = 6,
}

/// <summary>
/// The answer a turn-on or turn-off gives, in the shape <c>ProductWriteAttempt</c> already uses: an
/// outcome the controller maps to a status code, and a refusal's own sentence when there is one.
/// </summary>
public sealed record ProductPriceTrackingWrite(
    ProductPriceTrackingWriteOutcome Outcome,
    string? Refusal)
{
    public static ProductPriceTrackingWrite Tracked() =>
        new(ProductPriceTrackingWriteOutcome.Tracked, null);

    public static ProductPriceTrackingWrite AlreadyTracked() =>
        new(ProductPriceTrackingWriteOutcome.AlreadyTracked, null);

    public static ProductPriceTrackingWrite Removed() =>
        new(ProductPriceTrackingWriteOutcome.Removed, null);

    public static ProductPriceTrackingWrite NotTracked() =>
        new(ProductPriceTrackingWriteOutcome.NotTracked, null);

    public static ProductPriceTrackingWrite ProductNotFound() =>
        new(ProductPriceTrackingWriteOutcome.ProductNotFound, null);

    public static ProductPriceTrackingWrite Refused(string refusal) =>
        new(ProductPriceTrackingWriteOutcome.Refused, refusal);
}

/// <summary>
/// The narrow persistence port over one business's price-tracking mappings (issue #923, parent #817):
/// which of its own products a business wants priced.
///
/// <para><strong>Ordinary tenant-owned data, reached the ordinary way.</strong> No method here takes a
/// business id, a predicate, a column list, a page or a statement: the owner comes from the
/// authenticated actor's membership, the central query filters scope every read, and
/// <c>BusinessOwnershipEnforcer</c> stamps the insert and refuses anything that would cross a
/// boundary. There is no <c>IgnoreQueryFilters</c> in the adapter behind it and no unscoped context
/// anywhere near it, so another business's mapping is unreachable by listing and by id alike.</para>
///
/// <para><strong>Issue #938 gave it the two writes and the status read the switch is made of.</strong>
/// Issue #923 shipped it with add, list and find and said so: turning tracking on is this issue, and
/// turning it off is "whichever task the owner asks for it in", which is this one too. What replaced
/// #923's <c>AddAsync</c> is <see cref="TrackAsync"/>, and the difference is the point of it - the
/// product is read, the switch's eligibility decided and the mapping written as <strong>one</strong>
/// operation, so a caller cannot hold an eligibility answer or a captured name that stopped being
/// true. Deciding which master product a product <em>is</em> still belongs to the matcher (#941) and
/// the Super Admin review (#928), and no method here writes a status, a master product or a match
/// source that a business chose.</para>
/// </summary>
public interface IProductPriceTrackingStore
{
    /// <summary>
    /// Turns tracking on for one of the caller's own products, deciding and writing it as one atomic
    /// operation.
    ///
    /// <para><strong>The authoritative read and the write are one step.</strong> Whether the product
    /// exists in this business, what its name and unit label now are, whether that unit label may be
    /// tracked at all, and whether a mapping already exists are all read with the write lock already
    /// held - so a rename, a unit-label edit or another tab's switch either lands before the lock and
    /// is seen, or waits behind it. That is why this method takes a product id and a clock instant
    /// rather than values a caller captured: the search text stored is the product's own text at the
    /// moment the row is written, never text a stale form carried.</para>
    ///
    /// <para>The business is never a parameter: it is stamped from the resolved scope, so a caller
    /// cannot choose an owner, and a product of another business is simply not there to track.</para>
    /// </summary>
    /// <param name="productId">The product, which must be one of the caller's own.</param>
    /// <param name="requestedAtUtc">The instant to record as both created and updated.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    /// <exception cref="Exception">
    /// The unique index on <c>(BusinessId, ProductId)</c> refused the mapping regardless - the
    /// backstop for a writer this process did not serialise with - which
    /// <see cref="IsDuplicateMapping"/> recognises. Nothing is written, and the caller is answered
    /// <see cref="ProductPriceTrackingWriteOutcome.AlreadyTracked"/> rather than a server error.
    /// </exception>
    Task<ProductPriceTrackingWrite> TrackAsync(
        long productId,
        DateTime requestedAtUtc,
        CancellationToken cancellationToken);

    /// <summary>
    /// Turns tracking off for one of the caller's own products: it removes <em>that business's own
    /// mapping row</em> and nothing else.
    ///
    /// <para>No master product, supplier listing, match suggestion or price observation is touched,
    /// and no other business's mapping is reachable to touch - the shared catalogue is about products
    /// and one business withdrawing its interest says nothing about it. That is the issue's sixth
    /// criterion, and it is true by construction here: this method's only write is a delete of one
    /// row of one tenant-owned table.</para>
    /// </summary>
    Task<ProductPriceTrackingWrite> UntrackAsync(long productId, CancellationToken cancellationToken);

    /// <summary>
    /// One of the caller's products with the mapping it has, or <see langword="null"/> when there is
    /// no such product <em>in this business</em> - which is the same answer another business's product
    /// gives.
    ///
    /// <para>The matched master product's name and size travel with it, and only while the mapping is
    /// matched: that is the reviewed limit of this read (see <see cref="MatchedMasterProduct"/>), and
    /// the join is made here rather than by a second port so that nothing can ask this adapter for a
    /// master product a mapping does not name.</para>
    /// </summary>
    Task<ProductPriceTrackingView?> FindForProductAsync(
        long productId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Every mapping of the caller's business, ordered by the search text. It takes no filter at all:
    /// a caller-supplied predicate over a business's own list is how a list becomes a query engine.
    /// </summary>
    Task<IReadOnlyList<ProductPriceTrackingRecord>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// One mapping of the caller's business by its key, or <see langword="null"/> when there is no
    /// such mapping <em>of that business</em> - which is the same answer another business's mapping
    /// gives, because the central filter matches none of its rows.
    /// </summary>
    Task<ProductPriceTrackingRecord?> FindAsync(long id, CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="exception"/> is the unique index on <c>(BusinessId, ProductId)</c>
    /// refusing a second mapping for one product, and nothing else.
    ///
    /// <para>It is asked of the adapter rather than decided in a use case because recognising a
    /// provider's constraint violation is provider knowledge: an EF or SQLite type must not reach
    /// <c>Inventory.Application</c>. Any other database failure is left untranslated rather than
    /// reported to a caller as "already tracked" - the same shape
    /// <c>IAvailableSupplierPresetStore.IsDuplicateCopy</c> has.</para>
    /// </summary>
    bool IsDuplicateMapping(Exception exception);
}
