namespace Inventory.Application.Imports;

/// <summary>
/// One pending reimbursement file, already read and parsed by the
/// <see cref="IPendingReimbursementXmlSource"/> adapter (issue #299).
///
/// These are raw imported facts, deliberately kept separate from any derived accounting value:
/// the use case decides what to do with the file, the persistence port writes exactly these
/// values, and no reconciliation rule reads them from here.
/// </summary>
/// <param name="FileName">The file's own name, as it will be persisted for provenance.</param>
/// <param name="ContentHash">
/// The SHA-256 hex hash of the file's bytes - the idempotency key. Duplicate detection is per
/// business (AGENTS.md § Tenant ownership: two businesses may legitimately hold the same
/// external value), which the persistence port's tenant-scoped lookup provides.
/// </param>
/// <param name="Reimbursements">Every <c>row</c> element the file contained; never empty.</param>
public sealed record PendingReimbursementXmlFile(
    string FileName,
    string ContentHash,
    IReadOnlyList<ImportedReimbursementFacts> Reimbursements);

/// <summary>One reimbursement <c>row</c> and its nested device/payment/fee facts.</summary>
public sealed record ImportedReimbursementFacts
{
    public string? ReportType { get; init; }
    public DateTime? ReimbursementStartDate { get; init; }
    public DateTime? ReimbursementEndDate { get; init; }
    public DateTime? ReimbursementPayoutDate { get; init; }
    public string? CompanyName { get; init; }
    public string? CustomerId { get; init; }
    public bool IsReimbursement { get; init; }
    public bool IsInvoice { get; init; }
    public string? Email { get; init; }
    public int? ActiveDevices { get; init; }
    public int? TotalDevices { get; init; }
    public string? InvoicePaymentMethod { get; init; }
    public string? InvoicePaymentMethodId { get; init; }
    public decimal? Total { get; init; }
    public string? SupportEmail { get; init; }
    public string? ErpId { get; init; }
    public string? DistributorActorId { get; init; }
    public string? FinanceEntityId { get; init; }

    /// <summary>Every attribute of the row as JSON, so the raw imported fact stays auditable.</summary>
    public string? RawAttributesJson { get; init; }

    public IReadOnlyList<ImportedReimbursementDeviceFacts> Devices { get; init; } = [];
    public IReadOnlyList<ImportedDevicePaymentFacts> DevicePayments { get; init; } = [];
    public IReadOnlyList<ImportedFeeFacts> Fees { get; init; } = [];
    public IReadOnlyList<ImportedPaymentMethodFacts> PaymentMethods { get; init; } = [];
}

/// <summary>One <c>device</c> element of a reimbursement row.</summary>
public sealed record ImportedReimbursementDeviceFacts
{
    public string? EntityId { get; init; }
    public string? MachineType { get; init; }
    public string? VerticalTypeName { get; init; }
    public string? VerticalProductTypeName { get; init; }
    public string? HardwareSerial { get; init; }
    public string? MachineNumber { get; init; }
    public string? ActorCode { get; init; }
    public string? Location { get; init; }
    public int? TotalBillableTransactionCount { get; init; }
    public decimal? TotalBillableTransactionAmount { get; init; }
    public int? TotalNotBillableTransactionCount { get; init; }
    public decimal? TotalNotBillableTransactionAmount { get; init; }
    public decimal? ServiceFee { get; init; }
    public decimal? ProcessingFee { get; init; }
    public bool HasServicePerTransaction { get; init; }
    public decimal? TotalExtraCharge { get; init; }
    public decimal? NetAmount { get; init; }
    public string? RawAttributesJson { get; init; }
}

/// <summary>One <c>devicePayments</c> element of a reimbursement row.</summary>
public sealed record ImportedDevicePaymentFacts
{
    public string? EntityId { get; init; }
    public string? PaymentMethodDescription { get; init; }
    public string? RecognitionDescription { get; init; }
    public int? SalesCount { get; init; }
    public decimal? TotalSum { get; init; }
    public decimal? ProcessingFees { get; init; }
    public decimal? ServiceFees { get; init; }
    public string? RawAttributesJson { get; init; }
}

/// <summary>
/// One <c>fees</c> element of a reimbursement row. The fee excluding GST/VAT, the GST/VAT
/// percentage and the fee including GST/VAT stay separate values, exactly as imported.
/// </summary>
public sealed record ImportedFeeFacts
{
    public string? FeesTypeId { get; init; }
    public string? FeeTypeDescription { get; init; }
    public bool IsService { get; init; }
    public decimal? TotalSum { get; init; }
    public decimal? TotalSumWithVat { get; init; }
    public decimal? VatPercentage { get; init; }
    public decimal? AverageFeeAmount { get; init; }
    public decimal? TotalCount { get; init; }
    public bool IsPreviousPeriod { get; init; }
    public string? RawAttributesJson { get; init; }
}

/// <summary>One <c>paymentMethods</c> element of a reimbursement row.</summary>
public sealed record ImportedPaymentMethodFacts
{
    public string? PaymentMethodId { get; init; }
    public string? PaymentMethodDescription { get; init; }
    public bool IsNayaxReimbursement { get; init; }
    public string? BillingProvider { get; init; }
    public int? TotalSalesCount { get; init; }
    public decimal? TotalSalesSum { get; init; }
    public string? RecognitionDescription { get; init; }
    public bool IsPreviousPeriod { get; init; }
    public string? RawAttributesJson { get; init; }
}
