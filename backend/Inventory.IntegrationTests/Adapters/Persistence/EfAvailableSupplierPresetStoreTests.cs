using Inventory.Application.Businesses;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the "Available to add" suppliers of issue #935 (third task of #817): which
/// platform supplier records a business is offered, the copy a one-click add writes, and the top-up
/// a change of the business's state makes.
///
/// <para>SQLite over the real migration history rather than InMemory, because what has to be proven
/// is relational or central: that the copy lands stamped with the caller's own business through the
/// ordinary enforcer, that the filtered unique index of issue #934 is the backstop under the
/// availability pre-check, and that the central query filters keep one business's copies out of the
/// other's answers.</para>
///
/// <para>Two businesses throughout, in different states, as AGENTS.md § Tenant ownership requires:
/// the properties under test are about what a read and a write can reach across a boundary, and the
/// same platform list is supposed to produce a different supplier list in Canberra and in Perth.</para>
/// </summary>
public class EfAvailableSupplierPresetStoreTests
{
    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    #region What a business is offered

    /// <summary>
    /// The list is the enabled records that serve this business's state, minus the ones it already
    /// holds a copy of - and it carries the id, the name and the website, which is the whole of what
    /// a member-facing endpoint publishes about a platform table.
    /// </summary>
    [Fact]
    public async Task The_list_offers_the_serving_records_this_business_has_no_copy_of()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", "https://www.coles.com.au", nationwide: true);
        var jbMetro = await fixture.AddPresetAsync(
            "JB Metro South Coast & ACT", "https://jbmetro.test", servedStates: "NSW,ACT");
        await fixture.AddPresetAsync("Perth Wholesale", null, servedStates: "WA");
        await fixture.AddPresetAsync("Costco", null, nationwide: true, isEnabled: false);
        await fixture.AddPresetAsync("No market recorded", null);

        // Canberra already holds Coles, so the list offers the one record left that serves it.
        await fixture.SeedCopyAsync(fixture.CanberraId, coles, "Coles");

        var canberra = await fixture.ListAsync(fixture.CanberraId);

        var offered = Assert.Single(canberra);
        Assert.Equal(new AvailableSupplierPreset(jbMetro, "JB Metro South Coast & ACT", "https://jbmetro.test"), offered);
    }

    /// <summary>
    /// The same platform list, two businesses, two different answers - and neither answer is
    /// affected by what the other business holds a copy of.
    /// </summary>
    [Fact]
    public async Task Two_businesses_in_different_states_are_offered_different_records()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", null, nationwide: true);
        await fixture.AddPresetAsync("JB Metro South Coast & ACT", null, servedStates: "NSW,ACT");
        await fixture.AddPresetAsync("Perth Wholesale", null, servedStates: "WA");

        // Perth holds the nationwide one; Canberra does not, and must still be offered it.
        await fixture.SeedCopyAsync(fixture.PerthId, coles, "Coles");

        Assert.Equal(
            ["Coles", "JB Metro South Coast & ACT"],
            (await fixture.ListAsync(fixture.CanberraId)).Select(preset => preset.Name));
        Assert.Equal(
            ["Perth Wholesale"],
            (await fixture.ListAsync(fixture.PerthId)).Select(preset => preset.Name));
    }

    /// <summary>
    /// A business with no recorded state - a legacy one - is offered the nationwide records alone. A
    /// state is never guessed from its time zone or anything else.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_recorded_state_is_offered_the_nationwide_records_only()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        await fixture.AddPresetAsync("Coles", null, nationwide: true);
        await fixture.AddPresetAsync("JB Metro NSW", null, servedStates: "NSW");

        Assert.Equal(
            ["Coles"],
            (await fixture.ListAsync(fixture.LegacyId)).Select(preset => preset.Name));
    }

    /// <summary>
    /// A supplier the business typed in itself is not a copy of anything, so it never hides a record
    /// from the list - not even one with the very same name. Only the link counts, which is the rule
    /// issue #934 established and the reason the backfill left same-name duplicates unlinked.
    /// </summary>
    [Fact]
    public async Task A_supplier_the_business_typed_in_itself_hides_no_record()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        await fixture.AddPresetAsync("Coles", null, nationwide: true);
        await fixture.SeedOwnSupplierAsync(fixture.CanberraId, "Coles");

        Assert.Equal(
            ["Coles"],
            (await fixture.ListAsync(fixture.CanberraId)).Select(preset => preset.Name));
    }

    #endregion

    #region Adding one

    /// <summary>
    /// The add writes one linked copy of that record into the caller's own business, with the name
    /// and the website copied, no contact detail, the GST defaults left unset and not hidden -
    /// exactly the row the approval's copy writes. The other business receives nothing.
    /// </summary>
    [Fact]
    public async Task Adding_a_record_writes_one_linked_copy_in_the_caller_s_own_business()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var jbMetro = await fixture.AddPresetAsync(
            "JB Metro South Coast & ACT", "https://jbmetro.test", servedStates: "NSW,ACT");

        var added = await fixture.AddAsync(fixture.CanberraId, jbMetro);

        Assert.Equal(SupplierPresetAdditionOutcome.Added, added.Outcome);

        var copy = Assert.Single(await fixture.SuppliersOfAsync(fixture.CanberraId));
        Assert.Equal(fixture.CanberraId, copy.BusinessId);
        Assert.Equal("JB Metro South Coast & ACT", copy.Name);
        Assert.Equal("https://jbmetro.test", copy.Url);
        Assert.Equal(jbMetro, copy.SupplierPresetId);
        Assert.False(copy.IsHidden);
        Assert.Null(copy.ContactName);
        Assert.Null(copy.Phone);
        Assert.Null(copy.Email);
        Assert.Null(copy.Address);
        Assert.Equal(GstRules.None, copy.ProductLineGstDefault);
        Assert.Equal(GstRules.None, copy.DeliveryGstDefault);
        Assert.Equal(GstRules.None, copy.PackageGstDefault);

        // The other business is untouched, and the record is still on offer to it.
        Assert.Empty(await fixture.SuppliersOfAsync(fixture.PerthId));
        Assert.Empty(await fixture.ListAsync(fixture.CanberraId));

        // And nothing was written back to the platform list.
        var preset = Assert.Single(await fixture.PresetsAsync());
        Assert.Equal("JB Metro South Coast & ACT", preset.Name);
        Assert.True(preset.IsEnabled);
    }

    /// <summary>
    /// A record that does not serve this business, one that is no longer suggested, and one that
    /// does not exist are all "not offered", and none of them writes anything. The Perth record is
    /// the one that matters most: a business cannot add a record by id that the list would never
    /// have shown it.
    /// </summary>
    [Fact]
    public async Task A_record_this_business_is_not_offered_cannot_be_added_by_id()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var perthOnly = await fixture.AddPresetAsync("Perth Wholesale", null, servedStates: "WA");
        var disabled = await fixture.AddPresetAsync("Costco", null, nationwide: true, isEnabled: false);

        foreach (var presetId in new[] { perthOnly, disabled, 9999 })
        {
            Assert.Equal(
                SupplierPresetAvailability.NotOffered,
                await fixture.AvailabilityAsync(fixture.CanberraId, presetId));

            var attempt = await fixture.AddAsync(fixture.CanberraId, presetId);
            Assert.Equal(SupplierPresetAdditionOutcome.NotAvailable, attempt.Outcome);
        }

        Assert.Empty(await fixture.SuppliersOfAsync(fixture.CanberraId));
        Assert.Empty(await fixture.SuppliersOfAsync(fixture.PerthId));
    }

    /// <summary>
    /// Adding the same record twice is refused with "already added" and leaves one copy: a
    /// double-click, or two tabs, gets the outcome it wanted rather than a second supplier.
    /// </summary>
    [Fact]
    public async Task Adding_the_same_record_twice_leaves_one_copy()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", null, nationwide: true);

        Assert.Equal(SupplierPresetAdditionOutcome.Added, (await fixture.AddAsync(fixture.CanberraId, coles)).Outcome);
        Assert.Equal(
            SupplierPresetAdditionOutcome.AlreadyAdded,
            (await fixture.AddAsync(fixture.CanberraId, coles)).Outcome);

        Assert.Single(await fixture.SuppliersOfAsync(fixture.CanberraId));
        Assert.Equal(
            SupplierPresetAvailability.AlreadyCopied,
            await fixture.AvailabilityAsync(fixture.CanberraId, coles));
    }

    /// <summary>
    /// The race the pre-check cannot win, against the real index: the copy exists by the time the
    /// insert runs, the database refuses it, and the adapter recognises <em>that</em> violation - so
    /// the use case can answer the same "already added" instead of a <c>500</c>. One row either way.
    ///
    /// <para>It is staged by copying twice through the adapter's own write method, which is exactly
    /// what the losing request of two simultaneous adds does after its pre-check answered
    /// "available".</para>
    /// </summary>
    [Fact]
    public async Task The_unique_index_refuses_a_second_copy_and_the_adapter_recognises_that_refusal()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", null, nationwide: true);

        await using var db = fixture.ScopedContext(fixture.CanberraId);
        var store = new EfAvailableSupplierPresetStore(db);

        await store.CopyAsync(BusinessId.From(fixture.CanberraId), coles, CancellationToken.None);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            store.CopyAsync(BusinessId.From(fixture.CanberraId), coles, CancellationToken.None));

        Assert.True(
            store.IsDuplicateCopy(error),
            $"The adapter must recognise the (BusinessId, SupplierPresetId) index refusing a second copy. It saw: {error}");

        // One row, and the context is still usable: the refused insert was detached rather than left
        // tracked for the next save to retry.
        Assert.Single(await fixture.SuppliersOfAsync(fixture.CanberraId));
        Assert.False(store.IsDuplicateCopy(new InvalidOperationException("something else")));
    }

    /// <summary>
    /// The two businesses hold their own copies of the same record, which is why the unique index
    /// leads with the business: Perth adding Coles must not be denied because Canberra has it.
    /// </summary>
    [Fact]
    public async Task Both_businesses_may_hold_their_own_copy_of_one_record()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", null, nationwide: true);

        Assert.Equal(SupplierPresetAdditionOutcome.Added, (await fixture.AddAsync(fixture.CanberraId, coles)).Outcome);
        Assert.Equal(SupplierPresetAdditionOutcome.Added, (await fixture.AddAsync(fixture.PerthId, coles)).Outcome);

        Assert.Equal(coles, Assert.Single(await fixture.SuppliersOfAsync(fixture.CanberraId)).SupplierPresetId);
        Assert.Equal(coles, Assert.Single(await fixture.SuppliersOfAsync(fixture.PerthId)).SupplierPresetId);
    }

    #endregion

    #region The state change's top-up

    /// <summary>
    /// A business that changes state receives the records the new state serves and keeps everything
    /// it had: the copy of a record that no longer matches stays, a hidden copy stays hidden, and a
    /// supplier it typed in itself is untouched. Nothing is removed, relinked or edited, because a
    /// purchase may already name it.
    /// </summary>
    [Fact]
    public async Task A_state_change_adds_the_newly_matching_records_and_removes_nothing()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        var coles = await fixture.AddPresetAsync("Coles", null, nationwide: true);
        var jbNsw = await fixture.AddPresetAsync("JB Metro NSW", null, servedStates: "NSW");
        var perth = await fixture.AddPresetAsync("Perth Wholesale", null, servedStates: "WA");
        await fixture.AddPresetAsync("Costco", null, nationwide: true, isEnabled: false);

        // Trading in Western Australia until now: it holds the nationwide record and the Perth one,
        // and the Perth copy is marked "Not used".
        await fixture.SeedCopyAsync(fixture.PerthId, coles, "Coles");
        await fixture.SeedCopyAsync(fixture.PerthId, perth, "Perth Wholesale", isHidden: true);
        await fixture.SeedOwnSupplierAsync(fixture.PerthId, "The corner shop");

        await fixture.SetStateAsync(fixture.PerthId, "NSW");
        var added = await fixture.TopUpAsync(fixture.PerthId);

        Assert.Equal(1, added);

        var suppliers = await fixture.SuppliersOfAsync(fixture.PerthId);
        Assert.Equal(
            ["Coles", "JB Metro NSW", "Perth Wholesale", "The corner shop"],
            suppliers.Select(supplier => supplier.Name).Order(StringComparer.Ordinal));
        Assert.Equal(jbNsw, suppliers.Single(supplier => supplier.Name == "JB Metro NSW").SupplierPresetId);

        // The record the new state does not serve is still there, still linked, still hidden.
        var stale = suppliers.Single(supplier => supplier.Name == "Perth Wholesale");
        Assert.Equal(perth, stale.SupplierPresetId);
        Assert.True(stale.IsHidden);

        // And the business's own supplier is still its own.
        Assert.Null(suppliers.Single(supplier => supplier.Name == "The corner shop").SupplierPresetId);

        // The other business got nothing out of it.
        Assert.Empty(await fixture.SuppliersOfAsync(fixture.CanberraId));
    }

    /// <summary>
    /// The top-up is idempotent: running it again adds nothing, because every record the state
    /// serves is already copied. That is what makes a second settings save harmless.
    /// </summary>
    [Fact]
    public async Task A_second_top_up_adds_nothing()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        await fixture.AddPresetAsync("Coles", null, nationwide: true);
        await fixture.AddPresetAsync("JB Metro South Coast & ACT", null, servedStates: "NSW,ACT");

        Assert.Equal(2, await fixture.TopUpAsync(fixture.CanberraId));
        Assert.Equal(0, await fixture.TopUpAsync(fixture.CanberraId));

        Assert.Equal(2, (await fixture.SuppliersOfAsync(fixture.CanberraId)).Count);
    }

    /// <summary>
    /// The whole chain through the real settings use case, inside its own write transaction: the
    /// Owner changes the business's state, and the records that state serves arrive with the saved
    /// settings. A save that changes something else adds nothing.
    /// </summary>
    [Fact]
    public async Task The_settings_save_adds_the_records_only_when_the_state_changes()
    {
        await using var fixture = await AvailablePresetFixture.CreateAsync();
        await fixture.AddPresetAsync("Coles", null, nationwide: true);
        await fixture.AddPresetAsync("JB Metro NSW", null, servedStates: "NSW");

        // The Canberra business holds nothing yet, and starts out in the ACT.
        await fixture.SaveSettingsAsync(fixture.CanberraId, state: "NSW");

        Assert.Equal(
            ["Coles", "JB Metro NSW"],
            (await fixture.SuppliersOfAsync(fixture.CanberraId))
                .Select(supplier => supplier.Name)
                .Order(StringComparer.Ordinal));

        // A second save that changes only the name adds nothing, even though the platform list has
        // grown in the meantime: a record enabled later is offered under "Available to add" and is
        // never pushed into an existing business.
        await fixture.AddPresetAsync("Woolworths", null, nationwide: true);
        await fixture.SaveSettingsAsync(fixture.CanberraId, state: "NSW", name: "Renamed Co");

        Assert.Equal(2, (await fixture.SuppliersOfAsync(fixture.CanberraId)).Count);
    }

    #endregion

    #region Fixture

    private sealed class AvailablePresetFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private AvailablePresetFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        /// <summary>A business trading in the Australian Capital Territory.</summary>
        public int CanberraId { get; private set; }

        /// <summary>A business trading in Western Australia, which must be offered other records.</summary>
        public int PerthId { get; private set; }

        /// <summary>A business with no recorded state, as every business had before issue #933.</summary>
        public int LegacyId { get; private set; }

        public static async Task<AvailablePresetFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new AvailablePresetFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using var setup = TestAppDbContext.Unrestricted(fixture._options);
            await setup.Database.MigrateAsync();

            // The platform list the migrations shipped is removed, so each test states the records
            // it is about rather than inheriting nine.
            setup.SupplierPresets.RemoveRange(await setup.SupplierPresets.ToListAsync());
            await setup.SaveChangesAsync();

            fixture.CanberraId = await fixture.SeedBusinessAsync("Coastal Vending", "ACT");
            fixture.PerthId = await fixture.SeedBusinessAsync("Vending Co", "WA");
            fixture.LegacyId = await fixture.SeedBusinessAsync("Legacy Vending", state: null);

            return fixture;
        }

        public AppDbContext ScopedContext(int businessId) => Track(TestAppDbContext.For(_options, businessId));

        /// <summary>The list, through a context scoped to that business as a request's would be.</summary>
        public async Task<IReadOnlyList<AvailableSupplierPreset>> ListAsync(int businessId)
        {
            await using var db = TestAppDbContext.For(_options, businessId);
            return await new EfAvailableSupplierPresetStore(db)
                .ListAvailableAsync(BusinessId.From(businessId), CancellationToken.None);
        }

        public async Task<SupplierPresetAvailability> AvailabilityAsync(int businessId, int presetId)
        {
            await using var db = TestAppDbContext.For(_options, businessId);
            return await new EfAvailableSupplierPresetStore(db)
                .FindAvailableAsync(BusinessId.From(businessId), presetId, CancellationToken.None);
        }

        /// <summary>The one-click add, through the real use case on a scoped context.</summary>
        public async Task<SupplierPresetAddition> AddAsync(int businessId, int presetId)
        {
            await using var db = TestAppDbContext.For(_options, businessId);
            return await new AddSupplierFromPreset(
                    new FakeCurrentBusinessProvider(businessId),
                    new EfAvailableSupplierPresetStore(db))
                .Handle(presetId, CancellationToken.None);
        }

        /// <summary>The state change's top-up, through the real use case on a scoped context.</summary>
        public async Task<int> TopUpAsync(int businessId)
        {
            await using var db = TestAppDbContext.For(_options, businessId);
            return await new AddMatchingSupplierPresets(new EfAvailableSupplierPresetStore(db))
                .Handle(BusinessId.From(businessId), CancellationToken.None);
        }

        /// <summary>
        /// One real settings save by the business's Owner, through <c>UpdateBusinessSettings</c> and
        /// its shared write transaction - the path the top-up actually runs inside.
        /// </summary>
        public async Task SaveSettingsAsync(int businessId, string state, string name = "Coastal Vending")
        {
            await using var db = TestAppDbContext.For(_options, businessId);

            await new UpdateBusinessSettings(
                    new FakeCurrentBusinessProvider(businessId),
                    FakeAuthenticatedActorAccessor.Identified(
                        MembershipWriteRaceHarness.Actor("55555555-5555-5555-5555-555555555555")),
                    new EfBusinessSettingsStore(db),
                    new AddMatchingSupplierPresets(new EfAvailableSupplierPresetStore(db)),
                    new MembershipWriteGuard(new EfBusinessMembershipWriteStore(db)),
                    new RecordingSettingsAudit(),
                    new FakeClock(CreatedAt))
                .Handle(
                    new BusinessSettingsSubmission(
                        name,
                        "owner@example.com",
                        null,
                        GstRegistered: true,
                        "Australia/Sydney",
                        state),
                    CancellationToken.None);
        }

        /// <summary>
        /// Adds one platform record. Every market is stated explicitly, because a market is part of
        /// what decides who receives a record and a default would make these tests say less than
        /// they look like they say.
        /// </summary>
        public async Task<int> AddPresetAsync(
            string name,
            string? url = null,
            bool nationwide = false,
            string servedStates = "",
            bool isEnabled = true)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            var preset = new SupplierPreset
            {
                Name = name,
                NormalisedName = SupplierPresetPolicy.NormalisedName(name),
                Url = url,
                IsEnabled = isEnabled,
                ServesNationwide = nationwide,
                ServedStates = servedStates,
                CreatedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            };

            db.SupplierPresets.Add(preset);
            await db.SaveChangesAsync();
            return preset.Id;
        }

        /// <summary>A copy that business already holds, as the approval's provisioning left it.</summary>
        public async Task SeedCopyAsync(int businessId, int presetId, string name, bool isHidden = false)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.Suppliers.Add(new Supplier
            {
                BusinessId = businessId,
                Name = name,
                SupplierPresetId = presetId,
                IsHidden = isHidden,
            });
            await db.SaveChangesAsync();
        }

        /// <summary>A supplier the business typed in itself: no link, and refused nothing.</summary>
        public async Task SeedOwnSupplierAsync(int businessId, string name)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.Suppliers.Add(new Supplier { BusinessId = businessId, Name = name });
            await db.SaveChangesAsync();
        }

        public async Task SetStateAsync(int businessId, string state)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            (await db.Businesses.SingleAsync(business => business.Id == businessId)).State = state;
            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<Supplier>> SuppliersOfAsync(int businessId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.Suppliers
                .AsNoTracking()
                .Where(supplier => supplier.BusinessId == businessId)
                .OrderBy(supplier => supplier.Id)
                .ToListAsync();
        }

        public async Task<IReadOnlyList<SupplierPreset>> PresetsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.SupplierPresets.AsNoTracking().OrderBy(preset => preset.Id).ToListAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private async Task<int> SeedBusinessAsync(string name, string? state)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            var business = new Business
            {
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                State = state,
                CreatedAtUtc = CreatedAt,
            };

            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            return business.Id;
        }

        private AppDbContext Track(AppDbContext context)
        {
            _contexts.Add(context);
            return context;
        }

        /// <summary>The settings audit, which these tests do not assert on but the use case needs.</summary>
        private sealed class RecordingSettingsAudit : IBusinessSettingsAudit
        {
            public void Record(BusinessSettingsAuditEntry entry)
            {
            }
        }
    }

    #endregion
}
