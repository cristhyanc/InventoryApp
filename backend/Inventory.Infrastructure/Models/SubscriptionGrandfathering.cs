namespace Inventory.Infrastructure.Models;

/// <summary>
/// The grandfathering step's own record (issue #966): the fixed cutoff every eligibility decision is
/// made against, and the completion marker enforcement waits for.
///
/// <para><strong>One row, and at most one.</strong> Like <see cref="PlatformOptionVisibility"/>, the
/// row's key is the fixed <see cref="SingletonId"/> and the database assigns nothing, so two
/// concurrent first runs collide on the primary key instead of recording two cutoffs - and the
/// adapter resolves that collision by reading the cutoff the other run recorded, which is the whole
/// point: <strong>the cutoff is fixed once and never moves.</strong></para>
///
/// <para><strong>A platform table, the eleventh</strong> (issue #966; the tenth arrived with issue
/// #937). It is about <em>no business at all</em>: the step runs once over every business, so the
/// cutoff and the completion are one answer for the whole application and there is no
/// <c>BusinessId</c> to carry, no <see cref="IBusinessOwned"/> to implement, no query filter to
/// apply and no ownership stamp to make. What the step <em>writes per business</em> is an ordinary
/// tenant-owned <see cref="BusinessPlanGrant"/> row, stamped and filtered centrally with nothing
/// added - the same shape as the supplier presets, where the platform record is the suggestion and
/// the business's own row is the result. It is declared in <c>PlatformEntities</c> in
/// <c>BusinessOwnershipCoverageTests</c>, never in <c>DeliberatelyGlobalEntities</c>.</para>
///
/// <para><strong>No row at all is the shipped state, and it means nothing is enforced.</strong> The
/// migration that adds this table inserts nothing, so every existing installation keeps exactly the
/// behaviour it has today: with no completion marker the effective-entitlement policy treats every
/// business as unrestricted however the rollout setting is set, and the API logs a startup error
/// naming the missing step. Enforcement starts only once a human has run the command and this row
/// says it finished.</para>
///
/// <para><strong>Who writes it.</strong> Only the human-run <c>grandfather-business-plans</c>
/// command, which is not reachable from a request at all: starting the web host and running a
/// command are separate branches of <c>Program.cs</c>. No endpoint reads or writes this table; the
/// per-request entitlement read and the startup check read it, and nothing anywhere updates the
/// cutoff.</para>
/// </summary>
public class SubscriptionGrandfathering
{
    /// <summary>
    /// The only key this table's single row may have. Written by the adapter rather than assigned by
    /// the database, so a second insert is refused by the primary key.
    /// </summary>
    public const int SingletonId = 1;

    /// <summary>The fixed key; see <see cref="SingletonId"/>.</summary>
    public int Id { get; set; } = SingletonId;

    /// <summary>
    /// The cutoff eligibility is decided against, as a UTC instant: a business created before it and
    /// trading at it is grandfathered, and everything created at or after it starts on Free. Written
    /// by the first run from the application clock - so it is also when the step began - and
    /// <strong>never updated</strong>: a later run reads this value, so a resumed or repeated run
    /// grants exactly the businesses the first one would have.
    /// </summary>
    public DateTime CutoffUtc { get; set; }

    /// <summary>
    /// When a run finished granting every eligible business, or <c>null</c> while none has. This is
    /// the completion marker: enforcement takes effect only once it exists, so an interrupted run
    /// leaves some businesses granted and none restricted.
    /// </summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>
    /// How many eligible businesses held the grandfathered grant when the step completed - the ones
    /// the completing run granted plus the ones that already held one, so a resumed run records the
    /// whole picture rather than only its own share. Operational evidence about the step: the
    /// entitlement rule never reads it, and a rerun after completion leaves it as the run that
    /// finished recorded it.
    /// </summary>
    public int GrantedBusinessCount { get; set; }
}
