using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The rules the provider identity columns of issue #759 keep, proved against SQLite because every
/// one of them is a database behaviour: a filtered unique index per business, and the business's own
/// sales provider being write-once.
///
/// <para>The uniqueness is per business and only over the rows that <em>have</em> an id. Both halves
/// matter: two operators' catalogues may legitimately both contain product 100 (AGENTS.md § Tenant
/// ownership), and "this row has no provider id" is the state every row of a business with no
/// provider is in - a plain unique index would allow exactly one of them.</para>
/// </summary>
public class ProviderIdentityColumnPersistenceTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ProviderIdentityColumnPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NayaxBusiness(BusinessA, "Vending A"),
            NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private void SaveAs(int businessId, Action<AppDbContext> write)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        write(db);
        db.SaveChanges();
    }

    [Fact]
    public void Two_businesses_can_hold_the_same_external_product_id()
    {
        SaveAs(BusinessA, db => db.Products.Add(
            new Product { Id = 5001, Name = "Chips", ExternalProductId = "100" }));
        SaveAs(BusinessB, db => db.Products.Add(
            new Product { Id = 7002, Name = "Cola", ExternalProductId = "100" }));

        using var verify = TestAppDbContext.Unrestricted(_options);
        var products = verify.Products.Where(product => product.ExternalProductId == "100").ToList();

        Assert.Equal(2, products.Count);
        Assert.Equal([BusinessA, BusinessB], products.Select(product => product.BusinessId).Order());
    }

    [Fact]
    public void One_business_cannot_hold_the_same_external_product_id_twice()
    {
        SaveAs(BusinessA, db => db.Products.Add(
            new Product { Id = 5001, Name = "Chips", ExternalProductId = "100" }));

        var duplicate = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db => db.Products.Add(
            new Product { Id = 5002, Name = "Pretzels", ExternalProductId = "100" })));

        Assert.Equal(
            SQLitePCL.raw.SQLITE_CONSTRAINT,
            Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
    }

    [Fact]
    public void One_business_cannot_hold_the_same_external_category_id_twice()
    {
        SaveAs(BusinessA, db => db.Categories.Add(
            new Category { Id = 55, Name = "Snacks", ExternalCategoryId = "10" }));

        var duplicate = Assert.Throws<DbUpdateException>(() => SaveAs(BusinessA, db => db.Categories.Add(
            new Category { Id = 56, Name = "Drinks", ExternalCategoryId = "10" })));

        Assert.Equal(
            SQLitePCL.raw.SQLITE_CONSTRAINT,
            Assert.IsType<SqliteException>(duplicate.InnerException).SqliteErrorCode);
    }

    /// <summary>
    /// The filter is the other half of the rule: a business whose rows carry no provider id at all -
    /// one with no provider, and every locally created row of one that has - is the normal case, not
    /// a duplicate.
    /// </summary>
    [Fact]
    public void One_business_may_hold_many_rows_with_no_external_id()
    {
        SaveAs(BusinessA, db => db.Products.AddRange(
            new Product { Id = 5001, Name = "Locally created" },
            new Product { Id = 5002, Name = "Also local" }));
        SaveAs(BusinessA, db => db.Categories.AddRange(
            new Category { Id = 55, Name = "Local" },
            new Category { Id = 56, Name = "Also local" }));

        using var verify = TestAppDbContext.For(_options, BusinessA);
        Assert.Equal(2, verify.Products.Count(product => product.ExternalProductId == null));
        Assert.Equal(2, verify.Categories.Count(category => category.ExternalCategoryId == null));
    }

    /// <summary>
    /// <c>SalesProvider</c> is write-once (issue #759): every external id the business's rows hold
    /// was issued by the stored provider, so changing it would reinterpret stored identifiers as
    /// another system's - a Nayax product id read as a Cantaloupe one is not a mistake any report
    /// would show. Switching provider is a separate design (#761).
    /// </summary>
    [Fact]
    public void A_sales_provider_that_is_set_cannot_be_changed()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var business = db.Businesses.Single(row => row.Id == BusinessA);
        business.SalesProvider = (ExternalProvider)99;

        var refused = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        Assert.Contains("cannot be changed once it is set", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>Clearing it is a change too: "no provider" after ids were issued is not a state.</summary>
    [Fact]
    public void A_sales_provider_that_is_set_cannot_be_cleared()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        var business = db.Businesses.Single(row => row.Id == BusinessA);
        business.SalesProvider = null;

        var refused = Assert.Throws<InvalidOperationException>(() => db.SaveChanges());

        Assert.Contains("cannot be changed once it is set", refused.Message, StringComparison.Ordinal);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            ExternalProvider.Nayax,
            verify.Businesses.Single(row => row.Id == BusinessA).SalesProvider);
    }

    /// <summary>
    /// Claiming an unset provider is not a change, and is how a business gets one at all: storing
    /// its Nayax credentials does exactly this, in the credential save's own transaction.
    /// </summary>
    [Fact]
    public void A_business_with_no_sales_provider_may_be_given_one()
    {
        using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Businesses.Add(new Business
            {
                Id = 3,
                Name = "Vending C",
                Status = BusinessStatus.Active,
                CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            setup.SaveChanges();
        }

        using (var claim = TestAppDbContext.Unrestricted(_options))
        {
            claim.Businesses.Single(row => row.Id == 3).SalesProvider = ExternalProvider.Nayax;
            claim.SaveChanges();
        }

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            ExternalProvider.Nayax,
            verify.Businesses.Single(row => row.Id == 3).SalesProvider);
    }
}
