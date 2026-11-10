using System.Globalization;
using System.Text.Json;
using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// One line of the reader's answer, after validation (issue #1163). Every field here either matched
/// the published schema or is absent; nothing was coerced.
/// </summary>
/// <param name="Text">The line as printed. Required: a line nobody can recognise is not a line.</param>
/// <param name="Kind">What the line is.</param>
/// <param name="DeliveredPacks">
/// How many packs were delivered - supplied, picked or shipped. The ordered quantity is read into
/// its own field and never used (see <see cref="ReceiptScanPayloadLine.OrderedPacks"/>).
/// </param>
/// <param name="OrderedPacks">
/// How many packs were ordered. Deliberately never the quantity: it is read so the reader has
/// somewhere to put it other than the delivered quantity.
/// </param>
/// <param name="UnitsPerPack">The pack size, resolved from the reader's explicit pack-size answer.</param>
/// <param name="PackSize">Which of the three pack-size answers the reader gave.</param>
/// <param name="LineTotal">The line total as printed, before discounts.</param>
/// <param name="DiscountTotal">The discounts attached to this line, as a positive amount.</param>
/// <param name="PriceBasis">Whether the printed price includes GST. Required.</param>
/// <param name="Taxability">Whether the receipt marks the line taxable; unstated when absent.</param>
/// <param name="GstMarker">The GST marker the reader saw.</param>
/// <param name="ProductId">The product the reader matched, before this business's own ids are checked.</param>
/// <param name="IsDrinkContainer">
/// Whether the line is a drink in a deposit container, which is what decides who shares a levy.
/// </param>
public sealed record ReceiptScanPayloadLine(
    string Text,
    ReceiptLineKind Kind,
    int? DeliveredPacks,
    int? OrderedPacks,
    int? UnitsPerPack,
    ReceiptPackSize PackSize,
    decimal? LineTotal,
    decimal DiscountTotal,
    ReceiptPriceGstBasis PriceBasis,
    ReceiptLineTaxability Taxability,
    string? GstMarker,
    long? ProductId,
    bool IsDrinkContainer);

/// <summary>The reader's answer, after validation (issue #1163).</summary>
/// <param name="SupplierName">The supplier as printed.</param>
/// <param name="SupplierId">The supplier the reader matched, before this business's own ids are checked.</param>
/// <param name="OrderDate">When the order was placed. Never the purchase date.</param>
/// <param name="ReceiptDate">The receipt's own transaction or invoice date.</param>
/// <param name="DeliveryDate">When the goods were delivered, supplied or picked up.</param>
/// <param name="InvoiceNumber">The supplier's invoice or order number as printed.</param>
/// <param name="ReceiptTotal">The total printed on the receipt.</param>
/// <param name="ReceiptTotalBasis">Whether that total includes GST.</param>
/// <param name="GstTotal">The GST total, when the receipt printed one.</param>
/// <param name="Lines">The lines that matched the schema.</param>
public sealed record ReceiptScanPayload(
    string? SupplierName,
    long? SupplierId,
    DateOnly? OrderDate,
    DateOnly? ReceiptDate,
    DateOnly? DeliveryDate,
    string? InvoiceNumber,
    decimal? ReceiptTotal,
    ReceiptPriceGstBasis ReceiptTotalBasis,
    decimal? GstTotal,
    IReadOnlyList<ReceiptScanPayloadLine> Lines);

/// <summary>
/// Validates the reader's JSON against the published output schema (issue #1163), and is the only
/// thing in this application that reads it.
///
/// <para><strong>Receipt text is data, and so is this payload.</strong> It is a language model's
/// answer about a document a third party wrote, so nothing in it is trusted: every field is read only
/// for the JSON kind the schema documents for it, anything that does not match is dropped, and no
/// value is coerced. A quoted number is not a number, an unrecognised token is not a default, and a
/// field the schema requires being absent drops the line that lacked it. It is
/// <c>NayaxSqsTransactionParser</c>'s discipline applied to a payload with even less claim to be
/// trusted.</para>
///
/// <para><strong>Nothing it drops fails the request.</strong> A receipt that is partly readable is
/// worth more than no receipt, so a bad line is left out and the rest are kept; the caller decides
/// that an answer with no usable line at all is "could not read this receipt".</para>
///
/// <para><strong>Extra fields are ignored.</strong> A model that answers with more than it was asked
/// for has answered the schema plus noise, and the noise is not read.</para>
/// </summary>
public static class ReceiptScanPayloadParser
{
    /// <summary>The date format the schema asks for, and the only one accepted.</summary>
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>
    /// The answer <paramref name="json"/> describes, or <see langword="null"/> when it is not an
    /// object of the published shape at all - not JSON, not an object, or carrying no
    /// <c>lines</c> array.
    /// </summary>
    public static ReceiptScanPayload? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            // Malformed, truncated, or prose. Never logged or echoed: the text it would quote is a
            // business document.
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object
                || Property(root, "lines") is not { ValueKind: JsonValueKind.Array } lines)
            {
                return null;
            }

            return new ReceiptScanPayload(
                Text(root, "supplierName"),
                Integer(root, "supplierId"),
                Date(root, "orderDate"),
                Date(root, "receiptDate"),
                Date(root, "deliveryDate"),
                Text(root, "invoiceNumber"),
                Money(root, "receiptTotal"),
                ReceiptGstAnswers.ParseBasis(Text(root, "receiptTotalIncludesGst"))
                    ?? ReceiptPriceGstBasis.Unknown,
                Money(root, "gstTotal"),
                lines.EnumerateArray()
                    .Select(ParseLine)
                    .Where(line => line is not null)
                    .Select(line => line!)
                    .ToList());
        }
    }

    /// <summary>
    /// One line, or <see langword="null"/> when it does not match the schema: not an object, no
    /// readable text, an unrecognised kind or pack-size answer, or no stated GST basis - which the
    /// schema requires on every line, because a basis nobody stated is not the same as one stated to
    /// be unknown.
    /// </summary>
    private static ReceiptScanPayloadLine? ParseLine(JsonElement element)
    {
        if (element.ValueKind is not JsonValueKind.Object)
        {
            return null;
        }

        if (Text(element, "text") is not { } text
            || ReceiptLineKinds.Parse(Text(element, "kind")) is not { } kind
            || ReceiptPackSizes.Parse(Text(element, "packSize")) is not { } packSize
            || ReceiptGstAnswers.ParseBasis(Text(element, "priceIncludesGst")) is not { } basis)
        {
            return null;
        }

        return new ReceiptScanPayloadLine(
            text,
            kind,
            Count(element, "deliveredPacks"),
            Count(element, "orderedPacks"),
            ReceiptPackSizes.UnitsPerPack(packSize, Count(element, "unitsPerPack")),
            packSize,
            Money(element, "lineTotal"),
            // A discount is printed as a negative line, and a reader may report it either way round.
            // Its magnitude is what is subtracted, so the sign cannot flip a discount into a surcharge.
            Math.Abs(Money(element, "discountTotal") ?? 0m),
            basis,
            // Unstated rather than required: an absent taxability blocks an ex-GST conversion, which
            // is the safe answer, so dropping the line would lose a readable inc-GST line for nothing.
            ReceiptGstAnswers.ParseTaxability(Text(element, "taxable")) ?? ReceiptLineTaxability.Unknown,
            Text(element, "gstMarker"),
            Integer(element, "productId"),
            Boolean(element, "isDrinkContainer") ?? false);
    }

    private static JsonElement? Property(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value : null;

    /// <summary>
    /// The field as non-blank text, or <see langword="null"/>. A number is not read as text: a figure
    /// arriving where a description belongs is a contract mismatch, not a label.
    /// </summary>
    private static string? Text(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } text
        && text.GetString() is { } value
        && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;

    /// <summary>
    /// The field as money: a JSON number, read as a <see cref="decimal"/> so it keeps the exact scale
    /// the payload printed, and never negative - a negative amount on a receipt line is a discount,
    /// which has its own field.
    /// </summary>
    private static decimal? Money(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetDecimal(out var value)
            ? value
            : null;

    /// <summary>The field as a non-negative whole count, or <see langword="null"/>.</summary>
    private static int? Count(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetInt32(out var value)
        && value >= 0
            ? value
            : null;

    /// <summary>
    /// The field as an identifier: a whole number above zero, or <see langword="null"/>. Zero and
    /// negatives are no business's product or supplier.
    /// </summary>
    private static long? Integer(JsonElement element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } number
        && number.TryGetInt64(out var value)
        && value > 0
            ? value
            : null;

    /// <summary>The field as a boolean. Only JSON <c>true</c> is true; <c>"true"</c> is not.</summary>
    private static bool? Boolean(JsonElement element, string name) =>
        Property(element, name) is { } property && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.ValueKind is JsonValueKind.True
            : null;

    /// <summary>
    /// The field as a calendar date in exactly the format the schema asks for. A date in another
    /// format, or one that is not a date, is absent rather than interpreted: a receipt read as
    /// 03/04 could be two different months.
    /// </summary>
    private static DateOnly? Date(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateOnly.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
}
