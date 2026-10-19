using Inventory.Application.CatalogueReview;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Persistence;

public sealed partial class EfCatalogueReviewStore
{
    /// <summary>
    /// One apply's working state: the rows it read inside the transaction, the lookups it decides
    /// each entry against, and what it has staged so far (issue #738).
    ///
    /// <para><strong>Why the results are assembled after the save.</strong> A row this apply creates
    /// has no key until the database assigns one, and the result - and the field-decision rows -
    /// name rows by key. So each staged write is remembered as the <em>entity</em> it wrote, and the
    /// keys are read off those entities once, after the single save.</para>
    ///
    /// <para><strong>The carrier lookups are updated as it goes</strong>, so a second entry naming a
    /// provider id this apply has just recorded sees it and is refused rather than recording the same
    /// id twice and failing on the filtered unique index.</para>
    /// </summary>
    private sealed class ApplyWork
    {
        private readonly Dictionary<long, CatalogueReviewApplyDecision> _decisionsByItem;
        private readonly Dictionary<string, Product> _productByProviderId;
        private readonly Dictionary<string, Category> _categoryByProviderId;
        private readonly List<Product> _products;
        private readonly List<Category> _categories;
        private readonly ProviderCatalogueReview _review;

        private readonly List<StagedLink> _links = [];
        private readonly List<StagedField> _automatic = [];
        private readonly List<StagedField> _accepted = [];
        private readonly List<StagedField> _skipped = [];

        public ApplyWork(
            CatalogueReviewApplyPlan plan,
            ProviderCatalogueReview review,
            List<Product> products,
            List<Category> categories,
            List<ProviderCatalogueFieldDecision> fieldDecisions)
        {
            _review = review;
            _products = products;
            _categories = categories;
            AppliedAtUtc = plan.AppliedAtUtc;
            FieldDecisions = fieldDecisions;
            _decisionsByItem = plan.Decisions.ToDictionary(decision => decision.ItemId);
            _productByProviderId = products
                .Where(product => product.ExternalProductId is not null)
                .ToDictionary(product => product.ExternalProductId!, product => product, StringComparer.Ordinal);
            _categoryByProviderId = categories
                .Where(category => category.ExternalCategoryId is not null)
                .ToDictionary(
                    category => category.ExternalCategoryId!, category => category, StringComparer.Ordinal);
        }

        public DateTime AppliedAtUtc { get; }

        public List<ProviderCatalogueFieldDecision> FieldDecisions { get; }

        public List<string> Refusals { get; } = [];

        public List<PendingFieldDecision> PendingFieldDecisions { get; } = [];

        /// <summary>This review's entries of one kind, in the order the database assigned them.</summary>
        public IEnumerable<ProviderCatalogueReviewItem> Items(ProviderCatalogueEntryKind kind) =>
            _review.Items.Where(item => item.Kind == kind).OrderBy(item => item.Id);

        public CatalogueReviewApplyDecision? Decision(long itemId) =>
            _decisionsByItem.GetValueOrDefault(itemId);

        /// <summary>The product carrying that provider id now, including one this apply just linked.</summary>
        public Product? ProductCarrying(string? providerId) =>
            providerId is null ? null : _productByProviderId.GetValueOrDefault(providerId);

        /// <summary>The category carrying that provider id now, including one this apply just created.</summary>
        public Category? CategoryCarrying(string? providerId) =>
            providerId is null ? null : _categoryByProviderId.GetValueOrDefault(providerId);

        public Product? LocalProduct(long localRowId) =>
            _products.FirstOrDefault(product => product.Id == localRowId);

        public Category? LocalCategory(long localRowId) =>
            _categories.FirstOrDefault(category => category.Id == localRowId);

        public void Refuse(ProviderCatalogueReviewItem item, string reason) =>
            Refusals.Add($"Entry {item.Id} (provider {Noun(item.Kind)} {item.ExternalId}): {reason}");

        public void Created(Product product, ProviderCatalogueReviewItem item)
        {
            _products.Add(product);
            _productByProviderId[item.ExternalId] = product;
            _links.Add(new StagedLink(item, product, null, Created: true));
        }

        public void Created(Category category, ProviderCatalogueReviewItem item)
        {
            _categories.Add(category);
            _categoryByProviderId[item.ExternalId] = category;
            _links.Add(new StagedLink(item, null, category, Created: true));
            // A created category takes the provider's name on creation, so there is no local name it
            // replaced and nothing to list as a change.
        }

        public void Linked(Product product, ProviderCatalogueReviewItem item)
        {
            _productByProviderId[item.ExternalId] = product;
            _links.Add(new StagedLink(item, product, null, Created: false));
        }

        public void Linked(Category category, ProviderCatalogueReviewItem item)
        {
            _categoryByProviderId[item.ExternalId] = category;
            _links.Add(new StagedLink(item, null, category, Created: false));
        }

        /// <summary>
        /// The provider owns a linked category's name, so a difference is written and listed rather
        /// than offered. Submitting the same name is not a change and lists nothing.
        /// </summary>
        public void RenameCategory(Category category, ProviderCatalogueReviewItem item)
        {
            if (!CatalogueReviewPolicy.IsAutomaticChange(category.Name, item.ProviderName))
                return;

            _automatic.Add(new StagedField(
                item, null, category, ProviderManagedCatalogueFields.Name, category.Name, item.ProviderName));
            category.Name = item.ProviderName;
        }

        /// <summary>
        /// The two fields the provider owns on a product it names: its name, and its category - the
        /// local category carrying the provider's group id, which this same apply may have just linked
        /// or created. A group no local category carries leaves the product uncategorised rather than
        /// pointing it at a category that merely shares a number, exactly as the direct import does.
        /// </summary>
        public void ApplyProviderOwnedProductFields(
            Product product, ProviderCatalogueReviewItem item, bool created)
        {
            if (CatalogueReviewPolicy.IsAutomaticChange(product.Name, item.ProviderName))
            {
                // A created row takes these values rather than having them changed: there is no local
                // value it replaced, and the link the result already reports carries the name. Listing
                // them as changes would make a new row read like an overwritten one.
                if (!created)
                    _automatic.Add(new StagedField(
                        item, product, null, ProviderManagedCatalogueFields.Name, product.Name, item.ProviderName));
                product.Name = item.ProviderName;
            }

            var providerCategory = CategoryCarrying(item.ProviderGroupExternalId);
            var currentName = CurrentCategoryName(product);

            if (!created && CatalogueReviewPolicy.IsAutomaticChange(currentName, providerCategory?.Name))
                _automatic.Add(new StagedField(
                    item,
                    product,
                    null,
                    ProviderManagedCatalogueFields.CategoryId,
                    currentName,
                    providerCategory?.Name));

            // Both properties are written every time, for the reason the direct import writes both:
            // a category this apply is creating has no key yet, so the navigation is what names it
            // and the foreign key must not be left holding an earlier answer.
            product.Category = providerCategory;
            product.CategoryId = providerCategory?.Id;
            product.UpdatedAt = AppliedAtUtc;
        }

        /// <summary>
        /// The fields whose provider value the operator negotiates. A created row has no local value
        /// to keep, so it takes the provider's; an existing row is asked only while the provider value
        /// differs from both its own and the value this field was last decided about, and a field
        /// nobody answered is left alone and proposed again next time.
        /// </summary>
        public void ApplyNegotiableFields(
            Product product,
            ProviderCatalogueReviewItem item,
            CatalogueReviewApplyDecision? decision,
            bool created)
        {
            foreach (var field in new[] { NegotiableCatalogueField.UnitPrice, NegotiableCatalogueField.UnitVolume })
            {
                var providerValue = ProviderValue(field, item);
                if (providerValue is null)
                    continue;

                if (created)
                {
                    Write(product, field, providerValue);
                    _accepted.Add(new StagedField(item, product, null, field.ToString(), null, providerValue));
                    PendingFieldDecisions.Add(new PendingFieldDecision(
                        product, field, providerValue, ProviderFieldDecision.TakeProvider));
                    continue;
                }

                var localValue = LocalValue(field, product);
                var lastDecided = FieldDecisions
                    .FirstOrDefault(stored => stored.ProductId == product.Id && stored.Field == field)
                    ?.ProviderValue;

                if (!CatalogueReviewPolicy.IsProposed(localValue, providerValue, lastDecided))
                    continue;

                var answer = field == NegotiableCatalogueField.UnitPrice
                    ? decision?.UnitPrice
                    : decision?.UnitVolume;
                if (answer is null)
                    continue;

                if (answer == ProviderFieldDecision.TakeProvider)
                {
                    Write(product, field, providerValue);
                    _accepted.Add(new StagedField(
                        item, product, null, field.ToString(), localValue, providerValue));
                }
                else
                {
                    _skipped.Add(new StagedField(
                        item, product, null, field.ToString(), localValue, providerValue));
                }

                PendingFieldDecisions.Add(
                    new PendingFieldDecision(product, field, providerValue, answer.Value));
            }
        }

        /// <summary>The applied result, with every row named by the key the save assigned it.</summary>
        public CatalogueReviewApplied Result(CatalogueReviewApplyPlan plan) =>
            new(
                plan.Review.ReviewId,
                AlreadyApplied: false,
                AppliedAtUtc,
                _links
                    .Select(link => new CatalogueReviewLinkApplied(
                        link.Item.Kind.ToString(),
                        link.Item.ExternalId,
                        link.LocalRowId,
                        link.Item.ProviderName,
                        link.Created))
                    .ToList(),
                _automatic.Select(ToApplied).ToList(),
                _accepted.Select(ToApplied).ToList(),
                _skipped.Select(ToApplied).ToList(),
                _links
                    .Where(link => link.Item.Kind == ProviderCatalogueEntryKind.Product)
                    .Sum(link => link.Item.UnresolvedSaleCount));

        private static CatalogueReviewFieldApplied ToApplied(StagedField staged) =>
            new(
                staged.Item.Kind.ToString(),
                staged.Item.ExternalId,
                staged.LocalRowId,
                staged.Field,
                staged.PreviousValue,
                staged.NewValue);

        private string? CurrentCategoryName(Product product) =>
            product.Category?.Name
            ?? (product.CategoryId is { } categoryId ? LocalCategory(categoryId)?.Name : null);

        private static void Write(Product product, NegotiableCatalogueField field, string providerValue)
        {
            if (field == NegotiableCatalogueField.UnitPrice)
            {
                // Only text this renderer could have written is accepted; anything else leaves the
                // local price exactly as it is rather than writing a nearby number onto a product.
                if (ProviderCatalogueFieldValue.TryParsePrice(providerValue, out var price))
                    product.UnitPrice = price;
                return;
            }

            product.Unit = providerValue;
        }

        private static string? LocalValue(NegotiableCatalogueField field, Product product) =>
            field == NegotiableCatalogueField.UnitPrice
                ? ProviderCatalogueFieldValue.FromPrice(product.UnitPrice)
                : ProviderCatalogueFieldValue.FromLabel(product.Unit);

        private static string? ProviderValue(
            NegotiableCatalogueField field, ProviderCatalogueReviewItem item) =>
            field == NegotiableCatalogueField.UnitPrice ? item.ProviderUnitPrice : item.ProviderUnitVolume;

        private static string Noun(ProviderCatalogueEntryKind kind) =>
            kind == ProviderCatalogueEntryKind.Category ? "category" : "product";

        /// <summary>One staged link, remembered as the entity it wrote so its key can be read after the save.</summary>
        private sealed record StagedLink(
            ProviderCatalogueReviewItem Item, Product? Product, Category? Category, bool Created)
        {
            public long LocalRowId => Product?.Id ?? Category?.Id ?? 0;
        }

        /// <summary>One staged field change, remembered the same way.</summary>
        private sealed record StagedField(
            ProviderCatalogueReviewItem Item,
            Product? Product,
            Category? Category,
            string Field,
            string? PreviousValue,
            string? NewValue)
        {
            public long LocalRowId => Product?.Id ?? Category?.Id ?? 0;
        }
    }

    /// <summary>
    /// One field decision to record once the product it is about has its key: which provider value
    /// was decided about, and which way.
    /// </summary>
    private sealed record PendingFieldDecision(
        Product Product,
        NegotiableCatalogueField Field,
        string ProviderValue,
        ProviderFieldDecision Decision);
}
