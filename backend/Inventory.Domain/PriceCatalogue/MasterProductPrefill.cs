using System.Globalization;

namespace Inventory.Domain.PriceCatalogue;

/// <summary>
/// The one rule that turns a shared master product into the two values a business's own product form
/// is filled in with (issue #1049, parent #817): the product <strong>Name</strong>, and the
/// <strong>unit label</strong> beside it.
///
/// <para><strong>It is in the Domain because it is a rule and not a presentation choice.</strong>
/// Three features depend on the same two strings - the business lookup this issue adds, the create
/// that links a product to a master product (#1051) and the Name autocomplete (#1052) - and a second
/// implementation in a controller or an Angular component would be a second answer to "what is this
/// product called here?". A prefilled product that spelled its size differently would be matched to a
/// different master product, or to none, by the very matcher this prefill is meant to feed.</para>
///
/// <para><strong>Two properties hold, and they are what this formatter exists for.</strong> Every
/// label it produces parses back to the master product's own size with
/// <see cref="AutomaticProductMatchPolicy.TryParseSize"/>, the rule the automatic matcher (#941)
/// applies - so a product created from a prefill can be matched to the master product it was
/// prefilled from. And every name and label it produces passes
/// <see cref="ProductPriceTrackingPolicy.TrackingRefusal"/>, so "Track supplier prices" is never
/// refused on a product whose details this formatter chose. Both are asserted in
/// <c>MasterProductPrefillTests</c> over every declared unit.</para>
///
/// <para><strong>It reads nothing and writes nothing.</strong> It is given a description - a brand, a
/// name and a size - and returns text: no master product row, no mapping, no business, no product and
/// no price is reachable from here, and a lookup that uses it changes nothing at all.</para>
/// </summary>
public static class MasterProductPrefill
{
    /// <summary>
    /// The format the size value is written in: the decimals the stored scale can hold, with trailing
    /// zeros dropped, in the invariant culture.
    ///
    /// <para>It is <c>MasterProductPolicy.NormalisedKey</c>'s own spelling of a size, deliberately:
    /// <c>375</c>, <c>375.0</c> and <c>375.000</c> are one decimal with three default spellings, and a
    /// comma-decimal locale would otherwise produce <c>"1,25l"</c> - a label neither the matcher's
    /// unit words nor a supplier's shelf would read as 1.25 litres.</para>
    /// </summary>
    private const string SizeFormat = "0.###";

    /// <summary>
    /// The product Name a business's product is prefilled with: the brand and the name, separated by
    /// one space.
    ///
    /// <para>Single spaces throughout, including inside either value: a stored brand or name holding
    /// a run of whitespace must not produce a double space a person has to delete, and must not fold
    /// to a different search text than the same product typed by hand.</para>
    /// </summary>
    public static string ProductName(string? brand, string? name) =>
        string.Join(
            ' ',
            $"{brand} {name}".Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// The unit label beside it: the size value with trailing zeros dropped, then the unit's own
    /// code, with no space between them - <c>45g</c>, <c>375ml</c>, <c>1.25l</c>, <c>1each</c>.
    ///
    /// <para>No space, because that is the shape a product's unit label already has in this
    /// application (<c>"375mL"</c>) and the shape
    /// <see cref="AutomaticProductMatchPolicy.TryParseSize"/> reads: a number and then a unit word,
    /// with whitespace folded away. The code is <c>MasterProductPolicy.SizeUnitCode</c>'s, so what a
    /// person sees is the catalogue's own vocabulary rather than a programmer's enum label.</para>
    /// </summary>
    /// <exception cref="Exceptions.DomainValidationException">
    /// The unit is not one of the declared ones, so there is no code to write it as. A persisted row
    /// carries an integer, and a value outside the declared list is not a size this application can
    /// state - it is refused rather than formatted into a label nothing could read back.
    /// </exception>
    public static string UnitLabel(decimal sizeValue, MasterProductSizeUnit sizeUnit) =>
        sizeValue.ToString(SizeFormat, CultureInfo.InvariantCulture)
            + MasterProductPolicy.SizeUnitCode(sizeUnit);

    /// <summary>
    /// Both values for one master product description, which is how every caller should take them:
    /// the two strings always travel together, because a Name without the unit label it was chosen
    /// with is half a prefill.
    /// </summary>
    /// <exception cref="Exceptions.DomainValidationException">The unit is not a declared one.</exception>
    public static MasterProductPrefillText For(
        string? brand,
        string? name,
        decimal sizeValue,
        MasterProductSizeUnit sizeUnit) =>
        new(ProductName(brand, name), UnitLabel(sizeValue, sizeUnit));
}

/// <summary>
/// What a business's product form is filled in with for one master product (issue #1049): the
/// product Name and the unit label beside it.
///
/// <para>Text and nothing else. It carries no master product key, no normalised key, no instant and
/// nothing about any business, because it is a suggestion about what to type rather than a record of
/// anything - and <see cref="MasterProductPrefill.For"/> is the only thing that builds one.</para>
/// </summary>
/// <param name="ProductName">The product Name: <c>{Brand} {Name}</c>, single-spaced.</param>
/// <param name="UnitLabel">The unit label: the size and its unit code, with no space.</param>
public sealed record MasterProductPrefillText(string ProductName, string UnitLabel);
