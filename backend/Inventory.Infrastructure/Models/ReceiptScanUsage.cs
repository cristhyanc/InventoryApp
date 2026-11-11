using System.Text.Json.Serialization;

namespace Inventory.Infrastructure.Models;

/// <summary>
/// How many receipt scans one business has made in one of its own calendar months (issue #1164,
/// sub-issue C of #975).
///
/// <para><strong>Why a counter and not a row per scan.</strong> What the cap needs is one number per
/// business per month, and nothing about the scan itself may be kept: a receipt is a business
/// document and the products on it are the business's own data, so the privacy rule issue #1163
/// wrote into <c>IReceiptScanLog</c>'s shape holds here too. There is deliberately no column that
/// could carry a file name, a supplier, a product, an amount, a draft or an outcome - a row says that
/// <em>n</em> scans happened in a month and nothing whatever about what was on them.</para>
///
/// <para><strong>The month is the business's own calendar month</strong>, stored as
/// <c>year * 100 + month</c> by <c>Inventory.Domain.ReceiptScanning.ReceiptScanQuotaPolicy</c> from a
/// business date <c>IBusinessCalendar</c> derived in the business's configured zone. That is what
/// makes the count start again at zero when the business's month turns over rather than when the
/// host's or UTC's does (AGENTS.md § Reporting dates and filters).</para>
///
/// <para><strong>Tenant-owned, with nothing added.</strong> A month's scan count is a fact about one
/// business, so the central mechanism gives it the business key, its index, the query filter and the
/// ownership stamp; no adapter writes a <c>BusinessId</c> of its own, and the unique index over
/// <c>(BusinessId, MonthKey)</c> is per business exactly as every other uniqueness rule over
/// tenant-owned data is.</para>
///
/// <para><strong>Nothing deletes or rewrites one.</strong> A month that has passed keeps its count,
/// which is the evidence of what was spent; a new month is a new row.</para>
/// </summary>
public class ReceiptScanUsage : IBusinessOwned
{
    /// <summary>The row's own key, assigned by the database.</summary>
    public int Id { get; set; }

    /// <inheritdoc />
    [JsonIgnore]
    public int BusinessId { get; set; }

    /// <summary>
    /// The business's calendar month this count is for, as <c>year * 100 + month</c> - so November
    /// 2026 is <c>202611</c>. An integer rather than text because it is a key under a unique index
    /// and an ordering, and it is produced in exactly one place.
    /// </summary>
    public int MonthKey { get; set; }

    /// <summary>
    /// How many scans have been counted in that month. Incremented once per call made to the reader,
    /// inside the write-locked transaction that compared it with the cap, and never decremented: a
    /// scan that failed after the call still spent the money.
    /// </summary>
    public int ScanCount { get; set; }

    /// <summary>When the month's first scan was counted, from the application clock.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the most recent one was, from the application clock.</summary>
    public DateTime UpdatedAtUtc { get; set; }
}
