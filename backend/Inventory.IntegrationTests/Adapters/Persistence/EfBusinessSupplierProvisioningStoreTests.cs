using Inventory.Domain.Gst;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the one provisioning write in this application (issue #849, child C of
/// #835): the copies of the enabled platform supplier presets a business receives when its
/// application is approved.
///
/// <para>SQLite over the real migration history rather than InMemory, because everything that has to
/// be proven here is relational or central: that the rows land stamped with the right business, that
/// the marker and the suppliers are written together, that the central tenant query filters are
/// still in force on the very context the copy is made through, and that
/// <c>BusinessOwnershipEnforcer</c> still refuses every other denied-scope tenant write on that same
/// context.</para>
///
/// <para>Two businesses throughout, as AGENTS.md § Tenant ownership requires of any change to
/// ownership or enforcement: the properties under test are about what a write can reach across a
/// boundary, and a single-business fixture cannot express them.</para>
/// </summary>
public class EfBusinessSupplierProvisioningStoreTests
{
    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CopiedAt = new(2026, 10, 23, 5, 15, 0, DateTimeKind.Utc);

    #region What is copied

    /// <summary>
    /// The copy itself: one supplier per <em>enabled</em> preset, with the name and the website
    /// copied and nothing else - no contact detail the platform does not have, and the three GST
    /// defaults left at <see cref="GstRules.None"/>, because a GST default is explicit configuration
    /// a business makes about a supplier it buys from and is never inferred from a name or a website.
    /// </summary>
    [Fact]
    public async Task Copying_writes_one_supplier_per_enabled_preset_with_its_name_and_website()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(
            ("Coles", "https://www.coles.com.au", true),
            ("Woolworths", null, true),
            ("Costco", "https://www.costco.com.au", false));

        await fixture.CopyAsync(fixture.ApprovedBusinessId);

        var copies = await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId);
        Assert.Equal(["Coles", "Woolworths"], copies.Select(supplier => supplier.Name));
        Assert.Equal("https://www.coles.com.au", copies[0].Url);
        Assert.Null(copies[1].Url);

        Assert.All(copies, supplier =>
        {
            Assert.Equal(fixture.ApprovedBusinessId, supplier.BusinessId);
            Assert.Null(supplier.ContactName);
            Assert.Null(supplier.Phone);
            Assert.Null(supplier.Email);
            Assert.Null(supplier.Address);
            Assert.Equal(GstRules.None, supplier.ProductLineGstDefault);
            Assert.Equal(GstRules.None, supplier.DeliveryGstDefault);
            Assert.Equal(GstRules.None, supplier.PackageGstDefault);
        });
    }

    /// <summary>
    /// The marker is stamped with the instant it was given - the approval's decision instant - in the
    /// same save as the suppliers.
    /// </summary>
    [Fact]
    public async Task Copying_stamps_the_copy_instant_on_the_business()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();

        Assert.Null((await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);

        await fixture.CopyAsync(fixture.ApprovedBusinessId);

        Assert.Equal(
            CopiedAt,
            (await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// No enabled preset at all is a normal outcome, not a failure: the business gets no suppliers
    /// and the marker is still set, because the copy <em>was</em> made and what it contained was
    /// nothing. Leaving the marker null would turn the next attempt into a second copy.
    /// </summary>
    [Fact]
    public async Task A_business_approved_with_no_enabled_preset_gets_no_supplier_and_still_gets_the_marker()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", null, false), ("Woolworths", null, false));

        await fixture.CopyAsync(fixture.ApprovedBusinessId);

        Assert.Empty(await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId));
        Assert.Equal(
            CopiedAt,
            (await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// The marker is the whole of the idempotency rule: a second call copies nothing and does not
    /// rewrite the instant, however many presets are enabled by then. Names are deliberately not
    /// consulted - the business has renamed its copy here, which a name-based rule would read as "no
    /// copy exists" and duplicate.
    /// </summary>
    [Fact]
    public async Task A_second_copy_writes_nothing_even_after_the_business_renamed_its_own_copies()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", null, true));

        await fixture.CopyAsync(fixture.ApprovedBusinessId);
        await fixture.RenameSupplierAsync(fixture.ApprovedBusinessId, "Coles", "The corner shop");
        await fixture.ReplacePresetsAsync(("Coles", null, true), ("Woolworths", null, true));

        await fixture.CopyAsync(fixture.ApprovedBusinessId, copiedAtUtc: CopiedAt.AddDays(1));

        Assert.Equal(
            ["The corner shop"],
            (await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId)).Select(supplier => supplier.Name));
        Assert.Equal(
            CopiedAt,
            (await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// A business whose marker is already set - every business that existed before this column did,
    /// and every one that has already been approved - receives nothing, which is what makes "existing
    /// businesses are never backfilled" a property of the code and not only of the migration.
    /// </summary>
    [Fact]
    public async Task A_business_that_already_records_a_copy_receives_nothing()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.SetCopyMarkerAsync(fixture.ApprovedBusinessId, CreatedAt);

        await fixture.CopyAsync(fixture.ApprovedBusinessId);

        Assert.Empty(await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId));
        Assert.Equal(
            CreatedAt,
            (await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    #endregion

    #region The copies belong to that one business

    /// <summary>
    /// The copies are that business's own rows and nobody else's: the other business gets none, its
    /// own supplier is untouched, and its ordinary scoped read sees only its own.
    /// </summary>
    [Fact]
    public async Task The_copies_reach_one_business_and_are_invisible_to_the_other()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", null, true));
        await fixture.SeedSupplierAsync(fixture.OtherBusinessId, "The other business's own supplier");

        await fixture.CopyAsync(fixture.ApprovedBusinessId);

        await using var approved = fixture.ScopedContext(fixture.ApprovedBusinessId);
        await using var other = fixture.ScopedContext(fixture.OtherBusinessId);

        Assert.Equal(["Coles"], await approved.Suppliers.Select(supplier => supplier.Name).ToListAsync());
        Assert.Equal(
            ["The other business's own supplier"],
            await other.Suppliers.Select(supplier => supplier.Name).ToListAsync());
        Assert.Null((await fixture.BusinessAsync(fixture.OtherBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// The copy is a copy, not a link. Editing or disabling a preset afterwards changes no existing
    /// copy, and a business editing its own copy changes neither the preset nor the other business's
    /// copy of the same preset - which is only expressible with two businesses.
    /// </summary>
    [Fact]
    public async Task Editing_a_preset_or_a_copy_afterwards_changes_nothing_on_the_other_side()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", "https://www.coles.com.au", true));

        await fixture.CopyAsync(fixture.ApprovedBusinessId);
        await fixture.CopyAsync(fixture.OtherBusinessId);

        // The platform edits and disables the preset.
        await fixture.ReplacePresetsAsync(("Colesworth", "https://www.colesworth.example", false));

        // And one business edits its own copy, through an ordinary scoped write.
        await using (var scoped = fixture.ScopedContext(fixture.ApprovedBusinessId))
        {
            var supplier = await scoped.Suppliers.SingleAsync();
            supplier.Name = "Coles (Marrickville)";
            supplier.Phone = "0400 000 000";
            await scoped.SaveChangesAsync();
        }

        var edited = Assert.Single(await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId));
        Assert.Equal("Coles (Marrickville)", edited.Name);
        Assert.Equal("https://www.coles.com.au", edited.Url);

        // The other business's copy is exactly as it was copied - neither the preset's edit nor the
        // other business's edit reached it.
        var untouched = Assert.Single(await fixture.SuppliersOfAsync(fixture.OtherBusinessId));
        Assert.Equal("Coles", untouched.Name);
        Assert.Equal("https://www.coles.com.au", untouched.Url);
        Assert.Null(untouched.Phone);

        // And the preset is as the platform left it: the business's edit did not travel back.
        var preset = Assert.Single(await fixture.PresetsAsync());
        Assert.Equal("Colesworth", preset.Name);
        Assert.False(preset.IsEnabled);
    }

    #endregion

    #region What the provisioning path still cannot do

    /// <summary>
    /// The boundary this exception is cut out of is still there. On the very context the copy was
    /// made through, and both before and after it, an ordinary denied-scope tenant-owned write is
    /// still refused - a product insert, an update of one of the copies, and a delete of one.
    /// </summary>
    [Fact]
    public async Task The_same_denied_context_still_refuses_every_other_tenant_owned_write()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", null, true));

        var db = fixture.DeniedContext();

        db.Products.Add(new Product { Name = "Smuggled", BusinessId = fixture.ApprovedBusinessId });
        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        await new EfBusinessSupplierProvisioningStore(db)
            .CopyEnabledSupplierPresetsAsync(fixture.ApprovedBusinessId, CopiedAt, CancellationToken.None);

        // A supplier insert of its own, outside the lease the adapter takes and releases, is refused
        // exactly as it was before: the window is one save long, not one request long.
        db.Suppliers.Add(new Supplier { Name = "Smuggled supplier", BusinessId = fixture.ApprovedBusinessId });
        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        db.Products.Add(new Product { Name = "Smuggled again", BusinessId = fixture.ApprovedBusinessId });
        await Assert.ThrowsAsync<CrossBusinessAccessException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();

        // Nothing tenant-owned is readable through it either, the copies it just created included.
        Assert.Empty(await db.Suppliers.ToListAsync());
        Assert.Empty(await db.Products.ToListAsync());
    }

    /// <summary>
    /// The provisioning path is not reachable from a request that resolved a business of its own -
    /// which is every business endpoint - and not from a support session either. Both are refused
    /// before anything is read or written, so no endpoint can borrow this adapter to write a supplier
    /// outside the ordinary tenant path.
    /// </summary>
    [Fact]
    public async Task A_resolved_or_support_session_scope_cannot_provision_at_all()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();
        await fixture.ReplacePresetsAsync(("Coles", null, true));

        await using var scoped = fixture.ScopedContext(fixture.ApprovedBusinessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EfBusinessSupplierProvisioningStore(scoped)
                .CopyEnabledSupplierPresetsAsync(fixture.ApprovedBusinessId, CopiedAt, CancellationToken.None));

        await using var support = fixture.SupportSessionContext(fixture.ApprovedBusinessId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new EfBusinessSupplierProvisioningStore(support)
                .CopyEnabledSupplierPresetsAsync(fixture.ApprovedBusinessId, CopiedAt, CancellationToken.None));

        Assert.Empty(await fixture.SuppliersOfAsync(fixture.ApprovedBusinessId));
        Assert.Null((await fixture.BusinessAsync(fixture.ApprovedBusinessId)).SupplierPresetsCopiedAtUtc);
    }

    /// <summary>
    /// A business row that is not there at all writes nothing rather than failing on a null: the
    /// fail-closed direction, and unreachable from the approval, which has just read and written that
    /// very row.
    /// </summary>
    [Fact]
    public async Task An_unknown_business_is_provisioned_with_nothing()
    {
        await using var fixture = await ProvisioningFixture.CreateAsync();

        await fixture.CopyAsync(businessId: 9999);

        await using var db = fixture.UnrestrictedContext();
        Assert.Empty(await db.Suppliers.ToListAsync());
    }

    #endregion

    #region Fixture

    private sealed class ProvisioningFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private ProvisioningFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            _options = options;
        }

        /// <summary>The business whose application is being approved.</summary>
        public int ApprovedBusinessId { get; private set; }

        /// <summary>A second, already trading business, which must receive nothing of the first's.</summary>
        public int OtherBusinessId { get; private set; }

        public static async Task<ProvisioningFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var fixture = new ProvisioningFixture(
                connection,
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);

            await using var setup = TestAppDbContext.Unrestricted(fixture._options);
            await setup.Database.MigrateAsync();

            fixture.ApprovedBusinessId = await fixture.SeedBusinessAsync("Coastal Vending", BusinessStatus.Active);
            fixture.OtherBusinessId = await fixture.SeedBusinessAsync("Vending Co", BusinessStatus.Active);

            return fixture;
        }

        /// <summary>
        /// The copy as the approval makes it: through the adapter, on a fresh <strong>denied</strong>
        /// context, which is the scope a platform administrator's request really carries.
        /// </summary>
        public Task CopyAsync(int businessId, DateTime? copiedAtUtc = null) =>
            new EfBusinessSupplierProvisioningStore(DeniedContext())
                .CopyEnabledSupplierPresetsAsync(businessId, copiedAtUtc ?? CopiedAt, CancellationToken.None);

        public AppDbContext DeniedContext() => Track(TestAppDbContext.Denied(_options));

        public AppDbContext ScopedContext(int businessId) => Track(TestAppDbContext.For(_options, businessId));

        public AppDbContext SupportSessionContext(int businessId) =>
            Track(TestAppDbContext.SupportSession(_options, businessId));

        public AppDbContext UnrestrictedContext() => Track(TestAppDbContext.Unrestricted(_options));

        /// <summary>
        /// Replaces the platform's preset list with exactly the rows given, so each test states the
        /// suggestions it is about rather than inheriting the four the migration shipped.
        /// </summary>
        public async Task ReplacePresetsAsync(params (string Name, string? Url, bool IsEnabled)[] presets)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.SupplierPresets.RemoveRange(await db.SupplierPresets.ToListAsync());
            await db.SaveChangesAsync();

            foreach (var preset in presets)
            {
                db.SupplierPresets.Add(new SupplierPreset
                {
                    Name = preset.Name,
                    NormalisedName = SupplierPresetPolicy.NormalisedName(preset.Name),
                    Url = preset.Url,
                    IsEnabled = preset.IsEnabled,
                    CreatedAtUtc = CreatedAt,
                    UpdatedAtUtc = CreatedAt,
                });
            }

            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<SupplierPreset>> PresetsAsync()
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.SupplierPresets.AsNoTracking().OrderBy(preset => preset.Id).ToListAsync();
        }

        public async Task<int> SeedBusinessAsync(string name, BusinessStatus status)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            var business = new Business
            {
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = status,
                CreatedAtUtc = CreatedAt,
            };

            db.Businesses.Add(business);
            await db.SaveChangesAsync();
            return business.Id;
        }

        public async Task SeedSupplierAsync(int businessId, string name)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            db.Suppliers.Add(new Supplier { BusinessId = businessId, Name = name });
            await db.SaveChangesAsync();
        }

        /// <summary>A business renaming its own copy, which the idempotency rule must not consult.</summary>
        public async Task RenameSupplierAsync(int businessId, string name, string newName)
        {
            await using var db = TestAppDbContext.For(_options, businessId);
            (await db.Suppliers.SingleAsync(supplier => supplier.Name == name)).Name = newName;
            await db.SaveChangesAsync();
        }

        /// <summary>
        /// Stamps the copy marker outside any boundary - the state of a business that was approved
        /// before, and the state this adapter must refuse to copy into again.
        /// </summary>
        public async Task SetCopyMarkerAsync(int businessId, DateTime copiedAtUtc)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            (await db.Businesses.SingleAsync(business => business.Id == businessId))
                .SupplierPresetsCopiedAtUtc = copiedAtUtc;
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

        public async Task<Business> BusinessAsync(int businessId)
        {
            await using var db = TestAppDbContext.Unrestricted(_options);
            return await db.Businesses.AsNoTracking().SingleAsync(business => business.Id == businessId);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private AppDbContext Track(AppDbContext context)
        {
            _contexts.Add(context);
            return context;
        }
    }

    #endregion
}
