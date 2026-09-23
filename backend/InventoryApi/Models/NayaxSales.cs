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

    /// <summary>
    /// The application's own primary key.
    ///
    /// <see cref="TransactionID"/> used to be the key, but it is a Nayax identifier from a remote
    /// operator account, not ours. Its uniqueness is only ever guaranteed within the account that
    /// issued it, so keying local rows by it would mean a second business's import could collide
    /// with - or silently overwrite - the first business's sales. Ownership of the row is ours to
    /// express, so the key is ours too, and the remote identifier is a unique value per business
    /// instead (see AppDbContext).
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// The Nayax transaction identifier. Unique per business, not globally: treat it as an
    /// external identity, never as a local key.
    /// </summary>
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
