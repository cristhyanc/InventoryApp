using System.Globalization;
using System.Text;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// How one cell of an uploaded Nayax sales export becomes a value, and how one CSV line becomes
/// cells (issue #1304).
///
/// <para><strong>Every rule here was already in <see cref="ClosedXmlNayaxSalesWorkbookReader"/> and
/// is unchanged.</strong> It is a type of its own because a second reader now exists:
/// <see cref="StreamingNayaxSalesHistoryCsvReader"/> reads a long history row by row and must read
/// each cell exactly as the uploaded import reads it, or the two imports would disagree about what a
/// file says. The workbook reader keeps its own cell handling for the two things only a worksheet
/// has - a date-typed cell and a numeric cell - and asks this type for everything that is text,
/// which is every cell of a CSV.</para>
///
/// <para>The conversions imported financial data depends on are therefore stated once: headers
/// matched on letters and digits only, numbers read with
/// <see cref="CultureInfo.InvariantCulture"/> so an unparsable number stays <c>null</c> rather than
/// becoming zero, the three-way reading of the status and the amount (issue #1298), the export's own
/// <c>d/M/yyyy h:mm:ss tt</c> instant text, and the GMT column's own offset-aware parsing
/// (issue #380).</para>
/// </summary>
internal static class NayaxSalesCells
{
    /// <summary>
    /// A header or cell name reduced to its letters and digits, lower-cased, so
    /// <c>Product Cost Price</c>, <c>ProductCostPrice</c> and <c>product_cost_price</c> are one
    /// column.
    /// </summary>
    public static string NormalizeHeader(string value) =>
        new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// One CSV line's fields, honouring quoted fields and doubled quotes. A line with fewer fields
    /// than the header row simply states nothing in the columns it does not reach.
    /// </summary>
    public static IReadOnlyList<string> ParseLine(string line)
    {
        var values = new List<string>();
        var value = new StringBuilder();
        var quoted = false;

        var index = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '"')
            {
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index += 2;
                    continue;
                }

                quoted = !quoted;
            }
            else if (character == ',' && !quoted)
            {
                values.Add(value.ToString());
                value.Clear();
            }
            else
            {
                value.Append(character);
            }

            index++;
        }

        values.Add(value.ToString());
        return values;
    }

    /// <summary>
    /// What a cell states, or <c>null</c> when it states nothing (issue #1298): no column, an empty
    /// cell, or a cell holding nothing but whitespace. A cell of spaces states nothing about a
    /// transaction, exactly as an empty one does, so it must not reach the import as an empty
    /// machine name, payment method or product name - which the update would then write over a
    /// stored one.
    /// </summary>
    public static string? Text(string? value)
    {
        if (value is null) return null;
        var text = value.Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>An identifier the file always has to carry; <c>0</c> when absent or unparsable.</summary>
    public static long Long(string? text) => long.TryParse(Text(text), out var value) ? value : 0;

    /// <summary>A nullable identifier: <c>null</c> when absent or unparsable.</summary>
    public static long? NullableLong(string? text) =>
        long.TryParse(Text(text), out var value) ? value : null;

    /// <summary>A decimal read with invariant culture; <c>null</c> when absent or unparsable.</summary>
    public static decimal? Decimal(string? text) =>
        decimal.TryParse(Text(text), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    /// <summary>
    /// The transaction-status cell, together with what the export carried there (issue #1298): no
    /// column or a blank cell, a readable status, or a non-blank value that is not one. The third
    /// case must never read as the first, or a status nobody could read would look exactly like a
    /// file that said nothing about the transaction.
    /// </summary>
    public static (int? Value, NayaxSalesCellInput Input) IntInput(string? raw)
    {
        var text = Text(raw);
        if (string.IsNullOrWhiteSpace(text))
            return (null, NayaxSalesCellInput.Absent);
        return int.TryParse(text, out var value)
            ? (value, NayaxSalesCellInput.Valid)
            : (null, NayaxSalesCellInput.Malformed);
    }

    /// <summary>
    /// The settlement-value cell, read the same three ways (issue #1298). An explicit <c>0</c> is a
    /// value the file states; a blank cell or a missing column is not, and is never defaulted to
    /// <c>0</c>.
    /// </summary>
    public static (decimal? Value, NayaxSalesCellInput Input) DecimalInput(string? raw)
    {
        var text = Text(raw);
        if (string.IsNullOrWhiteSpace(text))
            return (null, NayaxSalesCellInput.Absent);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? (value, NayaxSalesCellInput.Valid)
            : (null, NayaxSalesCellInput.Malformed);
    }

    /// <summary>
    /// The export's own <c>d/M/yyyy h:mm:ss tt</c> instant text, which is the only text form the
    /// machine-local <c>MachineAuthorizationTime</c> column is read in: an unrecognised instant
    /// stays <c>null</c> so the row is skipped rather than imported at a guessed time, and
    /// offset-aware parsing is deliberately not applied there, where an offset would contradict
    /// what the field means in the Nayax contract.
    /// </summary>
    public static DateTime? ExportDate(string? raw)
    {
        var text = Text(raw);
        if (text is null) return null;

        return DateTime.TryParseExact(
            text, "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// A value under a column the export itself names as GMT, read as a true UTC instant
    /// (issue #380). The header is what establishes the timezone, so a value carrying no designator
    /// is taken as UTC and one carrying an explicit offset is converted to the same physical instant
    /// rather than being shifted a second time. That reading is <see cref="NayaxGmtTimestamp"/>, the
    /// one parser the live Lynx JSON boundary uses for its own documented-GMT field (issue #471), so
    /// neither path can drift towards reading an offset-free GMT value against the host's time zone.
    ///
    /// <para>The export's own <c>d/M/yyyy h:mm:ss tt</c> text form is tried first, because
    /// <see cref="CultureInfo.InvariantCulture"/> would otherwise read <c>4/10/2026 11:30:00 PM</c>
    /// as 10 April.</para>
    /// </summary>
    public static DateTime? UtcDate(string? raw)
    {
        var text = Text(raw);
        if (text is null) return null;

        if (DateTime.TryParseExact(
                text, "d/M/yyyy h:mm:ss tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact))
            return DateTime.SpecifyKind(exact, DateTimeKind.Utc);

        return NayaxGmtTimestamp.ParseInstantUtc(text);
    }
}
