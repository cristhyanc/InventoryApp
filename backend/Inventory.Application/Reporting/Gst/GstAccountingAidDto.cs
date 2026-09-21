using Inventory.Application.Reporting.Shared;

namespace Inventory.Application.Reporting.Gst;

public record GstAccountingAidDto(
    DateTime From,
    DateTime To,
    decimal TaxableSales,
    decimal GstOnSales,
    decimal TaxableFees,
    decimal GstOnFees,
    decimal NetGst,
    ReportingDataQualityDto DataQuality,
    decimal GstFreeSales = 0m,
    decimal InventoryPurchaseGst = 0m)
{
    public decimal OperatingExpenseGst { get; init; }
}
