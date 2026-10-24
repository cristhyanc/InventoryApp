using Inventory.Application.PlatformOptions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// The two use cases behind the switchable Admin option (issue #873), without ASP.NET Core, EF Core
/// or a database: the default an installation that has never switched anything is answered with, and
/// what a committed switch stores and records.
/// </summary>
public class OptionVisibilityTests
{
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly DateTime SwitchedAt = new(2026, 10, 24, 6, 15, 0, DateTimeKind.Utc);

    /// <summary>
    /// The criterion "existing installations default to On": nothing stored is the shipped state, and
    /// it is answered as offered rather than as hidden. Nothing is written to answer it either - a
    /// read that stored the default would turn every first page load into a write.
    /// </summary>
    [Fact]
    public async Task With_nothing_stored_the_option_is_offered_and_nothing_is_written()
    {
        var store = new FakeOptionVisibilityStore();

        var settings = await new GetOptionVisibility(store).Handle(CancellationToken.None);

        Assert.True(settings.HistoricalGstClassificationVisible);
        Assert.Equal(OptionVisibilitySettings.Default, settings);
        Assert.Empty(store.Writes);
    }

    /// <summary>A stored answer is reported as it is, in both directions.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_answer_is_reported_rather_than_the_default(bool visible)
    {
        var settings = await new GetOptionVisibility(FakeOptionVisibilityStore.Holding(visible))
            .Handle(CancellationToken.None);

        Assert.Equal(visible, settings.HistoricalGstClassificationVisible);
    }

    /// <summary>
    /// A committed switch stores exactly what was asked for, answers with what was stored, and
    /// records one audit event naming the acting administrator and the resulting state - and nothing
    /// else, because the setting is about nobody's business or data.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_switch_stores_the_requested_state_and_audits_it_once(bool visible)
    {
        var store = FakeOptionVisibilityStore.Holding(!visible);
        var audit = new FakeOptionVisibilityAudit();
        var administrator = Administrator();

        var stored = await Switch(store, audit).Handle(visible, CancellationToken.None);

        Assert.Equal(visible, stored.HistoricalGstClassificationVisible);
        Assert.Equal(visible, Assert.Single(store.Writes).HistoricalGstClassificationVisible);
        Assert.Equal(SwitchedAt, store.LastUpdatedAtUtc);
        // The acting administrator's own identity, as the actor port reported it - never a value the
        // use case assembled, and never anything about a business or a member.
        Assert.Equal(
            new OptionVisibilityAuditEntry(
                administrator.DirectoryTenantId,
                administrator.ObjectId,
                visible,
                SwitchedAt),
            Assert.Single(audit.Entries));
    }

    /// <summary>
    /// Switching it to the state it is already in is still one ordinary write: there is no
    /// "unchanged" short-circuit, because the stored instant is what records that somebody confirmed
    /// the setting, and a silent no-op would make the audit trail disagree with the request.
    /// </summary>
    [Fact]
    public async Task Switching_it_to_the_state_it_already_holds_is_still_written_and_audited()
    {
        var store = FakeOptionVisibilityStore.Holding(false);
        var audit = new FakeOptionVisibilityAudit();

        var stored = await Switch(store, audit).Handle(visible: false, CancellationToken.None);

        Assert.False(stored.HistoricalGstClassificationVisible);
        Assert.Single(store.Writes);
        Assert.Single(audit.Entries);
    }

    /// <summary>
    /// An actor the request cannot identify writes nothing and is denied: a change nobody can be
    /// named for is a change the audit trail could not account for. The boundary has already
    /// established platform authority by the time this runs - this is the identity behind it.
    /// </summary>
    [Fact]
    public async Task An_unidentifiable_actor_is_denied_and_nothing_is_written()
    {
        var store = FakeOptionVisibilityStore.Holding(true);
        var audit = new FakeOptionVisibilityAudit();
        var useCase = new SetHistoricalGstClassificationVisibility(
            store,
            FakeAuthenticatedActorAccessor.Unidentifiable(),
            audit,
            new FakeClock(SwitchedAt));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => useCase.Handle(visible: false, CancellationToken.None));

        Assert.Empty(store.Writes);
        Assert.Empty(audit.Entries);
    }

    private static SetHistoricalGstClassificationVisibility Switch(
        FakeOptionVisibilityStore store,
        FakeOptionVisibilityAudit audit) =>
        new(store, IdentifiedAdministrator(), audit, new FakeClock(SwitchedAt));

    private static FakeAuthenticatedActorAccessor IdentifiedAdministrator() =>
        FakeAuthenticatedActorAccessor.Identified(Administrator());

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }
}
