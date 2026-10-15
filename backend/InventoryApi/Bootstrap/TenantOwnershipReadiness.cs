using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// What the startup readiness check found about tenant ownership.
/// </summary>
/// <param name="UnassignedRows">Tenant-owned rows still owned by nobody.</param>
/// <param name="BusinessCount">Businesses configured in this database, whatever their status.</param>
/// <param name="ActiveBusinessCount">
/// Businesses whose lifecycle status is <c>Active</c> (issue #540). A pending, rejected or
/// deactivated business is not one of them.
/// </param>
/// <param name="UsableMembershipCount">
/// Active memberships that belong to an <em>Active</em> business. A membership pointing at a
/// business in any other status grants nothing, so it is not counted as access.
/// </param>
public readonly record struct TenantOwnershipReadinessState(
    long UnassignedRows,
    int BusinessCount,
    int ActiveBusinessCount,
    int UsableMembershipCount)
{
    /// <summary>
    /// Bootstrapped means the rollout of issue #64 is done and the data is reachable: nothing is
    /// owned by nobody, there is a trading business, and somebody can sign in to it.
    ///
    /// <para>Counting zero unassigned rows on its own is not evidence of anything: a fresh database
    /// satisfies it trivially while having no business and nobody who can sign in. So all three
    /// parts have to hold - the data has an owner, at least one business is active, and at least one
    /// actor can actually reach one.</para>
    ///
    /// <para><strong>It deliberately says nothing about how many businesses there are</strong>
    /// (issue #681). A second business is not a failed bootstrap: since issue #541 a self-service
    /// application creates a <c>Pending</c> business, and since #544 a Super Admin may deactivate
    /// one, so a healthy deployment can legitimately hold several. Whether this is still the
    /// single-business rollout is <see cref="IsSingleBusinessRollout"/>'s separate question, and the
    /// operations that genuinely need one business ask <see cref="IsReady"/>.</para>
    /// </summary>
    public bool IsBootstrapped =>
        UnassignedRows == 0
        && ActiveBusinessCount >= 1
        && UsableMembershipCount > 0;

    /// <summary>
    /// Whether this database still holds exactly one business, which is the shape the first rollout
    /// was done in.
    ///
    /// It is a fact about the deployment rather than a health verdict: a second business is
    /// something an operator should know about, and it is what makes the operations below refuse,
    /// but it is not a defect.
    /// </summary>
    public bool IsSingleBusinessRollout => BusinessCount == 1;

    /// <summary>
    /// Bootstrapped <em>and</em> still a single-business rollout: what the two operations that have
    /// to pick "the" business require (<c>migrate-database</c>'s report and
    /// <c>migrate-nayax-connection</c>, which stores a credential for one business and refuses to
    /// choose between several).
    ///
    /// Since issue #681 it is deliberately not what the startup report logs an error about: those
    /// are different questions, and reporting "NOT bootstrapped" because a second business exists
    /// told an operator to re-run a backfill that had already completed.
    /// </summary>
    public bool IsReady => IsBootstrapped && IsSingleBusinessRollout && ActiveBusinessCount == 1;
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

        // The join is the point: an active membership on a business that is not Active authorises
        // nothing, so counting memberships alone would report access that does not exist. The
        // lifecycle status is read (issue #540), and only Active counts - a pending, rejected or
        // deactivated business resolves for nobody.
        var usableMemberships = db.BusinessMemberships
            .IgnoreQueryFilters()
            .Count(membership => membership.IsActive
                && businesses.Any(business =>
                    business.Id == membership.BusinessId && business.Status == BusinessStatus.Active));

        return new TenantOwnershipReadinessState(
            CountUnassignedRows(db),
            businesses.Count(),
            businesses.Count(business => business.Status == BusinessStatus.Active),
            usableMemberships);
    }

    /// <summary>
    /// Logs what startup found, as two separate facts (issue #681): whether the rollout is done, and
    /// how many businesses this deployment holds.
    ///
    /// <para>They used to be one. "NOT bootstrapped" was logged at <c>Error</c> whenever the
    /// business count was anything other than one - so the first self-service application, which
    /// creates a <c>Pending</c> business and is a perfectly ordinary event since issue #541, made
    /// every restart report a backfill failure and tell the operator to run a command that would
    /// have refused and changed nothing. An error that is routinely wrong is worse than no error:
    /// it is the one somebody learns to scroll past.</para>
    /// </summary>
    public static void Report(AppDbContext db, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(TenantOwnershipReadiness).FullName!);
        var state = Inspect(db);

        if (!state.IsBootstrapped)
        {
            logger.LogError(
                "Tenant ownership is NOT bootstrapped: {UnassignedRows} unassigned row(s), "
                    + "{BusinessCount} business(es) of which {ActiveBusinessCount} active, "
                    + "{UsableMembershipCount} active membership(s) on an active business. "
                    + "Bootstrapped requires no unassigned rows, at least one active business, and at "
                    + "least one active membership belonging to an active business. Until then "
                    + "signed-in callers see an empty dataset - the safe state, not a corruption. Run "
                    + "the reviewed `{Command} --dry-run` and then `{Command} --apply` as described "
                    + "in docs/tenant-rollout.md. The API will not assign ownership on its own.",
                state.UnassignedRows,
                state.BusinessCount,
                state.ActiveBusinessCount,
                state.UsableMembershipCount,
                BusinessBootstrapArguments.CommandName,
                BusinessBootstrapArguments.CommandName);
            return;
        }

        logger.LogInformation(
            "Tenant ownership is bootstrapped: {ActiveBusinessCount} active business(es) with "
                + "{UsableMembershipCount} usable membership(s), and no unassigned rows.",
            state.ActiveBusinessCount,
            state.UsableMembershipCount);

        if (state.IsSingleBusinessRollout)
        {
            return;
        }

        // A fact, not a fault. It is logged because the two human-invoked commands that have to pick
        // "the" business refuse while it holds, so an operator reaching for one of them should know
        // beforehand rather than from its refusal.
        logger.LogWarning(
            "This database holds {BusinessCount} business(es), of which {ActiveBusinessCount} are "
                + "active. That is expected once a self-service application has been recorded or a "
                + "business has been deactivated, and it is not a bootstrap failure. It does mean "
                + "`{Command}` and `migrate-nayax-connection`, which act on exactly one business, "
                + "will refuse until a human decides which one they mean. See docs/tenant-rollout.md.",
            state.BusinessCount,
            state.ActiveBusinessCount,
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
