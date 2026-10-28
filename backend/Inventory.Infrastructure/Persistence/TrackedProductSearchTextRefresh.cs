using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// What an edit of a product's name or unit label does to the price-tracking mapping that product
/// already has (issue #938, third criterion).
///
/// <para><strong>It runs inside the product update's own transaction, which is the whole reason it
/// lives here rather than in a use case.</strong> The rule compares the mapping's stored text with
/// the text the edit is storing, so the read, the comparison and the write have to be one operation
/// with the product's own write - the same reason the provider-managed, category-ownership and
/// unit-label refusals of <see cref="EfProductStore"/> are decided there (issues #736 and #895). A
/// use-case-level read followed by a separate write would be a window in which the product is renamed
/// again and the mapping ends up searching for a name nothing has.</para>
///
/// <para><strong>The rule itself is the Domain's and is not restated here.</strong>
/// <c>ProductPriceTrackingPolicy.RefreshFor</c> decides whether anything changes at all, what text to
/// store and which status the mapping ends in; this type only applies that answer and then asks
/// <c>RequireConsistentMatch</c> to check the row it produced, so a mapping returned to awaiting a
/// match cannot be left still pointing at a master product.</para>
///
/// <para><strong>It refuses nothing, and it never leaves a stale match standing either</strong>
/// (repaired on review of issue #938). An edit that makes the product unsearchable - a unit label
/// replaced by the generic <c>"unit"</c>, or a name the search text cannot hold - suspends the
/// mapping into <c>ProductPriceTrackingStatus.AwaitingProductDetails</c> and clears the master
/// product and the match source with it, so that master product's prices can no longer be read as
/// this product's and the previous size's text is no longer sent to the collector. The product edit
/// itself still succeeds: what a product may be saved with is issue #895's rule, not this one's.</para>
///
/// <para><strong>It never creates or removes a mapping</strong>, and it never touches a master
/// product, a supplier listing, a match suggestion or another business's mapping: turning tracking on
/// and off is the switch's own write, and a product edit is not a place a business acquires or loses
/// a tracked product. A product that is not tracked is left alone entirely.</para>
/// </summary>
internal static class TrackedProductSearchTextRefresh
{
    /// <summary>
    /// Applies the refresh to the caller's own mapping of <paramref name="productId"/>, if it has one
    /// and if anything changed. The caller's <c>SaveChanges</c> is what commits it, so the mapping and
    /// the product it describes are written together or not at all.
    /// </summary>
    /// <param name="db">The request's own context, already inside the update's transaction.</param>
    /// <param name="productId">The product being edited.</param>
    /// <param name="name">The product's name as the edit is storing it.</param>
    /// <param name="unit">The product's unit label as the edit is storing it.</param>
    /// <param name="updatedAtUtc">The instant to record on a mapping that changed.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async Task ApplyAsync(
        AppDbContext db,
        long productId,
        string name,
        string? unit,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        // Through the filtered DbSet, so this reaches the caller's own mapping and no other - the
        // central boundary, with no local business filter beside it.
        var mapping = await db.ProductPriceTrackings
            .SingleOrDefaultAsync(tracking => tracking.ProductId == productId, cancellationToken)
            .ConfigureAwait(false);

        if (mapping is null)
        {
            return;
        }

        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            mapping.Status,
            mapping.SearchName,
            mapping.UnitLabel,
            name,
            unit);

        if (refresh is null)
        {
            return;
        }

        mapping.Status = refresh.Status;

        // A mapping that stops being matched - returned to awaiting a match by a new size, or
        // suspended because the product can no longer be searched for at all - stops naming a master
        // product, and stops recording how a match that no longer stands was made. Which of the two
        // matched states it came from does not matter: both are a statement that this product is that
        // master product, and a changed or missing size is exactly the reason to stop making it.
        if (!ProductPriceTrackingPolicy.IsMatched(refresh.Status))
        {
            mapping.MasterProductId = null;
            mapping.MatchSource = null;
        }

        // Text only when the rule captured some. A suspended mapping keeps the text it holds, which
        // nothing reads while it is suspended: the collector work list is the Requested rows, so the
        // previous size is not searched for, and a corrected product captures its current name and
        // label afresh.
        if (refresh.Text is { } text)
        {
            mapping.SearchName = text.SearchName;
            mapping.UnitLabel = text.UnitLabel;
            mapping.WaitingKey = text.WaitingKey;
        }

        mapping.UpdatedAtUtc = updatedAtUtc;

        // The row this just produced, checked by the same rule that governs every other writer of
        // this table: a mapping names a master product in exactly the two matched states, and records
        // how that was decided exactly when it names one.
        ProductPriceTrackingPolicy.RequireConsistentMatch(
            mapping.Status,
            mapping.MasterProductId,
            mapping.MatchSource);
    }
}
