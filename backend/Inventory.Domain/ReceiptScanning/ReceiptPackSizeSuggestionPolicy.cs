namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// The one rule that suggests a pack size a receipt did not print, from what the business paid for
/// that product last time (issue #1165, sub-issue E of #975). A pure function: no clock, no
/// configuration, no model, no persistence.
///
/// <para><strong>It is a suggestion and never a reading.</strong> A pack size printed on the receipt
/// is what the line carries; this rule is reached only for a line that printed none, and what it
/// answers is marked as this application's own suggestion with the purchase it came from, so the
/// operator can see why the number is there.</para>
///
/// <para><strong>The packs delivered on the line are divided out, and that is the whole reason the
/// pack count is required.</strong> The ratio is
/// <c>lineAmount / (packs x lastUnitCost)</c>: dividing by the unit cost alone would give the units
/// on the <em>whole line</em>, and the caller then multiplies by the packs again to get the line's
/// units - so a two-carton line would record twice the stock it delivered. A line whose pack count
/// could not be read therefore gets no suggestion at all rather than one that might be out by a
/// factor of the packs.</para>
///
/// <para><strong>The tolerance is a declared quarter of a unit, not a percentage</strong> (issue
/// #1165's own reasoning). A percentage band would accept almost any ratio for a large carton - a 3%
/// price rise turns 24 into 24.7, which a loose rule would round to 25 and record a 25-unit carton's
/// cost - so the rule fires only while the price is essentially unchanged, and a carton whose price
/// moved is left for the operator to answer. The value is the issue's proposed one and is stated here
/// once as <see cref="Tolerance"/>, because changing it is a decision about inventory and costing
/// rather than an implementation detail.</para>
///
/// <para><strong>The amount and the last unit cost are on the same GST basis.</strong> Every purchase
/// amount in this application is GST-inclusive (AGENTS.md § Purchase GST classification), a stored
/// <c>PurchaseItem.UnitCost</c> therefore is too, and a scanned line's amount has already been
/// converted onto that convention by <see cref="ReceiptLineArithmeticPolicy"/> before it reaches
/// here. A line whose basis the receipt did not state has no amount at all, so it cannot reach this
/// rule by another route.</para>
/// </summary>
public static class ReceiptPackSizeSuggestionPolicy
{
    /// <summary>
    /// How far the ratio may be from a whole number and still be that pack size: a quarter of a unit,
    /// inclusive. Anything further is a real difference - a price change, a different pack, a
    /// misreading - and the pack size stays unknown.
    /// </summary>
    public const decimal Tolerance = 0.25m;

    /// <summary>
    /// The largest pack size this rule will suggest. It exists because the quotient of a real amount
    /// and a vanishingly small recorded unit cost is a number no pack has ever held, and refusing it
    /// is both the honest answer and what keeps the conversion to a whole count safe.
    /// </summary>
    public const int MaxUnitsPerPack = 100_000;

    /// <summary>
    /// How many units one pack held, or <see langword="null"/> when the figures do not say so
    /// closely enough to suggest anything.
    /// </summary>
    /// <param name="lineAmount">
    /// The line's GST-inclusive amount, as the line arithmetic derived it. <see langword="null"/> or
    /// non-positive is nothing to divide.
    /// </param>
    /// <param name="packs">
    /// How many packs the line delivered. <see langword="null"/> or non-positive means no suggestion:
    /// see the type's own remarks for why that is a refusal rather than an assumption of one pack.
    /// </param>
    /// <param name="lastUnitCost">
    /// What one unit cost on the most recent recorded purchase of this product from this supplier.
    /// <see langword="null"/> or non-positive is nothing to divide by - and a recorded $0.00 line is
    /// a real row in this application, not an impossible one.
    /// </param>
    public static int? Suggest(decimal? lineAmount, int? packs, decimal? lastUnitCost)
    {
        if (lineAmount is not { } amount
            || amount <= 0m
            || packs is not > 0
            || lastUnitCost is not { } unitCost
            || unitCost <= 0m)
        {
            return null;
        }

        var ratio = amount / (packs.Value * unitCost);

        // Beyond any real pack, which is also the range where rounding to a whole count stops being
        // a meaningful question. Checked before the rounding so the arithmetic cannot overflow.
        if (ratio > MaxUnitsPerPack + Tolerance)
        {
            return null;
        }

        // Away from zero, the rounding every amount in this application uses, so x.5 is x+1 rather
        // than the nearest even number.
        var whole = Math.Round(ratio, 0, MidpointRounding.AwayFromZero);

        return whole >= 1m && Math.Abs(ratio - whole) <= Tolerance ? (int)whole : null;
    }
}
