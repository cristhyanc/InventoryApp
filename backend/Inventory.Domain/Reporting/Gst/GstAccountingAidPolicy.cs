namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// Inputs required to derive the GST accounting-aid figures. All amounts are the already-migrated
/// bookkeeping report's own taxable-sales, GST-on-sales and GST-on-fees figures; this policy does
/// not re-derive GST from raw sales/fees a second way.
///
/// <paramref name="TaxableSalesIncludingGst"/> is bookkeeping's taxable sales as
/// <see cref="SalesGstPolicy"/> decided them (issue #1255): gross sales less the sales of products
/// whose GST rule is explicitly GST-free. It is deliberately not the period's gross sales, which
/// would put GST-free revenue into a taxable figure.
///
/// <paramref name="PurchaseInputGst"/> is the period's resolved purchase input GST, produced by
/// <see cref="PurchaseInputGstPolicy"/>. It carries only what an explicit classification resolved:
/// an unclassified purchase component contributes nothing here and is reported separately as
/// unresolved, so no GST is ever inferred from an amount (parent issue #62, decision D1).
/// </summary>
public readonly record struct GstAccountingAidInputs(
    decimal TaxableSalesIncludingGst,
    decimal GstOnSales,
    decimal NayaxFeesExGst,
    decimal GstOnFees,
    decimal OperatingExpenseGst,
    decimal PurchaseInputGst = 0m);

/// <summary>
/// The aid's derived figures.
///
/// <para><c>GstCredits</c> is the period's eligible GST credits: the Nayax fee GST, the
/// operating-expense GST and the resolved purchase input GST added together, each source counted
/// exactly once (issue #1318, where this is BAS label 1B). It is the whole of what <c>NetGst</c>
/// subtracts, so the two can never disagree about which credits were allowed.</para>
/// </summary>
public readonly record struct GstAccountingAidResult(
    decimal TaxableSales,
    decimal TaxableFees,
    decimal NetGst,
    decimal GstCredits);

public static class GstAccountingAidPolicy
{
    /// <summary>
    /// The aid's figures. <c>TaxableSales</c> is the GST-<em>exclusive</em> value of the period's
    /// taxable sales: the GST-inclusive taxable sales less the GST on them. Since issue #1255 that
    /// inclusive figure excludes GST-free products' sales, so neither figure is derived from gross
    /// sales any more.
    ///
    /// <para>Net GST is <c>GST on sales − GST credits</c>, where the credits are the three eligible
    /// sources added up once (issue #1318). That is the same subtraction this policy always made -
    /// <c>GstOnSales − GstOnFees − OperatingExpenseGst − PurchaseInputGst</c> - written so the
    /// credit total a BAS reader sees as 1B and the net GST beside it come from one place rather
    /// than from two additions that could drift apart. An unclassified purchase component
    /// contributes nothing to <see cref="GstAccountingAidResult.GstCredits"/>, because
    /// <c>PurchaseInputGst</c> already carries only what an explicit classification resolved.</para>
    /// </summary>
    public static GstAccountingAidResult Calculate(GstAccountingAidInputs inputs)
    {
        var credits = inputs.GstOnFees + inputs.OperatingExpenseGst + inputs.PurchaseInputGst;

        return new GstAccountingAidResult(
            TaxableSales: inputs.TaxableSalesIncludingGst - inputs.GstOnSales,
            TaxableFees: inputs.NayaxFeesExGst,
            NetGst: inputs.GstOnSales - credits,
            GstCredits: credits);
    }
}
