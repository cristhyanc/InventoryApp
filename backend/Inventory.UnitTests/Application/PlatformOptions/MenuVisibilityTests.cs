using Inventory.Application.PlatformOptions;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.PlatformOptions;

/// <summary>
/// The two use cases behind the switchable Admin menu destinations (issue #1148), without ASP.NET
/// Core, EF Core or a database: what an installation that has never switched anything is answered
/// with, what one committed switch stores and records, and what an unknown key does.
///
/// <para>This file replaces <c>OptionVisibilityTests</c> (issue #873), whose single named boolean is
/// now the catalogue key <c>admin.historical-gst-classification</c> and is derived from it.</para>
/// </summary>
public class MenuVisibilityTests
{
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
    private const string AdministratorObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    private static readonly DateTime SwitchedAt = new(2026, 11, 9, 6, 15, 0, DateTimeKind.Utc);

    /// <summary>
    /// The criterion "when no row exists for a key, that key reads as On": nothing stored is the
    /// state every installation is in, and every destination is offered. Nothing is written to answer
    /// it either - a read that stored the defaults would turn every first page load into a write.
    /// </summary>
    [Fact]
    public async Task With_nothing_stored_every_destination_is_offered_and_nothing_is_written()
    {
        var store = new FakeMenuVisibilityStore();

        var visibility = await new GetMenuVisibility(store).Handle(CancellationToken.None);

        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => option.Key),
            visibility.Options.Select(option => option.Key));
        Assert.All(visibility.Options, option => Assert.True(option.Visible));
        Assert.Empty(visibility.SwitchedOffKeys);
        Assert.Empty(store.Writes);
    }

    /// <summary>
    /// Every answered destination carries the menu group the catalogue put it in (issue #1153), so
    /// the Super Admin surface renders its Reports and Admin sections from the server's own answer
    /// rather than from a second copy of the grouping.
    /// </summary>
    [Fact]
    public async Task Every_answered_destination_carries_its_catalogue_group()
    {
        var visibility = await new GetMenuVisibility(new FakeMenuVisibilityStore())
            .Handle(CancellationToken.None);

        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => (option.Key, option.Group, option.Label)),
            visibility.Options.Select(option => (option.Key, option.Group, option.Label)));
    }

    /// <summary>
    /// Each of the eight Reports destinations (issue #1153) behaves exactly as an Admin one does: it
    /// defaults to On with nothing stored, one switch stores and audits that one key, and the shared
    /// answer then names it as switched off. Every key is asserted individually, because a key that
    /// was declared but could not be operated would be a switch nobody can use.
    /// </summary>
    [Theory]
    [InlineData(MenuVisibilityKeys.ReportsBookkeeping)]
    [InlineData(MenuVisibilityKeys.ReportsDaily)]
    [InlineData(MenuVisibilityKeys.ReportsTransactions)]
    [InlineData(MenuVisibilityKeys.ReportsReconciliation)]
    [InlineData(MenuVisibilityKeys.ReportsMachines)]
    [InlineData(MenuVisibilityKeys.ReportsProducts)]
    [InlineData(MenuVisibilityKeys.ReportsSiteCommissions)]
    [InlineData(MenuVisibilityKeys.ReportsGst)]
    public async Task A_reports_destination_defaults_to_offered_and_is_switched_like_any_other(string key)
    {
        var store = new FakeMenuVisibilityStore();
        var audit = new FakeMenuVisibilityAudit();

        Assert.True((await new GetMenuVisibility(store).Handle(CancellationToken.None)).IsVisible(key));

        var stored = await Switch(store, audit).Handle(key, visible: false, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(key, stored.Key);
        Assert.Equal(MenuVisibilityGroups.Reports, stored.Group);
        Assert.False(stored.Visible);
        Assert.Equal(new StoredMenuVisibility(key, false), Assert.Single(store.Writes));
        Assert.Equal(key, Assert.Single(audit.Entries).Key);
        Assert.Equal(
            [key],
            (await new GetMenuVisibility(store).Handle(CancellationToken.None)).SwitchedOffKeys);
    }

    /// <summary>
    /// A stored answer is reported as it is, in both directions, and only for the key it was stored
    /// for: switching one destination off leaves every other one offered.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_stored_answer_is_reported_for_its_own_key_alone(bool visible)
    {
        var visibility = await new GetMenuVisibility(
                FakeMenuVisibilityStore.Holding(MenuVisibilityKeys.Imports, visible))
            .Handle(CancellationToken.None);

        Assert.Equal(visible, visibility.IsVisible(MenuVisibilityKeys.Imports));
        Assert.All(
            visibility.Options.Where(option => option.Key != MenuVisibilityKeys.Imports),
            option => Assert.True(option.Visible));
        Assert.Equal(
            visible ? [] : new[] { MenuVisibilityKeys.Imports },
            visibility.SwitchedOffKeys);
    }

    /// <summary>
    /// Several destinations are switched off independently, and the answer names exactly those - the
    /// list every signed-in caller is reported through <c>GET /api/me/access</c>.
    /// </summary>
    [Fact]
    public async Task The_switched_off_keys_are_exactly_the_keys_stored_as_off()
    {
        var store = FakeMenuVisibilityStore.HoldingAll(
            (MenuVisibilityKeys.AvcoTransition, false),
            (MenuVisibilityKeys.CostingRepair, true),
            (MenuVisibilityKeys.HistoricalGstClassification, false));

        var visibility = await new GetMenuVisibility(store).Handle(CancellationToken.None);

        Assert.Equal(
            [MenuVisibilityKeys.AvcoTransition, MenuVisibilityKeys.HistoricalGstClassification],
            visibility.SwitchedOffKeys.Order(StringComparer.Ordinal).ToArray());
        Assert.True(visibility.IsVisible(MenuVisibilityKeys.CostingRepair));
    }

    /// <summary>
    /// A stored row whose key the catalogue no longer declares is ignored rather than reported. The
    /// catalogue is the closed list of what exists, so a retired key must not reach a client as a
    /// destination it would then have to recognise - and nothing deletes the row, so retiring a key
    /// stays reversible.
    /// </summary>
    [Fact]
    public async Task A_stored_key_the_catalogue_does_not_declare_is_ignored()
    {
        var store = FakeMenuVisibilityStore.HoldingAll(
            ("admin.retired-destination", false),
            (MenuVisibilityKeys.Imports, false));

        var visibility = await new GetMenuVisibility(store).Handle(CancellationToken.None);

        Assert.Equal([MenuVisibilityKeys.Imports], visibility.SwitchedOffKeys);
        Assert.Equal(
            MenuVisibilityCatalogue.Options.Select(option => option.Key),
            visibility.Options.Select(option => option.Key));
    }

    /// <summary>
    /// A committed switch stores exactly what was asked for against exactly one key, answers with
    /// what was stored and its label, and records one audit event naming the acting administrator,
    /// the key and the resulting state - and nothing else, because the setting is about nobody's
    /// business or data.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_switch_stores_the_requested_state_for_one_key_and_audits_it_once(bool visible)
    {
        var store = FakeMenuVisibilityStore.Holding(MenuVisibilityKeys.CostingRepair, !visible);
        var audit = new FakeMenuVisibilityAudit();
        var administrator = Administrator();

        var stored = await Switch(store, audit)
            .Handle(MenuVisibilityKeys.CostingRepair, visible, CancellationToken.None);

        Assert.NotNull(stored);
        Assert.Equal(MenuVisibilityKeys.CostingRepair, stored.Key);
        Assert.Equal("Costing Repair", stored.Label);
        Assert.Equal(visible, stored.Visible);
        Assert.Equal(
            new StoredMenuVisibility(MenuVisibilityKeys.CostingRepair, visible),
            Assert.Single(store.Writes));
        Assert.Equal(SwitchedAt, store.LastUpdatedAtUtc);
        // The acting administrator's own identity, as the actor port reported it - never a value the
        // use case assembled, and never anything about a business or a member.
        Assert.Equal(
            new MenuVisibilityAuditEntry(
                administrator.DirectoryTenantId,
                administrator.ObjectId,
                MenuVisibilityKeys.CostingRepair,
                visible,
                SwitchedAt),
            Assert.Single(audit.Entries));
    }

    /// <summary>
    /// Two writes to two different keys each change their own key alone: the second does not
    /// overwrite, re-assert or clear the first. That is the acceptance criterion about a concurrent
    /// second write, at the level this use case decides it - one row per key, named in the call.
    /// </summary>
    [Fact]
    public async Task A_second_write_to_another_key_leaves_the_first_key_as_it_was()
    {
        var store = new FakeMenuVisibilityStore();
        var switcher = Switch(store, new FakeMenuVisibilityAudit());

        await switcher.Handle(MenuVisibilityKeys.Imports, visible: false, CancellationToken.None);
        await switcher.Handle(MenuVisibilityKeys.AvcoTransition, visible: false, CancellationToken.None);

        var visibility = await new GetMenuVisibility(store).Handle(CancellationToken.None);

        Assert.Equal(
            [MenuVisibilityKeys.AvcoTransition, MenuVisibilityKeys.Imports],
            visibility.SwitchedOffKeys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(
            [MenuVisibilityKeys.Imports, MenuVisibilityKeys.AvcoTransition],
            store.Writes.Select(write => write.Key).ToArray());
    }

    /// <summary>
    /// Switching a destination to the state it is already in is still one ordinary write: there is no
    /// "unchanged" short-circuit, because the stored instant is what records that somebody confirmed
    /// the setting, and a silent no-op would make the audit trail disagree with the request.
    /// </summary>
    [Fact]
    public async Task Switching_a_key_to_the_state_it_already_holds_is_still_written_and_audited()
    {
        var store = FakeMenuVisibilityStore.Holding(MenuVisibilityKeys.Imports, visible: false);
        var audit = new FakeMenuVisibilityAudit();

        var stored = await Switch(store, audit)
            .Handle(MenuVisibilityKeys.Imports, visible: false, CancellationToken.None);

        Assert.False(stored!.Visible);
        Assert.Single(store.Writes);
        Assert.Single(audit.Entries);
    }

    /// <summary>
    /// A key the catalogue does not declare is rejected, and nothing at all is stored or audited. The
    /// catalogue is closed on purpose: a caller that could invent a key would be writing rows nothing
    /// reads and could not be told apart from a caller switching a real destination off.
    /// </summary>
    [Theory]
    [InlineData("admin.users")]
    [InlineData("reports.sales")]
    [InlineData("Admin.Imports")]
    [InlineData("")]
    public async Task An_unknown_key_is_rejected_and_nothing_is_written(string key)
    {
        var store = new FakeMenuVisibilityStore();
        var audit = new FakeMenuVisibilityAudit();

        var stored = await Switch(store, audit).Handle(key, visible: false, CancellationToken.None);

        Assert.Null(stored);
        Assert.Empty(store.Writes);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// An actor the request cannot identify writes nothing and is denied: a change nobody can be
    /// named for is a change the audit trail could not account for. The boundary has already
    /// established platform authority by the time this runs - this is the identity behind it.
    /// </summary>
    [Fact]
    public async Task An_unidentifiable_actor_is_denied_and_nothing_is_written()
    {
        var store = new FakeMenuVisibilityStore();
        var audit = new FakeMenuVisibilityAudit();
        var useCase = new SetMenuVisibility(
            store,
            FakeAuthenticatedActorAccessor.Unidentifiable(),
            audit,
            new FakeClock(SwitchedAt));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(
            () => useCase.Handle(MenuVisibilityKeys.Imports, visible: false, CancellationToken.None));

        Assert.Empty(store.Writes);
        Assert.Empty(audit.Entries);
    }

    /// <summary>
    /// The legacy named answer of issue #873 is derived from the catalogue key and from nothing else,
    /// which is what keeps the rollout compatible: the frontend that still reads
    /// <c>historicalGstClassificationVisible</c> is reading this one destination's state.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_legacy_gst_answer_is_the_catalogue_keys_own_state(bool visible)
    {
        var visibility = await new GetMenuVisibility(
                FakeMenuVisibilityStore.Holding(MenuVisibilityKeys.HistoricalGstClassification, visible))
            .Handle(CancellationToken.None);

        Assert.Equal(visible, visibility.IsVisible(MenuVisibilityKeys.HistoricalGstClassification));
    }

    private static SetMenuVisibility Switch(
        FakeMenuVisibilityStore store,
        FakeMenuVisibilityAudit audit) =>
        new(store, IdentifiedAdministrator(), audit, new FakeClock(SwitchedAt));

    private static FakeAuthenticatedActorAccessor IdentifiedAdministrator() =>
        FakeAuthenticatedActorAccessor.Identified(Administrator());

    private static ActorIdentity Administrator()
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, AdministratorObjectId, out var identity));
        return identity!;
    }
}
