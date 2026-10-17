using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The rules the provider identity link tables of issue #732 keep, proved against SQLite because
/// every one of them is a database behaviour: two unique indexes, a composite foreign key that
/// cannot be satisfied across businesses, and a cascade that takes a link with the row it names.
///
/// The cross-business cases are asserted twice over, at both layers that refuse them: through EF,
/// where <see cref="BusinessOwnershipEnforcer"/> speaks first, and through raw SQL on the same
/// connection, where nothing but the schema is left to refuse. That second assertion is the
/// acceptance criterion - application validation alone is explicitly not enough for this link.
/// </summary>
public class ProviderIdentityLinkPersistenceTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProviderIdentityLinkPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();

        SaveAs(BusinessA, db =>
        {
            db.Categories.Add(new Category { Id = 10, Name = "Snacks" });
            db.Products.Add(new Product { Id = 100, Name = "Chips", CategoryId = 10 });
        });
        SaveAs(BusinessB, db =>
        {
            db.Categories.Add(new Category { Id = 11, Name = "Drinks" });
            db.Products.Add(new Product { Id = 200, Name = "Cola", CategoryId = 11 });
        });
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SaveAs(int businessId, Action<AppDbContext> write)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        write(db);
        db.SaveChanges();
    }

    /// <summary>
    /// The uniqueness is per business, like every other constraint over an externally supplied
    /// value (AGENTS.md § Tenant ownership and data isolation). Two operators' Nayax catalogues can
    /// legitimately both contain product 100, and whichever imported first must not deny the other
    /// its own id - which is exactly what a global index over <c>ExternalId</c> would do.
    /// </summary>
    [Fact]
    public void Two_businesses_can_hold_the_same_external_id()
    {
        SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 100 }));
        SaveAs(BusinessB, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 200 }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var links = verify.ProductExternalIds.Where(link => link.ExternalId == "100").ToList();

        Assert.Equal(2, links.Count);
        Assert.Equal([BusinessA, BusinessB], links.Select(link => link.BusinessId).Order());
        Assert.Equal([100L, 200L], links.Select(link => link.ProductId).Order());
    }

    [Fact]
    public void One_business_cannot_point_the_same_external_id_at_two_products()
    {
        SaveAs(BusinessA, db => db.Products.Add(new Product { Id = 101, Name = "Pretzels" }));
        SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 100 }));

        var duplicate = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 101 })));

        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
    }

    /// <summary>
    /// The other uniqueness rule, and the one that keeps "which Nayax product is this?" answerable:
    /// a local row carries at most one id per provider. A second provider would be a second row,
    /// which is why the index is scoped by <c>Provider</c> rather than by the product alone.
    /// </summary>
    [Fact]
    public void One_product_cannot_hold_two_nayax_ids()
    {
        SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 100 }));

        var duplicate = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "999", ProductId = 100 })));

        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
    }

    [Fact]
    public void One_category_cannot_hold_two_nayax_ids()
    {
        SaveAs(BusinessA, db => db.CategoryExternalIds.Add(
            new CategoryExternalId { Provider = ExternalProvider.Nayax, ExternalId = "10", CategoryId = 10 }));

        var duplicate = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db => db.CategoryExternalIds.Add(
            new CategoryExternalId { Provider = ExternalProvider.Nayax, ExternalId = "99", CategoryId = 10 })));

        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
    }

    /// <summary>
    /// Through EF, with a correctly stamped business and another business's product id, the write
    /// is refused - by the foreign key.
    ///
    /// <para>Worth pinning precisely, because it is not the layer one would guess.
    /// <c>BusinessOwnershipEnforcer</c> deliberately stays quiet here: its relationship rule looks
    /// the principal up by the whole foreign key, and because <c>BusinessId</c> is <em>part of</em>
    /// this key, "business A's product 200" simply does not exist, which the enforcer leaves to
    /// referential integrity to report rather than mistaking for an ownership violation. The
    /// composite key is what turns that into a refusal instead of an orphan.</para>
    /// </summary>
    [Fact]
    public void A_link_naming_another_businesss_product_is_refused_when_written_through_ef()
    {
        var crossBusiness = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db =>
            db.ProductExternalIds.Add(
                new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "200", ProductId = 200 })));

        Assert.Equal(
            SQLitePCL.raw.SQLITE_CONSTRAINT,
            Assert.IsType<SqliteException>(crossBusiness.InnerException).SqliteErrorCode);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(verify.ProductExternalIds);
    }

    /// <summary>
    /// Moving an existing link to another business is refused too, and here it is the enforcer that
    /// speaks: ownership is immutable, so the link cannot be carried out of the business whose
    /// product it names.
    /// </summary>
    [Fact]
    public void A_link_cannot_be_moved_to_another_business()
    {
        SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 100 }));

        var moved = Assert.Throws<CrossBusinessAccessException>(() => SaveAs(BusinessA, db =>
        {
            var link = db.ProductExternalIds.Single();
            link.BusinessId = BusinessB;
        }));

        Assert.Contains("immutable", moved.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// And with the enforcer out of the way: the same insert written straight to SQLite. This is
    /// what the composite foreign key exists for - a link is a row that will later resolve a remote
    /// identifier to a local product, so "it points at my own product" must hold even for a writer
    /// that never passed through <c>AppDbContext</c>.
    /// </summary>
    [Fact]
    public void A_link_naming_another_businesss_row_is_refused_by_the_database()
    {
        AssertConstraintRefuses(
            """
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (1, 1, '200', 200);
            """);

        AssertConstraintRefuses(
            """
            INSERT INTO CategoryExternalIds (BusinessId, Provider, ExternalId, CategoryId)
                VALUES (1, 1, '11', 11);
            """);
    }

    /// <summary>A link to a product that does not exist at all is the same constraint.</summary>
    [Fact]
    public void A_link_to_no_product_at_all_is_refused_by_the_database() =>
        AssertConstraintRefuses(
            """
            INSERT INTO ProductExternalIds (BusinessId, Provider, ExternalId, ProductId)
                VALUES (1, 1, '555', 555);
            """);

    [Fact]
    public void Deleting_a_product_deletes_its_links()
    {
        SaveAs(BusinessA, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "100", ProductId = 100 }));

        SaveAs(BusinessA, db => db.Products.Remove(db.Products.Single(product => product.Id == 100)));

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(verify.ProductExternalIds);
        // Business B's catalogue and its link are untouched by another business's delete.
        Assert.Equal(200L, verify.Products.Single().Id);
    }

    [Fact]
    public void Deleting_a_category_deletes_its_links()
    {
        SaveAs(BusinessA, db => db.CategoryExternalIds.Add(
            new CategoryExternalId { Provider = ExternalProvider.Nayax, ExternalId = "10", CategoryId = 10 }));

        SaveAs(BusinessA, db => db.Categories.Remove(db.Categories.Single(category => category.Id == 10)));

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(verify.CategoryExternalIds);
        // The product the category held keeps existing; only the link went with the category.
        Assert.Contains(verify.Products, product => product.Id == 100);
    }

    /// <summary>
    /// A link is ordinary tenant-owned data, so the central query filter hides another business's
    /// rows without this table or any query over it saying so.
    /// </summary>
    [Fact]
    public void A_link_is_invisible_to_another_business()
    {
        SaveAs(BusinessB, db => db.ProductExternalIds.Add(
            new ProductExternalId { Provider = ExternalProvider.Nayax, ExternalId = "200", ProductId = 200 }));

        using var asA = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(asA.ProductExternalIds);

        using var asB = TestAppDbContext.For(_options, BusinessB);
        Assert.Equal("200", asB.ProductExternalIds.Single().ExternalId);
    }

    private void AssertConstraintRefuses(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;

        var refused = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(SQLitePCL.raw.SQLITE_CONSTRAINT, refused.SqliteErrorCode);
    }
}
