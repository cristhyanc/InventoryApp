using System.Globalization;

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
/// <para><strong>Invariant culture, because the stored value is matched exactly.</strong> A
/// culture-sensitive format could write a comma for a decimal point, and the resulting row would
/// never match the value it was written for - the operator would be asked the same question every
/// time. Prices are rendered with their own trailing zeros preserved by a fixed two-decimal format,
/// so <c>3.5</c> and <c>3.50</c> are the same answer rather than two.</para>
/// </summary>
public static class ProviderCatalogueFieldValue
{
    /// <summary>A money value as the review's comparable text, or <see langword="null"/> for no value.</summary>
    public static string? FromPrice(decimal? price) =>
        price?.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>
    /// A label as the review's comparable text. Blank text is no value: a provider that reports an
    /// empty label has not named one, and proposing "" over a local label would be a deletion nobody
    /// asked for.
    /// </summary>
    public static string? FromLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label;

    /// <summary>
    /// Reads a stored price back, returning <see langword="false"/> for any text this helper could
    /// not have produced. Deliberately strict for the reason <see cref="NayaxExternalId.TryParse"/>
    /// is: a value that is not a price must fail to parse rather than parse to a nearby number, because
    /// the caller is about to write it onto a product.
    /// </summary>
    public static bool TryParsePrice(string? value, out decimal price) =>
        decimal.TryParse(
            value,
            NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out price);
}
