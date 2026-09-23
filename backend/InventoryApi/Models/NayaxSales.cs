using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class NayaxSales : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public long TransactionID { get; set; }
    public int? TransactionStatusId { get; set; }
    public long MachineID { get; set; }
    public long? NayaxProductId { get; set; }
    public string? MachineName { get; set; }
    public decimal SettlementValue { get; set; }
    public string? PaymentMethod { get; set; }
    public string? ProductName { get; set; }
    public DateTime MachineAuthorizationTime { get; set; }
    public decimal? NayaxProductCostPrice { get; set; }
    public decimal? UnitCostAtSale { get; set; }
    public decimal? CostOfGoodsSold { get; set; }
    public SaleCostingStatus CostingStatus { get; set; } = SaleCostingStatus.Pending;
    public SaleCostSource CostSource { get; set; } = SaleCostSource.Unknown;
}
