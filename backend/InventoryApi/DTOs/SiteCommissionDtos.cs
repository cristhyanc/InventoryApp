using System.Text.Json.Serialization;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.DTOs;

public record SiteCommissionAgreementDto(long SiteId, DateTime EffectiveFrom, DateTime? EffectiveTo, decimal CommissionRate,
    CommissionFrequency Frequency, CommissionBasis Basis, int? PaymentDueDaysAfterPeriodEnd);

/// <summary>
/// The date an existing agreement is to stop covering sales after, inclusive (issue #1254).
///
/// <para>It carries the end date and nothing else: which agreement is being ended is the route, and
/// the rate, start date, frequency, basis and due days of an agreement are not editable - correcting
/// one would rewrite commission already reported, which the repository owner's decision of 14
/// November 2026 keeps out.</para>
/// </summary>
/// <param name="EndDate">
/// The date the agreement is to stop covering sales after, inclusive. Marked
/// <see cref="JsonRequiredAttribute"/> so a body that omits it is refused outright rather than bound
/// to <c>default(DateTime)</c> - the field is a <c>DateTime</c>, so an under-posted body would
/// otherwise reach the rule as 1 January 0001 and be refused with a sentence about the agreement's
/// start date, which is not what went wrong.
/// </param>
public record EndSiteCommissionAgreementDto([property: JsonRequired] DateTime EndDate);
public record SiteCommissionMachineDto(long MachineId, string MachineName, int TransactionCount, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionDue, bool IsComplete = true, bool HasConfigurationGap = false, bool HasOverlap = false);
public record SiteCommissionProductDto(string ProductName, int TotalVends, decimal TotalSales);
public record SiteCommissionRowDto(long SiteId, string SiteName, DateTime PeriodStart, DateTime PeriodEnd, CommissionFrequency Frequency,
    CommissionBasis Basis, decimal GrossSales, decimal CardSales, decimal CashSales, decimal EligibleSales, decimal CommissionRate,
    decimal CommissionDue, IReadOnlyList<SiteCommissionMachineDto> Machines,
    IReadOnlyList<SiteCommissionProductDto> Products,
    string? DataQuality = null)
{
    public bool HasConfigurationGap { get; init; }
    public bool HasOverlap { get; init; }
    public bool UsesMultipleRates { get; init; }
}
public record SiteCommissionReportDto(DateTime From, DateTime To, IReadOnlyList<SiteCommissionRowDto> Rows)
{
    /// <summary>
    /// Whether this is an answer about commission at all (issue #731). <c>false</c> only when the
    /// provider is not usable, where the empty row list means "nothing could be calculated" and must
    /// never be read as "no site is owed anything"; the request still succeeds with <c>200</c>,
    /// because an unconnected provider is a state of this business and not a failure of the call.
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// The stable reason code for an unavailable report - <c>NayaxNotConnected</c> or
    /// <c>PermissionNotGranted</c>, the same two codes the reorder and dashboard surfaces publish -
    /// and null for an available one. A code a client branches on, never a sentence to display.
    /// </summary>
    public string? UnavailableReason { get; init; }

    /// <summary>The one sentence that explains an unavailable report; null for an available one.</summary>
    public string? UnavailableDetail { get; init; }
}
