using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Providers;

namespace Inventory.Application.CatalogueReview;

/// <summary>
/// Starts a provider catalogue review (issue #738, parent #329 decision C1): read the provider's
/// catalogue, reconcile it against the business's own rows through the provider id columns alone, and
/// store the result as the business's pending review.
///
/// <para><strong>It is a preview of a catalogue import, not an import.</strong> No product, category,
/// price, name or sale is written. The one thing it writes is the review itself, because the apply is
/// authorised by a stored review rather than by a payload - every provider name, price and group id
/// the apply writes comes from these rows.</para>
///
/// <para><strong>Nothing is linked by name.</strong> A provider entry resolves to a local row only
/// through that row's <c>ExternalProductId</c>/<c>ExternalCategoryId</c>. Two rows that happen to
/// share a name are two rows, and a local key that happens to equal a provider id is a coincidence
/// (AGENTS.md § Database and migrations, issue #735). So an entry no row carries the id of is
/// reported as unlinked, with the operator's two honest options - link to a row they name, or create
/// a new one - and the review picks neither.</para>
///
/// <para><strong>The provider reads come first, and the transaction after them.</strong> Same shape
/// as the direct import: a failed provider read propagates and nothing is stored, so a partial remote
/// catalogue is never recorded as a review of a complete one, and no provider request is ever made
/// while a database transaction is held open.</para>
/// </summary>
public sealed class StartCatalogueReview
{
    private readonly INayaxLynxClient _nayax;
    private readonly ICatalogueReviewStore _store;
    private readonly IClock _clock;

    public StartCatalogueReview(INayaxLynxClient nayax, ICatalogueReviewStore store, IClock clock)
    {
        _nayax = nayax;
        _store = store;
        _clock = clock;
    }

    public async Task<CatalogueReviewDto> Handle(CancellationToken cancellationToken)
    {
        // Two independent remote reads, so they stay a concurrent fan-out, exactly as the direct
        // import runs them (docs/architecture.md § Concurrency inside one request).
        var productsTask = _nayax.GetProductsAsync(cancellationToken);
        var groupsTask = _nayax.GetProductGroupssAsync(cancellationToken);
        await Task.WhenAll(productsTask, groupsTask);
        var remoteProducts = await productsTask;
        var remoteGroups = await groupsTask;

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var local = await _store.ReadLocalCatalogueAsync(cancellationToken);

        var categoryByProviderId = local.Categories
            .Where(category => category.ProviderId is not null)
            .ToDictionary(category => category.ProviderId!, category => category, StringComparer.Ordinal);
        var productByProviderId = local.Products
            .Where(product => product.ProviderId is not null)
            .ToDictionary(product => product.ProviderId!, product => product, StringComparer.Ordinal);
        var categoryById = local.Categories.ToDictionary(category => category.CategoryId);
        var lastDecided = local.LastDecided.ToDictionary(
            decided => (decided.ProductId, decided.Field), decided => decided.ProviderValue);

        var categories = new List<CatalogueReviewCategoryDto>();
        var products = new List<CatalogueReviewProductDto>();

        // Groups first, so a product's group resolves against the same list the apply will link or
        // create.
        var entries = new List<CatalogueReviewEntry>();
        entries.AddRange(BuildCategoryEntries(remoteGroups, categoryByProviderId));
        entries.AddRange(BuildProductEntries(remoteProducts, productByProviderId, local));

        var review = await _store.ReplacePendingReviewAsync(entries, _clock.UtcNow, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var item in review.Items)
        {
            if (item.Entry.Kind == ProviderCatalogueEntryKind.Category)
                categories.Add(ToCategoryDto(item, categoryById));
            else
                products.Add(ToProductDto(item, local, categoryByProviderId, categoryById, lastDecided));
        }

        return new CatalogueReviewDto(
            review.ReviewId,
            review.StartedAtUtc,
            categories,
            products,
            CatalogueReviewPolicy.UnitVolumeUnavailableNote);
    }

    /// <summary>
    /// One entry per remote group, deduplicated by external id. A group with no identifier cannot be
    /// linked and a blank name cannot name a category - the same two guards the direct import
    /// applies.
    /// </summary>
    private static List<CatalogueReviewEntry> BuildCategoryEntries(
        List<NayaxProductGroup> remoteGroups,
        IReadOnlyDictionary<string, LocalCatalogueCategory> categoryByProviderId)
    {
        var entries = new List<CatalogueReviewEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in remoteGroups)
        {
            if (group.ProductGroupID is not int groupId || string.IsNullOrWhiteSpace(group.ProductGroupName))
                continue;

            var externalId = NayaxExternalId.From(groupId);
            if (!seen.Add(externalId))
                continue;

            categoryByProviderId.TryGetValue(externalId, out var linkedCategory);
            entries.Add(new CatalogueReviewEntry(
                ProviderCatalogueEntryKind.Category,
                externalId,
                group.ProductGroupName,
                ProviderGroupExternalId: null,
                ProviderUnitPrice: null,
                ProviderUnitVolume: null,
                LinkedProductId: null,
                LinkedCategoryId: linkedCategory?.CategoryId,
                UnresolvedSaleCount: 0));
        }

        return entries;
    }

    /// <summary>
    /// One entry per remote product, deduplicated by external id. A blank provider name cannot name a
    /// local row, and the provider owns a linked row's name: linking one would replace a real name
    /// with nothing. Such an entry is left out of the review rather than offered as a decision nobody
    /// can take safely.
    /// </summary>
    private static List<CatalogueReviewEntry> BuildProductEntries(
        List<NayaxProduct> remoteProducts,
        IReadOnlyDictionary<string, LocalCatalogueProduct> productByProviderId,
        LocalCatalogueSnapshot local)
    {
        var entries = new List<CatalogueReviewEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var product in remoteProducts)
        {
            if (string.IsNullOrWhiteSpace(product.ProductName))
                continue;

            var externalId = NayaxExternalId.From(product.NayaxProductId);
            if (!seen.Add(externalId))
                continue;

            productByProviderId.TryGetValue(externalId, out var linkedProduct);
            entries.Add(new CatalogueReviewEntry(
                ProviderCatalogueEntryKind.Product,
                externalId,
                product.ProductName,
                product.ProductGroupId is long groupId ? NayaxExternalId.From(groupId) : null,
                ProviderCatalogueFieldValue.FromPrice(product.ProductDefaultRetailPrice),
                // No provider value, and deliberately so: the authoritative Lynx contract publishes
                // no unit or volume label - see CatalogueReviewPolicy.UnitVolumeUnavailableNote.
                ProviderUnitVolume: null,
                LinkedProductId: linkedProduct?.ProductId,
                LinkedCategoryId: null,
                UnresolvedSaleCount: local.UnresolvedSaleCountByProviderProductId.TryGetValue(
                    externalId, out var unresolved)
                    ? unresolved
                    : 0));
        }

        return entries;
    }

    /// <summary>
    /// A group entry. Its one provider-owned field is the name: an already-linked category whose
    /// local name differs is renamed by the apply and the change is listed, never offered.
    /// </summary>
    private static CatalogueReviewCategoryDto ToCategoryDto(
        PersistedCatalogueReviewItem item,
        IReadOnlyDictionary<long, LocalCatalogueCategory> categoryById)
    {
        var linked = item.Entry.LinkedCategoryId is { } categoryId
            && categoryById.TryGetValue(categoryId, out var category)
                ? category
                : null;

        var automatic = new List<CatalogueReviewAutomaticChangeDto>();
        if (linked is not null && CatalogueReviewPolicy.IsAutomaticChange(linked.Name, item.Entry.ProviderName))
            automatic.Add(new CatalogueReviewAutomaticChangeDto(
                ProviderManagedCatalogueFields.Name, linked.Name, item.Entry.ProviderName));

        return new CatalogueReviewCategoryDto(
            item.ItemId,
            item.Entry.ExternalId,
            item.Entry.ProviderName,
            item.Entry.LinkedCategoryId,
            linked?.Name,
            automatic);
    }

    /// <summary>
    /// A product entry. Its provider-owned fields - the name and the category - are listed as
    /// automatic changes for a linked product; its negotiable ones are offered only while the
    /// provider value differs from both the local value and the value this field was last decided
    /// about.
    /// </summary>
    private static CatalogueReviewProductDto ToProductDto(
        PersistedCatalogueReviewItem item,
        LocalCatalogueSnapshot local,
        IReadOnlyDictionary<string, LocalCatalogueCategory> categoryByProviderId,
        IReadOnlyDictionary<long, LocalCatalogueCategory> categoryById,
        IReadOnlyDictionary<(long ProductId, NegotiableCatalogueField Field), string> lastDecided)
    {
        var linked = item.Entry.LinkedProductId is { } productId
            ? local.Products.FirstOrDefault(product => product.ProductId == productId)
            : null;

        var automatic = new List<CatalogueReviewAutomaticChangeDto>();
        var choices = new List<CatalogueReviewFieldDto>();

        if (linked is not null)
        {
            if (CatalogueReviewPolicy.IsAutomaticChange(linked.Name, item.Entry.ProviderName))
                automatic.Add(new CatalogueReviewAutomaticChangeDto(
                    ProviderManagedCatalogueFields.Name, linked.Name, item.Entry.ProviderName));

            var providerCategory = item.Entry.ProviderGroupExternalId is { } groupProviderId
                && categoryByProviderId.TryGetValue(groupProviderId, out var resolved)
                    ? resolved
                    : null;
            var localCategoryName = linked.CategoryId is { } linkedCategoryId
                && categoryById.TryGetValue(linkedCategoryId, out var currentCategory)
                    ? currentCategory.Name
                    : null;

            if (CatalogueReviewPolicy.IsAutomaticChange(localCategoryName, providerCategory?.Name))
                automatic.Add(new CatalogueReviewAutomaticChangeDto(
                    ProviderManagedCatalogueFields.CategoryId, localCategoryName, providerCategory?.Name));

            AddChoice(choices, NegotiableCatalogueField.UnitPrice, linked, item, lastDecided);
            AddChoice(choices, NegotiableCatalogueField.UnitVolume, linked, item, lastDecided);
        }
        else
        {
            // No local row yet, so there is no local value to keep and no previous decision to
            // remember: the offer is simply the provider's value, and the operator's keep-or-take
            // answer is what the apply writes onto the row they name. A created row has nothing to
            // keep and takes the provider value.
            AddUnlinkedChoice(choices, NegotiableCatalogueField.UnitPrice, item.Entry.ProviderUnitPrice);
            AddUnlinkedChoice(choices, NegotiableCatalogueField.UnitVolume, item.Entry.ProviderUnitVolume);
        }

        return new CatalogueReviewProductDto(
            item.ItemId,
            item.Entry.ExternalId,
            item.Entry.ProviderName,
            item.Entry.ProviderGroupExternalId,
            item.Entry.LinkedProductId,
            linked?.Name,
            item.Entry.UnresolvedSaleCount,
            automatic,
            choices);
    }

    private static void AddChoice(
        List<CatalogueReviewFieldDto> choices,
        NegotiableCatalogueField field,
        LocalCatalogueProduct linked,
        PersistedCatalogueReviewItem item,
        IReadOnlyDictionary<(long ProductId, NegotiableCatalogueField Field), string> lastDecided)
    {
        var localValue = LocalValue(field, linked);
        var providerValue = ProviderValue(field, item.Entry);
        lastDecided.TryGetValue((linked.ProductId, field), out var decided);

        if (CatalogueReviewPolicy.IsProposed(localValue, providerValue, decided))
            choices.Add(new CatalogueReviewFieldDto(field.ToString(), localValue, providerValue));
    }

    private static void AddUnlinkedChoice(
        List<CatalogueReviewFieldDto> choices, NegotiableCatalogueField field, string? providerValue)
    {
        if (providerValue is not null)
            choices.Add(new CatalogueReviewFieldDto(field.ToString(), null, providerValue));
    }

    private static string? LocalValue(NegotiableCatalogueField field, LocalCatalogueProduct product) =>
        field == NegotiableCatalogueField.UnitPrice
            ? ProviderCatalogueFieldValue.FromPrice(product.UnitPrice)
            : ProviderCatalogueFieldValue.FromLabel(product.Unit);

    private static string? ProviderValue(NegotiableCatalogueField field, CatalogueReviewEntry entry) =>
        field == NegotiableCatalogueField.UnitPrice ? entry.ProviderUnitPrice : entry.ProviderUnitVolume;
}
