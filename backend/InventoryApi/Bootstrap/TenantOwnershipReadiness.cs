using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// What the startup readiness check found about tenant ownership.
/// </summary>
/// <param name="UnassignedRows">Tenant-owned rows still owned by nobody.</param>
/// <param name="BusinessCount">Businesses configured in this database, active or not.</param>
/// <param name="ActiveBusinessCount">Businesses that are currently active.</param>
/// <param name="UsableMembershipCount">
/// Active memberships that belong to an <em>active</em> business. A membership pointing at a
/// deactivated business grants nothing, so it is not counted as access.
/// </param>
public readonly record struct TenantOwnershipReadinessState(
    long UnassignedRows,
    int BusinessCount,
    int ActiveBusinessCount,
    int UsableMembershipCount)
{
    /// <summary>
    /// Ready means the rollout is actually usable, not merely that no row is unassigned.
    ///
    /// Counting zero unassigned rows on its own is not evidence of anything: a fresh database
    /// satisfies it trivially while having no business and nobody who can sign in. For this
    /// first single-business rollout every part must hold - the data has an owner, there is
    /// exactly one business and it is active, and at least one actor can actually reach it.
    ///
    /// "Exactly one" is deliberate rather than "at least one": this rollout serves a single
    /// vending business, so a second business appearing is a state a human should look at, not
    /// something to report as a healthy steady state.
    /// </summary>
    public bool IsReady =>
        UnassignedRows == 0
        && BusinessCount == 1
        && ActiveBusinessCount == 1
        && UsableMembershipCount > 0;
}

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

    public static TenantOwnershipReadinessState Inspect(AppDbContext db)
    {
        var businesses = db.Businesses.IgnoreQueryFilters();

        // The join is the point: an active membership on a deactivated business authorises
        // nothing, so counting memberships alone would report access that does not exist.
        var usableMemberships = db.BusinessMemberships
            .IgnoreQueryFilters()
            .Count(membership => membership.IsActive
                && businesses.Any(business => business.Id == membership.BusinessId && business.IsActive));

        return new TenantOwnershipReadinessState(
            CountUnassignedRows(db),
            businesses.Count(),
            businesses.Count(business => business.IsActive),
            usableMemberships);
    }

    public static void Report(AppDbContext db, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(TenantOwnershipReadiness).FullName!);
        var state = Inspect(db);

        if (state.IsReady)
        {
            logger.LogInformation(
                "Tenant ownership is bootstrapped: one active business with "
                    + "{UsableMembershipCount} usable membership(s), and no unassigned rows.",
                state.UsableMembershipCount);
            return;
        }

        logger.LogError(
            "Tenant ownership is NOT bootstrapped: {UnassignedRows} unassigned row(s), "
                + "{BusinessCount} business(es) of which {ActiveBusinessCount} active, "
                + "{UsableMembershipCount} active membership(s) on an active business. Ready requires "
                + "no unassigned rows, exactly one business which is active, and at least one active "
                + "membership belonging to it. Until then signed-in callers see an empty dataset - the "
                + "safe state, not a corruption. Run the reviewed `{Command} --dry-run` and then "
                + "`{Command} --apply` as described in docs/tenant-rollout.md. The API will not assign "
                + "ownership on its own.",
            state.UnassignedRows,
            state.BusinessCount,
            state.ActiveBusinessCount,
            state.UsableMembershipCount,
            BusinessBootstrapArguments.CommandName,
            BusinessBootstrapArguments.CommandName);
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
