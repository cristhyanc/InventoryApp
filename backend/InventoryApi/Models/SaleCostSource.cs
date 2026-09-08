namespace InventoryApi.Models;

public enum SaleCostSource
{
    Unknown = 0,
    InventoryLedger = 1,
    NayaxTransactionExport = 2,
    Estimated = 3
}
