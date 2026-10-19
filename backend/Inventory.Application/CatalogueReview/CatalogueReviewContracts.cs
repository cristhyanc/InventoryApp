using Inventory.Domain.Providers;

namespace Inventory.Application.CatalogueReview;

/// <summary>
/// One local row's negotiable field as the review presents it: what the row holds now, what the
/// provider says, and - for a field the provider owns outright - nothing to decide.
/// <paramref name="Field"/> is the <see cref="NegotiableCatalogueField"/> name, so the wire contract
/// does not depend on the enum's numbers.
/// </summary>
public sealed record CatalogueReviewFieldDto(string Field, string? LocalValue, string? ProviderValue);

/// <summary>
/// One provider-owned field change the apply will make without asking: the provider owns a linked
/// row's name and its category (<see cref="ProviderManagedCatalogueFields"/>), so any difference is
/// applied and listed rather than offered.
/// </summary>
public sealed record CatalogueReviewAutomaticChangeDto(string Field, string? LocalValue, string? ProviderValue);

/// <summary>
/// One provider product group in the review. <paramref name="LinkedCategoryId"/> is the local
/// category carrying this group's id, or <c>null</c> when no category does - the entry the operator
/// may link to an existing category or create a new one for.
/// </summary>
public sealed record CatalogueReviewCategoryDto(
    long ItemId,
    string ExternalId,
    string ProviderName,
    long? LinkedCategoryId,
    string? LocalName,
    IReadOnlyList<CatalogueReviewAutomaticChangeDto> AutomaticChanges)
{
    /// <summary>Whether a local category already carries this group's provider id.</summary>
    public bool IsLinked => LinkedCategoryId.HasValue;
}

/// <summary>
/// One provider product in the review.
///
/// <para><paramref name="AutomaticChanges"/> is what the apply will write without asking - the
/// provider's name and the category its group resolves to. <paramref name="Choices"/> is what it will
/// ask about: the negotiable fields whose provider value differs from both the local value and the
/// provider value this field was last decided about, so a value the operator skipped is absent until
/// the provider changes it again.</para>
///
/// <para><paramref name="UnresolvedSaleCount"/> is how many stored sales carry this provider product
/// id, are still unresolved and have known raw provenance. The catalogue apply never touches one:
/// resolving them is the separate, explicit, audited operation.</para>
/// </summary>
public sealed record CatalogueReviewProductDto(
    long ItemId,
    string ExternalId,
    string ProviderName,
    string? ProviderGroupExternalId,
    long? LinkedProductId,
    string? LocalName,
    int UnresolvedSaleCount,
    IReadOnlyList<CatalogueReviewAutomaticChangeDto> AutomaticChanges,
    IReadOnlyList<CatalogueReviewFieldDto> Choices)
{
    /// <summary>Whether a local product already carries this product's provider id.</summary>
    public bool IsLinked => LinkedProductId.HasValue;
}

/// <summary>
/// A started provider catalogue review (issue #738, parent #329 decision C1): the provider's
/// catalogue as read at <paramref name="StartedAtUtc"/>, reconciled against the business's own rows.
///
/// <para>It is a preview in the strict sense - starting a review changes no product, no category, no
/// price and no sale. The one thing it writes is itself, because the apply is authorised by a review
/// rather than by a payload.</para>
/// </summary>
public sealed record CatalogueReviewDto(
    long ReviewId,
    DateTime StartedAtUtc,
    IReadOnlyList<CatalogueReviewCategoryDto> Categories,
    IReadOnlyList<CatalogueReviewProductDto> Products,
    string UnitVolumeNote)
{
    /// <summary>Provider groups no local category carries the id of.</summary>
    public int UnlinkedCategoryCount => Categories.Count(category => !category.IsLinked);

    /// <summary>Provider products no local product carries the id of.</summary>
    public int UnlinkedProductCount => Products.Count(product => !product.IsLinked);

    /// <summary>
    /// How many stored sales linking the products in this review would make resolvable, in total.
    /// Nothing here resolves one.
    /// </summary>
    public int UnresolvedSaleCount => Products.Sum(product => product.UnresolvedSaleCount);
}

/// <summary>What the operator decided about one negotiable field of one reviewed product.</summary>
public sealed record CatalogueReviewFieldDecisionRequest(
    NegotiableCatalogueField Field,
    ProviderFieldDecision Decision);

/// <summary>
/// What the operator decided about one reviewed entry.
///
/// <para><paramref name="Action"/> applies only to an entry no local row carries the id of: an
/// already-linked entry has nothing to link, and its provider changes are applied on their own.
/// <paramref name="LocalRowId"/> is required by - and only by -
/// <see cref="CatalogueReviewAction.LinkExisting"/>.</para>
///
/// <para>An entry no decision names is deferred, which is the honest default: a review the operator
/// has only half decided must not link the other half by omission.</para>
/// </summary>
public sealed record CatalogueReviewDecisionRequest(
    long ItemId,
    CatalogueReviewAction Action = CatalogueReviewAction.Defer,
    long? LocalRowId = null,
    IReadOnlyList<CatalogueReviewFieldDecisionRequest>? Fields = null);

/// <summary>
/// The request that applies a review. It carries decisions and nothing else: every provider name,
/// price, group id and external id the apply writes comes from the stored review's own items, so
/// nothing a caller submits reaches a catalogue field.
/// </summary>
public sealed record ApplyCatalogueReviewRequest(
    long ReviewId,
    IReadOnlyList<CatalogueReviewDecisionRequest>? Decisions = null);

/// <summary>One link the apply recorded. <paramref name="Created"/> says whether the local row is new.</summary>
public sealed record CatalogueReviewLinkApplied(
    string Kind,
    string ExternalId,
    long LocalRowId,
    string Name,
    bool Created);

/// <summary>One field the apply wrote or deliberately left alone, with the values it decided between.</summary>
public sealed record CatalogueReviewFieldApplied(
    string Kind,
    string ExternalId,
    long LocalRowId,
    string Field,
    string? PreviousValue,
    string? ProviderValue);

/// <summary>
/// What one apply did (issue #738).
///
/// <para><paramref name="AlreadyApplied"/> is the idempotent answer: a review that has been applied is
/// applied, so a second apply writes nothing at all and says so rather than failing or repeating
/// itself.</para>
///
/// <para><paramref name="AutomaticChanges"/> are the provider-owned fields the apply wrote without
/// asking - a linked row's name, and its category. <paramref name="AcceptedChanges"/> and
/// <paramref name="SkippedChanges"/> are the negotiable ones, and both are recorded as decided-about
/// so neither value is proposed again until the provider changes it.</para>
/// </summary>
public sealed record CatalogueReviewApplied(
    long ReviewId,
    bool AlreadyApplied,
    DateTime AppliedAtUtc,
    IReadOnlyList<CatalogueReviewLinkApplied> Links,
    IReadOnlyList<CatalogueReviewFieldApplied> AutomaticChanges,
    IReadOnlyList<CatalogueReviewFieldApplied> AcceptedChanges,
    IReadOnlyList<CatalogueReviewFieldApplied> SkippedChanges,
    int UnresolvedSaleCount)
{
    /// <summary>Local rows this apply created.</summary>
    public int CreatedCount => Links.Count(link => link.Created);

    /// <summary>Existing local rows this apply linked.</summary>
    public int LinkedCount => Links.Count(link => !link.Created);
}
