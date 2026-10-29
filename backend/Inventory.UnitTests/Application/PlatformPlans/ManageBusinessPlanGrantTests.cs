using Inventory.Application.PlatformPlans;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformPlans;

/// <summary>
/// The Super Admin's manual plan grant (issue #982), without ASP.NET Core, EF Core or a database:
/// what a decision writes, what it records, and every refusal that writes nothing.
///
/// <para>The authorization that decides <em>who</em> may reach it is the boundary's and is asserted
/// over real HTTP in <c>SuperAdminPlanGrantsApiTests</c>; the relational guarantees - the write lock,
/// the ownership lease, and the grant and its audit row committing together - are asserted against
/// real SQLite in <c>EfPlatformPlanGrantStoreTests</c>.</para>
/// </summary>
public class ManageBusinessPlanGrantTests
{
    private const int BusinessId = 7;
    private const int OtherBusinessId = 9;
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly DateTime DecidedAt = new(2026, 10, 29, 13, 45, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_business_with_no_grant_reads_as_holding_none()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var view = await Subject(store).GetAsync(BusinessId, CancellationToken.None);

        Assert.NotNull(view);
        Assert.Equal(BusinessId, view.BusinessId);
        Assert.Null(view.Grant);
    }

    [Fact]
    public async Task An_unknown_business_is_not_found_and_nothing_is_written()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);
        var subject = Subject(store);

        Assert.Null(await subject.GetAsync(404, CancellationToken.None));
        Assert.Null(await subject.SetAsync(
            404, "Pro", endsOnUtc: null, "Pilot", revision: 0, CancellationToken.None));
        Assert.Null(await subject.EndAsync(404, "Over", revision: 1, CancellationToken.None));

        Assert.Empty(store.Writes);
        Assert.Empty(store.Transactions);
    }

    /// <summary>
    /// The first grant: the plan, no end date, the reason, revision 1, and one audit entry naming the
    /// administrator - with no reason text on it, because that entry is what reaches a log.
    /// </summary>
    [Fact]
    public async Task A_first_grant_is_written_with_revision_one_and_audited_once()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);
        var audit = new FakePlatformPlanGrantAudit();

        var view = await Subject(store, audit).SetAsync(
            BusinessId, "Pro", endsOnUtc: null, " Complimentary pilot ", revision: 0, CancellationToken.None);

        Assert.NotNull(view?.Grant);
        Assert.Equal(SubscriptionPlan.Pro, view.Grant.Plan);
        Assert.Equal(DecidedAt, view.Grant.Period.StartsAtUtc);
        Assert.Null(view.Grant.Period.EndsAtUtc);
        Assert.Equal("Complimentary pilot", view.Grant.Reason);
        Assert.Equal(1, view.Grant.Revision);

        var write = Assert.Single(store.Writes);
        Assert.Equal(BusinessId, write.BusinessId);
        Assert.Equal(PlatformPlanGrantChange.Created, write.Change);
        Assert.Null(write.FromPlan);
        Assert.Equal("Complimentary pilot", write.GrantReason);
        Assert.Equal("Complimentary pilot", write.AuditReason);
        Assert.Equal(DecidedAt, write.OccurredAtUtc);

        var entry = Assert.Single(audit.Entries);
        // The actor is the normalised identity, which is the one form every access decision and
        // every audit trail in this application compares.
        Assert.Equal(Administrator().DirectoryTenantId, entry.ActorDirectoryTenantId);
        Assert.Equal(Administrator().ObjectId, entry.ActorObjectId);
        Assert.Equal(BusinessId, entry.BusinessId);
        Assert.Equal(PlatformPlanGrantChange.Created, entry.Change);
        Assert.Null(entry.FromPlan);
        Assert.Equal(SubscriptionPlan.Pro, entry.ToPlan);
        Assert.Equal(1, entry.Revision);
        Assert.Equal(DecidedAt, entry.OccurredAtUtc);

        // The log entry has no field a reason could travel in at all, which is what keeps the
        // administrator's prose out of the log pipeline structurally.
        Assert.DoesNotContain(
            "reason",
            string.Join(
                ' ',
                typeof(PlatformPlanGrantAuditEntry)
                    .GetProperties()
                    .Select(property => property.Name)),
            StringComparison.OrdinalIgnoreCase);

        var transaction = Assert.Single(store.Transactions);
        Assert.True(transaction.Committed);
    }

    /// <summary>The authoritative read is the one inside the transaction, and the write follows it.</summary>
    [Fact]
    public async Task The_decision_re_reads_the_business_inside_the_write_transaction()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        await Subject(store).SetAsync(
            BusinessId, "Starter", endsOnUtc: null, "Pilot", revision: 0, CancellationToken.None);

        Assert.Equal(2, store.Reads);
        Assert.Single(store.Transactions);
    }

    /// <summary>
    /// The end date an administrator chooses is the last day the grant applies to, and what is stored
    /// is the exclusive instant at the start of the next one.
    /// </summary>
    [Fact]
    public async Task An_end_date_is_stored_as_the_exclusive_start_of_the_following_day()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var view = await Subject(store).SetAsync(
            BusinessId,
            "Starter",
            new DateOnly(2026, 12, 31),
            "Trial until year end",
            revision: 0,
            CancellationToken.None);

        Assert.Equal(new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc), view?.Grant?.Period.EndsAtUtc);
        Assert.True(view!.Grant!.Period.Covers(new DateTime(2026, 12, 31, 23, 59, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task A_change_records_the_plan_it_moved_from_and_the_next_revision()
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Grant(SubscriptionPlan.Starter, revision: 3));
        var audit = new FakePlatformPlanGrantAudit();

        var view = await Subject(store, audit).SetAsync(
            BusinessId, "Pro", endsOnUtc: null, "Upgraded for the pilot", revision: 3, CancellationToken.None);

        Assert.Equal(SubscriptionPlan.Pro, view?.Grant?.Plan);
        Assert.Equal(4, view?.Grant?.Revision);

        var write = Assert.Single(store.Writes);
        Assert.Equal(PlatformPlanGrantChange.Changed, write.Change);
        Assert.Equal(SubscriptionPlan.Starter, write.FromPlan);
        Assert.Equal(4, write.Revision);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(SubscriptionPlan.Starter, entry.FromPlan);
        Assert.Equal(SubscriptionPlan.Pro, entry.ToPlan);
    }

    /// <summary>
    /// The concurrency criterion: a write carrying a revision other than the stored one is refused
    /// with the fixed sentence and writes nothing, so two Super Admin tabs cannot overwrite each
    /// other.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task A_stale_revision_is_refused_and_writes_nothing(int presented)
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Grant(SubscriptionPlan.Starter, revision: 3));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, "Pro", endsOnUtc: null, "Upgrade", presented, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.StaleRevisionMessage, refusal.Message);
        Assert.Empty(store.Writes);
        Assert.False(Assert.Single(store.Transactions).Committed);
    }

    /// <summary>
    /// And it is the revision read <em>inside</em> the transaction that decides: a grant another
    /// administrator committed while this caller was deciding makes the caller's view stale, even
    /// though it matched when they read it.
    /// </summary>
    [Fact]
    public async Task A_grant_written_between_the_two_reads_makes_the_caller_stale()
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Grant(SubscriptionPlan.Starter, revision: 1));

        store.BeforeInTransactionRead = () =>
            store.Replace(BusinessId, Grant(SubscriptionPlan.Pro, revision: 2));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, "Starter", endsOnUtc: null, "Downgrade", revision: 1, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.StaleRevisionMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// Free, a name this code does not declare, and a <em>number</em> are each refused before
    /// anything is read or locked. The numeric cases are the reason the plan arrives as a name:
    /// <c>"30"</c> must not be accepted as Pro, because a caller never supplies a stored
    /// representation.
    /// </summary>
    [Fact]
    public async Task Free_a_number_and_an_undeclared_name_are_refused_before_any_transaction_is_opened()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        foreach (var plan in new[] { "Free", "Platinum", "30", "0", "99", "", "   ", null })
        {
            var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
                BusinessId, plan, endsOnUtc: null, "Why not", revision: 0, CancellationToken.None));

            Assert.Equal(ManualPlanGrantPolicy.UnsupportedPlanMessage, refusal.Message);
        }

        Assert.Empty(store.Writes);
        Assert.Empty(store.Transactions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_grant_without_a_reason_is_refused(string? reason)
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
            BusinessId, "Pro", endsOnUtc: null, reason, revision: 0, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.ReasonRequiredMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    [Fact]
    public async Task A_reason_longer_than_the_column_is_refused()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
            BusinessId,
            "Pro",
            endsOnUtc: null,
            new string('r', ManualPlanGrantPolicy.MaxReasonLength + 1),
            revision: 0,
            CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.ReasonTooLongMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    /// <summary>An end date already over would entitle nothing at any instant, so it is refused.</summary>
    [Fact]
    public async Task An_end_date_in_the_past_is_refused_and_writes_nothing()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() => Subject(store).SetAsync(
            BusinessId,
            "Pro",
            new DateOnly(2026, 10, 28),
            "Backdated",
            revision: 0,
            CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.EndDateInThePastMessage, refusal.Message);
        Assert.Empty(store.Writes);
        Assert.False(Assert.Single(store.Transactions).Committed);
    }

    /// <summary>
    /// Ending a grant stops it at this instant, keeps the plan, the start and the reason it was
    /// granted for, bumps the revision, and reports no plan on the audit entry - "ended" is not
    /// "granted Free".
    /// </summary>
    [Fact]
    public async Task Ending_a_grant_stops_it_now_and_keeps_the_row()
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Grant(SubscriptionPlan.Pro, revision: 2));
        var audit = new FakePlatformPlanGrantAudit();

        var view = await Subject(store, audit).EndAsync(
            BusinessId, "Pilot finished", revision: 2, CancellationToken.None);

        Assert.Equal(SubscriptionPlan.Pro, view?.Grant?.Plan);
        Assert.Equal(DecidedAt, view?.Grant?.Period.EndsAtUtc);
        Assert.Equal("Granted for the pilot", view?.Grant?.Reason);
        Assert.Equal(3, view?.Grant?.Revision);
        Assert.False(view!.Grant!.Period.Covers(DecidedAt));

        var write = Assert.Single(store.Writes);
        Assert.Equal(PlatformPlanGrantChange.Ended, write.Change);
        Assert.Equal(SubscriptionPlan.Pro, write.FromPlan);
        Assert.Equal("Granted for the pilot", write.GrantReason);
        Assert.Equal("Pilot finished", write.AuditReason);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal(PlatformPlanGrantChange.Ended, entry.Change);
        Assert.Equal(SubscriptionPlan.Pro, entry.FromPlan);
        Assert.Null(entry.ToPlan);
        Assert.Equal(DecidedAt, entry.EndsAtUtc);
    }

    [Fact]
    public async Task Ending_a_grant_a_business_does_not_hold_is_refused()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).EndAsync(
            BusinessId, "Over", revision: 0, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.NoGrantToEndMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    /// <summary>A grant that has already ended is not ended again, so its end instant never moves.</summary>
    [Fact]
    public async Task Ending_an_already_ended_grant_is_refused()
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(
                BusinessId,
                new PlatformPlanGrantState(
                    SubscriptionPlan.Pro,
                    new EntitlementPeriod(DecidedAt.AddDays(-30), DecidedAt.AddDays(-1)),
                    "Granted for the pilot",
                    Revision: 2,
                    DecidedAt.AddDays(-30)));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).EndAsync(
            BusinessId, "Over", revision: 2, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.NoGrantToEndMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// A business holding two complimentary grants is a state this surface cannot produce - it
    /// creates one and only ever updates it - and it refuses to choose between them rather than
    /// silently deciding an entitlement.
    /// </summary>
    [Fact]
    public async Task More_than_one_grant_is_refused_rather_than_chosen_between()
    {
        var store = FakePlatformPlanGrantStore
            .WithBusiness(BusinessId)
            .Holding(BusinessId, Grant(SubscriptionPlan.Starter, revision: 1))
            .Holding(BusinessId, Grant(SubscriptionPlan.Pro, revision: 1));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => Subject(store).SetAsync(
            BusinessId, "Pro", endsOnUtc: null, "Upgrade", revision: 1, CancellationToken.None));

        Assert.Equal(ManualPlanGrantPolicy.AmbiguousGrantMessage, refusal.Message);
        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// A decision is addressed to the business the route named and to no other: the second business
    /// in this store is never read and never written, which is the unit-level half of the two-business
    /// isolation the adapter tests assert relationally.
    /// </summary>
    [Fact]
    public async Task A_decision_touches_only_the_business_it_names()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);
        store.Holding(OtherBusinessId, Grant(SubscriptionPlan.Pro, revision: 5));

        await Subject(store).SetAsync(
            BusinessId, "Starter", endsOnUtc: null, "Pilot", revision: 0, CancellationToken.None);

        var untouched = Assert.Single(store.GrantsOf(OtherBusinessId));
        Assert.Equal(SubscriptionPlan.Pro, untouched.Plan);
        Assert.Equal(5, untouched.Revision);
        Assert.Equal(BusinessId, Assert.Single(store.Writes).BusinessId);
    }

    /// <summary>
    /// A request whose actor cannot be named writes nothing: an audit trail that could not say who
    /// granted a plan would be worse than none, so the actor is read before anything else.
    /// </summary>
    [Fact]
    public async Task A_request_with_no_identifiable_actor_writes_nothing()
    {
        var store = FakePlatformPlanGrantStore.WithBusiness(BusinessId);
        var subject = new ManageBusinessPlanGrant(
            FakeAuthenticatedActorAccessor.NotAuthenticated(),
            store,
            new FakePlatformPlanGrantAudit(),
            new FakeClock(DecidedAt));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() => subject.SetAsync(
            BusinessId, "Pro", endsOnUtc: null, "Pilot", revision: 0, CancellationToken.None));

        Assert.Empty(store.Writes);
        Assert.Empty(store.Transactions);
    }

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }

    private static PlatformPlanGrantState Grant(SubscriptionPlan plan, int revision) =>
        new(
            plan,
            new EntitlementPeriod(DecidedAt.AddDays(-10), null),
            "Granted for the pilot",
            revision,
            DecidedAt.AddDays(-10));

    private static ManageBusinessPlanGrant Subject(
        FakePlatformPlanGrantStore store,
        FakePlatformPlanGrantAudit? audit = null) =>
        new(
            FakeAuthenticatedActorAccessor.Identified(Administrator()),
            store,
            audit ?? new FakePlatformPlanGrantAudit(),
            new FakeClock(DecidedAt));
}
