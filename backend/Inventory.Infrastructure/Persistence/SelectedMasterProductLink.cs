using Inventory.Application.Products;
using Inventory.Domain.PriceCatalogue;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The link a product created from a master product suggestion gets (issue #1051, parent #817): one
/// <c>Matched</c> price-tracking mapping naming that master product, written in the same transaction
/// as the product itself.
///
/// <para><strong>It runs inside <see cref="EfProductStore.CreateAsync"/>'s own transaction, which is
/// why it lives here rather than in a use case</strong> - the same reason
/// <see cref="TrackedProductSearchTextRefresh"/> does. Whether that master product exists, what its
/// brand, name and size now are, and whether a Super Admin has ruled the pair out are statements
/// about <em>stored</em> rows, so the reads, the comparison and both writes are one operation. The
/// create holds SQLite's write lock from the moment its transaction opens
/// (<see cref="SqliteWriteLockTransaction"/>), so a master product edited or deleted after the
/// operator's lookup either lands before that lock and is seen, or waits behind it: there is no
/// window in which this links to a description nobody has.</para>
///
/// <para><strong>Resolving and writing are two steps on purpose.</strong>
/// <see cref="ResolveAsync"/> is asked <em>before</em> anything is inserted, so a refusal leaves the
/// transaction with nothing to roll back and "nothing is created" is true by construction rather than
/// by a rollback behaving. <see cref="Add"/> then stages the mapping once the product has a key, and
/// the caller's commit is what makes the product and its link one write.</para>
///
/// <para><strong>Every rule is the Domain's.</strong>
/// <see cref="MasterProductPrefill"/> recomputes the Name and unit label,
/// <see cref="MasterProductSelectionPolicy"/> decides whether the submitted values are still those
/// and carries the three refusals' codes and sentences,
/// <see cref="ProductMatchSuggestionPolicy.BlocksAutomaticMatching"/> decides which refused pairs
/// stand, and <see cref="ProductPriceTrackingPolicy"/> captures the search text and checks the row
/// this produced. This type reads, writes and sequences; it does not judge.</para>
///
/// <para><strong>What it writes is one row of one tenant-owned table.</strong> No master product,
/// match suggestion, supplier listing or price observation is created, changed or deleted - the three
/// platform tables are read only - and the mapping's <c>BusinessId</c> is left unset for
/// <c>BusinessOwnershipEnforcer</c> to stamp from the resolved scope, so no business is a parameter
/// here and another business's row is unreachable.</para>
/// </summary>
internal static class SelectedMasterProductLink
{
    /// <summary>
    /// Decides the link before anything is written: either the mapping's text and the master product
    /// to name, or the conflict to answer the whole create with.
    /// </summary>
    /// <param name="db">The request's own context, already inside the create's transaction.</param>
    /// <param name="masterProductId">The master product the operator picked.</param>
    /// <param name="name">The product Name the create is about to store.</param>
    /// <param name="unit">The unit label it is about to store.</param>
    /// <param name="cancellationToken">The request's cancellation token.</param>
    public static async Task<ResolvedLink> ResolveAsync(
        AppDbContext db,
        long masterProductId,
        string name,
        string? unit,
        CancellationToken cancellationToken)
    {
        // A platform table, read by key: a master product is a fact about a product and about no
        // business, so there is no scope to apply and nothing of anybody's to leak. Three columns and
        // the key, which is all the prefill needs.
        var master = await db.MasterProducts
            .AsNoTracking()
            .Where(candidate => candidate.Id == masterProductId)
            .Select(candidate => new
            {
                candidate.Brand,
                candidate.Name,
                candidate.SizeValue,
                candidate.SizeUnit,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (master is null)
        {
            // Deleted since the lookup, or never there: an id a request supplied is not evidence of
            // anything, and "no such master product" is the same answer for both.
            return ResolvedLink.Conflict(
                MasterProductSelectionPolicy.UnavailableCode,
                MasterProductSelectionPolicy.UnavailableMessage);
        }

        var prefill = MasterProductPrefill.For(
            master.Brand,
            master.Name,
            master.SizeValue,
            master.SizeUnit);

        if (!MasterProductSelectionPolicy.IsUnchangedPrefill(prefill, name, unit))
        {
            // The master product was corrected after the operator looked it up, or the request was
            // tampered with. The server cannot tell those apart and the operator does the same thing
            // about either: choose again, or create the product without the link.
            return ResolvedLink.Conflict(
                MasterProductSelectionPolicy.ChangedCode,
                MasterProductSelectionPolicy.ChangedMessage);
        }

        // The text the #938 switch itself would capture, from the values the product is being created
        // with - so the stored key is the fold of the stored text by construction, and the waiting key
        // the refusals below are looked up under is the one this mapping will carry.
        var captured = ProductPriceTrackingPolicy.Capture(name, unit);

        // Every suggestion of this pair, then the Domain's own predicate over their statuses: a
        // rejection or an undone application is kept for ever precisely so no later writer repeats
        // the pair, and a business picking the product by hand is not an exception to that.
        var statuses = await db.ProductMatchSuggestions
            .AsNoTracking()
            .Where(suggestion => suggestion.WaitingKey == captured.WaitingKey
                && suggestion.MasterProductId == masterProductId)
            .Select(suggestion => suggestion.Status)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (statuses.Any(ProductMatchSuggestionPolicy.BlocksAutomaticMatching))
        {
            return ResolvedLink.Conflict(
                MasterProductSelectionPolicy.RefusedCode,
                MasterProductSelectionPolicy.RefusedMessage);
        }

        return ResolvedLink.Linked(masterProductId, captured);
    }

    /// <summary>
    /// Stages the mapping for the product that has just been inserted. The caller's
    /// <c>SaveChanges</c> and commit are what store it, so the product and its link are written
    /// together or not at all.
    /// </summary>
    /// <param name="db">The request's own context, inside the create's transaction.</param>
    /// <param name="productId">The product that was just created.</param>
    /// <param name="link">The resolved link, from <see cref="ResolveAsync"/>.</param>
    /// <param name="atUtc">The instant to record as both created and updated.</param>
    public static void Add(AppDbContext db, long productId, ResolvedLink link, DateTime atUtc)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(link);

        var text = link.Text!;

        var mapping = new Models.ProductPriceTracking
        {
            // BusinessId is deliberately absent: BusinessOwnershipEnforcer stamps it from the
            // resolved scope on SaveChanges, and a denied scope is refused there rather than here.
            ProductId = productId,
            // Settled, because an operator who created this product from the catalogue's own
            // description of it has decided what it is - and a Super Admin may still undo it, like
            // any other settled match. The matcher (#941) therefore leaves it alone: it only ever
            // moves mappings that are still awaiting a match.
            Status = ProductPriceTrackingStatus.Matched,
            MasterProductId = link.MasterProductId,
            MatchSource = ProductPriceTrackingMatchSource.BusinessSelection,
            WaitingKey = text.WaitingKey,
            SearchName = text.SearchName,
            UnitLabel = text.UnitLabel,
            CreatedAtUtc = atUtc,
            UpdatedAtUtc = atUtc,
        };

        // The row this just produced, checked by the same rule that governs every other writer of
        // this table: a mapping names a master product in exactly the two matched states, and records
        // how that was decided exactly when it names one.
        ProductPriceTrackingPolicy.RequireConsistentMatch(
            mapping.Status,
            mapping.MasterProductId,
            mapping.MatchSource);

        db.ProductPriceTrackings.Add(mapping);
    }

    /// <summary>
    /// What <see cref="ResolveAsync"/> decided: the link to write, or the conflict the whole create
    /// is answered with.
    ///
    /// <para>Exactly one of the two, which is what keeps a caller from writing a mapping it was
    /// refused: a conflict carries no text and no master product to write.</para>
    /// </summary>
    /// <param name="MasterProductId">The master product to name, when there is a link.</param>
    /// <param name="Text">The search text and waiting key to store, when there is a link.</param>
    /// <param name="Refusal">The conflict to answer with, when there is no link.</param>
    public sealed record ResolvedLink(
        long? MasterProductId,
        ProductPriceTrackingText? Text,
        ProductWriteAttempt? Refusal)
    {
        public static ResolvedLink Linked(long masterProductId, ProductPriceTrackingText text) =>
            new(masterProductId, text, null);

        public static ResolvedLink Conflict(string code, string message) =>
            new(null, null, ProductWriteAttempt.MasterProductConflict(code, message));
    }
}
