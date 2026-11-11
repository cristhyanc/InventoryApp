namespace Inventory.Domain.ReceiptScanning;

/// <summary>
/// What the receipt's own totals say about a container deposit levy (issue #1163).
/// </summary>
public enum ReceiptLevyOutcome
{
    /// <summary>
    /// The lines already add up to the printed total without the levy, so the levy is already inside
    /// the drink prices and is printed again only as a disclosure. Nothing is added to any line.
    /// </summary>
    AlreadyIncluded = 10,

    /// <summary>
    /// The lines add up to the printed total only once the levy is added, so the levy is a real extra
    /// cost of the drinks it was charged for and is shared across them.
    /// </summary>
    AddedToDrinkLines = 20,

    /// <summary>
    /// The totals match neither with the levy nor without it, or there is no drink line to share it
    /// across. No line's cost is changed and the levy stays a separate line flagged for review.
    /// </summary>
    Unresolved = 30,
}

/// <summary>One drink line the levy could belong to, and how many units it delivered.</summary>
/// <param name="LineNumber">
/// The caller's own index for the line, returned unchanged in the allocation so the caller does not
/// have to match amounts back to lines.
/// </param>
/// <param name="Units">
/// How many units the line delivered. The levy is charged per container, so units are the share.
/// </param>
public sealed record ReceiptLevyDrinkLine(int LineNumber, int Units);

/// <summary>How much of the levy one drink line carries.</summary>
public sealed record ReceiptLevyAllocation(int LineNumber, decimal Amount);

/// <summary>The reconciliation's answer.</summary>
/// <param name="Outcome">What the totals said.</param>
/// <param name="Allocations">
/// Empty unless <paramref name="Outcome"/> is <see cref="ReceiptLevyOutcome.AddedToDrinkLines"/>;
/// otherwise one entry per drink line, summing exactly to the levy.
/// </param>
public sealed record ReceiptLevyReconciliation(
    ReceiptLevyOutcome Outcome,
    IReadOnlyList<ReceiptLevyAllocation> Allocations);

/// <summary>
/// Whether a printed container deposit levy is already inside the drink prices, and who pays it if
/// it is not (issue #1163). A pure function over amounts the caller has already put on one GST
/// basis.
///
/// <para><strong>A levy is part of the cost of the drinks it was charged for</strong>, not a delivery
/// charge and not a line of its own. JB Metro's online order shows why the question has to be asked
/// of the totals rather than assumed either way: it includes the levy in each drink's line price
/// <em>and</em> prints it separately, so lines + shipping already equal the grand total and adding
/// the levy again would overstate every one of those drinks' costs.</para>
///
/// <para><strong>The comparison is made on one basis, and the caller makes it one.</strong> Each
/// comparable amount is passed as a <see cref="decimal"/>? already converted onto the printed total's
/// basis; a <see langword="null"/> is a line that could not be converted - a mixed-GST receipt - and
/// is by itself enough to leave the question unresolved. Guessing there would move money between
/// lines on the strength of an arithmetic coincidence.</para>
///
/// <para><strong>Unresolved changes nothing.</strong> It is the answer for a receipt with a discount
/// the reader missed, a line it could not read, or mixed GST bases, and for a levy with no drink line
/// to belong to. The levy then stays its own flagged line, which is visible and correctable, rather
/// than being silently spread over whatever happened to be on the receipt.</para>
/// </summary>
public static class ReceiptLevyPolicy
{
    /// <summary>
    /// How far the lines may be from the printed total and still count as matching it: one cent, for
    /// the rounding a converted amount can carry. Anything larger is a real difference and the
    /// question is left unresolved.
    /// </summary>
    public const decimal Tolerance = 0.01m;

    /// <summary>The answer when nothing is changed, so an unresolved result has one shape.</summary>
    public static ReceiptLevyReconciliation Unresolved { get; } =
        new(ReceiptLevyOutcome.Unresolved, []);

    /// <summary>
    /// Decides the levy, and shares it when it has to be shared.
    /// </summary>
    /// <param name="levyAmount">
    /// The printed levy, on the comparison basis, as a positive amount. Zero or less is nothing to
    /// reconcile and is reported as <see cref="ReceiptLevyOutcome.Unresolved"/> rather than silently
    /// treated as included.
    /// </param>
    /// <param name="printedTotal">
    /// The total printed on the receipt, on the comparison basis, or <see langword="null"/> when none
    /// could be read - with no total there is nothing to compare against.
    /// </param>
    /// <param name="comparableAmounts">
    /// Every amount that makes up that total on the comparison basis: the stock lines, the
    /// <see cref="ReceiptLineKind.Other"/> lines and the delivery and package charges. A
    /// <see langword="null"/> entry is an amount that could not be converted.
    /// </param>
    /// <param name="drinkLines">
    /// The stock lines the receipt's own container marks say the levy was charged for.
    /// </param>
    public static ReceiptLevyReconciliation Reconcile(
        decimal levyAmount,
        decimal? printedTotal,
        IReadOnlyList<decimal?> comparableAmounts,
        IReadOnlyList<ReceiptLevyDrinkLine> drinkLines)
    {
        ArgumentNullException.ThrowIfNull(comparableAmounts);
        ArgumentNullException.ThrowIfNull(drinkLines);

        if (levyAmount <= 0m || printedTotal is not { } total)
        {
            return Unresolved;
        }

        // One unconvertible amount is enough. A mixed-GST receipt cannot be compared on one basis at
        // all, and a sum that silently left an amount out would match whichever reading the gap
        // happened to fit.
        if (comparableAmounts.Any(amount => amount is null))
        {
            return Unresolved;
        }

        var lines = comparableAmounts.Sum(amount => amount!.Value);

        // Asked in this order on purpose: a receipt both readings would fit keeps the one that
        // changes no line cost.
        if (Matches(lines, total))
        {
            return new ReceiptLevyReconciliation(ReceiptLevyOutcome.AlreadyIncluded, []);
        }

        if (!Matches(lines + levyAmount, total))
        {
            return Unresolved;
        }

        var allocations = Share(levyAmount, drinkLines);

        // The totals say the levy is a real extra cost, and there is still nobody to charge it to:
        // no line is marked as a drink, or no drink line has a readable unit count. It stays its own
        // flagged line rather than being spread over whatever else is on the receipt.
        return allocations.Count == 0
            ? Unresolved
            : new ReceiptLevyReconciliation(ReceiptLevyOutcome.AddedToDrinkLines, allocations);
    }

    /// <summary>
    /// How a levy is shared across drink lines: by number of units, to the cent, with the remainder
    /// cents on the last line so the allocations sum to exactly the levy.
    ///
    /// <para>Empty - nothing to share - when there is no drink line, or when no drink line has a
    /// readable unit count. A levy spread over a count of nothing is not an allocation.</para>
    /// </summary>
    public static IReadOnlyList<ReceiptLevyAllocation> Share(
        decimal levyAmount,
        IReadOnlyList<ReceiptLevyDrinkLine> drinkLines)
    {
        ArgumentNullException.ThrowIfNull(drinkLines);

        var shareable = drinkLines.Where(line => line.Units > 0).ToList();
        var totalUnits = shareable.Sum(line => (long)line.Units);
        if (levyAmount <= 0m || totalUnits == 0)
        {
            return [];
        }

        // Counted in cents, because a levy divided in dollars does not add back up to itself: the
        // allocations have to sum to exactly the amount the receipt printed.
        var levyCents = (long)decimal.Round(levyAmount * 100m, 0, MidpointRounding.AwayFromZero);
        var allocations = new List<ReceiptLevyAllocation>(shareable.Count);
        var allocatedCents = 0L;

        for (var index = 0; index < shareable.Count; index++)
        {
            var line = shareable[index];
            var cents = index == shareable.Count - 1
                ? levyCents - allocatedCents
                : levyCents * line.Units / totalUnits;

            allocatedCents += cents;
            allocations.Add(new ReceiptLevyAllocation(line.LineNumber, cents / 100m));
        }

        return allocations;
    }

    /// <summary>
    /// Whether two amounts are the same money, within the one cent a converted amount can carry.
    /// </summary>
    private static bool Matches(decimal amount, decimal total) =>
        Math.Abs(amount - total) <= Tolerance;
}
