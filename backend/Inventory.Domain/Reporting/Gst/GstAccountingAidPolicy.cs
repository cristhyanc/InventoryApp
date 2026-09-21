namespace Inventory.Domain.Reporting.Gst;

/// <summary>
/// Inputs required to derive the GST accounting-aid figures. All amounts are the already-migrated
/// bookkeeping report's own GST-on-sales and GST-on-fees figures; this policy does not re-derive
/// GST from raw sales/fees a second way.
/// </summary>
public readonly record struct GstAccountingAidInputs(
    decimal Sales,
    decimal GstOnSales,
    decimal NayaxFeesExGst,
    decimal GstOnFees,
    decimal OperatingExpenseGst);

public readonly record struct GstAccountingAidResult(
    decimal TaxableSales,
    decimal TaxableFees,
    decimal NetGst);

public static class GstAccountingAidPolicy
{
    public static GstAccountingAidResult Calculate(GstAccountingAidInputs inputs) => new(
        TaxableSales: inputs.Sales - inputs.GstOnSales,
        TaxableFees: inputs.NayaxFeesExGst,
        NetGst: inputs.GstOnSales - inputs.GstOnFees - inputs.OperatingExpenseGst);
}
