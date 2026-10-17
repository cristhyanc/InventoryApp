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
/// The one lookup that turns a Nayax product id into a local product id (issue #733), over
/// relational SQLite and two businesses, because everything it has to get right is a database
/// behaviour: the exact text match the unique index is built on, and the tenant query filter that
/// keeps one business's links out of another's answer.
/// </summary>
public class EfNayaxProductLinkStoreTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

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
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
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

    private void LinkAs(int businessId, long productId, string name, long nayaxProductId) =>
        SaveAs(businessId, db =>
        {
            db.Products.Add(new Product { Id = productId, Name = name });
            db.ProductExternalIds.Add(new ProductExternalId
            {
                Provider = ExternalProvider.Nayax,
                ExternalId = NayaxExternalId.From(nayaxProductId),
                ProductId = productId,
            });
        });

    private async Task<IReadOnlyDictionary<long, long>> ResolveAsAsync(
        int businessId, params long[] nayaxProductIds)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfNayaxProductLinkStore(db).GetLocalProductIdsAsync(
            nayaxProductIds, CancellationToken.None);
    }

    [Fact]
    public async Task A_linked_nayax_id_resolves_to_the_product_the_link_names()
    {
        LinkAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);

        var resolved = await ResolveAsAsync(BusinessA, 100);

        Assert.Equal(4100, resolved[100]);
    }

    /// <summary>
    /// The whole point of the link table: the local key and the Nayax id are different numbers, so
    /// an answer that came from comparing them would be wrong rather than coincidentally right. A
    /// local product whose id happens to equal somebody's Nayax id is not a match for it.
    /// </summary>
    [Fact]
    public async Task An_unlinked_nayax_id_resolves_to_nothing_even_when_a_product_has_that_id()
    {
        LinkAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);
        SaveAs(BusinessA, db => db.Products.Add(new Product { Id = 100, Name = "Unrelated product numbered 100" }));

        var resolved = await ResolveAsAsync(BusinessA, 100, 999);

        Assert.Equal(4100, resolved[100]);
        Assert.False(resolved.ContainsKey(999));
    }

    /// <summary>
    /// A link is tenant-owned, so another business's link is not an answer to this business's
    /// question - even though both businesses legitimately hold a link for Nayax product 100.
    /// </summary>
    [Fact]
    public async Task One_business_s_link_never_resolves_another_business_s_nayax_id()
    {
        LinkAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);
        LinkAs(BusinessB, productId: 7200, "Cola", nayaxProductId: 100);

        var forA = await ResolveAsAsync(BusinessA, 100);
        var forB = await ResolveAsAsync(BusinessB, 100);

        Assert.Equal(4100, forA[100]);
        Assert.Equal(7200, forB[100]);
    }

    [Fact]
    public async Task An_empty_request_resolves_nothing()
    {
        LinkAs(BusinessA, productId: 4100, "Chips", nayaxProductId: 100);

        Assert.Empty(await ResolveAsAsync(BusinessA));
    }

    /// <summary>
    /// Another provider's link is not a Nayax link. Nayax is the only provider today, so this is
    /// asserted by writing the row the schema allows rather than through a second enum member: the
    /// predicate must select on the provider, not on the table alone.
    /// </summary>
    [Fact]
    public async Task A_link_recorded_for_another_provider_is_not_a_nayax_match()
    {
        SaveAs(BusinessA, db =>
        {
            db.Products.Add(new Product { Id = 4100, Name = "Chips" });
            db.ProductExternalIds.Add(new ProductExternalId
            {
                Provider = (ExternalProvider)99,
                ExternalId = "100",
                ProductId = 4100,
            });
        });

        Assert.Empty(await ResolveAsAsync(BusinessA, 100));
    }

    /// <summary>
    /// <c>ExternalId</c> is opaque text compared exactly, never parsed by the query. A stored value
    /// the one Nayax conversion could not have written - padded digits here - therefore matches
    /// nothing, rather than being read as the number inside it.
    /// </summary>
    [Fact]
    public async Task A_stored_external_id_that_is_not_a_nayax_id_resolves_to_nothing()
    {
        SaveAs(BusinessA, db =>
        {
            db.Products.Add(new Product { Id = 4100, Name = "Chips" });
            db.ProductExternalIds.Add(new ProductExternalId
            {
                Provider = ExternalProvider.Nayax,
                ExternalId = " 100 ",
                ProductId = 4100,
            });
        });

        Assert.Empty(await ResolveAsAsync(BusinessA, 100));
    }
}
