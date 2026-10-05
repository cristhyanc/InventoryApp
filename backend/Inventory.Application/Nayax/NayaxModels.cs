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

/// <summary>
/// One item of the Nayax Lynx <c>GET /v1/machines/{MachineID}/lastSales</c> response
/// ("Get Last Sales for Machine by MachineID",
/// https://devzone.nayax.com/reference/lynx/machines/get-last-sales-for-machine-by-machineid).
///
/// The response carries two authorization timestamps with different meanings, and only one of them is
/// an instant (issue #380):
/// <list type="bullet">
///   <item><see cref="AuthorizationDateTimeGmt"/> (<c>AuthorizationDateTimeGMT</c>) is documented as
///   "The date and time when the transaction was authorized, in GMT" - the authoritative instant, and
///   the only timestamp this integration may turn into a persisted UTC value.</item>
///   <item><see cref="MachineAuthorizationTime"/> is documented as "The local date and time when the
///   machine authorized the transaction" - machine-local wall-clock time carrying no offset. Its ticks
///   are not a UTC instant, and the documented sample payload prints it with a trailing <c>Z</c> and
///   equal to the GMT field anyway, so neither its shape nor our own persistence mapping can be read
///   as evidence that it is UTC. It is kept as a raw imported fact (it is also one of the three values
///   the Generate eReceipt request echoes back) and is never used as a sale instant.</item>
/// </list>
/// Machine-local time cannot be converted here even in principle: Nayax's only machine timezone
/// metadata is <c>MachineTimeZoneOffset</c> on the machine basic-info endpoints, a bare
/// <c>number</c> offset with no daylight-saving rule, and the sales payload carries no timezone
/// identifier at all. Reading the GMT field is therefore what makes the sale instant correct across a
/// daylight-saving transition without assuming a fixed <c>+10</c>/<c>+11</c> offset.
/// </summary>
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

    /// <summary>
    /// The authorization instant in GMT, as a <see cref="DateTimeOffset"/> because the payload carries
    /// an explicit offset (<c>"2024-10-09T16:53:51.225Z"</c>). Nullable so that a payload item which
    /// does not carry it stays distinguishable from one authorized at
    /// <see cref="DateTimeOffset.MinValue"/>: the documented schema declares the field non-nullable, so
    /// an absent value means the payload did not match its contract, and a sale is then not imported at
    /// all rather than imported at a defaulted or guessed instant.
    /// </summary>
    [JsonPropertyName("AuthorizationDateTimeGMT")]
    public DateTimeOffset? AuthorizationDateTimeGmt { get; set; }

    /// <summary>
    /// The machine's local wall-clock authorization time, with no offset. A raw imported fact only; see
    /// the type remarks. Never read as an instant.
    /// </summary>
    public DateTime MachineAuthorizationTime { get; set; }

    /// <summary>
    /// The one normalization of a Nayax sale timestamp into a true UTC instant, at the integration
    /// boundary and nowhere else, or <c>null</c> when the payload carried no authoritative GMT value.
    ///
    /// <see cref="DateTimeOffset.UtcDateTime"/> is offset-aware and idempotent: an instant that already
    /// arrived as <c>Z</c> is returned unchanged, one that arrived as <c>+11:00</c> becomes the same
    /// physical instant, and applying the conversion again cannot shift it a second time. That is what
    /// makes re-encountering a transaction - which the rolling last-sales window does on every refresh,
    /// and an uploaded export does on every re-upload - safe.
    /// </summary>
    [JsonIgnore]
    public DateTime? AuthorizationInstantUtc => AuthorizationDateTimeGmt?.UtcDateTime;
}

/// <summary>
/// One item of the Nayax Lynx <c>GET /v1/machines/{MachineID}/lastAlerts</c> response
/// ("Get Machine Last Alerts", https://devzone.nayax.com/reference/lynx/machines/get-machine-last-alerts).
/// Every documented response field is mapped with an explicit <see cref="JsonPropertyNameAttribute"/>
/// so the C# names stay independent of Nayax's JSON naming; types and nullability follow the
/// documented schema. <see cref="EventLogId"/> is the upstream event identity (issue #183
/// idempotency key), <see cref="EventDateTimeGmt"/> is the canonical event instant, and
/// <see cref="EventData"/> is the raw text the Event 501 parser reads and must stay unmodified.
/// </summary>
public class NayaxMachineAlert
{
    [JsonPropertyName("MachineID")]
    public long? MachineId { get; set; }

    /// <summary>The event time as recorded by the machine (VMC) clock; source data only.</summary>
    [JsonPropertyName("EventDateTimeVMC")]
    public DateTime EventDateTimeVmc { get; set; }

    [JsonPropertyName("TransactionID")]
    public long? TransactionId { get; set; }

    /// <summary>The unique identifier of the Nayax event log entry.</summary>
    [JsonPropertyName("EventLogID")]
    public long EventLogId { get; set; }

    [JsonPropertyName("SiteID")]
    public int SiteId { get; set; }

    [JsonPropertyName("EntityTypeID")]
    public int EntityTypeId { get; set; }

    [JsonPropertyName("EntityTypeName")]
    public string? EntityTypeName { get; set; }

    [JsonPropertyName("DeviceID")]
    public long? DeviceId { get; set; }

    [JsonPropertyName("EntityActorID")]
    public long? EntityActorId { get; set; }

    /// <summary>The event time in GMT.</summary>
    [JsonPropertyName("EventDateTimeGMT")]
    public DateTime EventDateTimeGmt { get; set; }

    [JsonPropertyName("EventCode")]
    public int EventCode { get; set; }

    [JsonPropertyName("EventSourceID")]
    public int EventSourceId { get; set; }

    [JsonPropertyName("EventSourceName")]
    public string? EventSourceName { get; set; }

    [JsonPropertyName("EventGroupId")]
    public int? EventGroupId { get; set; }

    [JsonPropertyName("EventGroupName")]
    public string? EventGroupName { get; set; }

    [JsonPropertyName("EventCategoryId")]
    public int? EventCategoryId { get; set; }

    [JsonPropertyName("EventCategoryName")]
    public string? EventCategoryName { get; set; }

    [JsonPropertyName("EventDescription")]
    public string? EventDescription { get; set; }

    /// <summary>Additional event data as a string; for Event 501 the stock-adjustment text.</summary>
    [JsonPropertyName("EventData")]
    public string? EventData { get; set; }

    [JsonPropertyName("JSONData")]
    public string? JsonData { get; set; }

    [JsonPropertyName("EventUserID")]
    public long? EventUserId { get; set; }
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
