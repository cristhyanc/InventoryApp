using System.Text.Json.Serialization;

namespace InventoryApi.Models;

public class ImportedFile : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FileHash { get; set; } = string.Empty;
    public DateTime ImportedAt { get; set; }
    public virtual ICollection<ImportedReimbursement> Reimbursements { get; set; } = new List<ImportedReimbursement>();
}

public class ImportedReimbursement : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ImportedFileId { get; set; }
    public virtual ImportedFile ImportedFile { get; set; } = null!;
    public string? ReportType { get; set; }
    public DateTime? ReimbursementStartDate { get; set; }
    public DateTime? ReimbursementEndDate { get; set; }
    public DateTime? ReimbursementPayoutDate { get; set; }
    public string? CompanyName { get; set; }
    public string? CustomerId { get; set; }
    public bool IsReimbursement { get; set; }
    public bool IsInvoice { get; set; }
    public string? Email { get; set; }
    public int? ActiveDevices { get; set; }
    public int? TotalDevices { get; set; }
    public string? InvoicePaymentMethod { get; set; }
    public string? InvoicePaymentMethodId { get; set; }
    public decimal? Total { get; set; }
    public string? SupportEmail { get; set; }
    public string? ErpId { get; set; }
    public string? DistributorActorId { get; set; }
    public string? FinanceEntityId { get; set; }
    public string? RawAttributesJson { get; set; }
    public virtual ICollection<ImportedReimbursementDevice> Devices { get; set; } = new List<ImportedReimbursementDevice>();
    public virtual ICollection<ImportedDevicePayment> DevicePayments { get; set; } = new List<ImportedDevicePayment>();
    public virtual ICollection<ImportedFee> Fees { get; set; } = new List<ImportedFee>();
    public virtual ICollection<ImportedPaymentMethod> PaymentMethods { get; set; } = new List<ImportedPaymentMethod>();
}

public class ImportedReimbursementDevice : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ImportedReimbursementId { get; set; }
    public virtual ImportedReimbursement ImportedReimbursement { get; set; } = null!;
    public string? EntityId { get; set; }
    public string? MachineType { get; set; }
    public string? VerticalTypeName { get; set; }
    public string? VerticalProductTypeName { get; set; }
    public string? HardwareSerial { get; set; }
    public string? MachineNumber { get; set; }
    public string? ActorCode { get; set; }
    public string? Location { get; set; }
    public int? TotalBillableTransactionCount { get; set; }
    public decimal? TotalBillableTransactionAmount { get; set; }
    public int? TotalNotBillableTransactionCount { get; set; }
    public decimal? TotalNotBillableTransactionAmount { get; set; }
    public decimal? ServiceFee { get; set; }
    public decimal? ProcessingFee { get; set; }
    public bool HasServicePerTransaction { get; set; }
    public decimal? TotalExtraCharge { get; set; }
    public decimal? NetAmount { get; set; }
    public string? RawAttributesJson { get; set; }
}

public class ImportedDevicePayment : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ImportedReimbursementId { get; set; }
    public virtual ImportedReimbursement ImportedReimbursement { get; set; } = null!;
    public string? EntityId { get; set; }
    public string? PaymentMethodDescription { get; set; }
    public string? RecognitionDescription { get; set; }
    public int? SalesCount { get; set; }
    public decimal? TotalSum { get; set; }
    public decimal? ProcessingFees { get; set; }
    public decimal? ServiceFees { get; set; }
    public string? RawAttributesJson { get; set; }
}

public class ImportedFee : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ImportedReimbursementId { get; set; }
    public virtual ImportedReimbursement ImportedReimbursement { get; set; } = null!;
    public string? FeesTypeId { get; set; }
    public string? FeeTypeDescription { get; set; }
    public bool IsService { get; set; }
    public decimal? TotalSum { get; set; }
    public decimal? TotalSumWithVat { get; set; }
    public decimal? VatPercentage { get; set; }
    public decimal? AverageFeeAmount { get; set; }
    public decimal? TotalCount { get; set; }
    public bool IsPreviousPeriod { get; set; }
    public string? RawAttributesJson { get; set; }
}

public class ImportedPaymentMethod : IBusinessOwned
{
    /// <summary>
    /// The owning business (issue #64). Stamped from the caller's resolved business on insert
    /// and immutable afterwards; never read from API input, and never serialised out.
    /// </summary>
    [JsonIgnore]
    public int BusinessId { get; set; }

    public int Id { get; set; }
    public int ImportedReimbursementId { get; set; }
    public virtual ImportedReimbursement ImportedReimbursement { get; set; } = null!;
    public string? PaymentMethodId { get; set; }
    public string? PaymentMethodDescription { get; set; }
    public bool IsNayaxReimbursement { get; set; }
    public string? BillingProvider { get; set; }
    public int? TotalSalesCount { get; set; }
    public decimal? TotalSalesSum { get; set; }
    public string? RecognitionDescription { get; set; }
    public bool IsPreviousPeriod { get; set; }
    public string? RawAttributesJson { get; set; }
}
