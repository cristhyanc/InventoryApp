using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The one lookup that turns a Nayax product id into a local product id (issue #733, reading
/// <c>Product.ExternalProductId</c> since issue #759), over relational SQLite and two businesses,
/// because everything it has to get right is a database behaviour: the exact text match the filtered
/// unique index is built on, the tenant query filter that keeps one business's products out of
/// another's answer, and the business's own sales provider deciding whether its stored ids are Nayax
/// ids at all.
/// </summary>
public class EfNayaxProductLinkStoreTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const int BusinessWithoutProvider = 3;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfNayaxProductLinkStoreTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NayaxBusiness(BusinessA, "Vending A"),
            NayaxBusiness(BusinessB, "Vending B"),
            new Business
            {
                Id = BusinessWithoutProvider,
                Name = "Vending C",
                Status = BusinessStatus.Active,
                CreatedAtUtc = DateTime.UtcNow,
            });
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
        CreatedAtUtc = DateTime.UtcNow,
    };

    private void SaveAs(int businessId, Action<AppDbContext> write)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        write(db);
        db.SaveChanges();
    }

    private void RecordAs(int businessId, long productId, string name, long nayaxProductId) =>
        SaveAs(businessId, db => db.Products.Add(new Product
        {
            Id = productId,
            Name = name,
            ExternalProductId = NayaxExternalId.From(nayaxProductId),
        }));

    private async Task<IReadOnlyDictionary<long, long>> ResolveAsAsync(
        int businessId, params long[] nayaxProductIds)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfNayaxProductLinkStore(db).GetLocalProductIdsAsync(
            nayaxProductIds, CancellationToken.None);
    }

    [Fact]
    public async Task A_recorded_nayax_id_resolves_to_the_product_that_carries_it()
    {
        RecordAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);

        var resolved = await ResolveAsAsync(BusinessA, 100);

        Assert.Equal(4100, resolved[100]);
    }

    /// <summary>
    /// The whole point of the recorded id: the local key and the Nayax id are different numbers, so
    /// an answer that came from comparing them would be wrong rather than coincidentally right. A
    /// local product whose id happens to equal somebody's Nayax id is not a match for it.
    /// </summary>
    [Fact]
    public async Task An_unrecorded_nayax_id_resolves_to_nothing_even_when_a_product_has_that_id()
    {
        RecordAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);
        SaveAs(BusinessA, db => db.Products.Add(new Product { Id = 100, Name = "Unrelated product numbered 100" }));

        var resolved = await ResolveAsAsync(BusinessA, 100, 999);

        Assert.Equal(4100, resolved[100]);
        Assert.False(resolved.ContainsKey(999));
    }

    /// <summary>
    /// A product is tenant-owned, so another business's recorded id is not an answer to this
    /// business's question - even though both businesses legitimately hold Nayax product 100.
    /// </summary>
    [Fact]
    public async Task One_business_s_product_never_resolves_another_business_s_nayax_id()
    {
        RecordAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);
        RecordAs(BusinessB, productId: 7200, "Cola", nayaxProductId: 100);

        var forA = await ResolveAsAsync(BusinessA, 100);
        var forB = await ResolveAsAsync(BusinessB, 100);

        Assert.Equal(4100, forA[100]);
        Assert.Equal(7200, forB[100]);
    }

    [Fact]
    public async Task An_empty_request_resolves_nothing()
    {
        RecordAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);

        Assert.Empty(await ResolveAsAsync(BusinessA));
    }

    /// <summary>
    /// The external id column holds the id issued by the business's own sales provider and does not
    /// say which system that was (issue #759), so a business that does not sell through Nayax
    /// resolves no Nayax id - whatever its rows happen to carry. Asserted by storing the text a Nayax
    /// id would have, because the wrong answer here is to match it.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_sales_provider_resolves_no_nayax_id()
    {
        RecordAs(BusinessWithoutProvider, productId: 4100, "Chips", nayaxProductId: 100);

        Assert.Empty(await ResolveAsAsync(BusinessWithoutProvider, 100));
    }

    /// <summary>
    /// The same rule for a business whose provider is another one. Nayax is the only provider this
    /// application implements, so the stored value is written directly rather than through a second
    /// enum member: the lookup must select on the business's provider, not on the column alone.
    /// </summary>
    [Fact]
    public async Task A_business_selling_through_another_provider_resolves_no_nayax_id()
    {
        RecordAs(BusinessWithoutProvider, productId: 4100, "Chips", nayaxProductId: 100);
        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var business = db.Businesses.Single(row => row.Id == BusinessWithoutProvider);
            business.SalesProvider = (ExternalProvider)99;
            db.SaveChanges();
        }

        Assert.Empty(await ResolveAsAsync(BusinessWithoutProvider, 100));
    }

    /// <summary>
    /// An external id is opaque text compared exactly, never parsed by the query. A stored value the
    /// one Nayax conversion could not have written - padded digits here - therefore matches nothing,
    /// rather than being read as the number inside it.
    /// </summary>
    [Fact]
    public async Task A_stored_external_id_that_is_not_a_nayax_id_resolves_to_nothing()
    {
        SaveAs(BusinessA, db => db.Products.Add(
            new Product { Id = 4100, Name = "Chips", ExternalProductId = " 100 " }));

        Assert.Empty(await ResolveAsAsync(BusinessA, 100));
    }
}
