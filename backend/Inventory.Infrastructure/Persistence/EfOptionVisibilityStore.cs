using Inventory.Application.PlatformOptions;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IOptionVisibilityStore"/> (issue #873). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model it
/// reads and writes.
///
/// <para><strong>One table, one row, and nothing else at all.</strong> This adapter touches
/// <c>PlatformOptionVisibility</c> and no other table. The write it serves carries a <em>denied</em>
/// <c>BusinessScope</c>, so the tenant query filters and <c>BusinessOwnershipEnforcer</c> remain
/// fully in force over everything else on the context; the write passes the enforcer because
/// <see cref="Models.PlatformOptionVisibility"/> is not <c>IBusinessOwned</c>, not because anything
/// was relaxed. There is no <c>IgnoreQueryFilters</c> here and no statement over a tenant-owned
/// table - including on the read path, which an ordinary member's request makes.</para>
///
/// <para><strong>The singleton is the primary key's rule, not a query's.</strong> The row's key is
/// the fixed <see cref="Models.PlatformOptionVisibility.SingletonId"/> and the database assigns
/// nothing, so two writes that both find no row cannot leave two rows: the second one's insert
/// violates the primary key, and <see cref="SetHistoricalGstClassificationVisibleAsync"/> turns that
/// into the update it should have been. Only that violation is recognised; any other database
/// failure stays a database failure.</para>
/// </summary>
public sealed class EfOptionVisibilityStore : IOptionVisibilityStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a primary-key violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    private readonly AppDbContext _db;

    public EfOptionVisibilityStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<OptionVisibilitySettings?> FindAsync(CancellationToken cancellationToken)
    {
        var stored = await _db.PlatformOptionVisibility
            .AsNoTracking()
            .Select(row => new { row.HistoricalGstClassificationVisible })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // No row is the shipped state and is reported as it is: what "nothing stored" means is
        // GetOptionVisibility's decision, in one place, and never an adapter's.
        return stored is null
            ? null
            : new OptionVisibilitySettings(stored.HistoricalGstClassificationVisible);
    }

    /// <inheritdoc />
    public async Task<OptionVisibilitySettings> SetHistoricalGstClassificationVisibleAsync(
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            return await WriteAsync(visible, updatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsSingletonAlreadyInserted(exception))
        {
            // Another request inserted the one row between this one's read and its insert. The row
            // exists now, so the write this request asked for is an update - and the caller gets the
            // answer they would have got had the two arrived in the other order.
            _db.ChangeTracker.Clear();
            return await WriteAsync(visible, updatedAtUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<OptionVisibilitySettings> WriteAsync(
        bool visible,
        DateTime updatedAtUtc,
        CancellationToken cancellationToken)
    {
        var row = await _db.PlatformOptionVisibility
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            row = new Models.PlatformOptionVisibility
            {
                Id = Models.PlatformOptionVisibility.SingletonId,
                HistoricalGstClassificationVisible = visible,
                UpdatedAtUtc = updatedAtUtc,
            };
            _db.PlatformOptionVisibility.Add(row);
        }
        else
        {
            row.HistoricalGstClassificationVisible = visible;
            row.UpdatedAtUtc = updatedAtUtc;
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new OptionVisibilitySettings(row.HistoricalGstClassificationVisible);
    }

    /// <summary>
    /// Whether <paramref name="exception"/> is the primary key refusing a second row of this
    /// single-row table, and nothing else.
    /// </summary>
    private static bool IsSingletonAlreadyInserted(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains("PlatformOptionVisibility.Id", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
