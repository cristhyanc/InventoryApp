namespace InventoryApi.Models;

public class NayaxSales
{
    public long TransactionID { get; set; }
    public int? TransactionStatusId { get; set; }
    public long MachineID { get; set; }
    public long? NayaxProductId { get; set; }
    public string? MachineName { get; set; }
    public decimal SettlementValue { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ProductName { get; set; }
    public DateTime MachineAuthorizationTime { get; set; }
    public decimal? UnitCostAtSale { get; set; }
    public decimal? CostOfGoodsSold { get; set; }
    public SaleCostingStatus CostingStatus { get; set; } = SaleCostingStatus.Pending;
}
