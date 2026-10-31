using System.Globalization;
using Inventory.Application.Subscriptions;
using Inventory.Application.Time;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Grants the grandfathered Pro plan to every business that was already trading when plan
/// enforcement was first prepared (issue #966, decided 29 October 2026 on #965).
///
/// <para>It is the data half of <see cref="BusinessPlanGrandfatheringCommand"/>, separated from it
/// so the idempotency, the resumability and the eligibility rule can be tested against a real
/// relational database without a web host or a console.</para>
///
/// <para>The four properties that make it safe to point at production:
/// <list type="bullet">
///   <item><b>A dry run writes nothing at all</b> - not a grant, and not the cutoff. With no record
///   yet it reports the cutoff it <em>would</em> fix, so an operator reviews the eligibility the
///   apply will act on.</item>
///   <item><b>The cutoff is recorded once and never moves.</b> The first apply fixes it before any
///   grant is written; every later run reads it back and grants by it, which is what makes "a
///   business created after the cutoff is never grandfathered" true across runs, including runs days
///   apart.</item>
///   <item><b>Idempotent, and safely restartable.</b> Each business's grant is one transaction
///   that inserts only if that business holds no grandfathered grant, with a filtered unique index
///   underneath it; the completion marker is written last. An interrupted run therefore leaves some
///   businesses granted, the cutoff fixed, and <em>nothing enforced</em> - because enforcement waits
///   for that marker - and running it again finishes the rest by the same cutoff. A run after
///   completion reads the marker, changes nothing and reports that.</item>
///   <item><b>It grants and never revokes or downgrades.</b> There is no path here that deletes,
///   edits or ends a grant, that writes a billing subscription, or that touches anything else a
///   business owns. A business that already holds a grandfathered grant with an end date an owner
///   set keeps it exactly as it is.</item>
/// </list></para>
///
/// <para><strong>It is deliberately not an unrestricted context</strong>, exactly as
/// <see cref="NayaxConnectionMigrator"/> and <c>resolve-nayax-sale-products</c> are not (AGENTS.md §
/// Tenant ownership and data isolation). The <c>Businesses</c> table carries no tenant query filter,
/// so the enumeration reads it under a <em>denied</em> scope, and the grandfathering record is a
/// platform row that no filter applies to either; every grant is then written through a context
/// scoped to the one business it belongs to, with the central query filters and
/// <c>BusinessOwnershipEnforcer</c> fully in force. No business id is ever taken from a command
/// line.</para>
///
/// <para>It is independent of the <c>Subscriptions:EnforcementEnabled</c> setting, and that is the
/// point: this step is what a human runs <em>before</em> switching enforcement on (#965 § Launch
/// gate), and running it while the setting is off writes exactly the same grants.</para>
/// </summary>
public sealed class BusinessPlanGrandfatherer
{
    private readonly AppDbContext _tenancyDb;
    private readonly ISubscriptionGrandfatheringStore _record;
    private readonly Func<int, IManualPlanGrantStore> _grantStoreFactory;
    private readonly IClock _clock;

    /// <param name="tenancyDb">
    /// A context used only to enumerate the businesses and to read and write the grandfathering
    /// record. It needs no tenant scope and deliberately is not given an unrestricted one: neither
    /// <c>Businesses</c> nor the grandfathering row carries a tenant query filter, so a denied
    /// context reads them while reaching no tenant-owned row at all.
    /// </param>
    /// <param name="record">
    /// The grandfathering record's store, over <paramref name="tenancyDb"/>.
    /// </param>
    /// <param name="grantStoreFactory">
    /// Produces the grant store for one business, built over a context scoped to it - which is how
    /// each grant stays inside the central ownership boundary and inside its own transaction.
    /// </param>
    /// <param name="clock">
    /// Supplies the cutoff a first run records and the instant each grant is stamped with, so tests
    /// can pin both.
    /// </param>
    public BusinessPlanGrandfatherer(
        AppDbContext tenancyDb,
        ISubscriptionGrandfatheringStore record,
        Func<int, IManualPlanGrantStore> grantStoreFactory,
        IClock clock)
    {
        _tenancyDb = tenancyDb;
        _record = record;
        _grantStoreFactory = grantStoreFactory;
        _clock = clock;
    }

    public async Task<BusinessPlanGrandfatheringReport> RunAsync(
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var pendingCount = (await _tenancyDb.Database
            .GetPendingMigrationsAsync(cancellationToken)
            .ConfigureAwait(false))
            .Count();

        if (pendingCount > 0)
        {
            return Refusal(
                dryRun,
                $"The database has {pendingCount.ToString(CultureInfo.InvariantCulture)} pending "
                    + "migration(s), so the subscription tables "
                    + "may not exist or may not be at the expected shape. Apply the schema first, "
                    + "verify it, then run this command as a separate, reviewed step. Nothing was "
                    + "read or written.");
        }

        var existing = await _record.FindAsync(cancellationToken).ConfigureAwait(false);

        if (existing is { IsComplete: true })
        {
            // The expected result of running this twice. Nothing is written - not a grant, and not
            // the marker - and the cutoff reported is the one the completed run used.
            return new BusinessPlanGrandfatheringReport(
                dryRun,
                BusinessPlanGrandfatheringOutcome.AlreadyComplete,
                Refusal: null,
                existing.CutoffUtc,
                CutoffWasAlreadyRecorded: true,
                Businesses: [],
                CompletionMarkerWritten: false);
        }

        var now = _clock.UtcNow;

        // A dry run never records a cutoff: it reports the recorded one if there is one, and
        // otherwise the instant it would fix, so what an operator reviews is the eligibility the
        // apply will act on.
        var cutoff = existing?.CutoffUtc
            ?? (dryRun ? now : (await _record.StartAsync(now, cancellationToken).ConfigureAwait(false)).CutoffUtc);

        var candidates = await CandidatesAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<BusinessPlanGrandfatheringEntry>(candidates.Count);
        var grandfathered = 0;

        foreach (var candidate in candidates)
        {
            var decision = GrandfatheringEligibilityPolicy.Decide(candidate, cutoff);

            if (decision != GrandfatheringEligibilityPolicy.Decision.Eligible)
            {
                entries.Add(new BusinessPlanGrandfatheringEntry(
                    candidate.BusinessId,
                    decision,
                    Granted: false,
                    AlreadyHeld: false));
                continue;
            }

            var grants = _grantStoreFactory(candidate.BusinessId);

            if (dryRun)
            {
                var alreadyHeld = await HoldsGrandfatheredGrantAsync(grants, cancellationToken)
                    .ConfigureAwait(false);
                entries.Add(new BusinessPlanGrandfatheringEntry(
                    candidate.BusinessId,
                    decision,
                    Granted: false,
                    alreadyHeld));
                grandfathered++;
                continue;
            }

            var granted = await grants
                .GrantGrandfatheredPlanIfMissingAsync(cutoff, now, cancellationToken)
                .ConfigureAwait(false);

            entries.Add(new BusinessPlanGrandfatheringEntry(
                candidate.BusinessId,
                decision,
                granted,
                AlreadyHeld: !granted));
            grandfathered++;
        }

        if (!dryRun)
        {
            // Last, and only once every eligible business holds the grant: until this marker exists
            // every business is unrestricted, so a run that fell over before here has restricted
            // nobody.
            await _record
                .CompleteAsync(_clock.UtcNow, grandfathered, cancellationToken)
                .ConfigureAwait(false);
        }

        return new BusinessPlanGrandfatheringReport(
            dryRun,
            BusinessPlanGrandfatheringOutcome.Succeeded,
            Refusal: null,
            cutoff,
            CutoffWasAlreadyRecorded: existing is not null,
            entries,
            CompletionMarkerWritten: !dryRun);
    }

    /// <summary>
    /// Every business, with the four stored facts the eligibility rule is decided from. Read in
    /// business-id order so a report, a rerun and a test all see them the same way.
    /// </summary>
    private async Task<List<GrandfatheringCandidate>> CandidatesAsync(CancellationToken cancellationToken)
    {
        var rows = await _tenancyDb.Businesses
            .AsNoTracking()
            .OrderBy(business => business.Id)
            .Select(business => new
            {
                business.Id,
                business.Status,
                business.CreatedAtUtc,
                business.DecidedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.ConvertAll(row => new GrandfatheringCandidate(
            row.Id,
            row.Status,
            row.CreatedAtUtc,
            row.DecidedAtUtc));
    }

    private static async Task<bool> HoldsGrandfatheredGrantAsync(
        IManualPlanGrantStore grants,
        CancellationToken cancellationToken) =>
        (await grants.ListForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false))
            .Any(grant => grant.Source == ManualPlanGrantSource.Grandfathered);

    private static BusinessPlanGrandfatheringReport Refusal(bool dryRun, string reason) =>
        new(
            dryRun,
            BusinessPlanGrandfatheringOutcome.SchemaNotReady,
            reason,
            CutoffUtc: null,
            CutoffWasAlreadyRecorded: false,
            Businesses: [],
            CompletionMarkerWritten: false);
}
