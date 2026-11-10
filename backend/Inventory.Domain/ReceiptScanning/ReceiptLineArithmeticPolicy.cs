namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// What one scanned line costs, how many units it is, and what one unit cost (issue #1163).
///
/// <para>Every value is nullable because a receipt is allowed to be incomplete, and a figure this
/// application cannot derive must come back absent rather than guessed:
/// <see cref="ReceiptLineArithmetic.Missing"/> says which ones, so the purchase form can ask for
/// exactly those.</para>
/// </summary>
/// <param name="Amount">
/// The GST-inclusive amount the line will be recorded at - the printed total less its attached
/// discounts, converted onto this application's GST-inclusive convention - or
/// <see langword="null"/> when it cannot be derived.
/// </param>
/// <param name="Units">
/// <c>packs x unitsPerPack</c>, or <see langword="null"/> when either is absent. Never <c>1</c> by
/// default: a carton whose pack size is not printed is an unknown pack size, not a single item.
/// </param>
/// <param name="UnitCost">
/// <c>Amount / Units</c>, or <see langword="null"/> when either is absent.
/// </param>
/// <param name="Missing">Which values the line could not be completed with, in a stable order.</param>
public sealed record ReceiptLineArithmetic(
    decimal? Amount,
    int? Units,
    decimal? UnitCost,
    IReadOnlyList<ReceiptLineMissingValue> Missing);

/// <summary>
/// The one authoritative arithmetic of a scanned receipt line (issue #1163). It is a pure function:
/// no clock, no configuration, no model, no HTTP.
///
/// <para><strong>The amount is GST-inclusive, because every purchase amount in this application
/// is</strong> (AGENTS.md § Purchase GST classification). A price read from an ex-GST column is
/// therefore converted here, once, so the unit cost a draft offers is the unit cost the purchase
/// would hold; a price whose basis the receipt does not state is not converted at all, and a line
/// with no derivable amount simply has none.</para>
///
/// <para><strong>It classifies nothing.</strong> <see cref="ReceiptLineTaxability"/> is what the
/// receipt's own GST marker claims and is used for one thing only - deciding whether an ex-GST price
/// needs 10% adding. A purchase's GST classification and its provenance remain
/// <c>Inventory.Domain.Gst.GstClassification</c>'s, decided by a person or by an explicit rule, and
/// nothing here writes or implies one.</para>
///
/// <para><strong>The unit cost is deliberately not rounded.</strong> It is the plain quotient, which
/// is exactly what the purchase form submits today (<c>unitCostFromLineTotal</c> in
/// <c>frontend/inventory-app/src/app/components/purchases/purchase-line-price.ts</c>) and what makes
/// the saved line's own <c>round(Quantity * UnitCost, 2)</c> come back to the amount the receipt
/// printed. A quotient rounded to the four decimals the form displays would not: $100.00 over 300
/// units is $0.3333... per unit, and 300 x $0.3333 is $99.99.</para>
/// </summary>
public static class ReceiptLineArithmeticPolicy
{
    /// <summary>
    /// The GST rate an ex-GST price is converted with. The same 10% the rest of the application
    /// works in (<c>PurchaseGstPolicy</c> takes a GST-inclusive amount's GST as <c>amount / 11</c>,
    /// which is this rate's inverse).
    /// </summary>
    public const decimal GstRate = 0.10m;

    /// <summary>
    /// Currency rounding, to the cent with halves away from zero - the rounding every purchase
    /// amount in this application uses (<c>PurchaseGstPolicy</c>), rather than the banker's rounding
    /// <see cref="Math.Round(decimal, int)"/> would default to.
    /// </summary>
    public static decimal RoundToCents(decimal amount) =>
        Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// <paramref name="amount"/> on this application's GST-inclusive convention, or
    /// <see langword="null"/> when the receipt does not say enough to convert it: an unstated basis,
    /// or an ex-GST price whose taxability is unstated. A GST-free ex-GST price is already inclusive
    /// of the no GST it carries.
    /// </summary>
    public static decimal? ToGstInclusive(
        decimal amount,
        ReceiptPriceGstBasis basis,
        ReceiptLineTaxability taxability) => basis switch
        {
            ReceiptPriceGstBasis.IncludesGst => RoundToCents(amount),
            ReceiptPriceGstBasis.ExcludesGst => taxability switch
            {
                ReceiptLineTaxability.Taxable => RoundToCents(amount * (1m + GstRate)),
                ReceiptLineTaxability.GstFree => RoundToCents(amount),
                _ => null,
            },
            _ => null,
        };

    /// <summary>
    /// <paramref name="amount"/> on the GST-exclusive basis, or <see langword="null"/> on the same
    /// terms as <see cref="ToGstInclusive"/>. It exists for the levy reconciliation, which has to
    /// compare every amount on one basis and is given a printed total that is often the ex-GST one.
    /// </summary>
    public static decimal? ToGstExclusive(
        decimal amount,
        ReceiptPriceGstBasis basis,
        ReceiptLineTaxability taxability) => basis switch
        {
            ReceiptPriceGstBasis.ExcludesGst => taxability is ReceiptLineTaxability.Unknown
                ? null
                : RoundToCents(amount),
            ReceiptPriceGstBasis.IncludesGst => taxability switch
            {
                ReceiptLineTaxability.Taxable => RoundToCents(amount / (1m + GstRate)),
                ReceiptLineTaxability.GstFree => RoundToCents(amount),
                _ => null,
            },
            _ => null,
        };

    /// <summary>
    /// How many units the line delivered: <c>packs x unitsPerPack</c>, with both required to be
    /// whole numbers above zero, or <see langword="null"/> when either is absent or unusable.
    /// </summary>
    public static int? Units(int? packs, int? unitsPerPack) =>
        packs is > 0 && unitsPerPack is > 0 ? packs * unitsPerPack : null;

    /// <summary>
    /// What one unit cost: the unrounded quotient, or <see langword="null"/> when there is no amount
    /// or no unit count to divide by.
    /// </summary>
    public static decimal? UnitCost(decimal? amount, int? units) =>
        amount is { } value && units is > 0 ? value / units.Value : null;

    /// <summary>
    /// The whole line, evaluated once.
    /// </summary>
    /// <param name="printedLineTotal">
    /// The line total as printed, before discounts. <see langword="null"/> when the receipt printed
    /// none that could be read.
    /// </param>
    /// <param name="discountTotal">
    /// The discounts attached to this line, as a positive amount to subtract. A receipt prints a
    /// discount as a negative line under the item it belongs to; the caller sums them and passes
    /// their magnitude.
    /// </param>
    /// <param name="packs">How many packs were delivered - never ordered.</param>
    /// <param name="unitsPerPack">How many units one pack holds, as printed.</param>
    /// <param name="basis">Whether the printed price includes GST.</param>
    /// <param name="taxability">Whether the receipt marks the line as taxable.</param>
    public static ReceiptLineArithmetic Evaluate(
        decimal? printedLineTotal,
        decimal discountTotal,
        int? packs,
        int? unitsPerPack,
        ReceiptPriceGstBasis basis,
        ReceiptLineTaxability taxability)
    {
        var missing = new List<ReceiptLineMissingValue>();

        if (packs is not > 0)
        {
            missing.Add(ReceiptLineMissingValue.Packs);
        }

        if (unitsPerPack is not > 0)
        {
            missing.Add(ReceiptLineMissingValue.UnitsPerPack);
        }

        var net = DiscountedTotal(printedLineTotal, discountTotal);
        var amount = net is { } discounted ? ToGstInclusive(discounted, basis, taxability) : null;

        if (net is null)
        {
            // No usable printed total: either none was read, or the discounts attached to it exceed
            // it, which is not a reading of a receipt either.
            missing.Add(ReceiptLineMissingValue.LineTotal);
        }
        else if (amount is null)
        {
            // The total was printed and still produced no amount, so the GST basis is why: either
            // the receipt did not say, or it said "ex GST" without saying whether the line is
            // taxable. Reported as the field the operator has to answer rather than as a missing
            // total, which the receipt did print.
            missing.Add(basis is ReceiptPriceGstBasis.Unknown
                ? ReceiptLineMissingValue.PriceGstBasis
                : ReceiptLineMissingValue.Taxability);
        }

        var units = Units(packs, unitsPerPack);
        var unitCost = UnitCost(amount, units);

        if (unitCost is null)
        {
            missing.Add(ReceiptLineMissingValue.UnitCost);
        }

        return new ReceiptLineArithmetic(amount, units, unitCost, missing);
    }

    /// <summary>
    /// The printed total less its attached discounts, on the line's own GST basis, or
    /// <see langword="null"/> when there was no printed total, or when the discounts exceed it - a
    /// line that cost less than nothing is not a reading of a receipt, and inventing <c>0</c> for one
    /// would record stock as free.
    ///
    /// <para>Public because the levy reconciliation compares this figure, before any GST conversion,
    /// against the printed total: converting an amount and converting it back can move a cent, and
    /// over a receipt's worth of lines those cents are exactly what the comparison is looking for.</para>
    /// </summary>
    public static decimal? DiscountedTotal(decimal? printedLineTotal, decimal discountTotal) =>
        printedLineTotal is { } total && total - discountTotal >= 0m
            ? total - discountTotal
            : null;
}
