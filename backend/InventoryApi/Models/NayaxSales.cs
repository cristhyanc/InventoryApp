namespace InventoryApi.Models;

public class NayaxSales
{
    public long TransactionID { get; set; }
    public long MachineID { get; set; }
    public long? NayaxProductId { get; set; }
    public string? MachineName { get; set; }
    public decimal SettlementValue { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ProductName { get; set; }
    public decimal Quantity { get; set; }
    public DateTime MachineAuthorizationTime { get; set; }
}
