namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// Inputs required to derive the GST accounting-aid figures. All amounts are the already-migrated
/// bookkeeping report's own GST-on-sales and GST-on-fees figures; this policy does not re-derive
/// GST from raw sales/fees a second way.
///
/// <paramref name="PurchaseInputGst"/> is the period's resolved purchase input GST, produced by
/// <see cref="PurchaseInputGstPolicy"/>. It carries only what an explicit classification resolved:
/// an unclassified purchase component contributes nothing here and is reported separately as
/// unresolved, so no GST is ever inferred from an amount (parent issue #62, decision D1).
/// </summary>
public readonly record struct GstAccountingAidInputs(
    decimal Sales,
    decimal GstOnSales,
    decimal NayaxFeesExGst,
    decimal GstOnFees,
    decimal OperatingExpenseGst,
    decimal PurchaseInputGst = 0m);

public readonly record struct GstAccountingAidResult(
    decimal TaxableSales,
    decimal TaxableFees,
    decimal NetGst);

public static class GstAccountingAidPolicy
{
    public static GstAccountingAidResult Calculate(GstAccountingAidInputs inputs) => new(
        TaxableSales: inputs.Sales - inputs.GstOnSales,
        TaxableFees: inputs.NayaxFeesExGst,
        NetGst: inputs.GstOnSales - inputs.GstOnFees - inputs.OperatingExpenseGst - inputs.PurchaseInputGst);
}
