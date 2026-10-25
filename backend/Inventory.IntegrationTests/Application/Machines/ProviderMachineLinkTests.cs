using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Machines;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// Provider machine linking (issue #742, parent #329 child G3): a provider machine no local machine
/// carries the id of becomes a local machine - automatically when there is nothing of the business's
/// own it could be, and on the operator's decision when there is.
///
/// <para><strong>Relational, not in-memory, and deliberately so.</strong> What these tests assert is
/// that an apply is atomic - a refused decision commits <em>nothing</em> - that a provider machine id
/// is unique per business among the machines that carry one, and that another business's machine is
/// unreachable. None of that is a property an in-memory provider has.</para>
/// </summary>
public class ProviderMachineLinkTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime ReviewedAt = new(2026, 10, 21, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AppliedAt = new(2026, 10, 21, 2, 5, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public ProviderMachineLinkTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(Options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NayaxBusiness(BusinessA, "Vending A"),
            NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    private DbContextOptions<AppDbContext> Options { get; }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------------------------------------
    // A provider machine nothing local carries the id of, for a business with nothing unlinked.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_business_with_no_machines_of_its_own_links_every_provider_machine_automatically()
    {
        await using var db = Scoped(BusinessA);

        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"), Machine(8, "Gym"))).Handle();

        Assert.Empty(review.AwaitingReview);
        Assert.Equal(2, review.AutoLinked.Count);
        Assert.Equal(2, review.LinkedCount);
        Assert.All(review.AutoLinked, link => Assert.True(link.Created));

        var stored = await StoredMachines();
        Assert.Collection(
            stored.OrderBy(machine => machine.ExternalMachineId, StringComparer.Ordinal),
            machine => AssertMachine(machine, "Lobby level 1", "7"),
            machine => AssertMachine(machine, "Gym", "8"));

        // The keys are the database's own (issue #735's rule, decision A4): the local id of the
        // machine linked to provider machine 7 is not required to be 7, and nothing assigned it.
        Assert.All(stored, machine => Assert.True(machine.Id > 0));
    }

    [Fact]
    public async Task A_business_whose_only_machines_are_provider_linked_still_links_automatically()
    {
        await using var seed = Scoped(BusinessA);
        seed.Machines.Add(new LocalMachine { Name = "Lobby level 1", ExternalMachineId = "7" });
        await seed.SaveChangesAsync();

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"), Machine(8, "Gym"))).Handle();

        Assert.Empty(review.AwaitingReview);
        var linked = Assert.Single(review.AutoLinked);
        Assert.Equal(8, linked.ProviderMachineId);
        Assert.Equal("Gym", linked.Name);
        Assert.Equal(2, review.LinkedCount);
        Assert.Equal(2, (await StoredMachines()).Count);
    }

    [Fact]
    public async Task A_provider_machine_the_provider_names_nothing_takes_the_default_name()
    {
        await using var db = Scoped(BusinessA);

        var review = await Review(db, Fleet(Machine(7, providerName: null))).Handle();

        Assert.Equal(MachineCataloguePolicy.DefaultName("7"), Assert.Single(review.AutoLinked).Name);
        Assert.Equal(
            MachineCataloguePolicy.DefaultName("7"), Assert.Single(await StoredMachines()).Name);
    }

    [Fact]
    public async Task A_fleet_entry_with_no_machine_identifier_is_not_a_machine()
    {
        await using var db = Scoped(BusinessA);

        // 0 is the absent value a non-nullable field records - the documented Lynx contract declares
        // MachineID as nullable - and the AddMachines backfill excluded it for the same reason.
        var review = await Review(db, Fleet(Machine(0, "Nothing"), Machine(7, "Lobby level 1"))).Handle();

        Assert.Equal("7", Assert.Single(await StoredMachines()).ExternalMachineId);
        Assert.Single(review.AutoLinked);
    }

    // ---------------------------------------------------------------------------------------------
    // A business with a machine of its own: the review waits, and matches nothing by name.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_business_with_an_unlinked_machine_of_its_own_waits_for_review()
    {
        await using var seed = Scoped(BusinessA);
        seed.Machines.Add(new LocalMachine { Name = "Corridor" });
        await seed.SaveChangesAsync();

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"))).Handle();

        Assert.Empty(review.AutoLinked);
        Assert.Equal(0, review.LinkedCount);
        var awaiting = Assert.Single(review.AwaitingReview);
        Assert.Equal(7, awaiting.ProviderMachineId);
        Assert.Equal("Lobby level 1", awaiting.Name);
        Assert.False(awaiting.IsLinked);

        // Nothing at all was written: the operator's machine keeps carrying no provider id, and no
        // second machine was created for the provider's.
        var stored = Assert.Single(await StoredMachines());
        Assert.Equal("Corridor", stored.Name);
        Assert.Null(stored.ExternalMachineId);
    }

    [Fact]
    public async Task A_deactivated_machine_of_its_own_also_makes_the_review_wait()
    {
        await using var seed = Scoped(BusinessA);
        seed.Machines.Add(new LocalMachine { Name = "Corridor", IsActive = false });
        await seed.SaveChangesAsync();

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"))).Handle();

        // A retired machine is still a machine of this business the provider's may be, and linking a
        // retired row is better than duplicating it.
        Assert.Empty(review.AutoLinked);
        Assert.Single(review.AwaitingReview);
        Assert.Null(Assert.Single(await StoredMachines()).ExternalMachineId);
    }

    [Fact]
    public async Task A_machine_called_exactly_what_the_provider_calls_it_is_not_matched_by_name()
    {
        await using var seed = Scoped(BusinessA);
        seed.Machines.Add(new LocalMachine { Name = "Lobby level 1" });
        await seed.SaveChangesAsync();

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"))).Handle();

        // Two rows that share a name are two rows. The only ways a link comes about are the operator
        // naming a machine and a machine this feature creates.
        Assert.Empty(review.AutoLinked);
        Assert.Equal(7, Assert.Single(review.AwaitingReview).ProviderMachineId);
        Assert.Null(Assert.Single(await StoredMachines()).ExternalMachineId);
    }

    [Fact]
    public async Task A_local_machine_whose_key_equals_the_provider_number_is_not_matched_either()
    {
        await using var seed = Scoped(BusinessA);
        seed.Machines.Add(new LocalMachine { Name = "Corridor" });
        await seed.SaveChangesAsync();
        var localId = (await StoredMachines()).Single().Id;

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(localId, "Lobby level 1"))).Handle();

        // A local key that happens to equal a provider machine number is a coincidence (issue #735).
        Assert.Empty(review.AutoLinked);
        Assert.Single(review.AwaitingReview);
        Assert.Null(Assert.Single(await StoredMachines()).ExternalMachineId);
    }

    // ---------------------------------------------------------------------------------------------
    // Applying the operator's decisions.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Linking_records_the_provider_id_on_the_named_machine_and_leaves_its_name_alone()
    {
        var localId = await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        var applied = await Apply(db, Fleet(Machine(7, "Lobby level 1")))
            .Handle(Decisions(LinkTo(7, localId)));

        var link = Assert.Single(applied.Links);
        Assert.False(link.Created);
        Assert.Equal(localId, link.LocalMachineId);
        Assert.Equal(1, applied.LinkedCount);
        Assert.Equal(0, applied.CreatedCount);

        // The provider's id is recorded; the operator's own label is untouched, because nothing
        // synchronises a machine's name (issue #740).
        AssertMachine(Assert.Single(await StoredMachines()), "Corridor", "7");
    }

    [Fact]
    public async Task Creating_records_a_new_machine_with_the_providers_name_and_id()
    {
        var localId = await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        var applied = await Apply(db, Fleet(Machine(7, "Lobby level 1")))
            .Handle(Decisions(CreateFor(7)));

        var link = Assert.Single(applied.Links);
        Assert.True(link.Created);
        Assert.Equal("Lobby level 1", link.Name);
        Assert.Equal(1, applied.CreatedCount);

        var stored = await StoredMachines();
        Assert.Equal(2, stored.Count);
        AssertMachine(stored.Single(machine => machine.Id != localId), "Lobby level 1", "7");
        // The machine the operator already had is left exactly as it was.
        Assert.Null(stored.Single(machine => machine.Id == localId).ExternalMachineId);
    }

    [Fact]
    public async Task A_provider_machine_nobody_decided_about_is_left_alone()
    {
        await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        var applied = await Apply(db, Fleet(Machine(7, "Lobby level 1"), Machine(8, "Gym")))
            .Handle(Decisions(CreateFor(7)));

        Assert.Single(applied.Links);
        var stored = await StoredMachines();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, machine => Assert.NotEqual("8", machine.ExternalMachineId));
    }

    [Fact]
    public async Task Linking_a_second_machine_to_one_provider_machine_is_refused()
    {
        var first = await SeedMachine(BusinessA, "Corridor");
        var second = await SeedMachine(BusinessA, "Stairwell");

        await using var db = Scoped(BusinessA);
        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1")))
                .Handle(Decisions(LinkTo(7, first), LinkTo(7, second))));

        Assert.Contains("Nothing was applied.", refusal.Message, StringComparison.Ordinal);
        Assert.All(await StoredMachines(), machine => Assert.Null(machine.ExternalMachineId));
    }

    [Fact]
    public async Task Linking_a_provider_machine_another_machine_already_carries_is_refused()
    {
        await SeedMachine(BusinessA, "Corridor", externalMachineId: "7");
        var other = await SeedMachine(BusinessA, "Stairwell");

        await using var db = Scoped(BusinessA);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(LinkTo(7, other))));

        // The acceptance criterion: one business cannot link two machines to the same provider
        // machine id. The second machine keeps carrying nothing.
        Assert.Null((await StoredMachines()).Single(machine => machine.Id == other).ExternalMachineId);
    }

    [Fact]
    public async Task Creating_for_a_provider_machine_a_machine_already_carries_is_refused()
    {
        await SeedMachine(BusinessA, "Corridor", externalMachineId: "7");

        await using var db = Scoped(BusinessA);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(CreateFor(7))));

        Assert.Single(await StoredMachines());
    }

    [Fact]
    public async Task One_refused_decision_refuses_the_whole_apply()
    {
        var local = await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"), Machine(8, "Gym")))
                .Handle(Decisions(CreateFor(7), LinkTo(8, localMachineId: local + 5_000))));

        // Atomic: the acceptable creation is not kept either, because a half-applied review is the
        // outcome an operator cannot reason about.
        var stored = Assert.Single(await StoredMachines());
        Assert.Equal("Corridor", stored.Name);
        Assert.Null(stored.ExternalMachineId);
    }

    [Fact]
    public async Task A_decision_about_a_provider_machine_outside_the_fleet_is_refused()
    {
        var local = await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(LinkTo(9, local))));

        Assert.Contains(
            ApplyProviderMachineLinks.UnknownProviderMachineMessage(9),
            refusal.Message,
            StringComparison.Ordinal);
        Assert.Null(Assert.Single(await StoredMachines()).ExternalMachineId);
    }

    [Fact]
    public async Task A_link_decision_naming_no_machine_is_refused()
    {
        await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(
                Decisions(new ProviderMachineLinkDecision(7, ProviderMachineLinkAction.LinkExisting))));

        Assert.Null(Assert.Single(await StoredMachines()).ExternalMachineId);
    }

    [Fact]
    public async Task Applying_the_same_link_twice_changes_nothing_the_second_time()
    {
        var local = await SeedMachine(BusinessA, "Corridor");

        await using var first = Scoped(BusinessA);
        await Apply(first, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(LinkTo(7, local)));

        await using var second = Scoped(BusinessA);
        var applied = await Apply(second, Fleet(Machine(7, "Lobby level 1")))
            .Handle(Decisions(LinkTo(7, local)));

        // Recording the id the machine already carries is the state the apply was asked to reach.
        Assert.Equal(local, Assert.Single(applied.Links).LocalMachineId);
        AssertMachine(Assert.Single(await StoredMachines()), "Corridor", "7");
    }

    // ---------------------------------------------------------------------------------------------
    // A review the stored machines moved on from. The apply is decided on the rows as they are now,
    // which is the whole reason the authoritative read lives inside the write's own transaction, so
    // the state has to change between the review's read and the apply's mutation for these to mean
    // anything (AGENTS.md § Automated task contract, on stale-state guarantees).
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_provider_machine_another_machine_took_since_the_review_refuses_the_stale_apply()
    {
        var corridor = await SeedMachine(BusinessA, "Corridor");
        var stairwell = await SeedMachine(BusinessA, "Stairwell");
        var fleet = Fleet(Machine(7, "Lobby level 1"), Machine(8, "Gym"));

        await using var db = Scoped(BusinessA);
        var review = await Review(db, fleet).Handle();

        // The review the operator decides from: both provider machines are waiting, neither linked.
        Assert.Equal(
            [7L, 8L], review.AwaitingReview.Select(entry => entry.ProviderMachineId).ToList());

        // Between the review and the apply, provider machine 7 is linked to Corridor by somebody
        // else - another operator's apply, or an auto-link - through a second database scope.
        await using (var meanwhile = Scoped(BusinessA))
        {
            var claimed = await meanwhile.Machines.SingleAsync(machine => machine.Id == corridor);
            claimed.ExternalMachineId = "7";
            await meanwhile.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, fleet).Handle(Decisions(LinkTo(7, stairwell), CreateFor(8))));

        Assert.Contains(
            ProviderMachineLinkPolicy.LinkRefusal(
                "7", chosenMachineProviderId: null, anotherMachineCarriesIt: true)!,
            refusal.Message,
            StringComparison.Ordinal);

        // Current data decided it, not the stale review: Corridor keeps the link it gained, Stairwell
        // carries nothing, and the one decision in the batch that was acceptable - the machine for
        // provider machine 8 - is not kept either, because a refused apply commits nothing.
        var stored = await StoredMachines();
        Assert.Equal(2, stored.Count);
        Assert.Equal("7", stored.Single(machine => machine.Id == corridor).ExternalMachineId);
        Assert.Null(stored.Single(machine => machine.Id == stairwell).ExternalMachineId);
    }

    [Fact]
    public async Task A_machine_that_took_a_different_provider_id_since_the_review_is_not_overwritten()
    {
        var corridor = await SeedMachine(BusinessA, "Corridor");
        await SeedMachine(BusinessA, "Stairwell");
        var fleet = Fleet(Machine(7, "Lobby level 1"), Machine(9, "Foyer"));

        await using var db = Scoped(BusinessA);
        var review = await Review(db, fleet).Handle();

        Assert.Equal(
            [7L, 9L], review.AwaitingReview.Select(entry => entry.ProviderMachineId).ToList());

        // This time the machine the operator chose is the one that moved: Corridor became provider
        // machine 9 while the review sat on their screen.
        await using (var meanwhile = Scoped(BusinessA))
        {
            var claimed = await meanwhile.Machines.SingleAsync(machine => machine.Id == corridor);
            claimed.ExternalMachineId = "9";
            await meanwhile.SaveChangesAsync();
        }

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, fleet).Handle(Decisions(LinkTo(7, corridor))));

        Assert.Contains(
            ProviderMachineLinkPolicy.LinkRefusal(
                "7", chosenMachineProviderId: "9", anotherMachineCarriesIt: false)!,
            refusal.Message,
            StringComparison.Ordinal);

        // A machine's provider identity is the one it has, never the one a stale plan assumed it had.
        var stored = await StoredMachines();
        Assert.Equal("9", stored.Single(machine => machine.Id == corridor).ExternalMachineId);
        Assert.All(stored, machine => Assert.NotEqual("7", machine.ExternalMachineId));
    }

    // ---------------------------------------------------------------------------------------------
    // What the rest of the application sees.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Until_it_is_linked_a_provider_machine_resolves_to_no_local_machine()
    {
        await SeedMachine(BusinessA, "Corridor");

        await using var pending = Scoped(BusinessA);
        var review = await Review(pending, Fleet(Machine(7, "Lobby level 1"))).Handle();

        // It is in the review rather than hidden, it still carries the provider's own number, and
        // nothing resolves it to a local machine - so its sales, stock events and reports stay under
        // the raw provider id exactly as before (issue #741).
        Assert.Equal(7, Assert.Single(review.AwaitingReview).ProviderMachineId);
        Assert.Empty(await new EfNayaxMachineLinkStore(pending)
            .GetLocalMachineIdsAsync([7], CancellationToken.None));

        var local = (await StoredMachines()).Single().Id;
        await using var db = Scoped(BusinessA);
        await Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(LinkTo(7, local)));

        await using var afterwards = Scoped(BusinessA);
        var resolved = await new EfNayaxMachineLinkStore(afterwards)
            .GetLocalMachineIdsAsync([7], CancellationToken.None);
        Assert.Equal(local, resolved[7]);
    }

    [Fact]
    public async Task The_machine_list_shows_the_businesss_own_machines_and_the_linked_provider_ones_together()
    {
        await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        await Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(CreateFor(7)));

        await using var read = Scoped(BusinessA);
        var listed = await new EfLocalMachineStore(read).ListOrderedByNameAsync(CancellationToken.None);

        Assert.Collection(
            listed,
            machine =>
            {
                Assert.Equal("Corridor", machine.Name);
                Assert.False(machine.IsProviderNamed);
            },
            machine =>
            {
                Assert.Equal("Lobby level 1", machine.Name);
                Assert.True(machine.IsProviderNamed);
            });
    }

    // ---------------------------------------------------------------------------------------------
    // Two businesses.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_businesss_machine_cannot_be_linked()
    {
        var theirs = await SeedMachine(BusinessB, "Their corridor");
        await SeedMachine(BusinessA, "Corridor");

        await using var db = Scoped(BusinessA);
        await Assert.ThrowsAsync<DomainValidationException>(() =>
            Apply(db, Fleet(Machine(7, "Lobby level 1"))).Handle(Decisions(LinkTo(7, theirs))));

        // Invisible to the read through the central tenant query filter, so unreachable to the write.
        Assert.All(await StoredMachines(BusinessB), machine => Assert.Null(machine.ExternalMachineId));
        Assert.All(await StoredMachines(), machine => Assert.Null(machine.ExternalMachineId));
    }

    [Fact]
    public async Task Two_businesses_may_link_the_same_provider_machine_id()
    {
        await using var theirs = Scoped(BusinessB);
        await Review(theirs, Fleet(Machine(7, "Their lobby"))).Handle();

        await using var ours = Scoped(BusinessA);
        await Review(ours, Fleet(Machine(7, "Our lobby"))).Handle();

        // Two operators' fleets may legitimately both contain machine 7: the unique index is
        // per business, and neither business can see the other's machine.
        Assert.Equal("7", Assert.Single(await StoredMachines(BusinessA)).ExternalMachineId);
        Assert.Equal("7", Assert.Single(await StoredMachines(BusinessB)).ExternalMachineId);
    }

    [Fact]
    public async Task Another_businesss_machines_do_not_make_this_ones_review_wait()
    {
        await SeedMachine(BusinessB, "Their corridor");

        await using var db = Scoped(BusinessA);
        var review = await Review(db, Fleet(Machine(7, "Lobby level 1"))).Handle();

        // The auto-link rule is about this business's machines. Another business's unlinked machine
        // is not visible here and must not change the answer.
        Assert.Single(review.AutoLinked);
        Assert.Empty(review.AwaitingReview);
    }

    // ---------------------------------------------------------------------------------------------
    // Helpers.
    // ---------------------------------------------------------------------------------------------

    private AppDbContext Scoped(int businessId) => TestAppDbContext.For(Options, businessId);

    private ReviewProviderMachines Review(AppDbContext db, INayaxLynxClient nayax) =>
        new(nayax, new EfProviderMachineLinkStore(db), new FixedClock(ReviewedAt));

    private ApplyProviderMachineLinks Apply(AppDbContext db, INayaxLynxClient nayax) =>
        new(nayax, new EfProviderMachineLinkStore(db), new FixedClock(AppliedAt));

    private static ApplyProviderMachineLinksRequest Decisions(
        params ProviderMachineLinkDecision[] decisions) => new(decisions);

    private static ProviderMachineLinkDecision LinkTo(long providerMachineId, long localMachineId) =>
        new(providerMachineId, ProviderMachineLinkAction.LinkExisting, localMachineId);

    private static ProviderMachineLinkDecision CreateFor(long providerMachineId) =>
        new(providerMachineId, ProviderMachineLinkAction.CreateNew);

    private async Task<long> SeedMachine(
        int businessId, string name, string? externalMachineId = null)
    {
        await using var db = Scoped(businessId);
        var machine = new LocalMachine { Name = name, ExternalMachineId = externalMachineId };
        db.Machines.Add(machine);
        await db.SaveChangesAsync();
        return machine.Id;
    }

    private async Task<IReadOnlyList<LocalMachine>> StoredMachines(int businessId = BusinessA)
    {
        await using var verify = TestAppDbContext.Unrestricted(Options);
        return await verify.Machines
            .AsNoTracking()
            .Where(machine => machine.BusinessId == businessId)
            .ToListAsync();
    }

    private static void AssertMachine(LocalMachine machine, string name, string? externalMachineId)
    {
        Assert.Equal(name, machine.Name);
        Assert.Equal(externalMachineId, machine.ExternalMachineId);
        Assert.True(machine.IsActive);
    }

    private static Business NayaxBusiness(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = ReviewedAt,
        };

    /// <summary>
    /// One operator fleet, as the Lynx client would answer the machines read.
    ///
    /// <para>Only the <em>fresh</em> read is arranged: provider machine review and apply are declared
    /// fresh-only callers (issue #840), so a use case that read the cached list instead would get no
    /// fleet here and fail rather than quietly link against a ten-minute-old one.</para>
    /// </summary>
    private static INayaxLynxClient Fleet(params NayaxMachine[] machines)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException(
                "Provider machine review and apply must read the fresh machine list, never the cached one."));
        nayax.Setup(client => client.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(machines.ToList());
        return nayax.Object;
    }

    private static NayaxMachine Machine(long machineId, string? providerName, string? number = null) =>
        new() { MachineID = machineId, MachineName = providerName, MachineNumber = number };

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
