using System.Text.Json;

namespace Inventory.Application.NayaxSqs;

/// <summary>
/// Reads one allow-listed field of a Nayax SQS transaction message, answering a value only for the
/// JSON kind the published contract documents for it (issue #1102).
///
/// <para>This is the whole reason the message models declare <see cref="JsonElement"/> members. A
/// field of the wrong type in a financial payload must become a stated outcome with a reason code -
/// <c>"SeValue": "1.00"</c> is an invalid message - and never a thrown
/// <see cref="JsonException"/> whose text quotes the position inside a consumer's card transaction,
/// and never a silent default: binding a malformed amount to <c>0</c> would hand the sale paths a
/// zero-value transaction they would import as cancelled, and binding a malformed timestamp to
/// <see cref="DateTime.MinValue"/> would invent an instant the payload never stated.</para>
///
/// <para>Each reader is deliberately strict about the kind rather than coercing: a quoted number is
/// not a number, a number is not a timestamp, and only JSON <c>true</c> is true. A value this type
/// cannot read is reported as unreadable, which the parser turns into the reason code for that
/// field.</para>
/// </summary>
internal static class NayaxSqsJsonValues
{
    /// <summary>
    /// The field as a 64-bit integer, or <see langword="null"/> when it is absent, null, not a JSON
    /// number, fractional, or outside the range. Nayax documents the two root identifiers as
    /// integers, and an identifier that does not round-trip is not an identifier.
    /// </summary>
    public static long? AsInt64(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt64(out var value)
            ? value
            : null;

    /// <summary>The field as a 32-bit integer, on the same terms as <see cref="AsInt64"/>.</summary>
    public static int? AsInt32(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out var value)
            ? value
            : null;

    /// <summary>
    /// The field as a decimal, or <see langword="null"/> when it is absent, null or not a JSON
    /// number. A number is read as a decimal rather than a double so a money value keeps the exact
    /// scale the payload printed.
    /// </summary>
    public static decimal? AsDecimal(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Number } number && number.TryGetDecimal(out var value)
            ? value
            : null;

    /// <summary>
    /// The field as text, or <see langword="null"/> when it is absent, null or not a JSON string.
    /// A number is not read as text: an identifier or amount arriving where a description belongs is
    /// a contract mismatch rather than a label.
    /// </summary>
    public static string? AsString(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>
    /// The field as a date-time with no interpretation of its offset, read exactly as
    /// <c>System.Text.Json</c> reads a <see cref="DateTime"/> - which is how the identically named
    /// <c>lastSales</c> field is bound today. Used only for the machine's local wall-clock time,
    /// which is a raw imported fact; a field documented as GMT goes through
    /// <c>NayaxGmtTimestamp</c> instead.
    /// </summary>
    public static DateTime? AsDateTime(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.String } text && text.TryGetDateTime(out var value)
            ? value
            : null;

    /// <summary>
    /// <see langword="true"/> only for JSON <c>true</c>. Every flag in the message is documented as
    /// a boolean, and an absent, null or unreadable flag means "not set" - the flags this is read
    /// for (<c>Void</c>, <c>Settlment Failed</c>, the revalue pair, <c>Is Multivend</c>) each divert
    /// a message away from being saved, so reading anything other than <c>true</c> as true would
    /// discard sales.
    /// </summary>
    public static bool IsTrue(JsonElement? element) => element is { ValueKind: JsonValueKind.True };

    /// <summary>
    /// <see langword="true"/> when the field is not in the document at all or is JSON <c>null</c> -
    /// which the published example message's <c>Cancel Type</c> is on a settled transaction, so the
    /// two have to mean the same thing. A present but unreadable value is <em>not</em> absent.
    /// </summary>
    public static bool IsAbsent(JsonElement? element) =>
        element is null or { ValueKind: JsonValueKind.Null };

    /// <summary>
    /// The field as a JSON object, or <see langword="null"/> when it is absent, null or any other
    /// kind - so a <c>Data</c> that is not an object is a stated invalid outcome rather than a
    /// binding failure.
    /// </summary>
    public static JsonElement? AsObject(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value ? value : null;

    /// <summary>
    /// The field as a JSON array holding at least one item, or <see langword="null"/> when it is
    /// absent, null, not an array, or empty - the four shapes rule 1 treats alike, because a
    /// transaction with no product says nothing about what was vended.
    /// </summary>
    public static JsonElement? AsNonEmptyArray(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Array } array && array.GetArrayLength() > 0
            ? array
            : null;

    /// <summary>
    /// The first entry of the products array, or <see langword="null"/> when that entry is not an
    /// object. An entry that is not an object carries none of the three product fields and the
    /// message is still a sale: rule 1 asks only that the array hold an item, and a product name
    /// this application cannot read is the same situation as one the machine never reported.
    /// </summary>
    public static NayaxSqsTransactionProduct? FirstProduct(JsonElement? products)
    {
        if (products is not { ValueKind: JsonValueKind.Array } array || array.GetArrayLength() == 0)
            return null;

        var first = array[0];
        return first.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<NayaxSqsTransactionProduct>(first)
            : null;
    }
}
