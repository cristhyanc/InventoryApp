using Inventory.Application.CatalogueReview;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ICatalogueReviewStore"/> (issue #738, parent #329 decision
/// C1). It lives in Inventory.Infrastructure beside the other catalogue adapters, whose shape it
/// follows; since issue #739 it is the catalogue import's only persistence adapter, because the
/// direct write-through import it was built beside is gone.
///
/// <para><strong>The read-decide-write sequence stays in this adapter, and here that is the point
/// rather than a precedent.</strong> Two of the review's refusals are statements about <em>stored</em>
/// rows - this local row already carries a different provider id, and another local row already
/// carries this one - and the whole purpose of the apply is that they are decided in the same
/// transaction as the write they would permit. A use-case read followed by a separate write is a
/// window in which a product can take the provider id this plan was about to record on a different
/// row, which is exactly the silent identity error the provider-id work exists to end. The rules
/// themselves are not here: <see cref="CatalogueReviewPolicy"/> is, and this adapter supplies it the
/// rows it read.</para>
///
/// <para><strong>Identity comes from the external id columns, never from a key or a name.</strong> A
/// review entry resolves to a local row only through <c>Product.ExternalProductId</c> /
/// <c>Category.ExternalCategoryId</c>; a local key that equals a provider id is a coincidence (issue
/// #735) and two rows that share a name are two rows. Nothing here matches by name, in either
/// direction.</para>
///
/// <para><strong>It writes the business's own provider's ids, and refuses to do anything else</strong>
/// (issue #759). Every method asks first whether <c>Business.SalesProvider</c> is Nayax, because the
/// external id columns hold that provider's ids and nothing in them says who issued them.</para>
///
/// <para><strong>It touches no sale.</strong> Linking a provider product id to a local product makes
/// previously unresolved sales resolvable and resolves none of them; no statement here reads or writes
/// <c>NayaxSales</c> except the unresolved <em>count</em> the review reports.</para>
/// </summary>
public sealed partial class EfCatalogueReviewStore : ICatalogueReviewStore
{
    private readonly AppDbContext _db;

    public EfCatalogueReviewStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<ICatalogueReviewTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        new CatalogueReviewTransaction(_db.Database.IsRelational() && _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(cancellationToken)
            : null);

    /// <inheritdoc />
    public async Task<LocalCatalogueSnapshot> ReadLocalCatalogueAsync(CancellationToken cancellationToken)
    {
        await RequireNayaxSalesProviderAsync(cancellationToken);

        // Untracked: the review writes nothing local. Scoped to the caller's business by the central
        // AppDbContext query filters alone - no business predicate of this adapter's own.
        var products = await _db.Products
            .AsNoTracking()
            .OrderBy(product => product.Id)
            .Select(product => new LocalCatalogueProduct(
                product.Id,
                product.Name,
                product.ExternalProductId,
                product.UnitPrice,
                product.Unit,
                product.CategoryId))
            .ToListAsync(cancellationToken);

        var categories = await _db.Categories
            .AsNoTracking()
            .OrderBy(category => category.Id)
            .Select(category => new LocalCatalogueCategory(
                category.Id, category.Name, category.ExternalCategoryId))
            .ToListAsync(cancellationToken);

        var decided = await _db.ProviderCatalogueFieldDecisions
            .AsNoTracking()
            .Select(decision => new LastDecidedProviderField(
                decision.ProductId, decision.Field, decision.ProviderValue))
            .ToListAsync(cancellationToken);

        return new LocalCatalogueSnapshot(
            products, categories, decided, await UnresolvedSaleCountsAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async Task<PersistedCatalogueReview> ReplacePendingReviewAsync(
        IReadOnlyList<CatalogueReviewEntry> entries,
        DateTime startedAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await RequireNayaxSalesProviderAsync(cancellationToken);

        // The business's unapplied review is replaced, not added to: a review is a reading of the
        // provider's catalogue at an instant, and an older reading is not a second opinion. An
        // applied review is never touched - it is the record of what was decided. The items go with
        // it through the cascade.
        var pending = await _db.ProviderCatalogueReviews
            .Where(review => review.AppliedAtUtc == null)
            .ToListAsync(cancellationToken);
        _db.ProviderCatalogueReviews.RemoveRange(pending);

        var stored = new ProviderCatalogueReview
        {
            Provider = ExternalProvider.Nayax,
            StartedAtUtc = startedAtUtc,
        };

        foreach (var entry in entries)
            stored.Items.Add(new ProviderCatalogueReviewItem
            {
                Kind = entry.Kind,
                ExternalId = entry.ExternalId,
                ProviderName = entry.ProviderName,
                ProviderGroupExternalId = entry.ProviderGroupExternalId,
                ProviderUnitPrice = entry.ProviderUnitPrice,
                ProviderUnitVolume = entry.ProviderUnitVolume,
                LinkedProductId = entry.LinkedProductId,
                LinkedCategoryId = entry.LinkedCategoryId,
                UnresolvedSaleCount = entry.UnresolvedSaleCount,
            });

        _db.ProviderCatalogueReviews.Add(stored);
        await _db.SaveChangesAsync(cancellationToken);

        return Project(stored);
    }

    /// <inheritdoc />
    public async Task<PersistedCatalogueReview?> FindReviewAsync(
        long reviewId, CancellationToken cancellationToken)
    {
        await RequireNayaxSalesProviderAsync(cancellationToken);

        var stored = await _db.ProviderCatalogueReviews
            .AsNoTracking()
            .Include(review => review.Items)
            .FirstOrDefaultAsync(review => review.Id == reviewId, cancellationToken);

        return stored is null ? null : Project(stored);
    }

    /// <inheritdoc />
    public async Task<CatalogueReviewApplied> ApplyAsync(
        CatalogueReviewApplyPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        await RequireNayaxSalesProviderAsync(cancellationToken);

        // Re-read inside the caller's transaction, tracked: the apply is decided on the rows as they
        // are now, never on the ones the review happened to see.
        var review = await _db.ProviderCatalogueReviews
            .Include(stored => stored.Items)
            .FirstOrDefaultAsync(stored => stored.Id == plan.Review.ReviewId, cancellationToken)
            ?? throw new DomainValidationException(ApplyCatalogueReview.UnknownReviewMessage);

        if (review.AppliedAtUtc is not null)
            throw new DomainValidationException(
                "This catalogue review has already been applied, so nothing was changed. Start a new "
                    + "review to decide about the provider's catalogue again.");

        var products = await _db.Products.ToListAsync(cancellationToken);
        var categories = await _db.Categories.ToListAsync(cancellationToken);
        var fieldDecisions = await _db.ProviderCatalogueFieldDecisions.ToListAsync(cancellationToken);

        var work = new ApplyWork(plan, review, products, categories, fieldDecisions);

        ApplyCategories(work);
        ApplyProducts(work);

        if (work.Refusals.Count > 0)
            throw new DomainValidationException("Nothing was applied. " + string.Join(" ", work.Refusals));

        // Saved once, so every row this apply created has the key the database assigned before the
        // results and the field-decision rows name it. Nothing is visible outside the caller's
        // transaction until it commits.
        await _db.SaveChangesAsync(cancellationToken);

        RecordFieldDecisions(work);
        review.AppliedAtUtc = plan.AppliedAtUtc;
        review.AppliedBy = plan.AppliedBy;
        await _db.SaveChangesAsync(cancellationToken);

        return work.Result(plan);
    }

    /// <summary>
    /// Phase one: the provider's groups, so a product's group resolves against the categories this
    /// same apply links or creates. A category carrying the group's id already is renamed to the
    /// provider's name - the provider owns it - and the change is listed rather than offered.
    /// </summary>
    private void ApplyCategories(ApplyWork work)
    {
        foreach (var item in work.Items(ProviderCatalogueEntryKind.Category))
        {
            var carrier = work.CategoryCarrying(item.ExternalId);
            var decision = work.Decision(item.Id);
            var action = decision?.Action ?? CatalogueReviewAction.Defer;

            if (carrier is not null)
            {
                if (action != CatalogueReviewAction.Defer)
                {
                    work.Refuse(item, AlreadyLinkedRefusal);
                    continue;
                }

                work.RenameCategory(carrier, item);
                continue;
            }

            if (action == CatalogueReviewAction.Defer)
                continue;

            if (action == CatalogueReviewAction.CreateNew)
            {
                var created = new Category
                {
                    ExternalCategoryId = item.ExternalId,
                    Name = item.ProviderName,
                };
                _db.Categories.Add(created);
                work.Created(created, item);
                continue;
            }

            var chosen = work.LocalCategory(decision!.LocalRowId!.Value);
            if (chosen is null)
            {
                work.Refuse(item, UnknownRowRefusal(ProviderCatalogueEntryKind.Category));
                continue;
            }

            if (CatalogueReviewPolicy.LinkRefusal(
                    ProviderCatalogueEntryKind.Category,
                    item.ExternalId,
                    chosen.ExternalCategoryId,
                    anotherRowCarriesIt: false) is { } refusal)
            {
                work.Refuse(item, refusal);
                continue;
            }

            chosen.ExternalCategoryId = item.ExternalId;
            work.Linked(chosen, item);
            work.RenameCategory(chosen, item);
        }
    }

    /// <summary>
    /// Phase two: the provider's products. A product the provider names takes its name and its
    /// category from the provider, which is applied and listed; its price and unit/volume are
    /// negotiated per field, and both answers are recorded so neither value is proposed again until
    /// the provider changes it.
    /// </summary>
    private void ApplyProducts(ApplyWork work)
    {
        foreach (var item in work.Items(ProviderCatalogueEntryKind.Product))
        {
            var carrier = work.ProductCarrying(item.ExternalId);
            var decision = work.Decision(item.Id);
            var action = decision?.Action ?? CatalogueReviewAction.Defer;

            if (carrier is not null)
            {
                if (action != CatalogueReviewAction.Defer)
                {
                    work.Refuse(item, AlreadyLinkedRefusal);
                    continue;
                }

                work.ApplyProviderOwnedProductFields(carrier, item, created: false);
                work.ApplyNegotiableFields(carrier, item, decision, created: false);
                continue;
            }

            if (action == CatalogueReviewAction.Defer)
                continue;

            var chosen = ResolveOrCreateProduct(work, item, decision!, action);
            if (chosen is null)
                continue;

            var created = action == CatalogueReviewAction.CreateNew;
            work.ApplyProviderOwnedProductFields(chosen, item, created);
            work.ApplyNegotiableFields(chosen, item, decision, created);
        }
    }

    /// <summary>
    /// The non-defer half of a product entry's decision: create the row the database will key, or
    /// link the named existing one after both link refusals. Returns <c>null</c> once it has refused
    /// or recorded the outcome itself, so the caller has nothing left to do for this item.
    /// </summary>
    private Product? ResolveOrCreateProduct(
        ApplyWork work, ProviderCatalogueReviewItem item, CatalogueReviewApplyDecision decision,
        CatalogueReviewAction action)
    {
        if (action == CatalogueReviewAction.CreateNew)
        {
            var created = new Product
            {
                ExternalProductId = item.ExternalId,
                RestockTo = 0,
                CreatedAt = work.AppliedAtUtc,
            };
            _db.Products.Add(created);
            work.Created(created, item);
            return created;
        }

        var existing = work.LocalProduct(decision.LocalRowId!.Value);
        if (existing is null)
        {
            work.Refuse(item, UnknownRowRefusal(ProviderCatalogueEntryKind.Product));
            return null;
        }

        if (CatalogueReviewPolicy.LinkRefusal(
                ProviderCatalogueEntryKind.Product,
                item.ExternalId,
                existing.ExternalProductId,
                anotherRowCarriesIt: false) is { } refusal)
        {
            work.Refuse(item, refusal);
            return null;
        }

        existing.ExternalProductId = item.ExternalId;
        work.Linked(existing, item);
        return existing;
    }

    /// <summary>
    /// Writes what each decided field was decided <em>about</em>, after the save that gave every new
    /// product its key. One row per product and field, replaced rather than appended: the question it
    /// answers - "what did we last ask about?" - is single-valued, and a second row would make the
    /// skip rule depend on which one was read.
    /// </summary>
    private void RecordFieldDecisions(ApplyWork work)
    {
        foreach (var record in work.PendingFieldDecisions)
        {
            var existing = work.FieldDecisions.FirstOrDefault(decision =>
                decision.ProductId == record.Product.Id && decision.Field == record.Field);

            if (existing is null)
            {
                var added = new ProviderCatalogueFieldDecision
                {
                    ProductId = record.Product.Id,
                    Field = record.Field,
                    ProviderValue = record.ProviderValue,
                    Decision = record.Decision,
                    DecidedAtUtc = work.AppliedAtUtc,
                };
                _db.ProviderCatalogueFieldDecisions.Add(added);
                work.FieldDecisions.Add(added);
                continue;
            }

            existing.ProviderValue = record.ProviderValue;
            existing.Decision = record.Decision;
            existing.DecidedAtUtc = work.AppliedAtUtc;
        }
    }

    /// <summary>
    /// The business's stored sales that are still unresolved and carry a provider product id with
    /// known raw provenance, counted per provider id (issue #758).
    ///
    /// <para>A provenance-unknown row is deliberately excluded: its stored number may be either a raw
    /// provider id or the local product id the old synchronization wrote, so it is evidence about
    /// nothing and must not be counted as a sale a link would explain. The number is converted to the
    /// external id text here and is never read as a local product key.</para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, int>> UnresolvedSaleCountsAsync(
        CancellationToken cancellationToken)
    {
        var counted = await _db.NayaxSales
            .AsNoTracking()
            .Where(sale =>
                sale.ProductId == null
                && sale.ProviderProductIdIsRaw
                && sale.Provider == ExternalProvider.Nayax
                && sale.NayaxProductId != null)
            .GroupBy(sale => sale.NayaxProductId!.Value)
            .Select(grouped => new { ProviderId = grouped.Key, Count = grouped.Count() })
            .ToListAsync(cancellationToken);

        return counted.ToDictionary(
            row => NayaxExternalId.From(row.ProviderId), row => row.Count, StringComparer.Ordinal);
    }

    private static PersistedCatalogueReview Project(ProviderCatalogueReview stored) =>
        new(
            stored.Id,
            stored.StartedAtUtc,
            stored.AppliedAtUtc,
            stored.Items
                .OrderBy(item => item.Id)
                .Select(item => new PersistedCatalogueReviewItem(
                    item.Id,
                    new CatalogueReviewEntry(
                        item.Kind,
                        item.ExternalId,
                        item.ProviderName,
                        item.ProviderGroupExternalId,
                        item.ProviderUnitPrice,
                        item.ProviderUnitVolume,
                        item.LinkedProductId,
                        item.LinkedCategoryId,
                        item.UnresolvedSaleCount)))
                .ToList());

    private const string AlreadyLinkedRefusal =
        "a local row already carries this provider id, so it cannot be linked or created again. "
            + "Start a new review to see its current state.";

    private static string UnknownRowRefusal(ProviderCatalogueEntryKind kind) =>
        kind == ProviderCatalogueEntryKind.Category
            ? "the category named is not a category of this business."
            : "the product named is not a product of this business.";

    /// <summary>
    /// Refuses to run at all unless the current business's sales provider is Nayax (issue #759), for
    /// the same reason the catalogue import does: the external id columns this review reads and writes
    /// hold the id issued by <c>Business.SalesProvider</c>, and nothing in them says which system that
    /// was. Storing the Nayax credentials the review's catalogue read authenticates with is what sets
    /// that provider, so in every reachable state the answer is already Nayax; a business where it is
    /// not is a broken invariant rather than a user error.
    /// </summary>
    private async Task RequireNayaxSalesProviderAsync(CancellationToken cancellationToken)
    {
        if (await EfSalesProviderQueries.CurrentBusinessSellsThroughAsync(
                _db, ExternalProvider.Nayax, cancellationToken))
            return;

        throw new InvalidOperationException(
            "The Nayax catalogue cannot be reviewed for a business whose sales provider is not Nayax. "
                + "Store the business's Nayax credentials first, which is what records its sales "
                + "provider. Nothing was changed.");
    }
}
