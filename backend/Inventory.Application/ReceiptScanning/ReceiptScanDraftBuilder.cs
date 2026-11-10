using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// Turns one validated reader answer into the draft the endpoint returns (issue #1163).
///
/// <para><strong>Every money decision is the Domain's.</strong> The line arithmetic is
/// <see cref="ReceiptLineArithmeticPolicy"/>, the levy is <see cref="ReceiptLevyPolicy"/>, the date is
/// <see cref="ReceiptPurchaseDatePolicy"/>, and the pack size is <see cref="ReceiptPackSizes"/>. What
/// this builder does is the orchestration those pure rules cannot do for themselves: group the lines
/// by kind, put every amount on the printed total's GST basis for the levy comparison, apply whatever
/// the levy policy decided, and drop every suggestion that does not name something this business
/// owns.</para>
///
/// <para><strong>A suggestion that is not this business's own is not a suggestion.</strong> The ids
/// come from a language model, so a product or supplier id that is not in the lists this business was
/// read against is dropped and the line keeps its printed text - which is the whole tenancy
/// invariant here: a scan can never surface another business's product or supplier, even as an id.
/// Only a <see cref="ReceiptLineKind.Stock"/> line may carry one at all.</para>
/// </summary>
public static class ReceiptScanDraftBuilder
{
    /// <summary>
    /// The draft <paramref name="payload"/> describes, or <see langword="null"/> when nothing in it
    /// was usable - no line survived validation - which the caller reports as an unreadable receipt.
    /// </summary>
    /// <param name="payload">The reader's validated answer.</param>
    /// <param name="productIds">Every product id this business owns, as the scan was read against.</param>
    /// <param name="supplierIds">Every supplier id this business owns.</param>
    public static ReceiptScanDraft? Build(
        ReceiptScanPayload payload,
        IReadOnlySet<long> productIds,
        IReadOnlySet<int> supplierIds)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(productIds);
        ArgumentNullException.ThrowIfNull(supplierIds);

        // A line with nothing delivered is not a line of this purchase (issue #1163): the reader is
        // told to leave it out, and one that arrives anyway is left out here, which makes the rule
        // the application's rather than the prompt's.
        var lines = payload.Lines.Where(line => line.DeliveredPacks is not 0).ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var evaluated = lines.Select((line, index) => new EvaluatedLine(index, line, Evaluate(line))).ToList();

        var charges = evaluated.Where(line => line.Line.Kind is ReceiptLineKind.Delivery).ToList();
        var packaging = evaluated.Where(line => line.Line.Kind is ReceiptLineKind.Package).ToList();
        var levies = evaluated.Where(line => line.Line.Kind is ReceiptLineKind.Levy).ToList();
        var purchaseLines = evaluated
            .Where(line => line.Line.Kind is ReceiptLineKind.Stock or ReceiptLineKind.Other)
            .ToList();

        var levy = Reconcile(payload, purchaseLines, charges.Concat(packaging).ToList(), levies);

        var draftLines = purchaseLines
            .Select(line => ToDraftLine(line, levy.ShareOf(line.Index), productIds))
            .Concat(levy.UnresolvedLines.Select(line => ToLevyLine(line)))
            .ToList();

        return new ReceiptScanDraft(
            Read(payload.SupplierName),
            payload.SupplierId is { } supplierId
                && supplierId <= int.MaxValue
                && supplierIds.Contains((int)supplierId)
                ? ReceiptScanValue<int>.Suggested((int)supplierId)
                : null,
            ReceiptPurchaseDatePolicy.Choose(payload.DeliveryDate, payload.ReceiptDate, payload.OrderDate)
                is { } purchaseDate
                ? ReceiptScanValue<DateOnly>.Read(purchaseDate)
                : null,
            Read(payload.InvoiceNumber),
            Read(payload.ReceiptTotal),
            Read(payload.GstTotal),
            // Suggested rather than read: a charge the receipt printed ex GST has been converted onto
            // this application's GST-inclusive convention, and several printed lines may have been
            // summed into one figure, so neither is a quotation.
            ChargeTotal(charges),
            ChargeTotal(packaging),
            draftLines);
    }

    private static ReceiptLineArithmetic Evaluate(ReceiptScanPayloadLine line) =>
        ReceiptLineArithmeticPolicy.Evaluate(
            line.LineTotal,
            line.DiscountTotal,
            line.DeliveredPacks,
            line.UnitsPerPack,
            line.PriceBasis,
            line.Taxability);

    /// <summary>
    /// What the receipt's totals say about its levy lines, and what each purchase line therefore
    /// carries.
    ///
    /// <para>Several printed levy lines are reconciled as one amount, because the question the totals
    /// answer is about the receipt rather than about a line: a receipt that prints a 10c levy per
    /// drink line has one levy, printed several times.</para>
    /// </summary>
    private static LevyDecision Reconcile(
        ReceiptScanPayload payload,
        IReadOnlyList<EvaluatedLine> purchaseLines,
        IReadOnlyList<EvaluatedLine> charges,
        IReadOnlyList<EvaluatedLine> levies)
    {
        if (levies.Count == 0)
        {
            return LevyDecision.None;
        }

        // The comparison is made on the basis the receipt printed its own total in, so the total
        // itself needs no conversion and cannot be the thing that is converted wrongly.
        var basis = payload.ReceiptTotalBasis;
        var levyAmount = levies.Sum(line => OnBasis(line, basis) ?? 0m);
        var levyIsComparable = levies.All(line => OnBasis(line, basis) is not null);

        var comparable = purchaseLines.Concat(charges)
            .Select(line => OnBasis(line, basis))
            .ToList();

        var drinkLines = purchaseLines
            .Where(line => line.Line.Kind is ReceiptLineKind.Stock && line.Line.IsDrinkContainer)
            .Select(line => new ReceiptLevyDrinkLine(line.Index, line.Arithmetic.Units ?? 0))
            .ToList();

        var reconciliation = levyIsComparable
            ? ReceiptLevyPolicy.Reconcile(levyAmount, payload.ReceiptTotal, comparable, drinkLines)
            : ReceiptLevyPolicy.Unresolved;

        return reconciliation.Outcome switch
        {
            ReceiptLevyOutcome.AddedToDrinkLines => new LevyDecision(reconciliation.Allocations, []),
            // Already included: the drink prices carry it, so nothing is added and the levy line is
            // not returned either - it is not a cost of its own.
            ReceiptLevyOutcome.AlreadyIncluded => LevyDecision.None,
            _ => new LevyDecision([], levies),
        };
    }

    /// <summary>
    /// One line's amount on <paramref name="basis"/>, or <see langword="null"/> when the receipt did
    /// not say enough to convert it - which is what makes a mixed-GST receipt unresolvable rather
    /// than wrongly reconciled.
    /// </summary>
    private static decimal? OnBasis(EvaluatedLine line, ReceiptPriceGstBasis basis)
    {
        // The printed, discounted amount on the line's own basis - never the converted one, because
        // converting an amount and converting it back can move a cent, and over a receipt's worth of
        // lines those cents are what the comparison is looking for.
        if (ReceiptLineArithmeticPolicy.DiscountedTotal(line.Line.LineTotal, line.Line.DiscountTotal)
            is not { } printed)
        {
            return null;
        }

        if (basis == line.Line.PriceBasis && basis is not ReceiptPriceGstBasis.Unknown)
        {
            return ReceiptLineArithmeticPolicy.RoundToCents(printed);
        }

        return basis switch
        {
            ReceiptPriceGstBasis.IncludesGst => ReceiptLineArithmeticPolicy.ToGstInclusive(
                printed, line.Line.PriceBasis, line.Line.Taxability),
            ReceiptPriceGstBasis.ExcludesGst => ReceiptLineArithmeticPolicy.ToGstExclusive(
                printed, line.Line.PriceBasis, line.Line.Taxability),
            _ => null,
        };
    }

    /// <summary>
    /// The delivery or package charge the draft carries: the GST-inclusive sum of those lines, or
    /// <see langword="null"/> when there are none, or when one of them could not be converted onto
    /// the GST-inclusive convention. A charge that might be 10% wrong is worse than one the operator
    /// is asked for.
    /// </summary>
    private static ReceiptScanValue<decimal>? ChargeTotal(IReadOnlyList<EvaluatedLine> charges) =>
        charges.Count > 0 && charges.All(line => line.Arithmetic.Amount is not null)
            ? ReceiptScanValue<decimal>.Suggested(charges.Sum(line => line.Arithmetic.Amount!.Value))
            : null;

    private static ReceiptScanLine ToDraftLine(
        EvaluatedLine line,
        decimal levyShare,
        IReadOnlySet<long> productIds)
    {
        var amount = line.Arithmetic.Amount is { } value
            ? ReceiptLineArithmeticPolicy.RoundToCents(value + levyShare)
            : (decimal?)null;
        var unitCost = ReceiptLineArithmeticPolicy.UnitCost(amount, line.Arithmetic.Units);

        return new ReceiptScanLine(
            line.Line.Kind,
            ReceiptScanValue<string>.Read(line.Line.Text),
            Read(line.Line.DeliveredPacks),
            Read(line.Line.UnitsPerPack),
            // Suggested: the units are this application's product of two values it read, not a
            // figure the receipt printed.
            Suggested(line.Arithmetic.Units),
            amount is { } total
                // A line whose amount the receipt printed and this application did not change is a
                // quotation; one carrying a levy share, a discount or a GST conversion is not.
                ? (levyShare == 0m && line.Line.DiscountTotal == 0m
                    && line.Line.PriceBasis is ReceiptPriceGstBasis.IncludesGst
                        ? ReceiptScanValue<decimal>.Read(total)
                        : ReceiptScanValue<decimal>.Suggested(total))
                : null,
            Suggested(unitCost),
            // Only a stock line carries a product suggestion, and only one this business owns.
            line.Line.Kind is ReceiptLineKind.Stock
                && line.Line.ProductId is { } productId
                && productIds.Contains(productId)
                    ? ReceiptScanValue<long>.Suggested(productId)
                    : null,
            new ReceiptLineGstHint(line.Line.PriceBasis, line.Line.Taxability, line.Line.GstMarker),
            LevyUnresolved: false,
            // A levy share changes what the line costs, never whether it has a cost at all - it is
            // only ever allocated to a line that already had an amount and a unit count - so the
            // arithmetic's own list of what is still missing stands unchanged.
            line.Arithmetic.Missing);
    }

    /// <summary>
    /// A levy the totals could not place: its own line, with its printed amount and the flag that
    /// asks the operator to decide. Never a stock line, and never a product suggestion.
    /// </summary>
    private static ReceiptScanLine ToLevyLine(EvaluatedLine line) =>
        new(
            ReceiptLineKind.Levy,
            ReceiptScanValue<string>.Read(line.Line.Text),
            Packs: null,
            UnitsPerPack: null,
            Units: null,
            Read(line.Line.LineTotal),
            UnitCost: null,
            ProductId: null,
            new ReceiptLineGstHint(line.Line.PriceBasis, line.Line.Taxability, line.Line.GstMarker),
            LevyUnresolved: true,
            Missing: []);

    private static ReceiptScanValue<string>? Read(string? value) =>
        value is null ? null : ReceiptScanValue<string>.Read(value);

    private static ReceiptScanValue<decimal>? Read(decimal? value) =>
        value is { } amount ? ReceiptScanValue<decimal>.Read(amount) : null;

    private static ReceiptScanValue<int>? Read(int? value) =>
        value is { } count ? ReceiptScanValue<int>.Read(count) : null;

    private static ReceiptScanValue<int>? Suggested(int? value) =>
        value is { } count ? ReceiptScanValue<int>.Suggested(count) : null;

    private static ReceiptScanValue<decimal>? Suggested(decimal? value) =>
        value is { } amount ? ReceiptScanValue<decimal>.Suggested(amount) : null;

    /// <summary>One payload line and the arithmetic of it, kept together with its position.</summary>
    private sealed record EvaluatedLine(int Index, ReceiptScanPayloadLine Line, ReceiptLineArithmetic Arithmetic);

    /// <summary>
    /// What the levy reconciliation decided: what each line carries, and which levy lines are
    /// returned as their own flagged lines.
    /// </summary>
    private sealed record LevyDecision(
        IReadOnlyList<ReceiptLevyAllocation> Allocations,
        IReadOnlyList<EvaluatedLine> UnresolvedLines)
    {
        /// <summary>No levy to place: nothing is added and nothing is returned.</summary>
        public static LevyDecision None { get; } = new([], []);

        /// <summary>How much of the levy the line at <paramref name="index"/> carries.</summary>
        public decimal ShareOf(int index) =>
            Allocations.FirstOrDefault(allocation => allocation.LineNumber == index)?.Amount ?? 0m;
    }
}
