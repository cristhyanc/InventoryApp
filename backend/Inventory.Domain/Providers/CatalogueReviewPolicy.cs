namespace Inventory.Domain.Providers;

/// <summary>Which half of the catalogue one reviewed provider entry is (issue #738).</summary>
public enum ProviderCatalogueEntryKind
{
    /// <summary>A provider product, reviewed against <c>Product.ExternalProductId</c>.</summary>
    Product = 1,

    /// <summary>A provider product group, reviewed against <c>Category.ExternalCategoryId</c>.</summary>
    Category = 2,
}

/// <summary>
/// What the operator asked the review to do with one unlinked provider entry (issue #738).
/// Numbered from 1, so an unwritten integer is not an action.
/// </summary>
public enum CatalogueReviewAction
{
    /// <summary>
    /// Leave the entry alone this time. The honest answer for an entry the operator has not decided
    /// about yet, and the default for every entry no decision names.
    /// </summary>
    Defer = 1,

    /// <summary>Record the provider's id on a local row the operator named.</summary>
    LinkExisting = 2,

    /// <summary>Create a new local row carrying the provider's id and name.</summary>
    CreateNew = 3,
}

/// <summary>
/// One field whose provider value the operator negotiates rather than inherits (issue #738, parent
/// #329 decision C1). The provider owns a linked row's name and category outright
/// (<see cref="ProviderManagedCatalogueFields"/>); these are the ones where the local value may win.
/// </summary>
public enum NegotiableCatalogueField
{
    /// <summary>
    /// The catalogue selling price: <c>Product.UnitPrice</c> against the Nayax
    /// <c>ProductDefaultRetailPrice</c>.
    /// </summary>
    UnitPrice = 1,

    /// <summary>
    /// The product's unit/volume label: <c>Product.Unit</c>. <strong>No provider value feeds it
    /// today</strong> - see <see cref="CatalogueReviewPolicy.UnitVolumeUnavailableNote"/>.
    /// </summary>
    UnitVolume = 2,
}

/// <summary>
/// What the operator decided about one negotiable field's provider value (issue #738). The same two
/// answers serve both moments the review asks: "keep local or take provider" when a row is first
/// linked, and "accept or skip" when a linked row's provider value has since changed.
/// </summary>
public enum ProviderFieldDecision
{
    /// <summary>Keep the local value. The provider value is recorded as decided-about, and not proposed again until it changes.</summary>
    KeepLocal = 1,

    /// <summary>Write the provider value onto the local row.</summary>
    TakeProvider = 2,
}

/// <summary>
/// The deterministic rules of the provider catalogue review (issue #738, parent #329 decision C1):
/// when a link may be recorded, and when a provider field value is worth proposing at all.
///
/// <para>It holds no clock, no database and no configuration: every answer is a function of its
/// arguments, so the use case that builds a review, the adapter that applies one and a test all
/// decide identically.</para>
///
/// <para><strong>Nothing here matches by name.</strong> A link is only ever the operator's explicit
/// choice of a local row, or a row the review creates; two rows that happen to share a name are two
/// rows (AGENTS.md § Database and migrations, on a provider id never being a local key).</para>
/// </summary>
public static class CatalogueReviewPolicy
{
    /// <summary>
    /// Why the provider's unit/volume is never proposed. The authoritative Lynx contract for
    /// <c>GET /v1/operators/{OperatorID}/products</c> declares no unit or volume <em>label</em>: the
    /// only volume-shaped fields on it are <c>ProductVolumeTypeID</c>, an <c>int32</c> whose
    /// vocabulary the documentation does not publish, and <c>ProductPackageQuantity</c>, which is
    /// units per package. <c>Product.Unit</c> is a free-text label ("can", "bottle"), so neither
    /// field is it, and inventing the mapping is exactly what AGENTS.md § Nayax contract verification
    /// forbids. The field stays declared, and its provider value stays absent, until a human decides
    /// which Lynx field means it.
    /// </summary>
    public const string UnitVolumeUnavailableNote =
        "The provider publishes no unit/volume label for this product, so none is proposed.";

    /// <summary>
    /// Why a link may not be recorded, or <see langword="null"/> when it may.
    ///
    /// <para>Both refusals are statements about <em>stored</em> rows, so they are decided in the same
    /// transaction as the write that would follow them, never from a value read earlier.</para>
    ///
    /// <para>Re-recording the id a row already carries is <strong>not</strong> a refusal: it is the
    /// state the apply was asked to reach, which is what makes applying the same review twice a
    /// no-op.</para>
    /// </summary>
    /// <param name="kind">Which half of the catalogue is being linked, for the message.</param>
    /// <param name="providerId">The provider id the link would record.</param>
    /// <param name="localRowProviderId">The provider id the chosen local row carries today, if any.</param>
    /// <param name="anotherRowCarriesIt">Whether a different local row of this business already carries <paramref name="providerId"/>.</param>
    public static string? LinkRefusal(
        ProviderCatalogueEntryKind kind,
        string providerId,
        string? localRowProviderId,
        bool anotherRowCarriesIt)
    {
        var noun = Noun(kind);

        if (localRowProviderId is not null && !string.Equals(localRowProviderId, providerId, StringComparison.Ordinal))
            return $"This {noun} is already linked to provider {noun} {localRowProviderId}, so it cannot "
                + $"also be linked to {providerId}. Nothing was changed.";

        if (anotherRowCarriesIt)
            return $"Provider {noun} {providerId} is already linked to a different local {noun}, so it "
                + "cannot be linked again. Nothing was changed.";

        return null;
    }

    /// <summary>
    /// Why a link decision is not usable at all, or <see langword="null"/> when it is. These are
    /// statements about the decision itself rather than about stored rows, so they are answered before
    /// anything is read.
    /// </summary>
    public static string? DecisionRefusal(CatalogueReviewAction action, long? localRowId, bool alreadyLinked)
    {
        if (alreadyLinked && action != CatalogueReviewAction.Defer)
            return "This provider entry is already linked to a local row, so it cannot be linked or "
                + "created again. Its provider changes are applied on their own. Nothing was changed.";

        if (action == CatalogueReviewAction.LinkExisting && localRowId is null)
            return "Linking to an existing local row needs that row named. Nothing was changed.";

        if (action != CatalogueReviewAction.LinkExisting && localRowId is not null)
            return "A local row may only be named when linking to an existing one. Nothing was changed.";

        return null;
    }

    /// <summary>
    /// Whether a provider field value is worth putting to the operator.
    ///
    /// <para>Three things have to be true, and the third is the one that makes a skip stick: the
    /// provider has a value, it differs from the local one, and it differs from the provider value
    /// this field was last decided about. So a value the operator kept local is not proposed again
    /// <em>until the provider changes it again</em>, and neither is one they took and then edited
    /// back locally - in both cases the operator has already answered this exact provider value.</para>
    ///
    /// <para>Every value is opaque text, compared exactly: the caller renders a price or a label into
    /// invariant text once (<see cref="ProviderCatalogueFieldValue"/>) so that the proposal rule, the
    /// stored last-decided value and a test all compare the same characters.</para>
    /// </summary>
    public static bool IsProposed(string? localValue, string? providerValue, string? lastDecidedProviderValue) =>
        providerValue is not null
        && !string.Equals(providerValue, localValue, StringComparison.Ordinal)
        && !string.Equals(providerValue, lastDecidedProviderValue, StringComparison.Ordinal);

    /// <summary>
    /// Whether a provider-owned field's value is a change that a linked row's apply must write and
    /// report. Unlike <see cref="IsProposed"/> there is nothing to decide: the provider owns the
    /// field, so any difference is applied and listed.
    /// </summary>
    public static bool IsAutomaticChange(string? localValue, string? providerValue) =>
        !string.Equals(providerValue, localValue, StringComparison.Ordinal);

    private static string Noun(ProviderCatalogueEntryKind kind) =>
        kind == ProviderCatalogueEntryKind.Category ? "category" : "product";
}
