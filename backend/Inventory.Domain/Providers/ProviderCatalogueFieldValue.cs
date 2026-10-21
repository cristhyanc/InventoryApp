namespace Inventory.Domain.Providers;

/// <summary>
/// The one rendering of a negotiable catalogue field's value as the opaque text the review compares
/// and stores (issue #738).
///
/// <para><strong>Why text at all.</strong> The review has to remember the provider value it last
/// asked about, per field, so that a skipped value is not proposed again until the provider changes
/// it. One nullable text column holds that for every field, whatever its local type, which is what
/// keeps "last decided provider value" a single table rather than one column per field.</para>
///
/// <para><strong>The price rendering and its strict reader are gone</strong> (issue #792, A2 of
/// #786). They existed only to compare and write <c>Product.UnitPrice</c>, which no longer exists;
/// the historical text they wrote stays in <c>ProviderCatalogueReviewItems.ProviderUnitPrice</c> and
/// in the stored field decisions, and nothing reads it back.</para>
/// </summary>
public static class ProviderCatalogueFieldValue
{
    /// <summary>
    /// A label as the review's comparable text. Blank text is no value: a provider that reports an
    /// empty label has not named one, and proposing "" over a local label would be a deletion nobody
    /// asked for.
    /// </summary>
    public static string? FromLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label;
}
