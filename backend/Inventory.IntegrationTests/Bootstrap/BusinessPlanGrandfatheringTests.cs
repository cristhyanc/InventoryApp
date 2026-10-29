using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Bootstrap;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The human-run command that grants every established business its grandfathered Pro plan and
/// records the completion marker enforcement waits for (issue #966).
///
/// <para>Relational SQLite against the migrated schema rather than a model double, for the reason
/// the <c>migrate-nayax-connection</c> tests are: the grants land in a real table behind a real
/// filtered unique index, the cutoff behind a real primary key, and the claims worth making - "a dry
/// run writes nothing", "a rerun changes nothing", "an interrupted run resumes by the same cutoff",
/// "a business created after the cutoff is never granted" - are claims about what a database
/// actually contains.</para>
///
/// <para>Every business here is one the acceptance criteria name: two that were trading, one
/// offboarded, one pending application, one rejected one, one created after the cutoff, and one that
/// was pending at the cutoff and approved afterwards - the case that would quietly acquire Pro on a
/// rerun if eligibility were decided from today's status alone.</para>
/// </summary>
public class BusinessPlanGrandfatheringTests : IAsyncLifetime
{
    private static readonly DateTime Cutoff = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime BeforeCutoff = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AfterCutoff = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private DbContextOptions<AppDbContext> _options = null!;

    /// <summary>The two businesses that were trading before the cutoff.</summary>
    private int _tradingId;
    private int _secondTradingId;

    /// <summary>Offboarded after having traded: eligible, because its records are kept.</summary>
    private int _deactivatedId;

    private int _pendingId;
    private int _rejectedId;

    /// <summary>Created after the cutoff: a new business, which starts on Free.</summary>
    private int _createdAfterId;

    /// <summary>Pending at the cutoff, approved afterwards: starts on Free, including on a rerun.</summary>
    private int _approvedAfterId;

    public async Task InitializeAsync()
    {
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = TestAppDbContext.Unrestricted(_options);
        await db.Database.MigrateAsync();

        var trading = Business("Vending Co", BusinessStatus.Active, BeforeCutoff, decidedAtUtc: null);
        var secondTrading = Business(
            "Second Vending Co", BusinessStatus.Active, BeforeCutoff, BeforeCutoff.AddDays(1));
        var deactivated = Business("Offboarded Vending", BusinessStatus.Deactivated, BeforeCutoff, null);
        var pending = Business("Coastal Vending", BusinessStatus.Pending, BeforeCutoff, null);
        var rejected = Business("Inland Vending", BusinessStatus.Rejected, BeforeCutoff, BeforeCutoff.AddDays(2));
        var createdAfter = Business("Brand New Vending", BusinessStatus.Active, AfterCutoff, AfterCutoff);
        var approvedAfter = Business("Late Approval Vending", BusinessStatus.Active, BeforeCutoff, AfterCutoff);

        db.Businesses.AddRange(
            trading, secondTrading, deactivated, pending, rejected, createdAfter, approvedAfter);
        await db.SaveChangesAsync();

        _tradingId = trading.Id;
        _secondTradingId = secondTrading.Id;
        _deactivatedId = deactivated.Id;
        _pendingId = pending.Id;
        _rejectedId = rejected.Id;
        _createdAfterId = createdAfter.Id;
        _approvedAfterId = approvedAfter.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    #region Arguments

    [Fact]
    public void The_command_is_recognised_by_name()
    {
        Assert.True(BusinessPlanGrandfatheringCommand.Matches(["grandfather-business-plans", "--apply"]));
        Assert.False(BusinessPlanGrandfatheringCommand.Matches(["migrate-database"]));
        Assert.False(BusinessPlanGrandfatheringCommand.Matches([]));
    }

    [Fact]
    public void An_invocation_with_no_flag_is_a_dry_run()
    {
        Assert.True(BusinessPlanGrandfatheringArguments.TryParse(
            ["grandfather-business-plans"], out var apply, out var error));

        Assert.False(apply);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--dry-run", "--apply")]
    public void Both_flags_together_are_refused_and_do_not_apply(string first, string second)
    {
        Assert.False(BusinessPlanGrandfatheringArguments.TryParse(
            ["grandfather-business-plans", first, second], out var apply, out var error));

        Assert.False(apply);
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// There is no flag for a cutoff, a plan or a business, and an argument that looks like one is
    /// refused rather than ignored: a command line that could name a business would be a way to
    /// grant Pro to one that was not entitled to it.
    /// </summary>
    [Theory]
    [InlineData("--business-id=1")]
    [InlineData("--cutoff=2026-01-01")]
    [InlineData("--plan=Pro")]
    public void An_unrecognised_argument_is_refused(string argument)
    {
        Assert.False(BusinessPlanGrandfatheringArguments.TryParse(
            ["grandfather-business-plans", argument], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains(argument, error, StringComparison.Ordinal);
    }

    #endregion

    #region A dry run

    /// <summary>
    /// A dry run writes nothing at all - no grant, and no cutoff - and reports the eligibility an
    /// apply would act on, including the instant it would fix.
    /// </summary>
    [Fact]
    public async Task A_dry_run_writes_nothing_and_reports_what_an_apply_would_do()
    {
        var report = await RunAsync(dryRun: true);

        Assert.Equal(BusinessPlanGrandfatheringOutcome.Succeeded, report.Outcome);
        Assert.True(report.DryRun);
        Assert.Equal(Cutoff, report.CutoffUtc);
        Assert.False(report.CutoffWasAlreadyRecorded);
        Assert.False(report.CompletionMarkerWritten);

        Assert.Equal(
            [_tradingId, _secondTradingId, _deactivatedId],
            report.Eligible.Select(entry => entry.BusinessId));
        Assert.Empty(report.Granted);
        Assert.Empty(report.AlreadyHeld);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await reader.BusinessPlanGrants.AsNoTracking().ToListAsync());
        Assert.Empty(await reader.SubscriptionGrandfatherings.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// And it reports, business by business, which rule refused the others - which is what an
    /// operator reviews before authorising the apply.
    /// </summary>
    [Fact]
    public async Task A_dry_run_names_the_rule_that_refused_each_other_business()
    {
        var report = await RunAsync(dryRun: true);

        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.NotTradingAtCutoff,
            DecisionFor(report, _pendingId));
        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.NotTradingAtCutoff,
            DecisionFor(report, _rejectedId));
        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff,
            DecisionFor(report, _createdAfterId));
        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.DecidedAtOrAfterCutoff,
            DecisionFor(report, _approvedAfterId));
    }

    #endregion

    #region The first apply

    [Fact]
    public async Task The_first_apply_grants_every_eligible_business_and_records_the_completion_marker()
    {
        var report = await RunAsync(dryRun: false);

        Assert.Equal(BusinessPlanGrandfatheringOutcome.Succeeded, report.Outcome);
        Assert.Equal(Cutoff, report.CutoffUtc);
        Assert.False(report.CutoffWasAlreadyRecorded);
        Assert.True(report.CompletionMarkerWritten);
        Assert.Equal(
            [_tradingId, _secondTradingId, _deactivatedId],
            report.Granted.Select(entry => entry.BusinessId));

        await using var reader = TestAppDbContext.Unrestricted(_options);

        var grants = await reader.BusinessPlanGrants.AsNoTracking().OrderBy(grant => grant.BusinessId).ToListAsync();
        Assert.Equal([_tradingId, _secondTradingId, _deactivatedId], grants.Select(grant => grant.BusinessId));
        Assert.All(grants, grant =>
        {
            Assert.Equal(SubscriptionPlan.Pro, grant.Plan);
            Assert.Equal(ManualPlanGrantSource.Grandfathered, grant.Source);
            // From the cutoff, with no end date until an owner sets one.
            Assert.Equal(Cutoff, grant.StartsAtUtc);
            Assert.Null(grant.EndsAtUtc);
        });

        var record = await reader.SubscriptionGrandfatherings.AsNoTracking().SingleAsync();
        Assert.Equal(Cutoff, record.CutoffUtc);
        Assert.Equal(Cutoff, record.CompletedAtUtc);
        Assert.Equal(3, record.GrantedBusinessCount);

        // Nothing was written anywhere else: no business's status, no subscription.
        Assert.Empty(await reader.BusinessBillingSubscriptions.AsNoTracking().ToListAsync());
        Assert.Equal(
            [BusinessStatus.Active, BusinessStatus.Active, BusinessStatus.Deactivated,
                BusinessStatus.Pending, BusinessStatus.Rejected, BusinessStatus.Active,
                BusinessStatus.Active],
            await reader.Businesses.AsNoTracking().OrderBy(business => business.Id)
                .Select(business => business.Status).ToListAsync());
    }

    #endregion

    #region Rerunning

    /// <summary>
    /// The criterion "a rerun after completion changes nothing and reports that": no grant is added
    /// or rewritten, the completion instant and count stay as the completing run left them, and the
    /// outcome says so.
    /// </summary>
    [Fact]
    public async Task A_rerun_after_completion_changes_nothing_and_reports_that()
    {
        await RunAsync(dryRun: false);

        var rerun = await RunAsync(dryRun: false, now: Cutoff.AddDays(10));

        Assert.Equal(BusinessPlanGrandfatheringOutcome.AlreadyComplete, rerun.Outcome);
        Assert.True(rerun.Succeeded);
        Assert.Equal(Cutoff, rerun.CutoffUtc);
        Assert.True(rerun.CutoffWasAlreadyRecorded);
        Assert.False(rerun.CompletionMarkerWritten);
        Assert.Empty(rerun.Granted);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(3, await reader.BusinessPlanGrants.CountAsync());
        var record = await reader.SubscriptionGrandfatherings.AsNoTracking().SingleAsync();
        Assert.Equal(Cutoff, record.CompletedAtUtc);
        Assert.Equal(3, record.GrantedBusinessCount);
    }

    /// <summary>
    /// A dry run after completion is the same answer, and still writes nothing: an operator may
    /// always ask.
    /// </summary>
    [Fact]
    public async Task A_dry_run_after_completion_reports_that_too()
    {
        await RunAsync(dryRun: false);

        var report = await RunAsync(dryRun: true, now: Cutoff.AddDays(10));

        Assert.Equal(BusinessPlanGrandfatheringOutcome.AlreadyComplete, report.Outcome);
        Assert.Equal(Cutoff, report.CutoffUtc);
    }

    /// <summary>
    /// The criterion "an interrupted run can resume without granting anyone outside the cutoff": the
    /// cutoff recorded by the interrupted run is what the resumed run grants by, even though days
    /// have passed and a business has been created and approved in between.
    /// </summary>
    [Fact]
    public async Task An_interrupted_run_resumes_by_the_cutoff_it_already_recorded()
    {
        // The interrupted run: the cutoff is fixed and one business granted, with no completion
        // marker - exactly the state a run that fell over part way through leaves behind.
        await using (var tenancyDb = TestAppDbContext.Denied(_options))
        {
            await new EfSubscriptionGrandfatheringStore(tenancyDb)
                .StartAsync(Cutoff, CancellationToken.None);
        }

        await using (var scoped = TestAppDbContext.For(_options, _tradingId))
        {
            await new EfManualPlanGrantStore(scoped)
                .GrantGrandfatheredPlanIfMissingAsync(Cutoff, Cutoff, CancellationToken.None);
        }

        // A week later a new business has arrived and one that was pending has been approved.
        var newcomerId = await AddBusinessAsync(
            "Later Vending", BusinessStatus.Active, Cutoff.AddDays(3), Cutoff.AddDays(3));
        await ApproveAsync(_pendingId, Cutoff.AddDays(4));

        var resumed = await RunAsync(dryRun: false, now: Cutoff.AddDays(7));

        Assert.Equal(BusinessPlanGrandfatheringOutcome.Succeeded, resumed.Outcome);
        Assert.True(resumed.CutoffWasAlreadyRecorded);
        Assert.Equal(Cutoff, resumed.CutoffUtc);

        // The business the interrupted run had already granted is left alone, and the two it had not
        // reached are granted - by the original cutoff.
        Assert.Equal([_tradingId], resumed.AlreadyHeld.Select(entry => entry.BusinessId));
        Assert.Equal([_secondTradingId, _deactivatedId], resumed.Granted.Select(entry => entry.BusinessId));

        // Neither the newcomer nor the business approved after the cutoff is granted anything.
        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.CreatedAtOrAfterCutoff,
            DecisionFor(resumed, newcomerId));
        Assert.Equal(
            GrandfatheringEligibilityPolicy.Decision.DecidedAtOrAfterCutoff,
            DecisionFor(resumed, _pendingId));

        await using var reader = TestAppDbContext.Unrestricted(_options);
        var grants = await reader.BusinessPlanGrants.AsNoTracking().OrderBy(grant => grant.BusinessId).ToListAsync();
        Assert.Equal([_tradingId, _secondTradingId, _deactivatedId], grants.Select(grant => grant.BusinessId));
        Assert.All(grants, grant => Assert.Equal(Cutoff, grant.StartsAtUtc));

        var record = await reader.SubscriptionGrandfatherings.AsNoTracking().SingleAsync();
        Assert.Equal(Cutoff, record.CutoffUtc);
        Assert.Equal(Cutoff.AddDays(7), record.CompletedAtUtc);
        Assert.Equal(3, record.GrantedBusinessCount);
    }

    /// <summary>
    /// A business approved after the cutoff stays on Free even when the step is run again after the
    /// approval - the case a rerun deciding eligibility from today's status would get wrong.
    /// </summary>
    [Fact]
    public async Task A_pending_application_approved_after_the_cutoff_is_never_granted()
    {
        await RunAsync(dryRun: false);
        await ApproveAsync(_pendingId, Cutoff.AddDays(2));

        var rerun = await RunAsync(dryRun: false, now: Cutoff.AddDays(5));

        // The step has completed, so this rerun grants nobody at all; and a fresh run against the
        // same record would refuse that business by the recorded decision instant.
        Assert.Equal(BusinessPlanGrandfatheringOutcome.AlreadyComplete, rerun.Outcome);

        await using var reader = TestAppDbContext.Unrestricted(_options);
        Assert.DoesNotContain(
            _pendingId,
            await reader.BusinessPlanGrants.AsNoTracking().Select(grant => grant.BusinessId).ToListAsync());
    }

    #endregion

    #region Refusals

    /// <summary>
    /// With a pending migration the subscription tables may not exist or may not be at the expected
    /// shape, so the run is refused before anything is read or written.
    /// </summary>
    [Fact]
    public async Task A_database_with_pending_migrations_is_refused_without_writing_anything()
    {
        await using var pendingConnection = new SqliteConnection("Data Source=:memory:");
        await pendingConnection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(pendingConnection).Options;

        await using var tenancyDb = TestAppDbContext.Denied(options);
        var report = await new BusinessPlanGrandfatherer(
                tenancyDb,
                new EfSubscriptionGrandfatheringStore(tenancyDb),
                businessId => new EfManualPlanGrantStore(TestAppDbContext.For(options, businessId)),
                new FakeClock(Cutoff))
            .RunAsync(dryRun: false, CancellationToken.None);

        Assert.Equal(BusinessPlanGrandfatheringOutcome.SchemaNotReady, report.Outcome);
        Assert.False(report.Succeeded);
        Assert.NotNull(report.Refusal);
        Assert.Null(report.CutoffUtc);
        Assert.Empty(report.Businesses);
    }

    #endregion

    #region What the command prints

    /// <summary>
    /// The printed report is what an operator decides from, so it says which instant is being
    /// granted by, whether that instant was already recorded, and whether the completion marker was
    /// written. Nothing in it is a secret: no business name, no member and no credential.
    /// </summary>
    [Fact]
    public async Task The_printed_report_states_the_cutoff_the_counts_and_the_marker()
    {
        var report = await RunAsync(dryRun: false);

        var output = new StringWriter();
        BusinessPlanGrandfatheringCommand.Write(report, output);
        var text = output.ToString();

        Assert.Contains("APPLY", text, StringComparison.Ordinal);
        Assert.Contains("2026-11-01 00:00:00Z", text, StringComparison.Ordinal);
        Assert.Contains("Pro", text, StringComparison.Ordinal);
        Assert.Contains("Completion marker written", text, StringComparison.Ordinal);
        Assert.Contains("granted the grandfathered plan", text, StringComparison.Ordinal);
        Assert.Contains(
            "not eligible - created at or after the cutoff",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Vending Co", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_printed_dry_run_says_that_nothing_will_be_written()
    {
        var report = await RunAsync(dryRun: true);

        var output = new StringWriter();
        BusinessPlanGrandfatheringCommand.Write(report, output);
        var text = output.ToString();

        Assert.Contains("DRY RUN (nothing will be written)", text, StringComparison.Ordinal);
        Assert.Contains("an apply would fix this instant", text, StringComparison.Ordinal);
        Assert.Contains("Completion marker NOT written", text, StringComparison.Ordinal);
    }

    #endregion

    private async Task<BusinessPlanGrandfatheringReport> RunAsync(bool dryRun, DateTime? now = null)
    {
        var scoped = new List<AppDbContext>();

        await using var tenancyDb = TestAppDbContext.Denied(_options);

        try
        {
            return await new BusinessPlanGrandfatherer(
                    tenancyDb,
                    new EfSubscriptionGrandfatheringStore(tenancyDb),
                    businessId =>
                    {
                        var db = TestAppDbContext.For(_options, businessId);
                        scoped.Add(db);
                        return new EfManualPlanGrantStore(db);
                    },
                    new FakeClock(now ?? Cutoff))
                .RunAsync(dryRun, CancellationToken.None);
        }
        finally
        {
            foreach (var db in scoped)
            {
                await db.DisposeAsync();
            }
        }
    }

    private static GrandfatheringEligibilityPolicy.Decision DecisionFor(
        BusinessPlanGrandfatheringReport report,
        int businessId) =>
        report.Businesses.Single(entry => entry.BusinessId == businessId).Decision;

    private async Task<int> AddBusinessAsync(
        string name,
        BusinessStatus status,
        DateTime createdAtUtc,
        DateTime? decidedAtUtc)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var business = Business(name, status, createdAtUtc, decidedAtUtc);
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        return business.Id;
    }

    /// <summary>
    /// Approves a pending application outside the boundary, the way the Super Admin surface does:
    /// the status and the decision instant together.
    /// </summary>
    private async Task ApproveAsync(int businessId, DateTime decidedAtUtc)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var business = await db.Businesses.SingleAsync(candidate => candidate.Id == businessId);
        business.Status = BusinessStatus.Active;
        business.DecidedAtUtc = decidedAtUtc;
        await db.SaveChangesAsync();
    }

    private static Business Business(
        string name,
        BusinessStatus status,
        DateTime createdAtUtc,
        DateTime? decidedAtUtc) =>
        new()
        {
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = status,
            CreatedAtUtc = createdAtUtc,
            DecidedAtUtc = decidedAtUtc,
        };
}
