namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// What the GST accounting aid already calculated for a period, named as the BAS labels a reader is
/// reviewing (issue #1318). Every amount is one the aid's own figures produced; nothing here is a
/// second GST calculation.
/// </summary>
/// <param name="TotalSalesIncludingGst">
/// G1 - the period's whole GST-inclusive sales, GST-free sales included. It is deliberately not the
/// taxable part: a GST-free sale is still a sale, and leaving it out would understate G1.
/// </param>
/// <param name="GstOnSales">1A - the GST on the period's taxable sales (issue #1255's rule).</param>
/// <param name="PurchaseInputGst">
/// The resolved purchase input GST, including eligible delivery and packaging charges (issue #432).
/// It carries only what an explicit classification resolved, so an unclassified component
/// contributes no credit here and is reported as unresolved instead.
/// </param>
/// <param name="OperatingExpenseGst">The GST this business recorded on its operating expenses.</param>
/// <param name="GstOnFees">The GST on the period's Nayax processing fees (issue #1257).</param>
/// <param name="PurchaseGstIncomplete">
/// Whether any relevant purchase component in the period carries no GST classification.
/// </param>
/// <param name="SalesIncomplete">Whether the period's sales coverage is not <c>Complete</c>.</param>
/// <param name="FeesIncomplete">Whether the period's fee coverage is not <c>Complete</c>.</param>
/// <param name="FeeGstIncludesEstimates">
/// Whether part of the fee GST was estimated from the configured rate rather than taken from an
/// imported settlement.
/// </param>
/// <param name="HasSalesAssumedTaxable">
/// Whether any of the taxable sales are taxable only because those products carry no GST rule -
/// the default every product starts at (issue #1255).
/// </param>
public readonly record struct BasGstSummaryInputs(
    decimal TotalSalesIncludingGst,
    decimal GstOnSales,
    decimal PurchaseInputGst,
    decimal OperatingExpenseGst,
    decimal GstOnFees,
    bool PurchaseGstIncomplete = false,
    bool SalesIncomplete = false,
    bool FeesIncomplete = false,
    bool FeeGstIncludesEstimates = false,
    bool HasSalesAssumedTaxable = false);

/// <summary>
/// Which way the estimated net GST falls. It is an answer rather than a sign a presentation works
/// out for itself, so the screen, the export and any later surface describe the same period the same
/// way.
///
/// <para>There is deliberately no "unknown" member: a period whose net amount is unavailable has
/// <b>no</b> outcome, which <see cref="BasGstSummaryResult.Outcome"/> says by being
/// <see langword="null"/>. A fourth member would be a label a presentation could print.</para>
/// </summary>
public enum BasNetGstOutcome
{
    /// <summary>1A and 1B cancel out exactly: nothing is payable and nothing is refundable.</summary>
    Nil = 0,

    /// <summary>1A exceeds 1B: the estimate is GST payable.</summary>
    Payable = 1,

    /// <summary>1B exceeds 1A: the estimate is a GST credit.</summary>
    Credit = 2
}

/// <summary>
/// One period's BAS-labelled GST figures.
///
/// <para><c>GstCredits</c> is 1B: the three eligible credit sources, each counted once.
/// <c>NetGst</c> is 1A less 1B, signed - positive is payable and negative is a credit - and it is
/// the GST accounting aid's own net GST, never a second subtraction.</para>
///
/// <para><c>IsIncomplete</c> says this estimate rests on something missing or estimated: an
/// unclassified purchase component, sales or fee coverage that does not account for the whole
/// period, fee GST partly estimated from a rate, or sales taxable only by the no-rule default. It
/// never hides a figure; it says the total is not a final balance.</para>
///
/// <para><c>Outcome</c> is <see langword="null"/> when the net amount is one no presentation may
/// show - the internal zero of a period whose provider coverage never measured it. There is no
/// payable, credit or nil answer about an amount nobody has.</para>
/// </summary>
public readonly record struct BasGstSummaryResult(
    decimal TotalSales,
    decimal GstOnSales,
    decimal GstCredits,
    decimal PurchaseGstCredits,
    decimal OperatingExpenseGstCredits,
    decimal NayaxFeeGstCredits,
    decimal NetGst,
    BasNetGstOutcome? Outcome,
    bool IsIncomplete);

/// <summary>
/// The BAS-labelled view of a period's GST (issue #1318), for a business that is registered for GST.
///
/// <para>It owns exactly two rules of its own: which way the net figure falls - including when that
/// question has no answer at all - and whether the estimate rests on evidence that is missing or
/// estimated. Every amount comes from
/// <see cref="GstAccountingAidPolicy"/>, which this policy calls rather than re-deriving: 1B is that
/// policy's <see cref="GstAccountingAidResult.GstCredits"/> and the net figure its
/// <see cref="GstAccountingAidResult.NetGst"/>, so the Bookkeeping summary and the GST accounting
/// aid cannot report different numbers for the same period.</para>
/// </summary>
public static class BasGstSummaryPolicy
{
    public static BasGstSummaryResult Calculate(BasGstSummaryInputs inputs)
    {
        // The aid's own calculation, called with this period's figures. TaxableSalesIncludingGst and
        // NayaxFeesExGst feed only the aid's taxable-sales and taxable-fees figures, which the BAS
        // summary does not show, so they are passed as they stand on the sales side and as zero on
        // the fee side rather than invented.
        var aid = GstAccountingAidPolicy.Calculate(new GstAccountingAidInputs(
            TaxableSalesIncludingGst: inputs.TotalSalesIncludingGst,
            GstOnSales: inputs.GstOnSales,
            NayaxFeesExGst: 0m,
            GstOnFees: inputs.GstOnFees,
            OperatingExpenseGst: inputs.OperatingExpenseGst,
            PurchaseInputGst: inputs.PurchaseInputGst));

        return new BasGstSummaryResult(
            TotalSales: inputs.TotalSalesIncludingGst,
            GstOnSales: inputs.GstOnSales,
            GstCredits: aid.GstCredits,
            PurchaseGstCredits: inputs.PurchaseInputGst,
            OperatingExpenseGstCredits: inputs.OperatingExpenseGst,
            NayaxFeeGstCredits: inputs.GstOnFees,
            NetGst: aid.NetGst,
            Outcome: OutcomeOf(aid.NetGst, inputs),
            IsIncomplete: inputs.PurchaseGstIncomplete
                || inputs.SalesIncomplete
                || inputs.FeesIncomplete
                || inputs.FeeGstIncludesEstimates
                || inputs.HasSalesAssumedTaxable);
    }

    /// <summary>
    /// The payable/credit/nil answer, or none at all.
    ///
    /// <para>A zero net figure is only an answer while something measured it. When the period's
    /// sales or fee coverage is not <c>Complete</c>, a net figure of zero is the internal zero of
    /// figures nobody read - which is exactly the zero every presentation replaces with the
    /// coverage state instead of showing (issue #730, and <c>AGENTS.md</c> § Nayax processing fees
    /// and GST: only <c>Complete</c> may present a zero as a real zero). Calling that "nil" beside
    /// "No provider data" would publish a finished BAS balance nobody measured, so the outcome is
    /// withheld with the amount and the surfaces that render it say nothing about which way the
    /// period falls. A <b>non-zero</b> net figure in an incomplete period is real partial data,
    /// stays visible, and keeps its answer.</para>
    /// </summary>
    private static BasNetGstOutcome? OutcomeOf(decimal netGst, BasGstSummaryInputs inputs)
    {
        if (netGst == 0m && (inputs.SalesIncomplete || inputs.FeesIncomplete))
            return null;

        return netGst switch
        {
            > 0m => BasNetGstOutcome.Payable,
            < 0m => BasNetGstOutcome.Credit,
            _ => BasNetGstOutcome.Nil
        };
    }
}
