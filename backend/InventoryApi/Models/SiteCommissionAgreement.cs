using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public enum CommissionFrequency { None, Monthly, Quarterly }
public enum CommissionBasis { GrossSales, CardSales, SalesExGst }

public class SiteCommissionAgreement : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public long SiteId { get; set; }
    public DateTime EffectiveFrom { get; set; }
    public DateTime? EffectiveTo { get; set; }
    public decimal CommissionRate { get; set; }
    public CommissionFrequency Frequency { get; set; } = CommissionFrequency.None;
    public CommissionBasis Basis { get; set; } = CommissionBasis.GrossSales;
    public int? PaymentDueDaysAfterPeriodEnd { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
