using Inventory.Application.PriceCatalogue;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Suppliers;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for <c>schedule=due</c> (issue #1108, parent #817): which product-and-supplier
/// pairs one run is issued, decided per pair against each supplier's own schedule, with catch-up and
/// in-flight protection.
///
/// <para><strong>Per pair is the whole point, and only a relational test can show it.</strong> The
/// work list is bounded and rotates, so one run legitimately answers some of a supplier's pairs and
/// not others; the facts the rule reads - a pair's latest answered work item, and whether it has a
/// pending one in a run that is still open and unexpired - are rows, runs, outcomes and expiries.
/// Every test here therefore drives the real use case over the real adapters on a context with no
/// business at all, which is the scope <c>GET api/price-collector/work</c> actually has.</para>
///
/// <para><strong>The supplier schedules are set explicitly.</strong> The nine shipped presets arrive
/// from the migrations with their own schedules and their effective-from instant set to the
/// migration's own instant, which is a real "now": a test that left them alone would be asserting
/// against rows a migration chose, at a boundary later than its own clock. Clearing the collection
/// routes and then recording a schedule is how a test says "this test owns the supplier list".</para>
/// </summary>
public class PriceCollectorDueWorkListTests
{
    /// <summary>
    /// Wednesday 4 November 2026, 10:00 Sydney - inside a day rather than at its edge, so an
    /// assertion about "this morning" is not also an assertion about midnight.
    /// </summary>
    private static readonly DateTime Wednesday =
        new(2026, 11, 3, 23, 0, 0, DateTimeKind.Utc);

    private static readonly DateOnly WednesdayDate = new(2026, 11, 4);

    /// <summary>
    /// The instant every supplier's schedule became effective in these tests: long enough before
    /// them that the coverage boundary is always the occurrence's rather than the schedule's, except
    /// where a test moves it on purpose.
    /// </summary>
    private static readonly DateTime ScheduleEffectiveFrom =
        new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    #region A supplier whose occurrence is already answered

    /// <summary>
    /// The first criterion: on a Wednesday, a daily supplier's pairs are issued and a Thursday-only
    /// supplier's are not - because its current occurrence is the previous Thursday and that pair was
    /// answered on it.
    ///
    /// <para>The answer has to be set up. Without it Thursday's work is legitimately <em>overdue</em>
    /// on a Wednesday - that is the catch-up rule, asserted below - so a test that only checked "not
    /// today's day" would be asserting a rule this issue deliberately does not have.</para>
    /// </summary>
    [Fact]
    public async Task A_supplier_answered_on_its_own_occurrence_is_not_issued_again_before_the_next()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var daily = fixture.SupplierPresetId;
        var weekly = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await CollectableAsync(fixture, daily);
        await CollectableAsync(fixture, weekly);
        await fixture.SetSupplierScheduleAsync(
            daily,
            SupplierSearchFrequency.Daily,
            scheduleEffectiveFromUtc: ScheduleEffectiveFrom);
        await fixture.SetSupplierScheduleAsync(
            weekly,
            SupplierSearchFrequency.Weekly,
            [DayOfWeek.Thursday],
            scheduleEffectiveFromUtc: ScheduleEffectiveFrom);

        var cola = await TrackedProductAsync(fixture);

        // Answered at both suppliers on Thursday 29 October 2026, which is the weekly supplier's
        // current occurrence on this Wednesday and is well before the daily supplier's.
        var lastThursday = new DateTime(2026, 10, 29, 2, 0, 0, DateTimeKind.Utc);
        await AnsweredAsync(fixture, daily, cola, lastThursday);
        await AnsweredAsync(fixture, weekly, cola, lastThursday);

        var workList = await IssueDueAsync(fixture);

        var product = Assert.Single(workList.MatchedProducts);
        var workItem = Assert.Single(product.WorkItems);
        Assert.Equal(daily, workItem.SupplierPresetId);
    }

    /// <summary>
    /// And the catch-up direction, which is the same setup without the answer: a Thursday-only
    /// supplier nothing answered last Thursday is still owed that search on the Wednesday, and is
    /// issued.
    /// </summary>
    [Fact]
    public async Task A_missed_occurrence_is_still_issued_on_a_later_day()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var weekly = fixture.SupplierPresetId;
        await CollectableAsync(fixture, weekly);
        await fixture.SetSupplierScheduleAsync(
            weekly,
            SupplierSearchFrequency.Weekly,
            [DayOfWeek.Thursday],
            scheduleEffectiveFromUtc: ScheduleEffectiveFrom);

        var cola = await TrackedProductAsync(fixture);

        // The last answer is from the Thursday *before* the current occurrence.
        await AnsweredAsync(
            fixture,
            weekly,
            cola,
            new DateTime(2026, 10, 22, 2, 0, 0, DateTimeKind.Utc));

        var workList = await IssueDueAsync(fixture);

        Assert.Equal(weekly, Assert.Single(Assert.Single(workList.MatchedProducts).WorkItems).SupplierPresetId);
    }

    #endregion

    #region A newly tracked product

    /// <summary>
    /// A product nothing has ever searched has no answered work item, so it is due at once - whatever
    /// its suppliers' schedules say and whenever their occurrences were.
    /// </summary>
    [Fact]
    public async Task A_newly_tracked_product_is_issued_at_once()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var fortnightly = fixture.SupplierPresetId;
        await CollectableAsync(fixture, fortnightly);

        // A fortnightly supplier whose on week is this one but whose day is not today, so the only
        // reason this pair can be due is that it has never been answered.
        await fixture.SetSupplierScheduleAsync(
            fortnightly,
            SupplierSearchFrequency.Fortnightly,
            [DayOfWeek.Monday],
            new DateOnly(2026, 11, 2),
            ScheduleEffectiveFrom);

        var cola = await TrackedProductAsync(fixture);

        var workList = await IssueDueAsync(fixture);

        var product = Assert.Single(workList.MatchedProducts);
        Assert.Equal(cola, product.MasterProductId);
        Assert.Equal(fortnightly, Assert.Single(product.WorkItems).SupplierPresetId);
    }

    #endregion

    #region A capped run, then a second one

    /// <summary>
    /// A run capped by its work-item budget issues one product, says so, and a second <c>due</c> run
    /// the same day issues only the pairs the first did not answer.
    ///
    /// <para>The first run is <em>completed</em> before the second, which is what the scheduled
    /// workflow does: while it is still open its unanswered pairs are in flight and are deliberately
    /// not reissued, which the midnight test below is about.</para>
    /// </summary>
    [Fact]
    public async Task A_second_due_run_the_same_day_issues_only_the_pairs_the_first_did_not_answer()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);
        var pepsi = await TrackedProductAsync(
            fixture,
            "Pepsi",
            "Max",
            fixture.FirstBusinessPepsiId);

        // One work item of budget, so the run reaches exactly one of the two products.
        var first = await IssueDueAsync(fixture, budget: 1);
        var firstProduct = Assert.Single(first.MatchedProducts);
        Assert.True(first.WorkItemLimitReached);

        // The collector answers what it was issued, and the run closes.
        await fixture.AnswerWorkItemAsync(
            Assert.Single(firstProduct.WorkItems).WorkItemId,
            PriceCollectorWorkItemOutcome.Complete,
            Wednesday.AddMinutes(5));
        await fixture.CloseRunAsync(
            first.RunId!.Value,
            PriceCollectorRunStatus.Succeeded,
            Wednesday.AddMinutes(10));

        var second = await IssueDueAsync(fixture, budget: 1);

        var secondProduct = Assert.Single(second.MatchedProducts);
        Assert.NotEqual(firstProduct.MasterProductId, secondProduct.MasterProductId);
        Assert.Contains(secondProduct.MasterProductId, new[] { cola, pepsi });

        // And a third run has nothing left to do at all, which is the whole catch-up story closing.
        await fixture.AnswerWorkItemAsync(
            Assert.Single(secondProduct.WorkItems).WorkItemId,
            PriceCollectorWorkItemOutcome.Complete,
            Wednesday.AddMinutes(15));
        await fixture.CloseRunAsync(
            second.RunId!.Value,
            PriceCollectorRunStatus.Succeeded,
            Wednesday.AddMinutes(20));

        var third = await IssueDueAsync(fixture);
        Assert.Null(third.RunId);
        Assert.Empty(third.MatchedProducts);
    }

    #endregion

    #region Failures and not-searched pairs

    /// <summary>
    /// A failed search is an <em>answer</em> and covers the pair for its occurrence; a pair finalised
    /// as <c>NotSearched</c> is not, and is reissued at once - even while the run that recorded it is
    /// still open.
    ///
    /// <para>That asymmetry is deliberate and is the thing most likely to be got wrong. A failure is
    /// a search that happened and did not work, which the freshness model already accounts for; a
    /// <c>NotSearched</c> is a collector or a run budget declining to look, so nothing about the pair
    /// is known and re-issuing it costs nothing that was not already owed.</para>
    /// </summary>
    [Fact]
    public async Task A_not_searched_pair_is_reissued_at_once_and_a_failed_one_is_not()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var failing = fixture.SupplierPresetId;
        var skipped = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await CollectableAsync(fixture, failing);
        await CollectableAsync(fixture, skipped);
        await DailyAsync(fixture, failing);
        await DailyAsync(fixture, skipped);

        await TrackedProductAsync(fixture);

        var first = await IssueDueAsync(fixture);
        var issued = Assert.Single(first.MatchedProducts).WorkItems;
        Assert.Equal(2, issued.Count);

        var failed = issued.Single(item => item.SupplierPresetId == failing);
        var notSearched = issued.Single(item => item.SupplierPresetId == skipped);

        await fixture.AnswerWorkItemAsync(
            failed.WorkItemId,
            PriceCollectorWorkItemOutcome.Failed,
            Wednesday.AddMinutes(5));
        await fixture.AnswerWorkItemAsync(
            notSearched.WorkItemId,
            PriceCollectorWorkItemOutcome.NotSearched,
            Wednesday.AddMinutes(5));

        // The run is deliberately left OPEN: a NotSearched pair may be retried at once, which is the
        // criterion's "finalised not_searched while its run is still open".
        var second = await IssueDueAsync(fixture);

        var product = Assert.Single(second.MatchedProducts);
        Assert.Equal(skipped, Assert.Single(product.WorkItems).SupplierPresetId);
    }

    #endregion

    #region In flight

    /// <summary>
    /// A run issued before midnight and still open after it blocks the next day's <c>due</c> issue of
    /// its pending pairs - whenever that run was issued, which is the whole in-flight rule.
    ///
    /// <para>And nothing at all is issued here, so no run is created: the only pair there was is in
    /// flight, which is a success with nothing to do rather than an empty run. The open run is
    /// therefore still open afterwards, which is what makes the protection hold rather than expire
    /// itself away.</para>
    /// </summary>
    [Fact]
    public async Task A_run_still_open_after_midnight_blocks_the_next_days_issue_of_its_pending_pairs()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);

        // Issued at 23:30 Sydney on the Tuesday, with an expiry well after the Wednesday morning run.
        var lateTuesday = new DateTime(2026, 11, 3, 12, 30, 0, DateTimeKind.Utc);
        var openRun = await fixture.SeedRunAsync(lateTuesday, lateTuesday.AddHours(18));
        await fixture.SeedWorkItemAsync(openRun, cola, waitingKey: null, supplier);

        var workList = await IssueDueAsync(fixture);

        Assert.Null(workList.RunId);
        Assert.Empty(workList.MatchedProducts);
        Assert.False(workList.WorkItemLimitReached);

        // Nothing was written at all - the open run is untouched, so its pending work still blocks
        // and has not been turned into a page of NotSearched rows.
        var runs = await fixture.AllRunsAsync();
        Assert.Equal(PriceCollectorRunStatus.Running, Assert.Single(runs).Status);
        Assert.Single(await fixture.AllWorkItemsAsync());
    }

    /// <summary>
    /// Once that run has <strong>expired</strong> the pair is due again: expiry finalises its pending
    /// work as <c>NotSearched</c> (issues #924 and #937), which neither covers nor blocks. A pair
    /// stranded in a run nobody reported on is therefore catch-up work rather than work nothing can
    /// ever issue again.
    /// </summary>
    [Fact]
    public async Task A_pair_pending_in_an_expired_run_is_due_again()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);

        // The run's own expiry is before this issue's instant, so it is no longer unexpired.
        var yesterday = new DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
        var openRun = await fixture.SeedRunAsync(yesterday, yesterday.AddHours(6));
        await fixture.SeedWorkItemAsync(openRun, cola, waitingKey: null, supplier);

        var workList = await IssueDueAsync(fixture);

        Assert.NotNull(workList.RunId);
        Assert.Equal(supplier, Assert.Single(Assert.Single(workList.MatchedProducts).WorkItems).SupplierPresetId);

        // And issuing expired the run that was open, which is what turns its stranded pair into a
        // recorded NotSearched rather than leaving two open runs.
        Assert.Equal(PriceCollectorRunStatus.Expired, await fixture.RunStatusAsync(openRun));
    }

    /// <summary>
    /// A pair pending under the <em>previous</em> schedule revision is still in flight: the boundary
    /// moving does not make a search that is already running issuable a second time. The pair becomes
    /// due when that work is finalised or its run expires, and not before - which is what stops a
    /// schedule edit from paying for a search twice.
    /// </summary>
    [Fact]
    public async Task A_pair_pending_from_the_previous_schedule_revision_is_not_issued_again()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);

        var issuedEarlier = new DateTime(2026, 11, 3, 12, 30, 0, DateTimeKind.Utc);
        var openRun = await fixture.SeedRunAsync(issuedEarlier, issuedEarlier.AddHours(18));
        var pending = await fixture.SeedWorkItemAsync(openRun, cola, waitingKey: null, supplier);

        // The schedule is edited after that work was issued, which moves the boundary past it.
        await fixture.SetSupplierScheduleAsync(
            supplier,
            SupplierSearchFrequency.Weekly,
            [DayOfWeek.Wednesday, DayOfWeek.Saturday],
            scheduleEffectiveFromUtc: Wednesday.AddMinutes(-5));

        var blocked = await IssueDueAsync(fixture);
        Assert.Null(blocked.RunId);

        // Finalised as NotSearched - nothing looked - and now the new schedule's boundary applies.
        await fixture.AnswerWorkItemAsync(
            pending,
            PriceCollectorWorkItemOutcome.NotSearched,
            Wednesday.AddMinutes(1));

        var reissued = await IssueDueAsync(fixture);
        Assert.NotNull(reissued.RunId);
        Assert.Equal(supplier, Assert.Single(Assert.Single(reissued.MatchedProducts).WorkItems).SupplierPresetId);
    }

    /// <summary>
    /// And an answer given before the boundary moved does not satisfy the new schedule: a pair
    /// answered this morning, whose supplier's schedule is then saved this afternoon, is due again on
    /// the same day.
    /// </summary>
    [Fact]
    public async Task An_answer_from_before_a_schedule_change_does_not_cover_the_pair()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);
        await AnsweredAsync(fixture, supplier, cola, Wednesday.AddMinutes(-60));

        // Covered while the boundary is the occurrence's.
        Assert.Null((await IssueDueAsync(fixture)).RunId);

        // The schedule is saved, which advances the effective-from instant past that answer.
        await fixture.SetSupplierScheduleAsync(
            supplier,
            SupplierSearchFrequency.Weekly,
            [DayOfWeek.Wednesday],
            scheduleEffectiveFromUtc: Wednesday.AddMinutes(-30));

        var workList = await IssueDueAsync(fixture);
        Assert.NotNull(workList.RunId);
        Assert.Equal(supplier, Assert.Single(Assert.Single(workList.MatchedProducts).WorkItems).SupplierPresetId);
    }

    #endregion

    #region Nothing due

    /// <summary>
    /// A <c>due</c> call with nothing due creates <strong>no run</strong> and answers an empty list
    /// with no run id. Nothing is left open to expire into <c>NotSearched</c> rows, and #1110 relies
    /// on there being nothing to finalise.
    /// </summary>
    [Fact]
    public async Task A_due_call_with_nothing_due_creates_no_run_and_answers_an_empty_list()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);
        await AnsweredAsync(fixture, supplier, cola, Wednesday.AddMinutes(-60));

        // The only run and work item that exist are the answered one this setup wrote.
        var runsBefore = await fixture.AllRunsAsync();
        var workItemsBefore = await fixture.AllWorkItemsAsync();

        var workList = await IssueDueAsync(fixture);

        Assert.Null(workList.RunId);
        Assert.Null(workList.IssuedAtUtc);
        Assert.Null(workList.ExpiresAtUtc);
        Assert.Empty(workList.Suppliers);
        Assert.Empty(workList.MatchedProducts);
        Assert.Empty(workList.WaitingProducts);
        Assert.Empty(workList.MasterProductsForMatching);
        Assert.False(workList.WorkItemLimitReached);

        // No new run row, and no new work item: the answer is "nothing to do", not "an empty run"
        // that something would later have to finalise.
        Assert.Equal(
            runsBefore.Select(run => run.Id),
            (await fixture.AllRunsAsync()).Select(run => run.Id));
        Assert.Equal(
            workItemsBefore.Select(item => item.Id),
            (await fixture.AllWorkItemsAsync()).Select(item => item.Id));
    }

    /// <summary>
    /// A product whose pairs are all covered is not published with an empty work list: it is not
    /// published at all. "Search this and report nothing" is not a thing to ask a collector.
    /// </summary>
    [Fact]
    public async Task A_product_whose_every_pair_is_covered_is_not_published()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var covered = fixture.SupplierPresetId;
        var due = await fixture.SeedSupplierPresetAsync("Beta Grocer");
        await CollectableAsync(fixture, covered);
        await CollectableAsync(fixture, due);
        await DailyAsync(fixture, covered);
        await DailyAsync(fixture, due);

        var cola = await TrackedProductAsync(fixture);
        var pepsi = await TrackedProductAsync(fixture, "Pepsi", "Max", fixture.FirstBusinessPepsiId);

        // Cola is covered at both suppliers; Pepsi at neither.
        await AnsweredAsync(fixture, covered, cola, Wednesday.AddMinutes(-60));
        await AnsweredAsync(fixture, due, cola, Wednesday.AddMinutes(-60));

        var workList = await IssueDueAsync(fixture);

        var product = Assert.Single(workList.MatchedProducts);
        Assert.Equal(pepsi, product.MasterProductId);
        Assert.Equal(2, product.WorkItems.Count);
    }

    #endregion

    #region The schedule=all bypass

    /// <summary>
    /// <c>schedule=all</c> issues an already covered pair, and an already in-flight one, on purpose:
    /// a manual full run is a documented bypass of the due rule and of the in-flight protection
    /// alike. It is kept because somebody asking for a full run has decided to re-search everything,
    /// and it is the default so a manual run is unchanged by this issue.
    /// </summary>
    [Fact]
    public async Task Schedule_all_issues_a_covered_and_an_in_flight_pair_on_purpose()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        var cola = await TrackedProductAsync(fixture);

        // Answered a minute ago, and pending in a run that is still open and unexpired - the two
        // states that each stop `due` on their own.
        await AnsweredAsync(fixture, supplier, cola, Wednesday.AddMinutes(-1));
        var openRun = await fixture.SeedRunAsync(Wednesday.AddMinutes(-2), Wednesday.AddHours(6));
        await fixture.SeedWorkItemAsync(openRun, cola, waitingKey: null, supplier);

        // `due` is satisfied either way.
        Assert.Null((await IssueDueAsync(fixture)).RunId);

        // `all` issues it anyway.
        var all = await IssueAsync(fixture, PriceCollectorWorkSchedule.All);

        Assert.NotNull(all.RunId);
        Assert.Equal(supplier, Assert.Single(Assert.Single(all.MatchedProducts).WorkItems).SupplierPresetId);
    }

    #endregion

    #region The published suppliers and freshness

    /// <summary>
    /// The freshness limit the work list publishes for a supplier is the one its schedule derives, so
    /// the cadence the collector is run on and the age at which one of its prices stops being
    /// compared cannot disagree.
    /// </summary>
    [Fact]
    public async Task The_published_supplier_carries_the_freshness_its_schedule_derives()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await fixture.SetSupplierScheduleAsync(
            supplier,
            SupplierSearchFrequency.Weekly,
            [DayOfWeek.Wednesday, DayOfWeek.Saturday],
            scheduleEffectiveFromUtc: ScheduleEffectiveFrom);

        var stored = await fixture.WorkListStoreFor(fixture.FirstBusinessId)
            .ListCollectableSuppliersAsync(CancellationToken.None);

        var read = stored.Single(entry => entry.SupplierPresetId == supplier);

        Assert.Equal(SupplierSearchFrequency.Weekly, read.Schedule.Frequency);
        Assert.Equal([DayOfWeek.Wednesday, DayOfWeek.Saturday], read.Schedule.DaysOfWeek);
        Assert.Null(read.Schedule.FortnightStartDate);
        Assert.Equal(ScheduleEffectiveFrom, read.ScheduleEffectiveFromUtc);
        Assert.Equal(
            SupplierSearchSchedulePolicy.FreshnessHoursFor(read.Schedule),
            read.PriceCheckFreshnessHours);
        Assert.Equal(102, read.PriceCheckFreshnessHours);
    }

    /// <summary>
    /// A supplier whose <em>Search prices</em> switch is off is not due whatever its schedule says,
    /// and neither is one nothing collects: the first condition of the rule is unchanged from #1077.
    /// </summary>
    [Fact]
    public async Task A_supplier_that_is_not_searched_or_not_collectable_is_never_due()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var unsearched = fixture.SupplierPresetId;
        var unconnected = await fixture.SeedSupplierPresetAsync("Beta Grocer");

        await fixture.SetSupplierCollectionAsync(
            unsearched,
            SupplierPriceConnector.Kmart,
            searchPrices: false);
        await fixture.SetSupplierCollectionAsync(unconnected, connector: null);
        await DailyAsync(fixture, unsearched);
        await DailyAsync(fixture, unconnected);

        await TrackedProductAsync(fixture);

        var workList = await IssueDueAsync(fixture);

        Assert.Null(workList.RunId);
        Assert.Empty(await fixture.AllRunsAsync());
        Assert.Empty(await fixture.AllWorkItemsAsync());
    }

    #endregion

    #region Waiting products

    /// <summary>
    /// The rule is per pair whichever way the product is named: a waiting key is covered, blocked and
    /// caught up exactly as a master product is, because a work item names one or the other and the
    /// due read keys on both.
    /// </summary>
    [Fact]
    public async Task A_waiting_key_is_covered_and_caught_up_exactly_as_a_master_product_is()
    {
        await using var fixture = await PriceCatalogueFixture.CreateWithNoCollectableSuppliersAsync();

        var supplier = fixture.SupplierPresetId;
        await CollectableAsync(fixture, supplier);
        await DailyAsync(fixture, supplier);

        await fixture.TrackAsync(fixture.FirstBusinessId, fixture.FirstBusinessColaId);

        var first = await IssueDueAsync(fixture);
        var waiting = Assert.Single(first.WaitingProducts);
        var workItem = Assert.Single(waiting.WorkItems);

        await fixture.AnswerWorkItemAsync(
            workItem.WorkItemId,
            PriceCollectorWorkItemOutcome.NoMatches,
            Wednesday.AddMinutes(5));
        await fixture.CloseRunAsync(
            first.RunId!.Value,
            PriceCollectorRunStatus.Succeeded,
            Wednesday.AddMinutes(6));

        // Covered for the rest of the day.
        Assert.Null((await IssueDueAsync(fixture)).RunId);

        // And owed again tomorrow, which for a daily supplier is the next occurrence.
        var tomorrow = await IssueDueAsync(fixture, today: WednesdayDate.AddDays(1));
        Assert.Equal(waiting.WaitingKey, Assert.Single(tomorrow.WaitingProducts).WaitingKey);
    }

    #endregion

    #region Composition

    /// <summary>A collectable supplier with a route, which is the first condition of the rule.</summary>
    private static Task CollectableAsync(PriceCatalogueFixture fixture, int supplierPresetId) =>
        fixture.SetSupplierCollectionAsync(supplierPresetId, SupplierPriceConnector.Kmart);

    private static Task DailyAsync(PriceCatalogueFixture fixture, int supplierPresetId) =>
        fixture.SetSupplierScheduleAsync(
            supplierPresetId,
            SupplierSearchFrequency.Daily,
            scheduleEffectiveFromUtc: ScheduleEffectiveFrom);

    /// <summary>
    /// One master product the first business tracks, matched - which is the state that makes it a
    /// matched product in the work list rather than a waiting key.
    /// </summary>
    private static async Task<long> TrackedProductAsync(
        PriceCatalogueFixture fixture,
        string brand = "Coca-Cola",
        string name = "Classic",
        long? productId = null)
    {
        var master = await fixture.SeedMasterProductAsync(brand, name);
        var tracking = await fixture.TrackAsync(
            fixture.FirstBusinessId,
            productId ?? fixture.FirstBusinessColaId);
        await fixture.MatchAsync(tracking.Id, master);

        return master;
    }

    /// <summary>
    /// One answered work item for a pair, in a run of its own that is already closed - which is the
    /// state "this pair was answered at this instant" and nothing else.
    /// </summary>
    private static async Task AnsweredAsync(
        PriceCatalogueFixture fixture,
        int supplierPresetId,
        long masterProductId,
        DateTime answeredAtUtc)
    {
        var run = await fixture.SeedRunAsync(answeredAtUtc, answeredAtUtc.AddHours(6));
        var workItem = await fixture.SeedWorkItemAsync(
            run,
            masterProductId,
            waitingKey: null,
            supplierPresetId);

        await fixture.AnswerWorkItemAsync(
            workItem,
            PriceCollectorWorkItemOutcome.Complete,
            answeredAtUtc);
        await fixture.CloseRunAsync(
            run,
            PriceCollectorRunStatus.Succeeded,
            answeredAtUtc.AddMinutes(1));
    }

    private static Task<PriceCollectorWorkList> IssueDueAsync(
        PriceCatalogueFixture fixture,
        int? budget = null,
        DateOnly? today = null) =>
        IssueAsync(fixture, PriceCollectorWorkSchedule.Due, budget, today);

    /// <summary>
    /// One run issued by the real use case over the real adapters, on the denied scope a collector
    /// request carries, with the collection calendar fixed to the Wednesday these tests are about.
    /// </summary>
    private static Task<PriceCollectorWorkList> IssueAsync(
        PriceCatalogueFixture fixture,
        PriceCollectorWorkSchedule schedule,
        int? budget = null,
        DateOnly? today = null) =>
        new IssuePriceCollectorWorkList(
                fixture.DeniedWorkListStore(),
                fixture.DeniedRunStore(),
                new FixedRunSettings(PriceCollectorRunPolicy.DefaultExpiryHours),
                new FakeClock(Wednesday),
                new FakeSupplierSearchScheduleCalendar(today ?? WednesdayDate),
                budget ?? PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)
            .Handle(schedule, CancellationToken.None);

    private sealed class FixedRunSettings(int runExpiryHours) : IPriceCollectorRunSettings
    {
        public int RunExpiryHours { get; } = runExpiryHours;
    }

    #endregion
}
