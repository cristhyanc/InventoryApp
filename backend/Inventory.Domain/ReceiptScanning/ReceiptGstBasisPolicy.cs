namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// One printed line as far as <see cref="ReceiptGstBasisPolicy"/> needs it: its printed total, the
/// discounts attached to it, and the GST basis the reader gave its price.
/// </summary>
/// <param name="PrintedLineTotal">The line total as printed, before discounts, or <see langword="null"/>.</param>
/// <param name="DiscountTotal">The discounts attached to the line, as a positive amount.</param>
/// <param name="Basis">Whether the reader said the printed price includes GST.</param>
public sealed record ReceiptGstBasisLine(
    decimal? PrintedLineTotal,
    decimal DiscountTotal,
    ReceiptPriceGstBasis Basis);

/// <summary>
/// When a receipt's own totals settle a GST basis its lines leave unstated.
///
/// <para><strong>The case it exists for.</strong> A supermarket tax invoice prints its line prices
/// with no "inc GST" beside them and then states that its total includes GST. The reader correctly
/// reports each line's basis as unknown, and <see cref="ReceiptLineArithmeticPolicy"/> correctly
/// refuses to turn an amount of unknown basis into a cost - so every price on the receipt came back
/// blank. The totals answer the question the lines do not: when the printed lines add up, to the
/// cent, to a total the receipt says includes GST, those lines are GST-inclusive amounts.</para>
///
/// <para><strong>It is arithmetic on printed figures, not a guess.</strong> Every condition is a
/// refusal: a total that is not stated to include GST, a line with no printed total, a line the reader
/// said is ex GST, or a sum that differs from the total by any amount leaves every line exactly as the
/// reader reported it. A levy or a charge is a line like any other here, because the total counts
/// it.</para>
/// </summary>
public static class ReceiptGstBasisPolicy
{
    /// <summary>
    /// Whether the lines whose basis is <see cref="ReceiptPriceGstBasis.Unknown"/> are to be read as
    /// <see cref="ReceiptPriceGstBasis.IncludesGst"/>: <see langword="true"/> only when the receipt
    /// total is stated to include GST, there is at least one unknown line, no line is ex GST, every
    /// line has a printed total, and the lines less their discounts add up to the total to the cent.
    /// </summary>
    /// <param name="lines">Every delivered line on the receipt, of every kind.</param>
    /// <param name="receiptTotal">The total printed on the receipt.</param>
    /// <param name="receiptTotalBasis">Whether the receipt says that total includes GST.</param>
    public static bool UnknownLinesIncludeGst(
        IReadOnlyList<ReceiptGstBasisLine> lines,
        decimal? receiptTotal,
        ReceiptPriceGstBasis receiptTotalBasis)
    {
        ArgumentNullException.ThrowIfNull(lines);

        if (receiptTotalBasis is not ReceiptPriceGstBasis.IncludesGst
            || receiptTotal is not { } total
            || !lines.Any(line => line.Basis is ReceiptPriceGstBasis.Unknown)
            || lines.Any(line => line.Basis is ReceiptPriceGstBasis.ExcludesGst))
        {
            return false;
        }

        var sum = 0m;
        foreach (var line in lines)
        {
            if (ReceiptLineArithmeticPolicy.DiscountedTotal(line.PrintedLineTotal, line.DiscountTotal)
                is not { } printed)
            {
                return false;
            }

            sum += printed;
        }

        return ReceiptLineArithmeticPolicy.RoundToCents(sum) == ReceiptLineArithmeticPolicy.RoundToCents(total);
    }
}
