namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// What one line of a scanned receipt is (issue #1163, sub-issue B of #975), and the whole declared
/// vocabulary.
///
/// <para><strong>Only a declared kind is a kind.</strong> These values arrive from a language model's
/// JSON, so an enum is nothing like a validation: every reader resolves one through
/// <see cref="ReceiptLineKinds.Parse"/>, which answers <see langword="null"/> for anything the
/// published schema does not name, and a line whose kind cannot be resolved is dropped rather than
/// defaulted. Numbered from 10 and spaced for the reason every other vocabulary in this layer is:
/// <c>0</c> is what a default-constructed enum holds, and the one answer that must never come from
/// forgetting to decide is "this is stock the business bought".</para>
/// </summary>
public enum ReceiptLineKind
{
    /// <summary>Stock the business bought: the only kind that becomes a purchase line.</summary>
    Stock = 10,

    /// <summary>A delivery, freight or shipping charge. Summed into the draft's delivery cost.</summary>
    Delivery = 20,

    /// <summary>
    /// A container deposit levy. Never a purchase line of its own unless the totals cannot say
    /// whether it is already inside the drink prices (see <see cref="ReceiptLevyPolicy"/>).
    /// </summary>
    Levy = 30,

    /// <summary>A bag or packaging charge. Summed into the draft's package cost.</summary>
    Package = 40,

    /// <summary>
    /// A line that is not stock for resale - an office item, a service, a fee this application has
    /// no field for. It is returned so the operator can see it and so it counts towards the total
    /// reconciliation, and it never carries a product suggestion.
    /// </summary>
    Other = 50,
}

/// <summary>The declared <see cref="ReceiptLineKind"/> vocabulary and the published name of each member.</summary>
public static class ReceiptLineKinds
{
    /// <summary>Every declared kind, in the enum's own order.</summary>
    public static readonly IReadOnlyList<ReceiptLineKind> All =
    [
        ReceiptLineKind.Stock,
        ReceiptLineKind.Delivery,
        ReceiptLineKind.Levy,
        ReceiptLineKind.Package,
        ReceiptLineKind.Other,
    ];

    /// <summary>Whether <paramref name="kind"/> is one this code declares.</summary>
    public static bool IsDeclared(ReceiptLineKind kind) => All.Contains(kind);

    /// <summary>
    /// The kind's published name - the exact token the output schema allows - written out rather
    /// than taken from the enum member so renaming a C# member cannot silently change the contract
    /// the model is asked to answer in.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="kind"/> is not declared.</exception>
    public static string Name(ReceiptLineKind kind) => kind switch
    {
        ReceiptLineKind.Stock => "stock",
        ReceiptLineKind.Delivery => "delivery",
        ReceiptLineKind.Levy => "levy",
        ReceiptLineKind.Package => "package",
        ReceiptLineKind.Other => "other",
        _ => throw new ArgumentOutOfRangeException(
            nameof(kind), kind, "Only a declared receipt line kind has a published name."),
    };

    /// <summary>
    /// The kind <paramref name="token"/> names, or <see langword="null"/> when it names none. The
    /// comparison is ordinal and case-sensitive: the schema publishes lower-case tokens, and
    /// accepting <c>"Stock"</c> would be accepting a value the contract does not offer.
    /// </summary>
    public static ReceiptLineKind? Parse(string? token) =>
        token is null
            ? null
            : All.Where(kind => string.Equals(Name(kind), token, StringComparison.Ordinal))
                .Select(kind => (ReceiptLineKind?)kind)
                .FirstOrDefault();
}

/// <summary>
/// What the receipt said about one line's pack size (issue #1163), as an explicit choice rather than
/// as the presence or absence of a number.
///
/// <para><strong>It exists because of a failure the model test of 8 November 2026 found</strong>: a
/// reader that is simply asked for a pack size will supply one from general product knowledge, or
/// call an unmarked carton a single item. Making the three answers distinct forces the reader to say
/// which of them it means, and <see cref="NotPrinted"/> is then a usable answer - a missing figure
/// the operator is asked for - instead of a silent <c>1</c> that would divide a carton's price by
/// one unit and record a 24-times overstated cost.</para>
/// </summary>
public enum ReceiptPackSize
{
    /// <summary>The receipt prints the pack size, and <c>unitsPerPack</c> is what it prints.</summary>
    Printed = 10,

    /// <summary>The line is a genuine single item: one unit per "pack".</summary>
    SingleItem = 20,

    /// <summary>
    /// The receipt does not print a pack size. The units are unknown, and are never taken to be one.
    /// </summary>
    NotPrinted = 30,
}

/// <summary>The declared <see cref="ReceiptPackSize"/> vocabulary and the schema's token for each.</summary>
public static class ReceiptPackSizes
{
    /// <summary>Every declared answer, in the enum's own order.</summary>
    public static readonly IReadOnlyList<ReceiptPackSize> All =
    [
        ReceiptPackSize.Printed,
        ReceiptPackSize.SingleItem,
        ReceiptPackSize.NotPrinted,
    ];

    /// <summary>The answer's published token.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="packSize"/> is not declared.</exception>
    public static string Name(ReceiptPackSize packSize) => packSize switch
    {
        ReceiptPackSize.Printed => "printed",
        ReceiptPackSize.SingleItem => "single",
        ReceiptPackSize.NotPrinted => "notPrinted",
        _ => throw new ArgumentOutOfRangeException(
            nameof(packSize), packSize, "Only a declared pack-size answer has a published name."),
    };

    /// <summary>
    /// The answer <paramref name="token"/> names, or <see langword="null"/> when it names none. An
    /// unrecognised answer is not <see cref="ReceiptPackSize.NotPrinted"/>: the reader has to state
    /// which of the three it means, and a line that does not is dropped.
    /// </summary>
    public static ReceiptPackSize? Parse(string? token) =>
        token is null
            ? null
            : All.Where(packSize => string.Equals(Name(packSize), token, StringComparison.Ordinal))
                .Select(packSize => (ReceiptPackSize?)packSize)
                .FirstOrDefault();

    /// <summary>
    /// How many units one pack holds, given the reader's answer and the number it reported: the
    /// number only for <see cref="ReceiptPackSize.Printed"/>, exactly <c>1</c> for
    /// <see cref="ReceiptPackSize.SingleItem"/>, and nothing at all for
    /// <see cref="ReceiptPackSize.NotPrinted"/> - whatever number came with it.
    /// </summary>
    public static int? UnitsPerPack(ReceiptPackSize packSize, int? reported) => packSize switch
    {
        ReceiptPackSize.Printed => reported is > 0 ? reported : null,
        ReceiptPackSize.SingleItem => 1,
        _ => null,
    };
}

/// <summary>
/// Whether a printed line price already has GST in it (issue #1163). The application's purchase
/// amounts are GST-inclusive (AGENTS.md § Purchase GST classification), so a price read from an
/// ex-GST column has to be converted before it can become a unit cost - and a price whose basis
/// nobody can tell must never be converted either way.
/// </summary>
public enum ReceiptPriceGstBasis
{
    /// <summary>The printed price already includes GST. The app's own convention: nothing is added.</summary>
    IncludesGst = 10,

    /// <summary>The printed price excludes GST. A taxable line has 10% added to it.</summary>
    ExcludesGst = 20,

    /// <summary>
    /// The receipt does not say. A real state, and never collapsed into either of the two above: a
    /// guessed basis is a 10% error in a cost that feeds AVCO and every profit figure.
    /// </summary>
    Unknown = 30,
}

/// <summary>
/// Whether a line is taxable, as the receipt's own GST markers say (issue #1163). It is the
/// receipt's claim and not a classification: a recorded purchase's GST classification is
/// <c>Inventory.Domain.Gst.GstClassification</c>, decided by a person or by an explicit rule, and
/// nothing here writes one.
/// </summary>
public enum ReceiptLineTaxability
{
    /// <summary>The receipt marks the line as carrying GST.</summary>
    Taxable = 10,

    /// <summary>The receipt marks the line as GST-free.</summary>
    GstFree = 20,

    /// <summary>The receipt does not say, which is the ordinary case on a cash-register docket.</summary>
    Unknown = 30,
}

/// <summary>
/// The <c>yes</c> / <c>no</c> / <c>unknown</c> answers the output schema asks for, and how they
/// become the two tri-states above (issue #1163).
///
/// <para>One parser for both, because they are the same three tokens in the schema and a reader
/// that accepted <c>"true"</c> for one and not the other would be two contracts.</para>
/// </summary>
public static class ReceiptGstAnswers
{
    /// <summary>The affirmative token the schema allows.</summary>
    public const string Yes = "yes";

    /// <summary>The negative token the schema allows.</summary>
    public const string No = "no";

    /// <summary>The "the receipt does not say" token the schema allows.</summary>
    public const string Unknown = "unknown";

    /// <summary>Every token the schema allows, in the order the schema lists them.</summary>
    public static readonly IReadOnlyList<string> All = [Yes, No, Unknown];

    /// <summary>
    /// The price basis <paramref name="token"/> names, or <see langword="null"/> when it names none.
    /// An absent or unrecognised answer is not <see cref="ReceiptPriceGstBasis.Unknown"/>: "the
    /// receipt does not say" is a value the model has to state, and a missing field is a schema
    /// violation the caller drops.
    /// </summary>
    public static ReceiptPriceGstBasis? ParseBasis(string? token) => token switch
    {
        Yes => ReceiptPriceGstBasis.IncludesGst,
        No => ReceiptPriceGstBasis.ExcludesGst,
        Unknown => ReceiptPriceGstBasis.Unknown,
        _ => null,
    };

    /// <summary>
    /// The taxability <paramref name="token"/> names, or <see langword="null"/> when it names none,
    /// on exactly <see cref="ParseBasis"/>'s terms.
    /// </summary>
    public static ReceiptLineTaxability? ParseTaxability(string? token) => token switch
    {
        Yes => ReceiptLineTaxability.Taxable,
        No => ReceiptLineTaxability.GstFree,
        Unknown => ReceiptLineTaxability.Unknown,
        _ => null,
    };

    /// <summary>The token a price basis is published as, so the response speaks the schema's words.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="basis"/> is not declared.</exception>
    public static string Name(ReceiptPriceGstBasis basis) => basis switch
    {
        ReceiptPriceGstBasis.IncludesGst => Yes,
        ReceiptPriceGstBasis.ExcludesGst => No,
        ReceiptPriceGstBasis.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(
            nameof(basis), basis, "Only a declared price GST basis has a published name."),
    };

    /// <summary>The token a taxability is published as.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="taxability"/> is not declared.</exception>
    public static string Name(ReceiptLineTaxability taxability) => taxability switch
    {
        ReceiptLineTaxability.Taxable => Yes,
        ReceiptLineTaxability.GstFree => No,
        ReceiptLineTaxability.Unknown => Unknown,
        _ => throw new ArgumentOutOfRangeException(
            nameof(taxability), taxability, "Only a declared taxability has a published name."),
    };
}

/// <summary>
/// Where one value in a scanned draft came from (issue #1163): the receipt, or this application.
/// Every value the draft carries is marked, because an operator correcting a draft needs to know
/// which figures are quotations and which are calculations.
/// </summary>
public enum ReceiptValueProvenance
{
    /// <summary>Printed on the receipt and read off it.</summary>
    Read = 10,

    /// <summary>
    /// This application's own suggestion: a matched product or supplier, or a figure it calculated
    /// from values it read.
    /// </summary>
    Suggested = 20,
}

/// <summary>The published name of each <see cref="ReceiptValueProvenance"/> member.</summary>
public static class ReceiptValueProvenances
{
    /// <summary>Every declared provenance, in the enum's own order.</summary>
    public static readonly IReadOnlyList<ReceiptValueProvenance> All =
    [
        ReceiptValueProvenance.Read,
        ReceiptValueProvenance.Suggested,
    ];

    /// <summary>The provenance's published name.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="provenance"/> is not declared.</exception>
    public static string Name(ReceiptValueProvenance provenance) => provenance switch
    {
        ReceiptValueProvenance.Read => "read",
        ReceiptValueProvenance.Suggested => "suggested",
        _ => throw new ArgumentOutOfRangeException(
            nameof(provenance), provenance, "Only a declared provenance has a published name."),
    };
}

/// <summary>
/// What a scanned line could not be completed with (issue #1163) - the <c>missing</c> list the draft
/// publishes per line, so the form can ask for exactly the figures it needs instead of showing a
/// line that looks finished and is not.
/// </summary>
public enum ReceiptLineMissingValue
{
    /// <summary>How many packs were delivered.</summary>
    Packs = 10,

    /// <summary>How many units are in one pack. Never guessed from general product knowledge.</summary>
    UnitsPerPack = 20,

    /// <summary>The printed line total.</summary>
    LineTotal = 30,

    /// <summary>Whether the printed price includes GST.</summary>
    PriceGstBasis = 40,

    /// <summary>Whether the line is taxable, needed only to convert an ex-GST price.</summary>
    Taxability = 50,

    /// <summary>The unit cost, which no combination of the values above could produce.</summary>
    UnitCost = 60,
}

/// <summary>The published name of each <see cref="ReceiptLineMissingValue"/> member.</summary>
public static class ReceiptLineMissingValues
{
    /// <summary>Every declared missing-value marker, in the enum's own order.</summary>
    public static readonly IReadOnlyList<ReceiptLineMissingValue> All =
    [
        ReceiptLineMissingValue.Packs,
        ReceiptLineMissingValue.UnitsPerPack,
        ReceiptLineMissingValue.LineTotal,
        ReceiptLineMissingValue.PriceGstBasis,
        ReceiptLineMissingValue.Taxability,
        ReceiptLineMissingValue.UnitCost,
    ];

    /// <summary>The marker's published name.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not declared.</exception>
    public static string Name(ReceiptLineMissingValue value) => value switch
    {
        ReceiptLineMissingValue.Packs => "packs",
        ReceiptLineMissingValue.UnitsPerPack => "unitsPerPack",
        ReceiptLineMissingValue.LineTotal => "lineTotal",
        ReceiptLineMissingValue.PriceGstBasis => "priceIncludesGst",
        ReceiptLineMissingValue.Taxability => "taxable",
        ReceiptLineMissingValue.UnitCost => "unitCost",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value), value, "Only a declared missing value has a published name."),
    };
}
