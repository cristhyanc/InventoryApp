using System.Text.RegularExpressions;

namespace Inventory.Domain.Nayax;

/// <summary>Nayax Lynx machine-alert event codes this feature reacts to (issue #183).</summary>
public static class NayaxMachineAlertEventCodes
{
    /// <summary>Event 501, "Stock Adjust for Machine".</summary>
    public const int StockAdjustForMachine = 501;
}

/// <summary>
/// A successfully parsed <c>Product MDB: &lt;mdb&gt; | &lt;product name&gt; | &lt;signed quantity&gt;</c>
/// segment of a Nayax Event 501 alert's <c>EventData</c>.
/// </summary>
public readonly record struct ParsedNayaxStockAdjustment(int Mdb, string ProductName, int SignedQuantity);

/// <summary>
/// Deterministic, EF/HTTP-free parsing of a Nayax Event 501 "Stock Adjust for Machine" alert's raw
/// <c>EventData</c> text (issue #183). The supported form is
/// <c>Product MDB: &lt;mdb&gt; | &lt;product name&gt; | &lt;signed quantity&gt;</c>, optionally preceded by a free-text
/// employee/user-name prefix such as <c>Eunhye Chung 'Adjusted Stock, </c>: the parser anchors on the
/// <c>Product MDB:</c> marker rather than the prefix, so it does not depend on that prefix's presence,
/// format, or content. Anything that does not match the supported form fails to parse and must be
/// retained as Needs Review with no inventory movement; this type never guesses a value.
/// </summary>
public static class NayaxStockAdjustmentEventParser
{
    private static readonly Regex SegmentPattern = new(
        @"Product\s+MDB:\s*(?<mdb>\d+)\s*\|\s*(?<name>.+?)\s*\|\s*(?<qty>-?\d+)\s*$",
        RegexOptions.Compiled | RegexOptions.Singleline);

    public static bool TryParse(string? eventData, out ParsedNayaxStockAdjustment parsed, out string? failureReason)
    {
        parsed = default;

        if (string.IsNullOrWhiteSpace(eventData))
        {
            failureReason = "EventData is empty.";
            return false;
        }

        var match = SegmentPattern.Match(eventData.Trim());
        if (!match.Success)
        {
            failureReason =
                "EventData does not contain the supported 'Product MDB: <mdb> | <product name> | <signed quantity>' form.";
            return false;
        }

        if (!int.TryParse(match.Groups["mdb"].Value, out var mdb))
        {
            failureReason = "MDB code is not a valid integer.";
            return false;
        }

        if (!int.TryParse(match.Groups["qty"].Value, out var quantity))
        {
            failureReason = "Quantity is not a valid integer.";
            return false;
        }

        var name = match.Groups["name"].Value.Trim();
        if (name.Length == 0)
        {
            failureReason = "Product name is empty.";
            return false;
        }

        failureReason = null;
        parsed = new ParsedNayaxStockAdjustment(mdb, name, quantity);
        return true;
    }
}
