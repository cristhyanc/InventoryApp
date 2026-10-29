using Inventory.Domain.FinancialConfiguration;

namespace Inventory.Application.Commissions;

public sealed record SiteCommissionMachineReportRow(
    long MachineId,
    string MachineName,
    int TransactionCount,
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal EligibleSales,
    decimal CommissionDue,
    bool IsComplete = true,
    bool HasConfigurationGap = false,
    bool HasOverlap = false);

public sealed record SiteCommissionProductReportRow(
    string ProductName,
    int TotalVends,
    decimal TotalSales);

public sealed record SiteCommissionReportRow(
    long SiteId,
    string SiteName,
    DateTime PeriodStart,
    DateTime PeriodEnd,
    CommissionFrequency Frequency,
    CommissionBasis Basis,
    decimal GrossSales,
    decimal CardSales,
    decimal CashSales,
    decimal EligibleSales,
    decimal CommissionRate,
    decimal CommissionDue,
    decimal Paid,
    decimal Outstanding,
    DateTime? DueDate,
    string Status,
    IReadOnlyList<SiteCommissionMachineReportRow> Machines,
    IReadOnlyList<SiteCommissionProductReportRow> Products,
    IReadOnlyList<CommissionPayment> Payments,
    string? DataQuality = null)
{
    public bool HasConfigurationGap { get; init; }
    public bool HasOverlap { get; init; }
    public bool UsesMultipleRates { get; init; }
}

/// <summary>
/// Why the site commission report could not be produced for this business right now (issue #731,
/// parent #329 decision P9). Commission is calculated per site, and a site is only known from the
/// live Nayax machine fleet's <c>CustomerID</c>, so an unusable provider leaves the report with no
/// sites to report at all - not with sites that owe nothing.
///
/// <para>These are the same two per-feature availability answers issue #726 established for reorder
/// (<c>MachineNeedUnavailableReason</c>), published under the same two codes so a client meets one
/// vocabulary: they say "this feature is unavailable for this business right now", never "this
/// request failed". A transport failure, a cancellation, a <c>401</c> and the support-session
/// refusal are none of them and still propagate.</para>
///
/// <para>Numbered from 1, so no reason is reachable by forgetting to set one.</para>
/// </summary>
public enum SiteCommissionUnavailableReason
{
    /// <summary>
    /// The business has no Nayax credentials an ordinary operation may use - nothing stored, or a
    /// connection the status gate refuses (<c>NotConfigured</c>, <c>NeedsAttention</c>). An operator
    /// has to connect or reconnect Nayax; the stored agreements and payments are unaffected.
    /// </summary>
    NayaxNotConnected = 1,

    /// <summary>
    /// The credentials are usable and Nayax refused the fleet read for lack of permission
    /// (<c>403</c>). One feature's answer, not a verdict on the connection, so nothing here writes a
    /// connection status.
    /// </summary>
    PermissionNotGranted = 2,

    /// <summary>
    /// The business's effective plan does not include the sales provider (issue #967), so the
    /// credential gate refused the fleet read before the stored connection was read and no Nayax
    /// request was made. Distinct from <see cref="NayaxNotConnected"/>: the credentials may be valid
    /// and still saved, and the stored agreements and payments are unaffected either way.
    /// </summary>
    PlanRequired = 3,
}

/// <summary>
/// The stable codes <see cref="SiteCommissionUnavailableReason"/> is published under, written out
/// rather than taken from the enum member so renaming a C# member cannot silently change the API
/// contract. They are deliberately spelled exactly as <c>MachineNeedUnavailableReasons</c> spells
/// the same two answers.
/// </summary>
public static class SiteCommissionUnavailableReasons
{
    /// <summary>The published code for <see cref="SiteCommissionUnavailableReason.NayaxNotConnected"/>.</summary>
    public const string NayaxNotConnected = "NayaxNotConnected";

    /// <summary>The published code for <see cref="SiteCommissionUnavailableReason.PermissionNotGranted"/>.</summary>
    public const string PermissionNotGranted = "PermissionNotGranted";

    /// <summary>
    /// The published code for <see cref="SiteCommissionUnavailableReason.PlanRequired"/> (issue
    /// #967), spelled exactly as every other per-feature vocabulary spells the same answer.
    /// </summary>
    public const string PlanRequired = "PlanRequired";

    /// <exception cref="ArgumentOutOfRangeException">
    /// A reason this vocabulary has never heard of - a value cast from an integer, which an enum
    /// cannot prevent. Refused rather than published under a guessed code.
    /// </exception>
    public static string Code(SiteCommissionUnavailableReason reason) => reason switch
    {
        SiteCommissionUnavailableReason.NayaxNotConnected => NayaxNotConnected,
        SiteCommissionUnavailableReason.PermissionNotGranted => PermissionNotGranted,
        SiteCommissionUnavailableReason.PlanRequired => PlanRequired,
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown site commission unavailability reason."),
    };

    /// <summary>
    /// The one sentence each reason is explained with, shared by the API response and every report
    /// that composes this one, so a reader meets the same explanation wherever commission is
    /// withheld.
    /// </summary>
    public static string Describe(SiteCommissionUnavailableReason reason) => reason switch
    {
        SiteCommissionUnavailableReason.NayaxNotConnected =>
            "Site commission is unavailable because Nayax is not connected for this business, so no machine can be matched to a site. The amounts shown are not a measured zero.",
        SiteCommissionUnavailableReason.PermissionNotGranted =>
            "Site commission is unavailable because Nayax refused the machine list for lack of permission, so no machine can be matched to a site. The amounts shown are not a measured zero.",
        SiteCommissionUnavailableReason.PlanRequired =>
            "Site commission is unavailable because this business's plan does not include the Nayax connection, so no machine can be matched to a site. The stored agreements and payments are unchanged, and the amounts shown are not a measured zero.",
        _ => throw new ArgumentOutOfRangeException(
            nameof(reason), reason, "Unknown site commission unavailability reason."),
    };
}

public sealed record SiteCommissionReport(
    DateTime From,
    DateTime To,
    IReadOnlyList<SiteCommissionReportRow> Rows)
{
    /// <summary>
    /// Whether this report is an answer about commission at all (issue #731). <see langword="true"/>
    /// for every report built from a fleet read that succeeded, including one that legitimately
    /// found no site. <see langword="false"/> only when the provider is not usable, where the empty
    /// row list means "nothing could be calculated" and must never be read as "no site is owed
    /// anything".
    /// </summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// Why it is unavailable, for an unavailable report, and <see langword="null"/> for an available
    /// one - where there is nothing to explain.
    /// </summary>
    public SiteCommissionUnavailableReason? UnavailableReason { get; init; }

    /// <summary>
    /// The unavailable answer: the requested period, no rows at all, and the reason.
    ///
    /// <para>A factory rather than an object initializer so an unavailable report cannot be built
    /// holding a row - the empty list is the only shape this answer has - and so the reason is not
    /// optional in the one case that needs it.</para>
    /// </summary>
    public static SiteCommissionReport Unavailable(
        DateTime from, DateTime to, SiteCommissionUnavailableReason reason) =>
        new(from, to, [])
        {
            IsAvailable = false,
            UnavailableReason = reason,
        };
}
