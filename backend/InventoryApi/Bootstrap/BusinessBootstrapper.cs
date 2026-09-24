using System.Globalization;
using Inventory.Domain.Tenancy;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Assigns the existing single-business data to its Business record (issue #64, checkpoint 3).
///
/// This is the one place a backfill happens, and it is never automatic. It runs only when a
/// human invokes the <c>bootstrap-business</c> command, so applying migrations - including the
/// <c>Database.Migrate()</c> call at API startup - can never initiate it as a side effect.
///
/// The four properties that make it safe to point at real financial history:
/// <list type="bullet">
///   <item><b>Deterministic.</b> The table list comes from the EF model, ordered by table name,
///   and each table is a single <c>WHERE BusinessId = 0</c> update. The same database in the
///   same state always produces the same result.</item>
///   <item><b>Restart-safe.</b> It only ever touches unassigned rows, so a run interrupted
///   halfway can simply be run again; already-assigned tables become no-ops. The Business and
///   its memberships are resolved rather than duplicated for the same reason.</item>
///   <item><b>Reviewable.</b> Before/after row counts and financial totals are verified inside
///   the transaction and written to <see cref="BusinessBackfillAudit"/>. A dry run reports
///   exactly the same numbers and rolls back.</item>
///   <item><b>Identity is never invented.</b> The Entra mapping is human-supplied
///   configuration. Missing or malformed, the operation refuses and changes nothing.</item>
/// </list>
/// </summary>
public sealed class BusinessBootstrapper
{
    /// <summary>
    /// Not a valid business key, and the default every tenant-owned row was given by the
    /// checkpoint-2 migration. It is the definition of "not yet assigned to anyone".
    /// </summary>
    private const int Unassigned = 0;

    private readonly AppDbContext _db;
    private readonly BusinessBootstrapOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <param name="db">
    /// Must be an unrestricted context: the backfill deliberately works across the tenant
    /// boundary, which is exactly why that access is an explicit opt-in.
    /// </param>
    /// <param name="options">The human-supplied Entra mapping; never defaulted or inferred.</param>
    /// <param name="timeProvider">Supplies the audit timestamp, so tests can pin it.</param>
    public BusinessBootstrapper(AppDbContext db, BusinessBootstrapOptions options, TimeProvider timeProvider)
    {
        _db = db;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// The financial and inventory totals the backfill must leave untouched. Assigning ownership
    /// writes one integer column, so any movement here means something else changed and the run
    /// must be abandoned.
    /// </summary>
    private static readonly (string Name, string Sql)[] VerificationTotals =
    [
        ("Products.InventoryValue", "SELECT COALESCE(SUM(InventoryValue), 0) FROM Products"),
        ("Products.CostingQuantity", "SELECT COALESCE(SUM(CostingQuantity), 0) FROM Products"),
        ("Products.QuantityInStock", "SELECT COALESCE(SUM(QuantityInStock), 0) FROM Products"),
        ("NayaxSales.SettlementValue", "SELECT COALESCE(SUM(SettlementValue), 0) FROM NayaxSales"),
        ("NayaxSales.CostOfGoodsSold", "SELECT COALESCE(SUM(CostOfGoodsSold), 0) FROM NayaxSales"),
        ("Receipts.TotalAmount", "SELECT COALESCE(SUM(TotalAmount), 0) FROM Receipts"),
        ("ReceiptItems.LineTotal", "SELECT COALESCE(SUM(Quantity * UnitCost), 0) FROM ReceiptItems"),
        ("StockAdjustments.QuantityChange", "SELECT COALESCE(SUM(QuantityChange), 0) FROM StockAdjustments"),
        ("StockAdjustments.TotalCost", "SELECT COALESCE(SUM(TotalCost), 0) FROM StockAdjustments"),
        ("OperatingExpenses.TotalAmount", "SELECT COALESCE(SUM(TotalAmount), 0) FROM OperatingExpenses"),
        ("CommissionPayments.Amount", "SELECT COALESCE(SUM(Amount), 0) FROM CommissionPayments"),
        ("ImportedReimbursements.Total", "SELECT COALESCE(SUM(Total), 0) FROM ImportedReimbursements"),
    ];

    public async Task<BusinessBootstrapResult> RunAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (!TryReadConfiguredMembers(out var configuredMembers, out var configurationError))
        {
            return Failure(BusinessBootstrapOutcome.ConfigurationInvalid, dryRun, configurationError);
        }

        var pending = await _db.Database.GetPendingMigrationsAsync(cancellationToken);
        if (pending.Any())
        {
            return Failure(
                BusinessBootstrapOutcome.SchemaNotReady,
                dryRun,
                $"The database has {pending.Count()} pending migration(s). Apply the schema first, "
                    + "verify it, then run the bootstrap as a separate, reviewed step.");
        }

        var runId = Guid.NewGuid();
        var recordedAt = _timeProvider.GetUtcNow().UtcDateTime;

        // One transaction around discovery, mutation and verification: a failed check must leave
        // the database exactly as it was found.
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var businesses = await _db.Businesses.OrderBy(b => b.Id).ToListAsync(cancellationToken);
        if (businesses.Count > 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Failure(
                BusinessBootstrapOutcome.AmbiguousExistingBusiness,
                dryRun,
                $"{businesses.Count} businesses already exist. Which one owns the unassigned data "
                    + "is a human decision; the bootstrap will not choose.");
        }

        var business = businesses.SingleOrDefault();
        var businessCreated = business is null;

        if (business is null)
        {
            business = new Business
            {
                Name = _options.BusinessName.Trim(),
                IsActive = true,
                CreatedAtUtc = recordedAt,
            };
            _db.Businesses.Add(business);
            await _db.SaveChangesAsync(cancellationToken);
        }

        var membershipsCreated = await EnsureMembershipsAsync(business.Id, configuredMembers, recordedAt, cancellationToken);

        var tables = OwnedTableNames();

        // Checked before a single row is assigned: data must never be handed to a business that
        // nobody can sign in to. A revoked membership stays revoked - reactivating one is a
        // human decision - so if every configured actor matches only revoked rows, the right
        // answer is to stop and let a person resolve it, not to quietly strand the data.
        var unassignedTotal = await CountUnassignedAsync(tables, cancellationToken);
        if (unassignedTotal > 0)
        {
            if (!business.IsActive)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure(
                    BusinessBootstrapOutcome.BusinessInactive,
                    dryRun,
                    $"{unassignedTotal} row(s) are unassigned, but the business that would own them "
                        + "is deactivated, so its records would be unreachable whoever is a member. "
                        + "Reactivate the business deliberately, then run again. Nothing was assigned.");
            }

            // An active membership on a deactivated business grants nothing, so the business
            // check above has to come first: counting memberships alone would let this pass.
            var activeMemberships = await _db.BusinessMemberships
                .CountAsync(m => m.BusinessId == business.Id && m.IsActive, cancellationToken);

            if (activeMemberships == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return Failure(
                    BusinessBootstrapOutcome.NoActiveMembership,
                    dryRun,
                    $"{unassignedTotal} row(s) are unassigned, but the business has no active "
                        + "membership, so the data would be owned by a business nobody can access. "
                        + "Every configured actor matches only a revoked membership. Reactivate one "
                        + "deliberately, or configure an actor who should have access, then run again. "
                        + "Nothing was assigned.");
            }
        }

        var totalsBefore = await ReadTotalsAsync(cancellationToken);

        var reports = new List<BusinessBackfillTableReport>(tables.Count);

        foreach (var table in tables)
        {
            var totalBefore = await CountAsync($"SELECT COUNT(*) FROM \"{table}\"", cancellationToken);
            var unassignedBefore = await CountAsync(
                $"SELECT COUNT(*) FROM \"{table}\" WHERE BusinessId = {Unassigned}", cancellationToken);

            // Restart-safe and idempotent: only unassigned rows are touched, so a second run
            // assigns nothing and a run interrupted after this table simply resumes at the next.
            // The table name comes from the EF model, never from input, and the business id is a
            // bound parameter. Built by concatenation rather than interpolation so EF1002 is
            // answered by construction instead of a suppression.
            var updateSql = "UPDATE \"" + table + "\" SET BusinessId = {0} WHERE BusinessId = "
                + Unassigned.ToString(CultureInfo.InvariantCulture);

            var rowsAssigned = unassignedBefore == 0
                ? 0
                : await _db.Database.ExecuteSqlRawAsync(updateSql, [business.Id], cancellationToken);

            var totalAfter = await CountAsync($"SELECT COUNT(*) FROM \"{table}\"", cancellationToken);
            var unassignedAfter = await CountAsync(
                $"SELECT COUNT(*) FROM \"{table}\" WHERE BusinessId = {Unassigned}", cancellationToken);

            reports.Add(new BusinessBackfillTableReport(
                table, totalBefore, unassignedBefore, rowsAssigned, totalAfter, unassignedAfter));
        }

        var totalsAfter = await ReadTotalsAsync(cancellationToken);
        var totals = VerificationTotals
            .Select(total => new BusinessBackfillTotalReport(total.Name, totalsBefore[total.Name], totalsAfter[total.Name]))
            .ToList();

        var failures = Verify(reports, totals);
        if (failures.Count > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new BusinessBootstrapResult
            {
                Outcome = BusinessBootstrapOutcome.VerificationFailed,
                DryRun = dryRun,
                RunId = runId,
                BusinessId = business.Id,
                Tables = reports,
                Totals = totals,
                Message = "Verification failed and nothing was written: " + string.Join("; ", failures),
            };
        }

        _db.BusinessBackfillAudits.AddRange(reports.Select(report => new BusinessBackfillAudit
        {
            RunId = runId,
            BusinessId = business.Id,
            TableName = report.TableName,
            TotalRowsBefore = report.TotalRowsBefore,
            UnassignedRowsBefore = report.UnassignedRowsBefore,
            RowsAssigned = report.RowsAssigned,
            TotalRowsAfter = report.TotalRowsAfter,
            UnassignedRowsAfter = report.UnassignedRowsAfter,
            RecordedAtUtc = recordedAt,
        }));
        await _db.SaveChangesAsync(cancellationToken);

        if (dryRun)
        {
            // A dry run does all the real work, verifies it, then throws it away. That is what
            // makes its numbers trustworthy: they are measured, not predicted.
            await transaction.RollbackAsync(cancellationToken);
        }
        else
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new BusinessBootstrapResult
        {
            Outcome = BusinessBootstrapOutcome.Succeeded,
            DryRun = dryRun,
            RunId = runId,
            BusinessId = business.Id,
            BusinessCreated = businessCreated,
            MembershipsCreated = membershipsCreated,
            Tables = reports,
            Totals = totals,
            Message = dryRun
                ? "Dry run complete. Every change was rolled back; the numbers above are what an apply would do."
                : "Bootstrap applied and verified.",
        };
    }

    /// <summary>
    /// Validates the human-supplied mapping through the same
    /// <see cref="ActorIdentity"/> rule the request path uses, so a value that would not identify
    /// an actor at sign-in cannot be written as a membership here either.
    /// </summary>
    private bool TryReadConfiguredMembers(out List<ActorIdentity> members, out string error)
    {
        members = [];

        if (string.IsNullOrWhiteSpace(_options.BusinessName))
        {
            error = $"{BusinessBootstrapOptions.SectionName}:BusinessName is required.";
            return false;
        }

        if (_options.Members.Count == 0)
        {
            error = $"{BusinessBootstrapOptions.SectionName}:Members is empty. At least one Entra "
                + "(tid, oid) pair must be supplied, or the business would have no one who can sign in to it. "
                + "The bootstrap will not invent an identity.";
            return false;
        }

        for (var i = 0; i < _options.Members.Count; i++)
        {
            var member = _options.Members[i];
            if (!ActorIdentity.TryCreate(member.DirectoryTenantId, member.ObjectId, out var actor) || actor is null)
            {
                // Deliberately reports the position, never the value: this runs in an operator's
                // console and its output must not become a place personal identifiers leak.
                error = $"{BusinessBootstrapOptions.SectionName}:Members[{i}] is not a valid Entra "
                    + "(tid, oid) pair. Both must be present and well-formed GUIDs.";
                return false;
            }

            members.Add(actor);
        }

        var distinct = members.Distinct().Count();
        if (distinct != members.Count)
        {
            error = $"{BusinessBootstrapOptions.SectionName}:Members contains the same actor more than once.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private async Task<int> EnsureMembershipsAsync(
        int businessId,
        IReadOnlyList<ActorIdentity> configuredMembers,
        DateTime recordedAt,
        CancellationToken cancellationToken)
    {
        var existing = await _db.BusinessMemberships
            .Where(membership => membership.BusinessId == businessId)
            .ToListAsync(cancellationToken);

        var created = 0;

        foreach (var actor in configuredMembers)
        {
            var match = existing.FirstOrDefault(membership =>
                string.Equals(membership.DirectoryTenantId, actor.DirectoryTenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(membership.ObjectId, actor.ObjectId, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                // Re-running must not resurrect an approval a human deliberately revoked.
                continue;
            }

            _db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = businessId,
                DirectoryTenantId = actor.DirectoryTenantId,
                ObjectId = actor.ObjectId,
                IsActive = true,
                CreatedAtUtc = recordedAt,
            });
            created++;
        }

        if (created > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    private static List<string> Verify(
        IReadOnlyList<BusinessBackfillTableReport> reports,
        IReadOnlyList<BusinessBackfillTotalReport> totals)
    {
        var failures = new List<string>();

        foreach (var report in reports)
        {
            if (report.TotalRowsBefore != report.TotalRowsAfter)
            {
                failures.Add(
                    $"{report.TableName} row count changed from {report.TotalRowsBefore} to {report.TotalRowsAfter}");
            }

            if (report.UnassignedRowsAfter != 0)
            {
                failures.Add(
                    $"{report.TableName} still has {report.UnassignedRowsAfter} unassigned row(s)");
            }

            if (report.RowsAssigned != report.UnassignedRowsBefore)
            {
                failures.Add(
                    $"{report.TableName} assigned {report.RowsAssigned} row(s) but {report.UnassignedRowsBefore} were unassigned");
            }
        }

        failures.AddRange(totals
            .Where(total => !total.IsPreserved)
            .Select(total => $"{total.Name} changed from {Format(total.Before)} to {Format(total.After)}"));

        return failures;
    }

    private static string Format(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// Every tenant-owned table, from the EF model rather than a hand-maintained list, ordered by
    /// name so two runs process them identically. A tenant-owned entity added later is picked up
    /// automatically instead of being silently skipped by the backfill.
    /// </summary>
    private List<string> OwnedTableNames() =>
        _db.Model
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .Select(entityType => entityType.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Total rows still owned by nobody, across every tenant-owned table.
    /// </summary>
    private async Task<long> CountUnassignedAsync(IReadOnlyList<string> tables, CancellationToken cancellationToken)
    {
        var total = 0L;

        foreach (var table in tables)
        {
            total += await CountAsync(
                $"SELECT COUNT(*) FROM \"{table}\" WHERE BusinessId = {Unassigned}", cancellationToken);
        }

        return total;
    }

    private async Task<Dictionary<string, decimal>> ReadTotalsAsync(CancellationToken cancellationToken)
    {
        var totals = new Dictionary<string, decimal>(StringComparer.Ordinal);

        foreach (var (name, sql) in VerificationTotals)
        {
            totals[name] = await ScalarDecimalAsync(sql, cancellationToken);
        }

        return totals;
    }

    private async Task<long> CountAsync(string sql, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(sql, cancellationToken);
        return value is null ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private async Task<decimal> ScalarDecimalAsync(string sql, CancellationToken cancellationToken)
    {
        var value = await ScalarAsync(sql, cancellationToken);
        return value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    }

    private async Task<object?> ScalarAsync(string sql, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull ? null : value;
    }

    private static BusinessBootstrapResult Failure(BusinessBootstrapOutcome outcome, bool dryRun, string message) =>
        new()
        {
            Outcome = outcome,
            DryRun = dryRun,
            Message = message,
        };
}
