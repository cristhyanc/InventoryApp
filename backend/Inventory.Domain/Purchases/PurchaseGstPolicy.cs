using Inventory.Domain.Gst;

namespace Inventory.Domain.Purchases;

/// <summary>
/// One purchase line's GST inputs. <see cref="Quantity"/> and <see cref="UnitCost"/> are the
/// persisted line values, not a pre-computed amount, because the line amount's own rounding is
/// part of the rule (parent issue #62, decision D2).
/// </summary>
public readonly record struct PurchaseGstLine(decimal Quantity, decimal UnitCost, GstClassification Classification);

/// <summary>
/// One purchase-level charge's GST inputs - delivery or package, the only two fee types that exist
/// (parent issue #62, "Fee types"). A <c>null</c> or zero <see cref="Amount"/> is an absent charge:
/// it has no classification and is never unresolved (decision D3).
/// </summary>
public readonly record struct PurchaseGstCharge(decimal? Amount, GstClassification Classification);

/// <summary>
/// A purchase's input GST, plus the components the app was never told about. The unresolved pair is
/// returned separately rather than folded into the GST figure, so a report can show the known GST
/// alongside a clear incomplete status instead of presenting a guess as complete.
/// </summary>
public readonly record struct PurchaseGstResult(
    decimal InputGst,
    int UnresolvedComponentCount,
    decimal UnresolvedAmount);

/// <summary>
/// The one authoritative purchase input-GST calculation (parent issue #62's approved GST design).
/// Purchase amounts are GST-inclusive, so a taxable component's GST is its amount divided by 11.
///
/// Every rounding here is deliberate and pinned by <c>PurchaseGstPolicyTests</c>:
/// <list type="bullet">
///   <item>a line's GST-inclusive amount is <c>round(Quantity x UnitCost, 2)</c> (decision D2);</item>
///   <item>a taxable component's GST is <c>round(amount / 11, 2)</c>, rounded for that component;</item>
///   <item>the purchase's GST is the sum of those already-rounded component amounts, never
///   <c>invoiceTotal / 11</c>, which can differ by a cent.</item>
/// </list>
/// Both roundings use <see cref="MidpointRounding.AwayFromZero"/>, the ordinary currency rounding,
/// rather than the banker's rounding <see cref="Math.Round(decimal, int)"/> defaults to.
///
/// GST-free contributes $0. Unknown contributes nothing at all and is counted and totalled as
/// unresolved: the app must not infer 1/11 from an amount (AGENTS.md § Nayax processing fees and GST).
/// </summary>
public static class PurchaseGstPolicy
{
    /// <summary>
    /// The Australian GST-inclusive divisor: a taxable inclusive amount is 11 elevenths of its
    /// GST-exclusive value, so its GST component is the inclusive amount divided by 11.
    /// </summary>
    public const decimal GstInclusiveDivisor = 11m;

    /// <summary>A line's GST-inclusive amount, rounded to cents before any GST is derived from it.</summary>
    public static decimal LineAmount(decimal quantity, decimal unitCost) => RoundToCents(quantity * unitCost);

    /// <summary>Whether a delivery or package charge exists at all (decision D3).</summary>
    public static bool IsChargePresent(decimal? amount) => amount is { } value && value != 0m;

    /// <summary>
    /// One component's input GST: its own rounded share of a GST-inclusive amount when it is
    /// taxable, and nothing when it is GST-free or unclassified.
    /// </summary>
    public static decimal ComponentGst(decimal amount, GstClassification classification) =>
        classification == GstClassification.Taxable
            ? RoundToCents(amount / GstInclusiveDivisor)
            : 0m;

    /// <summary>
    /// The state to persist for a classification a person submitted (decision D4): an explicit
    /// choice is recorded as <see cref="GstClassificationSource.Manual"/>, and anything omitted or
    /// explicitly unknown stays unclassified. Nothing here infers a classification from a product,
    /// a supplier or an amount; rule-based classification is issues #430 and #433.
    /// </summary>
    public static GstClassificationState Classify(GstClassification? requested) =>
        requested is null or GstClassification.Unknown
            ? GstClassificationState.Unclassified
            : new GstClassificationState(requested.Value, GstClassificationSource.Manual);

    /// <summary>
    /// The state to persist for a delivery or package charge (decision D3): an absent or zero charge
    /// carries no classification, whatever was submitted or previously stored for it.
    /// </summary>
    public static GstClassificationState ClassifyCharge(decimal? amount, GstClassificationState state) =>
        IsChargePresent(amount) ? state : GstClassificationState.Unclassified;

    /// <summary>
    /// A purchase's input GST and its unresolved components, over its lines and its delivery and
    /// package charges.
    /// </summary>
    public static PurchaseGstResult Calculate(
        IEnumerable<PurchaseGstLine> lines,
        PurchaseGstCharge deliveryCharge,
        PurchaseGstCharge packageCharge)
    {
        var inputGst = 0m;
        var unresolvedCount = 0;
        var unresolvedAmount = 0m;

        foreach (var (amount, classification) in Components(lines, deliveryCharge, packageCharge))
        {
            if (classification == GstClassification.Unknown)
            {
                unresolvedCount++;
                unresolvedAmount += amount;
                continue;
            }

            inputGst += ComponentGst(amount, classification);
        }

        return new PurchaseGstResult(inputGst, unresolvedCount, unresolvedAmount);
    }

    /// <summary>
    /// The purchase's GST components as (rounded GST-inclusive amount, classification) pairs: every
    /// line, and each of the two charges only when it is actually present.
    /// </summary>
    private static IEnumerable<(decimal Amount, GstClassification Classification)> Components(
        IEnumerable<PurchaseGstLine> lines,
        PurchaseGstCharge deliveryCharge,
        PurchaseGstCharge packageCharge)
    {
        foreach (var line in lines)
            yield return (LineAmount(line.Quantity, line.UnitCost), line.Classification);

        if (IsChargePresent(deliveryCharge.Amount))
            yield return (RoundToCents(deliveryCharge.Amount!.Value), deliveryCharge.Classification);

        if (IsChargePresent(packageCharge.Amount))
            yield return (RoundToCents(packageCharge.Amount!.Value), packageCharge.Classification);
    }

    private static decimal RoundToCents(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);
}
