using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Reports, at startup, whether tenant ownership has actually been bootstrapped (issue #64,
/// checkpoint 3).
///
/// After the checkpoint-2 migration every pre-existing row is owned by nobody, which is the safe
/// state but also an invisible one: the API starts, authenticates, authorises, and then shows an
/// empty dataset. Without this the first symptom would be "all my data is gone", with nothing in
/// the logs to explain it.
///
/// It only reports. It deliberately does not backfill, because an automatic fix here would be
/// exactly the silent production data operation this design exists to prevent, and it does not
/// stop the host, because refusing to start would turn a data-not-yet-assigned state into an
/// outage of a running deployment.
/// </summary>
public static class TenantOwnershipReadiness
{
    private const int Unassigned = 0;

    public static void Report(AppDbContext db, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(TenantOwnershipReadiness).FullName!);

        var unassigned = CountUnassignedRows(db);
        var businesses = db.Businesses.IgnoreQueryFilters().Count();

        if (unassigned == 0)
        {
            logger.LogInformation(
                "Tenant ownership is bootstrapped: {BusinessCount} business(es) configured and no unassigned rows.",
                businesses);
            return;
        }

        logger.LogError(
            "Tenant ownership is NOT bootstrapped: {UnassignedRows} row(s) across tenant-owned tables have no "
                + "owning business, so every signed-in caller will see an empty dataset. This is the safe "
                + "state, not a corruption. Run the reviewed `{Command} --dry-run` and then `{Command} --apply` "
                + "as described in docs/tenant-rollout.md. The API will not assign ownership on its own.",
            unassigned,
            BusinessBootstrapCommand.CommandName,
            BusinessBootstrapCommand.CommandName);
    }

    /// <summary>
    /// Counts unassigned rows across every tenant-owned table, reading the table list from the EF
    /// model so a newly added owned entity is included without touching this code.
    /// </summary>
    private static long CountUnassignedRows(AppDbContext db)
    {
        var tables = db.Model
            .GetEntityTypes()
            .Where(entityType => typeof(IBusinessOwned).IsAssignableFrom(entityType.ClrType))
            .Select(entityType => entityType.GetTableName())
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (tables.Count == 0)
        {
            return 0;
        }

        var sql = string.Join(
            " + ",
            tables.Select(table => $"(SELECT COUNT(*) FROM \"{table}\" WHERE BusinessId = {Unassigned})"));

        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            connection.Open();
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {sql}";
        var value = command.ExecuteScalar();

        return value is null or DBNull ? 0L : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
