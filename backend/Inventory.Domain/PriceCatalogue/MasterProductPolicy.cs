using System.Globalization;

using Inventory.Domain.Exceptions;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// The fixed unit list a master product's individual size is measured in (issue #923, parent #817):
/// <c>ml</c>, <c>l</c>, <c>g</c>, <c>kg</c> and <c>each</c>, and nothing else.
///
/// <para>The list is closed because a size is only comparable against a size in the same unit: a
/// supplier listing that said "375" of something nobody declared could not be compared with
/// anything, and a free-text unit would make "ML", "mL", "millilitres" and "mls" four units. Volume
/// and weight are both here, and <see cref="Each"/> is the real unit of a product that is counted
/// rather than measured.</para>
///
/// <para>Numbered from 1, like every other persisted enum in this application, so a stored <c>0</c> -
/// which nothing writes - is never read as the first declared unit by accident.</para>
/// </summary>
public enum MasterProductSizeUnit
{
    /// <summary>Millilitres, written <c>ml</c>. The 375 of a 375 ml can.</summary>
    Millilitre = 1,

    /// <summary>Litres, written <c>l</c>.</summary>
    Litre = 2,

    /// <summary>Grams, written <c>g</c>.</summary>
    Gram = 3,

    /// <summary>Kilograms, written <c>kg</c>.</summary>
    Kilogram = 4,

    /// <summary>
    /// A countable item, written <c>each</c>: the unit of a product whose size is a number of things
    /// rather than a measurement. It is a declared unit rather than the absence of one, so "one
    /// item" and "nobody recorded a size" stay different states.
    /// </summary>
    Each = 5,
}

/// <summary>
/// The rules of one shared master product (issue #923, parent #817): the brand, the name or variant,
/// the individual size it comes in, and the normalised key that decides whether two descriptions are
/// the same product.
///
/// <para><strong>A master product is platform data about a product, not about anybody's
/// inventory.</strong> Nothing here reads or writes a <c>Product</c>, a business, a quantity, a cost
/// or a price: a business's own catalogue row is unchanged by this issue, and the mapping between the
/// two is <see cref="ProductPriceTrackingPolicy"/>'s subject. This policy therefore holds no money and
/// performs no inventory or GST calculation at all.</para>
///
/// <para><strong>The size is one value and one unit, deliberately.</strong> It is the size of the
/// individual item - 375 ml of a 375 ml can - and how many of them come in a pack is a fact about a
/// <em>supplier's</em> listing rather than about the product, so it lives on
/// <see cref="SupplierListingPolicy"/>'s side. Keeping the two apart is what lets one master product
/// be sold as a single, as a 10-pack and as a carton of 30 without three master products.</para>
/// </summary>
public static class MasterProductPolicy
{
    /// <summary>The longest accepted brand, measured on the trimmed value.</summary>
    public const int MaxBrandLength = 100;

    /// <summary>The longest accepted name or variant, measured on the trimmed value.</summary>
    public const int MaxNameLength = 200;

    /// <summary>
    /// The number of decimal places a size is recorded to. Three covers every unit in the list -
    /// 375 ml, 1.25 l, 0.5 kg, 1 each - and is the scale of the stored column, which is why a value
    /// that does not fit it is refused rather than rounded: a size somebody typed is not a value this
    /// application may quietly change.
    /// </summary>
    public const int SizeDecimals = 3;

    /// <summary>What a caller who submitted no brand, or only whitespace, is told.</summary>
    public const string BrandRequiredMessage = "A master product needs a brand.";

    /// <summary>What a caller who submitted a brand longer than <see cref="MaxBrandLength"/> is told.</summary>
    public const string BrandTooLongMessage = "A master product brand must be 100 characters or fewer.";

    /// <summary>What a caller who submitted no name, or only whitespace, is told.</summary>
    public const string NameRequiredMessage = "A master product needs a name.";

    /// <summary>What a caller who submitted a name longer than <see cref="MaxNameLength"/> is told.</summary>
    public const string NameTooLongMessage = "A master product name must be 200 characters or fewer.";

    /// <summary>
    /// What a caller who submitted a size that is not above zero is told. Zero is refused as firmly
    /// as a negative value: a product that comes in no quantity of anything is not a product, and a
    /// zero size would compare as cheaper per unit than every real one.
    /// </summary>
    public const string SizeNotPositiveMessage = "A master product size must be greater than zero.";

    /// <summary>
    /// What a caller who submitted a size with more precision than the stored scale is told. It names
    /// the scale rather than restating the value, which the caller already has.
    /// </summary>
    public const string SizeTooPreciseMessage =
        "A master product size is recorded to at most three decimal places.";

    /// <summary>
    /// What a caller who chose a unit that is not one of the declared ones is told. Built from the
    /// vocabulary itself so it cannot drift from what is accepted.
    /// </summary>
    public static string UnknownSizeUnitMessage { get; } =
        "A master product size unit must be one of "
            + string.Join(", ", Enum.GetValues<MasterProductSizeUnit>().Select(SizeUnitCode))
            + ".";

    /// <summary>
    /// The longest key <see cref="NormalisedKey"/> can produce, which is what bounds the stored
    /// column. Computed from the part limits rather than guessed; see
    /// <see cref="PriceCatalogueKey.MaxLengthOfParts"/>.
    /// </summary>
    public static int MaxNormalisedKeyLength { get; } = PriceCatalogueKey.MaxLengthOfParts(
        MaxBrandLength,
        MaxNameLength,
        // The size, as the invariant text below writes it: a value of this scale plus a sign and a
        // decimal point cannot be longer than its digits, and a generous bound here costs nothing.
        32,
        // The longest declared unit code.
        8);

    /// <summary>
    /// The code a unit is written as: <c>ml</c>, <c>l</c>, <c>g</c>, <c>kg</c>, <c>each</c>. The code
    /// is what a person reads and what <see cref="NormalisedKey"/> folds, so the enum member name -
    /// a programmer's label - never reaches a stored key.
    /// </summary>
    /// <exception cref="DomainValidationException">The unit is not a declared one.</exception>
    public static string SizeUnitCode(MasterProductSizeUnit unit) => unit switch
    {
        MasterProductSizeUnit.Millilitre => "ml",
        MasterProductSizeUnit.Litre => "l",
        MasterProductSizeUnit.Gram => "g",
        MasterProductSizeUnit.Kilogram => "kg",
        MasterProductSizeUnit.Each => "each",
        _ => throw new DomainValidationException(
            "A master product size unit must be one of ml, l, g, kg, each."),
    };

    /// <summary>
    /// Whether <paramref name="unit"/> is one of the declared units. An enum is a compile-time
    /// constraint and not a validation - a deserialized JSON number binds an arbitrary integer - so
    /// every submitted unit is checked against the declared vocabulary before anything is stored, the
    /// rule <c>Inventory.Domain.Gst.GstClassifications</c> states for a classification.
    /// </summary>
    public static bool IsDeclaredSizeUnit(MasterProductSizeUnit unit) => Enum.IsDefined(unit);

    /// <summary>The brand as it is to be stored: trimmed, never empty.</summary>
    /// <exception cref="DomainValidationException">
    /// The brand is blank, or longer than <see cref="MaxBrandLength"/> once trimmed.
    /// </exception>
    public static string NormaliseBrand(string? brand) =>
        NormaliseText(brand, MaxBrandLength, BrandRequiredMessage, BrandTooLongMessage);

    /// <summary>The name or variant as it is to be stored: trimmed, never empty.</summary>
    /// <exception cref="DomainValidationException">
    /// The name is blank, or longer than <see cref="MaxNameLength"/> once trimmed.
    /// </exception>
    public static string NormaliseName(string? name) =>
        NormaliseText(name, MaxNameLength, NameRequiredMessage, NameTooLongMessage);

    /// <summary>
    /// The size as it is to be stored: a positive decimal of at most <see cref="SizeDecimals"/>
    /// decimal places, in a declared unit.
    ///
    /// <para>The value is returned unchanged rather than rounded. A size is a stated fact about a
    /// product, so a value the stored scale cannot hold is refused and reported; silently rounding
    /// 374.9999 to 375 would record a product that does not exist, and silently rounding a price
    /// comparison's denominator is how a per-unit price becomes wrong by a hair in one direction
    /// every time.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The size is zero or negative, has more than <see cref="SizeDecimals"/> decimal places, or the
    /// unit is not a declared one. Nothing is stored.
    /// </exception>
    public static decimal NormaliseSize(decimal size, MasterProductSizeUnit unit)
    {
        if (!IsDeclaredSizeUnit(unit))
        {
            throw new DomainValidationException(UnknownSizeUnitMessage);
        }

        if (size <= 0m)
        {
            throw new DomainValidationException(SizeNotPositiveMessage);
        }

        if (decimal.Round(size, SizeDecimals) != size)
        {
            throw new DomainValidationException(SizeTooPreciseMessage);
        }

        return size;
    }

    /// <summary>
    /// The key two master-product descriptions are compared by: the brand, the name, the size and the
    /// unit code, folded by <see cref="PriceCatalogueKey"/>.
    ///
    /// <para>It is a stored column under a unique index rather than a comparison a query makes,
    /// exactly as <c>SupplierPreset.NormalisedName</c> is: "one description is one master product" has
    /// to be the database's rule, because a <c>WHERE</c> clause checked before an insert is a check
    /// two concurrent inserts can both pass - and two master products for one product is precisely
    /// what the parent issue exists to prevent.</para>
    ///
    /// <para>The size is written with <c>0.###</c> in the invariant culture, so <c>375</c>,
    /// <c>375.0</c> and <c>375.000</c> - three equal decimals with three different default spellings -
    /// produce one key, and a comma-decimal locale cannot produce a second.</para>
    /// </summary>
    /// <exception cref="DomainValidationException">
    /// The brand, name, size or unit is not one this policy accepts. The key of a description that
    /// could not be stored is never produced.
    /// </exception>
    public static string NormalisedKey(
        string? brand,
        string? name,
        decimal size,
        MasterProductSizeUnit unit) =>
        PriceCatalogueKey.Of(
            NormaliseBrand(brand),
            NormaliseName(name),
            NormaliseSize(size, unit).ToString("0.###", CultureInfo.InvariantCulture),
            SizeUnitCode(unit));

    private static string NormaliseText(
        string? value,
        int maxLength,
        string requiredMessage,
        string tooLongMessage)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainValidationException(requiredMessage);
        }

        var trimmed = value.Trim();

        return trimmed.Length <= maxLength
            ? trimmed
            : throw new DomainValidationException(tooLongMessage);
    }
}
