using System.Text.Json.Serialization;

namespace Inventory.Application.Nayax;

public class NayaxDevice
{
    public long DeviceID { get; set; }
    public string? SerialNumber { get; set; }
    public string? DeviceStatus { get; set; }
}

public class NayaxMachine
{
    public long MachineID { get; set; }
    public string? MachineName { get; set; }
    public string? MachineNumber { get; set; }
    public long? CustomerID { get; set; }
    public long? ActorID { get; set; }
}

public class NayaxMachineProduct
{
    public long? NayaxProductID { get; set; }
    public long MachineID { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public int? MDBCode { get; set; }
    public int? PAR { get; set; }
    public int? VendOutAlertThreshold { get; set; }
    public int? MissingStockByMDB { get; set; }
    public int? ProductMinimumPickQTY { get; set; }
    public decimal? CashPrice { get; set; }
    public decimal? CreditCardPrice { get; set; }
    public decimal? RetailPrice { get; set; }
    // Raw Nayax metadata; SiteCommissionAgreement is authoritative for application commission.
    public decimal? CommissionValue { get; set; }
    public long? ProductGroupID { get; set; }
    public string? DEXProductName { get; set; }
    public bool IsActive { get; set; } = true;
}

public class NayaxProductGroup
{
    /// <summary>A reference identifier for the product group.</summary>
    [JsonPropertyName("ProductGroupRef")]
    public string? ProductGroupRef { get; set; }

    /// <summary>A reference identifier for the actor associated with the product group.</summary>
    [JsonPropertyName("ActorRef")]
    public string? ActorRef { get; set; }

    /// <summary>The unique identifier of the product group.</summary>
    [JsonPropertyName("ProductGroupID")]
    public int? ProductGroupID { get; set; }

    /// <summary>The unique identifier of the actor associated with the product group.</summary>
    [JsonPropertyName("ActorID")]
    public long? ActorID { get; set; }

    /// <summary>The name of the product group.</summary>
    [JsonPropertyName("ProductGroupName")]
    public string? ProductGroupName { get; set; }

    /// <summary>The code associated with the product group.</summary>
    [JsonPropertyName("ProductGroupCode")]
    public long? ProductGroupCode { get; set; }

    /// <summary>A sub-code associated with the product group for further categorization.</summary>
    [JsonPropertyName("ProductGroupSubCode")]
    public long? ProductGroupSubCode { get; set; }

    /// <summary>The identifier of the user who created the product group.</summary>
    [JsonPropertyName("ProductGroupCreatedBy")]
    public long? ProductGroupCreatedBy { get; set; }

    /// <summary>The date and time when the product group was created.</summary>
    [JsonPropertyName("ProductGroupCreationDate")]
    public DateTime? ProductGroupCreationDate { get; set; }

    /// <summary>The identifier of the user who last updated the product group.</summary>
    [JsonPropertyName("ProductGroupUpdatedBy")]
    public long? ProductGroupUpdatedBy { get; set; }

    /// <summary>The date and time when the product group was last updated.</summary>
    [JsonPropertyName("ProductGroupLastUpdated")]
    public DateTime? ProductGroupLastUpdated { get; set; }

    /// <summary>The URL of the picture associated with the product group.</summary>
    [JsonPropertyName("ProductGroupPictureURL")]
    public string? ProductGroupPictureURL { get; set; }

    /// <summary>The category code associated with the product group.</summary>
    [JsonPropertyName("ProductGroupCategoryCode")]
    public string? ProductGroupCategoryCode { get; set; }
}

public class NayaxLastSalesReport
{
    public long TransactionID { get; set; }
    [JsonPropertyName("TransactionStatusID")]
    public int? TransactionStatusId { get; set; }

    public long MachineID { get; set; }

    public long? NayaxProductId { get; set; }

    public string? MachineName { get; set; }

    public decimal SettlementValue { get; set; }

    public string? PaymentMethod { get; set; }

    public string? ProductName { get; set; }

    [JsonPropertyName("ProductCostPrice")]
    public decimal? ProductCostPrice { get; set; }

    public DateTime MachineAuthorizationTime { get; set; }
}

/// <summary>
/// One entry from the Nayax Lynx machine last-alerts endpoint (issue #183). Field names follow the
/// same PascalCase-matches-JSON convention already used by <see cref="NayaxMachine"/>/
/// <see cref="NayaxDevice"/>; this is a best-effort shape based on the documented endpoint and the
/// observed Event 501 example, not a live-verified contract - see the "Known limitations" note in
/// the issue #183 pull request. Only <see cref="EventData"/>'s content and <see cref="EventID"/>'s
/// uniqueness are load-bearing for the parsing/idempotency this feature depends on.
/// </summary>
public class NayaxMachineAlert
{
    public long EventID { get; set; }
    public long MachineID { get; set; }
    public int EventCode { get; set; }
    public string? EventName { get; set; }
    public string? EventData { get; set; }
    public DateTime EventTimestamp { get; set; }
}

public class NayaxProduct
{
    [JsonPropertyName("NayaxProductID")]
    public long NayaxProductId { get; set; }

    [JsonPropertyName("ProductName")]
    public string? ProductName { get; set; }

    [JsonPropertyName("ProductCode")]
    public string? ProductCode { get; set; }

    [JsonPropertyName("ProductGroupID")]
    public long? ProductGroupId { get; set; }

    [JsonPropertyName("ProductGroupName")]
    public string? ProductGroupName { get; set; }

    [JsonPropertyName("Barcode")]
    public string? Barcode { get; set; }

    [JsonPropertyName("DEXProductName")]
    public string? DexProductName { get; set; }

    [JsonPropertyName("ProductDescription")]
    public string? ProductDescription { get; set; }

    [JsonPropertyName("ProductCostPrice")]
    public decimal? ProductCostPrice { get; set; }

    [JsonPropertyName("PACode")]
    public string? PaCode { get; set; }

    [JsonPropertyName("PCCode")]
    public string? PcCode { get; set; }

    [JsonPropertyName("MDBCode")]
    public int? MdbCode { get; set; }

    [JsonPropertyName("RetailPrice")]
    public decimal? RetailPrice { get; set; }

    [JsonPropertyName("CashPrice")]
    public decimal? CashPrice { get; set; }

    [JsonPropertyName("CreditCardPrice")]
    public decimal? CreditCardPrice { get; set; }

    [JsonPropertyName("PrePaidCardPrice")]
    public decimal? PrePaidCardPrice { get; set; }

    [JsonPropertyName("ExternalPrepaidPrice")]
    public decimal? ExternalPrepaidPrice { get; set; }

    [JsonPropertyName("MachinePrice")]
    public decimal? MachinePrice { get; set; }

    [JsonPropertyName("CommissionValue")]
    // Raw Nayax metadata; SiteCommissionAgreement is authoritative for application commission.
    public decimal? CommissionValue { get; set; }

    [JsonPropertyName("OperatorButtonCode")]
    public string? OperatorButtonCode { get; set; }

    [JsonPropertyName("ProductMinimumPickQTY")]
    public int? ProductMinimumPickQty { get; set; }

    [JsonPropertyName("VendOutAlertThreshold")]
    public int? VendOutAlertThreshold { get; set; }

    [JsonPropertyName("PAR")]
    public int? Par { get; set; }

    [JsonPropertyName("IsActive")]
    public bool? IsActive { get; set; }
}
