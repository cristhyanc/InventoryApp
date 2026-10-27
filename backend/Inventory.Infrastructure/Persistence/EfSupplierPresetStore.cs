using System.Linq.Expressions;
using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Suppliers;
using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="ISupplierPresetStore"/> (issue #848). Like every other
/// adapter in this folder it lives beside the <see cref="AppDbContext"/> and the persistence model it
/// reads and writes.
///
/// <para><strong>One table, and nothing else at all.</strong> This adapter touches
/// <c>SupplierPresets</c> and no other table - in particular not <c>Suppliers</c>, which is a
/// business's own tenant-owned data. The request it serves carries a <em>denied</em>
/// <c>BusinessScope</c>, so the tenant query filters and <c>BusinessOwnershipEnforcer</c> remain
/// fully in force over everything else on the context; a preset write passes the enforcer because
/// <see cref="Models.SupplierPreset"/> is not <c>IBusinessOwned</c>, not because anything was
/// relaxed. There is no <c>IgnoreQueryFilters</c> here and no statement over a tenant-owned table.</para>
///
/// <para><strong>Uniqueness is the index's, and this adapter only reports it.</strong> The
/// normalised-name column carries a unique index, so two concurrent inserts of one name end with one
/// row and one <c>SQLITE_CONSTRAINT</c>. <see cref="IsDuplicateName"/> recognises exactly that
/// violation and nothing else, so any other database failure stays a database failure rather than
/// being reported to a caller as a name conflict.</para>
/// </summary>
public sealed class EfSupplierPresetStore : ISupplierPresetStore
{
    /// <summary>SQLITE_CONSTRAINT, the primary result code a unique-index violation reports.</summary>
    private const int SqliteConstraintViolation = 19;

    /// <summary>
    /// What SQLite names in the message of the one constraint this adapter translates: it reports
    /// the offending columns rather than the index, and this table has exactly one unique index, so
    /// naming the column is unambiguous here.
    /// </summary>
    private const string NormalisedNameIndexColumn = "SupplierPresets.NormalisedName";

    /// <summary>
    /// The one projection every read uses, so a column added to the entity cannot reach one read and
    /// not another. It deliberately omits <c>NormalisedName</c>: it is an index key, not a field of
    /// the resource, and no caller compares names itself.
    ///
    /// <para>It projects the served states as the <em>stored</em> string, which
    /// <see cref="ToRecord"/> then reads with the Domain's own rule. The split exists because this
    /// expression is translated to SQL: a method call inside it could not be, and running the parse
    /// on the database would mean a second answer to what a stored market means.</para>
    /// </summary>
    private static readonly Expression<Func<Models.SupplierPreset, StoredSupplierPreset>> Projection =
        preset => new StoredSupplierPreset(
            preset.Id,
            preset.Name,
            preset.Url,
            preset.IsEnabled,
            preset.ServesNationwide,
            preset.ServedStates,
            preset.PricesIncludeGst,
            preset.PriceConnector,
            preset.PriceConnectorKey,
            preset.CreatedAtUtc,
            preset.UpdatedAtUtc);

    private readonly AppDbContext _db;

    public EfSupplierPresetStore(AppDbContext db)
    {
        _db = db;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SupplierPresetRecord>> ListOrderedByNameAsync(
        CancellationToken cancellationToken)
    {
        var stored = await _db.SupplierPresets
            .AsNoTracking()
            // By the comparison key and then the row key, so the order does not depend on the
            // database's collation for the display name and two presets that differ only in case
            // cannot swap places between reads.
            .OrderBy(preset => preset.NormalisedName)
            .ThenBy(preset => preset.Id)
            .Select(Projection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return stored.ConvertAll(ToRecord);
    }

    /// <inheritdoc />
    public Task<bool> ExistsWithNormalisedNameAsync(
        string normalisedName,
        int? excludingId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(normalisedName);

        // An exact comparison on the stored normalised value, which is the whole point of storing
        // it: no LOWER(), no NOCASE collation and no provider-dependent case folding is involved, so
        // this read asks the same question the unique index answers.
        var matching = _db.SupplierPresets
            .AsNoTracking()
            .Where(preset => preset.NormalisedName == normalisedName);

        // An edit excludes its own row: without that, saving a preset without renaming it would be
        // refused as a duplicate of itself.
        if (excludingId is { } id)
        {
            matching = matching.Where(preset => preset.Id != id);
        }

        return matching.AnyAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SupplierPresetRecord> AddAsync(
        NewSupplierPreset preset,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);

        var row = new Models.SupplierPreset
        {
            Name = preset.Name,
            NormalisedName = preset.NormalisedName,
            Url = preset.Url,
            // A new preset is suggested: a preset created already disabled would be a row nobody
            // asked for, and disabling is an edit away.
            IsEnabled = true,
            ServesNationwide = preset.ServesNationwide,
            ServedStates = preset.ServedStates,
            PricesIncludeGst = preset.PricesIncludeGst,
            PriceConnector = preset.PriceConnector,
            PriceConnectorKey = preset.PriceConnectorKey,
            CreatedAtUtc = preset.CreatedAtUtc,
            UpdatedAtUtc = preset.CreatedAtUtc,
        };

        _db.SupplierPresets.Add(row);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(Stored(row));
    }

    /// <inheritdoc />
    public async Task<SupplierPresetRecord?> UpdateAsync(
        int id,
        SupplierPresetEdit edit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edit);

        var row = await _db.SupplierPresets
            .SingleOrDefaultAsync(preset => preset.Id == id, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            return null;
        }

        row.Name = edit.Name;
        row.NormalisedName = edit.NormalisedName;
        row.Url = edit.Url;
        row.IsEnabled = edit.IsEnabled;
        row.ServesNationwide = edit.ServesNationwide;
        row.ServedStates = edit.ServedStates;
        row.PricesIncludeGst = edit.PricesIncludeGst;
        row.PriceConnector = edit.PriceConnector;
        row.PriceConnectorKey = edit.PriceConnectorKey;
        row.UpdatedAtUtc = edit.UpdatedAtUtc;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return ToRecord(Stored(row));
    }

    /// <inheritdoc />
    public bool IsDuplicateName(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite
                && sqlite.SqliteErrorCode == SqliteConstraintViolation
                && sqlite.Message.Contains(NormalisedNameIndexColumn, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The one place a stored row becomes the Application's record - every read and both writes go
    /// through it - so a read and a write cannot report the same row differently.
    ///
    /// <para>The served states are read with <c>SupplierPresetMasterPolicy.ReadServedStates</c> - the
    /// Domain's own inverse of the value a use case stored - rather than split here. The column and
    /// the published list are then one fact with one owner, and this adapter decides nothing about
    /// what a market means, exactly as it decides nothing about what two names being equal means.</para>
    /// </summary>
    private static SupplierPresetRecord ToRecord(StoredSupplierPreset row) =>
        new(
            row.Id,
            row.Name,
            row.Url,
            row.IsEnabled,
            row.ServesNationwide,
            SupplierPresetMasterPolicy.ReadServedStates(row.ServedStates),
            row.PricesIncludeGst,
            row.PriceConnector,
            row.PriceConnectorKey,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);

    /// <summary>
    /// The tracked entity a write just saved, as the same columns a read projects, so the two paths
    /// meet at <see cref="ToRecord"/> instead of each assembling a record of their own.
    /// </summary>
    private static StoredSupplierPreset Stored(Models.SupplierPreset row) =>
        new(
            row.Id,
            row.Name,
            row.Url,
            row.IsEnabled,
            row.ServesNationwide,
            row.ServedStates,
            row.PricesIncludeGst,
            row.PriceConnector,
            row.PriceConnectorKey,
            row.CreatedAtUtc,
            row.UpdatedAtUtc);

    /// <summary>
    /// One preset exactly as its columns hold it, which is what a SQL projection can produce: the
    /// served states are still the stored string here, and become a list only in
    /// <see cref="ToRecord"/>.
    /// </summary>
    private sealed record StoredSupplierPreset(
        int Id,
        string Name,
        string? Url,
        bool IsEnabled,
        bool ServesNationwide,
        string ServedStates,
        bool PricesIncludeGst,
        SupplierPriceConnector? PriceConnector,
        string? PriceConnectorKey,
        DateTime CreatedAtUtc,
        DateTime UpdatedAtUtc);
}
