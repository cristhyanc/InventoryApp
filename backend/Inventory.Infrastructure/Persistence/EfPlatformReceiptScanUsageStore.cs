using Inventory.Application.PlatformReceiptScans;
using Inventory.Application.Time;
using Inventory.Domain.ReceiptScanning;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IPlatformReceiptScanUsageStore"/> (issue #1164,
/// sub-issue C of #975, under that issue's own acceptance criterion that the Super Admin's business
/// detail shows scans used this month). Like every other adapter in this folder it lives beside the
/// <see cref="AppDbContext"/> and the persistence models it reads.
///
/// <para><strong>Two tables, read-only, and the whole of what this surface may reach.</strong> It
/// reads the <c>Businesses</c> row the route names - structurally non-tenant-owned, so no filter
/// applies to it - projecting its key and its stored time zone and nothing else, and reads that one
/// business's <c>ReceiptScanUsages</c> row for the month that zone puts it in. No receipt, draft,
/// purchase, product, supplier, sale, cost, machine, site, document, credential or plan record is
/// readable here, nothing is writable here at all, and no method takes a predicate, a column list, a
/// page, a sort, a month to ask for or a second business.</para>
///
/// <para><strong>The bypass, and why it is this narrow.</strong> The request it serves is the
/// platform administrator's, who is a member of no business, so it carries a <em>denied</em>
/// <c>BusinessScope</c>: the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of a business's
/// counter rows. This adapter therefore makes exactly one <c>IgnoreQueryFilters</c> statement, in one
/// place (<see cref="UsageOf"/>), pinned to the single business id the route named and projecting one
/// integer - this is the <strong>eighth</strong> reviewed <c>IgnoreQueryFilters</c> path in a request
/// (AGENTS.md § Tenant ownership and data isolation), and it is the fourth and seventh paths' terms
/// exactly: one business, one table, one projection. The alternatives were the ones issue #544
/// already rejected - an unscoped context in a request path would hand one adapter every tenant-owned
/// table at once, a second read-only connection would be a second cross-business path beside the
/// diagnostics one, and resolving the business into the request scope would make a platform read
/// impersonate a tenant. It takes <strong>no write lease of any kind</strong>, because it writes
/// nothing. A wider projection, a statement over another tenant-owned table, a predicate on anything
/// but that one business id, or any write here is a boundary change needing its own reviewed issue -
/// not an implementation detail.</para>
///
/// <para><strong>Which month it is, is the business's own answer.</strong> The month comes from a
/// business date derived in the zone stored on that business's row, through the same
/// <see cref="ZonedBusinessCalendar"/> the request-scoped calendar uses - so the platform page reports
/// the month the business is in rather than the month the administrator's browser or the host is in.
/// A stored zone this host cannot resolve <strong>fails closed</strong> with
/// <see cref="BusinessTimeZoneUnavailableException"/> rather than falling back to
/// <c>Australia/Sydney</c>, the host's zone or UTC: a count reported against the wrong month is a
/// count reported against the wrong cap. <see cref="IBusinessCalendar"/> itself cannot be used here,
/// because it resolves the zone of the business the <em>request</em> resolved, and a platform-admin
/// request resolves none.</para>
/// </summary>
public sealed class EfPlatformReceiptScanUsageStore : IPlatformReceiptScanUsageStore
{
    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public EfPlatformReceiptScanUsageStore(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<PlatformReceiptScanUsage?> FindAsync(
        int businessId,
        CancellationToken cancellationToken)
    {
        // The business row first, and not as proof of tenancy: it is what makes the route value the
        // subject of a read rather than something a request chose, and an id matching no row is
        // simply not found. Two columns, and the zone is the one the month is derived in.
        var business = await _db.Businesses
            .AsNoTracking()
            .Where(candidate => candidate.Id == businessId)
            .Select(candidate => new { candidate.Id, candidate.TimeZoneId })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (business is null)
        {
            return null;
        }

        if (!BusinessTimeZones.TryResolve(business.TimeZoneId, out var timeZone))
        {
            throw new BusinessTimeZoneUnavailableException(business.TimeZoneId);
        }

        var monthKey = ReceiptScanQuotaPolicy.MonthKeyFor(
            new ZonedBusinessCalendar(_clock, timeZone).Today);

        var used = await UsageOf(business.Id, monthKey)
            .AsNoTracking()
            .Select(row => (int?)row.ScanCount)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // A month with no counter has made no scans, which is zero and not an absent business.
        return new PlatformReceiptScanUsage(business.Id, monthKey, used ?? 0);
    }

    /// <summary>
    /// The one filter-ignoring statement this adapter makes, in one place: that single business's
    /// counter for that single month. Both values are the server's own - the key the business row
    /// carried, and the month its stored zone put it in - so there is no shape of this query that
    /// reaches a second business or a month a caller asked for.
    /// </summary>
    private IQueryable<Models.ReceiptScanUsage> UsageOf(int businessId, int monthKey) =>
        _db.ReceiptScanUsages
            .IgnoreQueryFilters()
            .Where(row => row.BusinessId == businessId && row.MonthKey == monthKey);
}
