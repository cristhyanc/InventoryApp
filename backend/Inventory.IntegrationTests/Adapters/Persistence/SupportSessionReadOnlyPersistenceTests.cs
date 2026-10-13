using Inventory.Application.SupportSessions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// What the persistence boundary does in a support session (issue #551), over relational SQLite with
/// two businesses.
///
/// <para>Two claims, and they are deliberately independent of every HTTP rule above them. The read
/// side is the ordinary tenant boundary: a support request is scoped to the one business its session
/// names, sees exactly that business's rows, and sees no other business's - the query filters compare
/// against the same business key they always did, so this is the existing mechanism rather than a
/// second one. The write side is new: <c>BusinessOwnershipEnforcer</c> refuses <em>every</em>
/// <c>SaveChanges</c> in this state, for its own business as much as for anybody else's, and for the
/// structurally non-tenant-owned tables as much as for the owned ones.</para>
///
/// <para>That second claim is what makes "read-only" a property of the database rather than of each
/// endpoint. The capability handler's method rule already refuses every non-<c>GET</c> request in a
/// session; this is what stops a <c>GET</c> that turns out to have a side effect.</para>
/// </summary>
public class SupportSessionReadOnlyPersistenceTests : IDisposable
{
    private const int SupportedBusiness = 1;
    private const int OtherBusiness = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public SupportSessionReadOnlyPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NewBusiness(SupportedBusiness, "Vending Co"),
            NewBusiness(OtherBusiness, "Other Vending Co"));
        setup.Products.AddRange(
            NewProduct(SupportedBusiness, "Supported Cola"),
            NewProduct(OtherBusiness, "Other Cola"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The criterion: tenant filters treat a support session as the business it names. Only that
    /// business's rows are visible, and the other business's are as invisible as they are to any
    /// member of the first.
    /// </summary>
    [Fact]
    public async Task A_support_session_reads_exactly_the_business_it_names()
    {
        await using var db = TestAppDbContext.SupportSession(_options, SupportedBusiness);

        var products = await db.Products.AsNoTracking().ToListAsync();

        Assert.Equal(["Supported Cola"], products.Select(product => product.Name));
        // The same business key the query filters compare against - the existing mechanism, not a
        // second one.
        Assert.Equal(SupportedBusiness, db.CurrentBusinessId);
        Assert.True(db.TenantFilteringEnabled);
    }

    /// <summary>
    /// Looking a row up by its key does not reach past the filter either - the case a per-controller
    /// <c>Where</c> clause would miss and the central mechanism does not.
    /// </summary>
    [Fact]
    public async Task Another_businesss_row_is_not_reachable_by_its_key()
    {
        long otherProductId;
        await using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            otherProductId = (await setup.Products.AsNoTracking()
                .SingleAsync(product => product.BusinessId == OtherBusiness)).Id;
        }

        await using var db = TestAppDbContext.SupportSession(_options, SupportedBusiness);

        Assert.Null(await db.Products.AsNoTracking()
            .SingleOrDefaultAsync(product => product.Id == otherProductId));
    }

    /// <summary>
    /// The criterion: the enforcer rejects a write of the supported business's own data. This is the
    /// one that matters - it is not an ownership refusal, because the row belongs to exactly the
    /// business the session is reading. It is refused because a support session writes nothing.
    /// </summary>
    [Fact]
    public async Task A_write_of_the_supported_businesss_own_data_is_refused()
    {
        await using var db = TestAppDbContext.SupportSession(_options, SupportedBusiness);
        db.Products.Add(NewProduct(SupportedBusiness, "A product a supporter added"));

        var refusal = await Assert.ThrowsAsync<SupportSessionReadOnlyException>(
            () => db.SaveChangesAsync());

        Assert.Equal(SupportSessionReadOnlyException.RefusalMessage, refusal.Message);
        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, await verify.Products.CountAsync());
    }

    /// <summary>
    /// An update and a delete of the business's own rows are refused on the same terms, so "read-only"
    /// is not a rule about inserts.
    /// </summary>
    [Fact]
    public async Task An_update_and_a_delete_of_the_businesss_own_rows_are_refused()
    {
        await using (var update = TestAppDbContext.SupportSession(_options, SupportedBusiness))
        {
            var product = await update.Products.SingleAsync();
            product.Name = "Renamed by a supporter";

            await Assert.ThrowsAsync<SupportSessionReadOnlyException>(() => update.SaveChangesAsync());
        }

        await using (var delete = TestAppDbContext.SupportSession(_options, SupportedBusiness))
        {
            delete.Products.Remove(await delete.Products.SingleAsync());

            await Assert.ThrowsAsync<SupportSessionReadOnlyException>(() => delete.SaveChangesAsync());
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            ["Other Cola", "Supported Cola"],
            await verify.Products.AsNoTracking()
                .Select(product => product.Name)
                .OrderBy(name => name)
                .ToListAsync());
    }

    /// <summary>
    /// The structurally non-tenant-owned tables are refused too, which the tenant-ownership half of
    /// the enforcer would not have covered: a membership, an invite, a business row or a support
    /// session are all things a support request must not write.
    /// </summary>
    [Fact]
    public async Task A_write_to_a_non_tenant_owned_table_is_refused_as_well()
    {
        await using var db = TestAppDbContext.SupportSession(_options, SupportedBusiness);
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = SupportedBusiness,
            DirectoryTenantId = "11111111-1111-1111-1111-111111111111",
            ObjectId = "99999999-9999-9999-9999-999999999999",
            Role = BusinessRole.Owner,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow,
            StatusChangedAtUtc = DateTime.UtcNow,
        });

        await Assert.ThrowsAsync<SupportSessionReadOnlyException>(() => db.SaveChangesAsync());

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await verify.BusinessMemberships.ToListAsync());
    }

    /// <summary>
    /// A <c>SaveChanges</c> with nothing tracked writes nothing, so it is not refused: a read path
    /// that happens to call it must not be turned into a failure by a rule about writing.
    /// </summary>
    [Fact]
    public async Task A_save_with_nothing_to_write_is_left_alone()
    {
        await using var db = TestAppDbContext.SupportSession(_options, SupportedBusiness);
        _ = await db.Products.AsNoTracking().ToListAsync();

        Assert.Equal(0, await db.SaveChangesAsync());
    }

    /// <summary>
    /// An ordinary member's write is completely unaffected, which is the regression this whole state
    /// has to avoid: the enforcer's new rule is about one scope state and nothing else.
    /// </summary>
    [Fact]
    public async Task An_ordinary_members_write_is_unaffected()
    {
        await using var db = TestAppDbContext.For(_options, SupportedBusiness);
        db.Products.Add(NewProduct(SupportedBusiness, "A product the business added"));

        Assert.Equal(1, await db.SaveChangesAsync());
    }

    private static Business NewBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        TimeZoneId = "Australia/Sydney",
        Status = BusinessStatus.Active,
        CreatedAtUtc = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
    };

    private static Product NewProduct(int businessId, string name) => new()
    {
        BusinessId = businessId,
        Name = name,
        Sku = $"{name}-SKU",
        UnitPrice = 2.50m,
        QuantityInStock = 5,
    };
}
