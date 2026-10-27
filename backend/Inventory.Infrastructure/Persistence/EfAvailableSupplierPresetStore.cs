using Inventory.Application.Suppliers;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="IAvailableSupplierPresetStore"/> (issue #935, third task
/// of #817). Like every other adapter in this folder it lives beside the <see cref="AppDbContext"/>
/// and the persistence models it reads and writes.
///
/// <para><strong>Three tables, and the direction matters.</strong> It reads the business's own row
/// for the one column the state rule needs (<c>Business.State</c>, by primary key), reads the
/// enabled <see cref="SupplierPreset"/> rows, reads which of them this business already holds a copy
/// of, and inserts <see cref="Supplier"/> rows. Nothing is ever written to <c>SupplierPresets</c>,
/// and nothing existing in the business is updated or deleted: the only write this adapter makes is
/// an insert.</para>
///
/// <para><strong>The business comes from tenancy resolution, and the supplier reads and writes are
/// the ordinary scoped ones.</strong> Every <c>Suppliers</c> statement here goes through the central
/// tenant query filters, the insert is stamped by <c>BusinessOwnershipEnforcer</c> rather than by
/// this adapter, and there is no <c>IgnoreQueryFilters</c>, no raw SQL and no unscoped context - so
/// a copy cannot land in, and an existing copy cannot be read from, any business but the caller's.
/// The <see cref="BusinessId"/> it takes is only ever the one resolution produced from the
/// authenticated actor's membership; the business row is read by that key, exactly as
/// <see cref="EfBusinessSettingsStore"/> reads it, because <c>Businesses</c> carries no tenant
/// filter of its own.</para>
///
/// <para><strong>The state rule is the Domain's.</strong> <c>SupplierPresetMarketPolicy</c> decides
/// which records serve this business, and it is applied in memory rather than in SQL: a market is
/// one stored comma-separated list of codes, and a <c>LIKE</c> over it would match a code inside
/// another's text. The same policy decides the approval's copy, so the two cannot disagree.</para>
///
/// <para><strong>The filtered unique index is the rule, and this adapter only reports it.</strong>
/// <c>(BusinessId, SupplierPresetId)</c> is unique over the linked rows (issue #934), so two
/// concurrent copies of one record end with one row and one <c>SQLITE_CONSTRAINT</c>.
/// <see cref="IsDuplicateCopy"/> recognises exactly that violation and nothing else, so any other
/// database failure stays a database failure rather than being reported to a caller as "already
/// added".</para>
/// </summary>
public sealed class EfAvailableSupplierPresetStore : IAvailableSupplierPresetStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// What SQLite names in the message of the one constraint this adapter translates: it reports
    /// the offending columns rather than the index. Both are named, because <c>Suppliers</c> carries
    /// other indexes and only this pair is "this business already holds a copy of that record".
    /// </summary>
    private const string LinkIndexColumns = "Suppliers.BusinessId, Suppliers.SupplierPresetId";

    private readonly AppDbContext _db;

    public EfAvailableSupplierPresetStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AvailableSupplierPreset>> ListAvailableAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        var offered = await OfferedAsync(businessId, cancellationToken).ConfigureAwait(false);

        return offered
            .ConvertAll(preset => new AvailableSupplierPreset(preset.Id, preset.Name, preset.Url));
    }

    /// <inheritdoc />
    public async Task<SupplierPresetAvailability> FindAvailableAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken)
    {
        var preset = await EnabledPresetAsync(presetId, cancellationToken).ConfigureAwait(false);
        if (preset is null)
        {
            return SupplierPresetAvailability.NotOffered;
        }

        var state = await StateOfAsync(businessId, cancellationToken).ConfigureAwait(false);
        if (!SupplierPresetMarketPolicy.ServesState(
            preset.ServesNationwide,
            preset.ServedStates,
            state))
        {
            return SupplierPresetAvailability.NotOffered;
        }

        return await AlreadyCopiedAsync(presetId, cancellationToken).ConfigureAwait(false)
            ? SupplierPresetAvailability.AlreadyCopied
            : SupplierPresetAvailability.Available;
    }

    /// <inheritdoc />
    public async Task<SupplierRecord> CopyAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken)
    {
        // Re-read rather than taken from the caller: the name, the website and the link are the
        // platform record's, so nothing a request carried decides what is stored. A record that has
        // gone by now is a caller the availability check already answered, so this is the
        // fail-closed direction rather than a reachable path.
        var preset = await EnabledPresetAsync(presetId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Supplier preset {presetId} is no longer available to copy.");

        return await InsertCopyAsync(preset, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public bool IsDuplicateCopy(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(LinkIndexColumns, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The records this business may add, in the order the Super Admin's own list uses: by the
    /// comparison key and then the row key, so the order does not depend on the database's collation
    /// for the display name and two records differing only in case cannot swap places between reads.
    ///
    /// <para>One statement per fact, and the state rule applied once over the result: the enabled
    /// records, the business's recorded state, and the preset ids it already holds a copy of.</para>
    /// </summary>
    private async Task<List<StoredPreset>> OfferedAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        var state = await StateOfAsync(businessId, cancellationToken).ConfigureAwait(false);

        var enabled = await _db.SupplierPresets
            .AsNoTracking()
            .Where(preset => preset.IsEnabled)
            .OrderBy(preset => preset.NormalisedName)
            .ThenBy(preset => preset.Id)
            .Select(preset => new StoredPreset(
                preset.Id,
                preset.Name,
                preset.Url,
                preset.ServesNationwide,
                preset.ServedStates))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var copied = await _db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.SupplierPresetId != null)
            .Select(supplier => supplier.SupplierPresetId!.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var alreadyHeld = copied.ToHashSet();

        return enabled
            .Where(preset => !alreadyHeld.Contains(preset.Id))
            .Where(preset => SupplierPresetMarketPolicy.ServesState(
                preset.ServesNationwide,
                preset.ServedStates,
                state))
            .ToList();
    }

    /// <summary>
    /// The business's recorded state, or <see langword="null"/> when it has none - which is also the
    /// answer for a business row that is not there at all, and the fail-closed direction: a business
    /// with no state receives the nationwide records only.
    /// </summary>
    private Task<string?> StateOfAsync(BusinessId businessId, CancellationToken cancellationToken) =>
        _db.Businesses
            .AsNoTracking()
            .Where(business => business.Id == businessId.Value)
            .Select(business => business.State)
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>One enabled platform record by id, or <see langword="null"/>.</summary>
    private Task<StoredPreset?> EnabledPresetAsync(int presetId, CancellationToken cancellationToken) =>
        _db.SupplierPresets
            .AsNoTracking()
            .Where(preset => preset.Id == presetId && preset.IsEnabled)
            .Select(preset => new StoredPreset(
                preset.Id,
                preset.Name,
                preset.Url,
                preset.ServesNationwide,
                preset.ServedStates))
            .SingleOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Whether the caller's business already holds a copy of that record. An ordinary scoped read,
    /// so it answers about the caller's business and no other.
    /// </summary>
    private Task<bool> AlreadyCopiedAsync(int presetId, CancellationToken cancellationToken) =>
        _db.Suppliers
            .AsNoTracking()
            .AnyAsync(supplier => supplier.SupplierPresetId == presetId, cancellationToken);

    /// <summary>
    /// The one insert, which both the single add and the state change's top-up reach through
    /// <see cref="CopyAsync"/>, so a copy is the same row whichever created it: the record's name,
    /// its website and its id, and nothing else.
    ///
    /// <para>A row the index refuses is detached before the exception leaves, so a caller that
    /// translates the violation - or a top-up that skips it - is not left with a failed insert
    /// tracked on the context that the next <c>SaveChanges</c> would retry.</para>
    /// </summary>
    private async Task<SupplierRecord> InsertCopyAsync(
        StoredPreset preset,
        CancellationToken cancellationToken)
    {
        var supplier = new Supplier
        {
            // The name and the website, and deliberately nothing else: the contact fields stay null
            // because the platform knows no contact for a suggested supplier, and the three GST
            // defaults keep GstRules.None because a GST default is explicit configuration a business
            // makes about a supplier it buys from (AGENTS.md § Product GST rules and supplier
            // defaults). IsHidden stays false - a supplier somebody has just added is in use.
            Name = preset.Name,
            Url = preset.Url,

            // Which record this copy is of (issue #934). It is what makes the name and the website
            // read-only on this row from the moment it exists, what the unique index below counts,
            // and what a later price comparison follows back.
            SupplierPresetId = preset.Id,

            // BusinessId is deliberately left unset: the central enforcer stamps it with the
            // caller's own business on SaveChanges, exactly as it does for a supplier the business
            // types in itself, so this adapter never assigns an owner.
        };

        _db.Suppliers.Add(supplier);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _db.Entry(supplier).State = EntityState.Detached;
            throw;
        }

        return new SupplierRecord(
            supplier.Id,
            supplier.Name,
            supplier.ContactName,
            supplier.Phone,
            supplier.Email,
            supplier.Address,
            supplier.Url,
            supplier.SupplierPresetId,
            supplier.IsHidden);
    }

    /// <summary>
    /// One platform record as its columns hold it, which is what a SQL projection can produce: the
    /// served states are still the stored string here, and are read by the Domain's own rule.
    /// </summary>
    private sealed record StoredPreset(
        int Id,
        string Name,
        string? Url,
        bool ServesNationwide,
        string ServedStates);
}
