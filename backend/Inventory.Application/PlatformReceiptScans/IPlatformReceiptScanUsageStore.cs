namespace Inventory.Application.PlatformReceiptScans;

/// <summary>
/// One business's receipt-scan count for its own current calendar month, as the Super Admin's
/// business detail reads it (issue #1164, sub-issue C of #975, that issue's own acceptance
/// criterion).
///
/// <para>Three values, and they are the whole of the projection the business-pinned read is allowed
/// to make: the key of the business row that was found, the month the count is about and the count.
/// Nothing about any receipt, any product, any supplier, any purchase or any other business can
/// travel on it, because there is no field for one.</para>
/// </summary>
/// <param name="BusinessId">The key of the business row that was found, not the route value.</param>
/// <param name="MonthKey">
/// That business's current calendar month in its <em>own</em> time zone, as
/// <c>year * 100 + month</c>. It is resolved from the zone stored on the business row, so the
/// platform page reports the month the business is actually in rather than the month the
/// administrator's browser or the host happens to be in.
/// </param>
/// <param name="Used">
/// How many scans have been counted in that month. A month with no counter has made none, which is
/// zero.
/// </param>
public sealed record PlatformReceiptScanUsage(int BusinessId, int MonthKey, int Used);

/// <summary>
/// The narrow persistence port over <strong>one business's monthly receipt-scan count</strong>, as
/// the Super Admin's business detail shows it (issue #1164).
///
/// <para><strong>It is deliberately its own port, and it is read-only.</strong>
/// <c>IPlatformBusinessDirectoryStore</c> reaches a business's lifecycle status, its memberships and
/// - with one reviewed two-column exception - no tenant-owned row at all, and it stays that way: a
/// scan count is tenant-owned, so widening the directory to read one would make the directory the
/// thing that reads a business's own data, and would make it a <em>list</em> across businesses. What
/// is here instead is one method over exactly one table, <c>ReceiptScanUsages</c>, plus the business
/// row the route named, pinned to that single business id - and there is no write of any kind on it,
/// so no lease is taken and the Super Admin cannot change what a business has used.</para>
///
/// <para><strong>The business is a parameter here, and that is the exception's whole shape.</strong>
/// Every tenant-owned read in this application is scoped to the business the caller's own membership
/// resolved; a platform read has to name the business it is about, so the id travels in - checked
/// against the stored business row, and the answer addressed to the key that row carries. It resolves
/// no tenancy for the request, which still carries a <em>denied</em> <c>BusinessScope</c>, and the
/// adapter behind this port is the only one permitted to read this table under one (see
/// <c>EfPlatformReceiptScanUsageStore</c>). No method takes a predicate, a column list, a page, a
/// sort, a month to ask for or more than one business.</para>
/// </summary>
public interface IPlatformReceiptScanUsageStore
{
    /// <summary>
    /// The business's current month and its scan count in it, or <see langword="null"/> when no
    /// business row has that key.
    /// </summary>
    /// <exception cref="Inventory.Application.Time.BusinessTimeZoneUnavailableException">
    /// The business's stored time zone is one this host cannot resolve, so which month it is in
    /// cannot be derived. It fails closed rather than answering in <c>Australia/Sydney</c>, the
    /// host's zone or UTC - a count reported for the wrong month is a count reported for the wrong
    /// cap (AGENTS.md § Reporting dates and filters).
    /// </exception>
    Task<PlatformReceiptScanUsage?> FindAsync(int businessId, CancellationToken cancellationToken);
}
