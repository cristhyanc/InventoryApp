using Inventory.Application.PlatformOptions;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IMenuVisibilityStore"/> (issue #1148, replacing issue
/// #873's single-row <c>EfOptionVisibilityStore</c>). Like every other adapter in this folder it
/// lives beside the <see cref="AppDbContext"/> and the persistence model it reads and writes.
///
/// <para><strong>One table, and nothing else at all.</strong> This adapter touches
/// <c>PlatformMenuVisibility</c> and no other table - the superseded <c>PlatformOptionVisibility</c>
/// included, which nothing writes any more. The write it serves carries a <em>denied</em>
/// <c>BusinessScope</c>, so the tenant query filters and <c>BusinessOwnershipEnforcer</c> remain
/// fully in force over everything else on the context; the write passes the enforcer because
/// <see cref="Models.PlatformMenuVisibility"/> is not <c>IBusinessOwned</c>, not because anything
/// was relaxed. There is no <c>IgnoreQueryFilters</c> here and no statement over a tenant-owned
/// table - including on the read path, which an ordinary member's request makes.</para>
///
/// <para><strong>One row per key is the primary key's rule, not a query's.</strong> The row's key is
/// the catalogue key the caller named and the database assigns nothing, so two writes to the
/// <em>same</em> destination that both find no row cannot leave two rows: the second one's insert
/// violates the primary key, and <see cref="SetAsync"/> turns that into the update it should have
/// been. Only that violation is recognised; any other database failure stays a database failure.
/// Two writes to <em>different</em> destinations never contend at all: each statement names its own
/// row, so neither can overwrite the other's answer.</para>
/// </summary>
public sealed class EfMenuVisibilityStore : IMenuVisibilityStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a primary-key violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    private readonly AppDbContext _db;

    public EfMenuVisibilityStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredMenuVisibility>> ListAsync(CancellationToken cancellationToken)
    {
        // Every stored row, as it is stored. A key with no row is absent rather than reported as
        // offered: what "nothing stored" means is GetMenuVisibility's decision, in one place, and
        // never an adapter's. There is no filter by catalogue key either - which keys exist is the
        // catalogue's answer, so a row it no longer declares is dropped there rather than hidden by
        // a predicate here that would have to be kept in step with it.
        var stored = await _db.PlatformMenuVisibility
            .AsNoTracking()
            .Select(row => new { row.Key, row.Visible })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. stored.Select(row => new StoredMenuVisibility(row.Key, row.Visible))];
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        string key,
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WriteAsync(key, visible, updatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRowAlreadyInserted(exception))
        {
            // Another request inserted this key's row between this one's read and its insert. The row
            // exists now, so the write this request asked for is an update - and the caller gets the
            // answer they would have got had the two arrived in the other order.
            _db.ChangeTracker.Clear();
            return await WriteAsync(key, visible, updatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> WriteAsync(
        string key,
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var row = await _db.PlatformMenuVisibility
            .SingleOrDefaultAsync(candidate => candidate.Key == key, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new Models.PlatformMenuVisibility
            {
                Key = key,
                Visible = visible,
                UpdatedAtUtc = updatedAtUtc,
            };
            _db.PlatformMenuVisibility.Add(row);
        }
        else
        {
            row.Visible = visible;
            row.UpdatedAtUtc = updatedAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return row.Visible;
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the primary key refusing a second row for the same
    /// destination, and nothing else.
    /// </summary>
    private static bool IsRowAlreadyInserted(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains("PlatformMenuVisibility.Key", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
