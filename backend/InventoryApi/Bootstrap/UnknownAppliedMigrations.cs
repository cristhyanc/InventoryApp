using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace InventoryApi.Bootstrap;

/// <summary>
/// The one place that answers "is this database's schema newer than this build?" (issue #1299,
/// audit #1228 finding OPS-03, confirmed by #1231).
///
/// Pending migrations say the schema is <i>behind</i> the code, and both
/// <see cref="DatabaseSchemaStartup"/> and <see cref="DatabaseMigrator"/> already looked only at
/// those. The opposite case was invisible: an older build started against a database a newer build
/// had already migrated - a manual redeploy, a restore, a half-failed release - found nothing
/// pending, reported "already up to date", answered <c>/health/ready</c> with 200, and then wrote
/// to a schema it does not understand. The comparison that catches it is the applied migration ids
/// in <c>__EFMigrationsHistory</c> against the ids this build contains: an applied id the build
/// does not know can only have been written by a build that is ahead of it.
///
/// EF Core never removes a migration from the history, so this comparison is a one-way signal: it
/// is not confused by a migration that was renamed in a later release, because the id the newer
/// build wrote is still the id this build is asked about.
/// </summary>
public static class UnknownAppliedMigrations
{
    /// <summary>
    /// What an operator must do about it. Deliberately the same sentence wherever the condition is
    /// reported, because the recovery is not obvious: a migration is never rolled back, so going
    /// back past one means restoring the backup taken before it was applied.
    /// </summary>
    public const string RecoveryGuidance =
        "Recover either by restoring the verified backup taken just before the release that applied "
        + "them and then deploying this build, or by rolling forward to a build that contains them. "
        + "Nothing here rolls a migration back. See docs/automation.md § Deploy Production.";

    /// <summary>
    /// The applied ids this build does not contain, de-duplicated and ordered so the report and the
    /// log message are stable whatever order the history table returns.
    /// </summary>
    public static IReadOnlyList<string> In(
        IEnumerable<string> appliedMigrations,
        IEnumerable<string> knownMigrations)
    {
        var known = new HashSet<string>(knownMigrations, StringComparer.Ordinal);

        return appliedMigrations
            .Where(applied => !known.Contains(applied))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(applied => applied, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Reads the history table and compares it with this build's migrations.</summary>
    public static IReadOnlyList<string> Detect(DatabaseFacade database) =>
        In(database.GetAppliedMigrations(), database.GetMigrations());

    /// <inheritdoc cref="Detect"/>
    public static async Task<IReadOnlyList<string>> DetectAsync(
        DatabaseFacade database,
        CancellationToken cancellationToken) =>
        In(await database.GetAppliedMigrationsAsync(cancellationToken), database.GetMigrations());

    /// <summary>
    /// The finding itself, naming every unknown id, without saying what the caller does about it -
    /// startup refuses to continue and the <c>migrate-database</c> report does not.
    /// </summary>
    public static string Summarise(IReadOnlyList<string> unknownApplied) =>
        $"The database schema is NEWER than this build: __EFMigrationsHistory holds "
        + $"{unknownApplied.Count} applied migration(s) this build does not contain, so a newer "
        + "build has already migrated this database. Unknown applied migration(s): "
        + string.Join(", ", unknownApplied)
        + ".";
}
