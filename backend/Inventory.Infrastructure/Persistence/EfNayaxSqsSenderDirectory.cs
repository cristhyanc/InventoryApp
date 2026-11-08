using Inventory.Application.NayaxSqs;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxSqsSenderDirectory"/> (issue #1103, extended by
/// issue #1104, both under the repository owner's approval of 5 November 2026):
/// <strong>two read-only statements</strong> that answer which business an AWS IAM user sends Nayax
/// sales for, and which businesses are on the queue at all.
///
/// <para><strong>Why it has to ignore the tenant query filters.</strong> It runs before any business
/// scope exists, because the business it is looking for is the answer rather than the input: a
/// received message carries the SQS <c>SenderId</c> system attribute and nothing else that could say
/// whose sale it is. The queue reader's own loop is not a request and resolves no actor, so the
/// context it holds has resolved no business and the central filter
/// <c>!TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> matches none of the sender
/// rows. This is therefore the <strong>eighth</strong> reviewed <c>IgnoreQueryFilters</c> path, and
/// the first one that is not in a request at all (AGENTS.md § Tenant ownership and data isolation).
/// The alternatives were the ones issue #544 already rejected and are worse here: an unscoped
/// <c>AppDbContext</c> would hand one adapter every tenant-owned table at once, and resolving each
/// business into a scope in turn would mean impersonating every business to find one.</para>
///
/// <para><strong>What makes it narrow is what it may touch, and it is written out here.</strong> One
/// method, which takes a sender id and nothing else - no predicate, no column list, no page, no sort,
/// no business id. One statement, over one table, projecting two columns of it: the ownership key,
/// which is not business data, and the enabled flag. Beside them it reads the <c>Businesses</c> row's
/// lifecycle status, which carries no tenant filter at all and no ownership question. Nothing a
/// business trades, buys, sells, counts, costs, reports or stores as a credential is reachable
/// through it; it cannot be written through; and no sender id, instant or revision is echoed
/// back.</para>
///
/// <para><strong>The lookup has exactly one answer because the index says so.</strong>
/// <c>BusinessNayaxSqsSender.AwsSenderId</c> is unique <em>globally</em>, filtered to non-null values
/// (issue #1101, the owner's approval of 6 November 2026, and the one declared exception to
/// "uniqueness is per business"), which is precisely what a lookup with no business to narrow it by
/// needs: a per-business index would make this a scan over a value two businesses could both hold.
/// <c>SingleOrDefault</c> is therefore the right shape, and a second row would be a loud failure
/// rather than a silent choice.</para>
///
/// <para><strong>An unknown sender is <c>null</c>, never a guess.</strong> No fuzzy match, no case
/// folding, no prefix: the id is stored exactly as a Super Admin entered it and is compared exactly.
/// A sender nothing has recorded is a message this application must not attribute to anybody.</para>
///
/// <para><strong>The second statement is issue #1104's eligibility read, and it is narrower than the
/// first.</strong> The hourly reconciliation has to know whose <c>lastSales</c> to compare, and that
/// answer is a <em>set of businesses</em> with nothing to narrow it by either - so it ignores the
/// filters for the same reason, in the same adapter, and projects <strong>only the ownership
/// key</strong>: not the sender id, not the enabled flag, not an instant, not a revision, not a name.
/// It takes no predicate, no page, no sort and no argument of any kind, so there is no input a caller
/// could widen it with, and its rule is the one the message path already applies - an enabled
/// mapping on an active business - so "whose sales do we accept?" and "whose sales do we verify?"
/// cannot drift apart. It is reached only from the in-process reconciliation runner's own loop, which
/// is not a request and resolves no actor.</para>
/// </summary>
public sealed class EfNayaxSqsSenderDirectory : INayaxSqsSenderDirectory
{
    private readonly AppDbContext _db;

    public EfNayaxSqsSenderDirectory(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<NayaxSqsSenderAttribution?> FindBusinessBySenderAsync(
        string? senderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(senderId))
        {
            // Nothing to look up, and nothing to read the database for: a message with no sender
            // attribute belongs to nobody, which the reader has already refused before reaching here.
            return null;
        }

        // The one statement. The join to Businesses is an ordinary unfiltered read of a structurally
        // non-tenant-owned table, and what it contributes is the lifecycle status alone - so "whose
        // sale is this, and is that business still trading?" is one answer rather than two reads that
        // could disagree between them.
        return await _db.BusinessNayaxSqsSenders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(sender => sender.AwsSenderId == senderId)
            .Join(
                _db.Businesses.AsNoTracking(),
                sender => sender.BusinessId,
                business => business.Id,
                (sender, business) => new NayaxSqsSenderAttribution(
                    sender.BusinessId, sender.IsEnabled, business.Status))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListEligibleBusinessIdsAsync(
        CancellationToken cancellationToken)
    {
        // The second statement, and the whole of it. One projection - the ownership key - from one
        // tenant-owned table, joined to the structurally non-tenant-owned Businesses row for the
        // lifecycle status alone, and ordered by the key so a sweep's order is the same every time
        // rather than whatever the query plan produced.
        return await _db.BusinessNayaxSqsSenders
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(sender => sender.IsEnabled)
            .Join(
                _db.Businesses.AsNoTracking().Where(business => business.Status == BusinessStatus.Active),
                sender => sender.BusinessId,
                business => business.Id,
                (sender, _) => sender.BusinessId)
            .OrderBy(businessId => businessId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }
}
