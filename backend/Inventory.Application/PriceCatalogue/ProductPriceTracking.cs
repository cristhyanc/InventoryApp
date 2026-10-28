using Inventory.Application.Time;
using Inventory.Domain.PriceCatalogue;

namespace Inventory.Application.PriceCatalogue;

/// <summary>
/// Where one of the business's products has got to in the shared supplier price catalogue, as the
/// product form's "Track supplier prices" switch needs it (issue #938, parent #817).
///
/// <para><strong>It publishes the eligibility answer, not just the state.</strong>
/// <see cref="CanTrack"/> and <see cref="TrackRefusal"/> are
/// <c>ProductPriceTrackingPolicy.TrackingRefusal</c>'s own answer about the stored product - the same
/// rule the write applies - so the switch is offered, and the unit-label message worded, by the
/// server that decides the write rather than by a second rule in a component. It is the shape the
/// product's own <c>canDelete</c>/<c>deletionRefusal</c> already has (issue #737).</para>
///
/// <para><strong>What it says about the shared catalogue is a name and a size.</strong>
/// <see cref="MasterProduct"/> is present only while the mapping is matched, and carries only that
/// master product's name and size - the reviewed limit of issue #938's platform-table read. Nothing
/// here says which other businesses track the product, or how many; there is nowhere for such a
/// value to travel, because a master product is about no business at all.</para>
/// </summary>
/// <param name="ProductId">The product this answer is about - always one of the caller's own.</param>
/// <param name="Tracked">Whether the business has asked for this product's supplier prices.</param>
/// <param name="Status">Where the mapping has got to, or <see langword="null"/> when it is not tracked.</param>
/// <param name="SearchName">The search name the mapping holds, or <see langword="null"/> when untracked.</param>
/// <param name="UnitLabel">The unit label it holds, or <see langword="null"/> when untracked.</param>
/// <param name="MasterProduct">
/// The matched master product's name and size, or <see langword="null"/> when the mapping is not
/// matched.
/// </param>
/// <param name="CanTrack">Whether tracking may be turned on for this product as it now stands.</param>
/// <param name="TrackRefusal">
/// Why it may not be, in the rule's own words, or <see langword="null"/> when it may.
/// </param>
public sealed record ProductPriceTrackingState(
    long ProductId,
    bool Tracked,
    ProductPriceTrackingStatus? Status,
    string? SearchName,
    string? UnitLabel,
    MatchedMasterProduct? MasterProduct,
    bool CanTrack,
    string? TrackRefusal);

/// <summary>
/// One tracked product, for the catalogue listing's "Tracked" column (issue #938, fifth criterion).
///
/// <para>The product id and the status, and deliberately nothing else: the column says which of the
/// business's products are tracked and where each one has got to, and a page that needed the search
/// text or a matched description reads the product's own resource for it.</para>
/// </summary>
/// <param name="ProductId">The business's own product.</param>
/// <param name="Status">Where its mapping has got to.</param>
public sealed record TrackedProduct(long ProductId, ProductPriceTrackingStatus Status);

/// <summary>
/// Reads where one of the caller's products has got to in the shared catalogue (issue #938).
///
/// <para>Which business is never asked: the mapping and the product are both read through the central
/// tenant query filters, so a product that is not this business's is <see langword="null"/> - the same
/// answer "no such product" gives, which is the point.</para>
/// </summary>
public sealed class GetProductPriceTracking
{
    private readonly IProductPriceTrackingStore _store;

    public GetProductPriceTracking(IProductPriceTrackingStore store)
    {
        _store = store;
    }

    /// <returns>
    /// The product's tracking state, or <see langword="null"/> when there is no such product in the
    /// caller's business.
    /// </returns>
    public async Task<ProductPriceTrackingState?> Handle(
        long productId,
        CancellationToken cancellationToken)
    {
        var view = await _store.FindForProductAsync(productId, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        // The eligibility rule is asked of the product as it now stands, not of the mapping: a
        // product that is already tracked is reported with the same answer, so a switch that is on
        // and a product whose label was since made generic are both readable states rather than one
        // being hidden behind the other.
        var refusal = ProductPriceTrackingPolicy.TrackingRefusal(view.ProductName, view.ProductUnit);

        return new ProductPriceTrackingState(
            view.ProductId,
            view.Tracking is not null,
            view.Tracking?.Status,
            view.Tracking?.SearchName,
            view.Tracking?.UnitLabel,
            view.MasterProduct,
            refusal is null,
            refusal);
    }
}

/// <summary>
/// Turns tracking on or off for one of the caller's own products (issue #938): the whole of what the
/// "Track supplier prices" switch does.
///
/// <para><strong>Nothing a caller submits is written.</strong> The request carries a product id and a
/// yes/no, and that is all: the search text is the product's own name and unit label read by the
/// adapter, the status of a new mapping is always
/// <see cref="ProductPriceTrackingStatus.Requested"/>, and no master product or match source is
/// reachable from here at all - deciding what a product <em>is</em> belongs to the matcher (#941) and
/// the Super Admin review (#928). A business asking for a price is not a business asserting what its
/// product is.</para>
///
/// <para><strong>Eligibility is not decided here, and that is deliberate.</strong> Whether the
/// product exists, what its unit label now is and whether a mapping already exists are decided by
/// <c>TrackAsync</c> itself, with the write lock held, in the same operation as the insert - exactly
/// as <c>AddSupplierFromPreset</c> leaves those answers to <c>CopyAsync</c> (issue #935). An answer
/// this use case read first would be a stale read by the time the row was written, and the search
/// text captured from it would be the name the product used to have.</para>
///
/// <para><strong>It is an ordinary tenant-owned write.</strong> It runs in the caller's resolved
/// business scope under the Products controller's existing <c>Inventory.Operate</c> capability - the
/// existing product-edit capability the issue names - through the central ownership stamp. A
/// read-only support session and a Super Admin's role preview refuse it by their own method rules,
/// like every other non-<c>GET</c> request, so nothing about either is written here.</para>
/// </summary>
public sealed class SetProductPriceTracking
{
    private readonly IProductPriceTrackingStore _store;
    private readonly IClock _clock;

    public SetProductPriceTracking(IProductPriceTrackingStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <param name="productId">The product, which must be one of the caller's own.</param>
    /// <param name="tracked">The state asked for: on creates the mapping, off removes it.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public async Task<ProductPriceTrackingWrite> Handle(
        long productId,
        bool tracked,
        CancellationToken cancellationToken)
    {
        if (!tracked)
        {
            return await _store.UntrackAsync(productId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _store
                .TrackAsync(productId, _clock.UtcNow, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (_store.IsDuplicateMapping(exception))
        {
            // The unique index refused a mapping the in-lock read did not see. The caller gets the
            // same answer either way, which is what makes it invisible to them rather than a server
            // error - and there is still exactly one mapping.
            return ProductPriceTrackingWrite.AlreadyTracked();
        }
    }
}

/// <summary>
/// The caller's own tracked products, for the catalogue listing's "Tracked" column (issue #938).
///
/// <para>It takes no filter, page or sort: the business's own list of tracked products is short by
/// construction - one row per product it asked about - and a caller-supplied predicate over it is how
/// a list becomes a query engine. Another business's mappings are not in it, because the central
/// query filters match none of their rows.</para>
/// </summary>
public sealed class ListTrackedProducts
{
    private readonly IProductPriceTrackingStore _store;

    public ListTrackedProducts(IProductPriceTrackingStore store)
    {
        _store = store;
    }

    public async Task<IReadOnlyList<TrackedProduct>> Handle(CancellationToken cancellationToken)
    {
        var mappings = await _store.ListAsync(cancellationToken).ConfigureAwait(false);

        return [.. mappings.Select(mapping => new TrackedProduct(mapping.ProductId, mapping.Status))];
    }
}
