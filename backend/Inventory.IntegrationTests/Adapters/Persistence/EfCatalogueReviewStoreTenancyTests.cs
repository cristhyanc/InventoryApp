using Inventory.Application.CatalogueReview;
using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Tenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Cross-tenant regression tests for the provider catalogue review's adapters (issue #738). The
/// review links a provider's id onto a local row and can create rows, so it is exactly the kind of
/// write that must stay inside the caller's business: it reads and writes through the same
/// <c>AppDbContext</c> global query filters and <c>BusinessOwnershipEnforcer</c> every other
/// tenant-owned store uses, never an ad hoc business predicate (AGENTS.md § Tenant ownership).
///
/// <para>Both businesses sell through Nayax and hold the <em>same</em> provider ids on purpose, which
/// is the legitimate case the per-business filtered unique index exists for: whichever reviewed first
/// must not deny the other its own catalogue. The two-business shape is also what proves that a
/// review id, a local row id and a recorded field decision of one business are all simply invisible
/// to the other rather than merely unlikely to be named.</para>
/// </summary>
public class EfCatalogueReviewStoreTenancyTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime StartedAt = new(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AppliedAt = new(2026, 10, 19, 2, 5, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfCatalogueReviewStoreTenancyTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            NayaxBusiness(BusinessA, "Vending A"), NayaxBusiness(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Two businesses review and link the same provider product and group ids. Each ends up with its
    /// own rows, its own recorded ids and its own field decisions, and neither touches the other's -
    /// which is the per-business uniqueness the filtered index expresses.
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_link_the_same_provider_ids_to_their_own_rows()
    {
        await SeedProductAsync(BusinessA, 50, "A chips", 3.00m);
        await SeedProductAsync(BusinessB, 60, "B chips", 4.00m);

        await LinkAsync(BusinessA, 50, providerPrice: 3.50m);
        await LinkAsync(BusinessB, 60, providerPrice: 3.50m);

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            [(BusinessA, 50L, NayaxExternalId.From(100), 3.50m), (BusinessB, 60L, NayaxExternalId.From(100), 3.50m)],
            await verify.Products.AsNoTracking()
                .OrderBy(product => product.BusinessId)
                .Select(product => ValueTuple.Create(
                    product.BusinessId, product.Id, product.ExternalProductId, product.UnitPrice))
                .ToListAsync());

        Assert.Equal(
            [(BusinessA, 50L), (BusinessB, 60L)],
            await verify.ProviderCatalogueFieldDecisions.AsNoTracking()
                .OrderBy(decision => decision.BusinessId)
                .Select(decision => ValueTuple.Create(decision.BusinessId, decision.ProductId))
                .ToListAsync());
    }

    /// <summary>
    /// A review of another business does not exist for this one: the read is tenant-filtered, so the
    /// apply refuses with the "start the review again" message rather than applying somebody else's
    /// decisions to this business's catalogue.
    /// </summary>
    [Fact]
    public async Task Another_business_review_cannot_be_read_or_applied()
    {
        await SeedProductAsync(BusinessB, 60, "B chips", 4.00m);

        long otherReviewId;
        await using (var other = TestAppDbContext.For(_options, BusinessB))
            otherReviewId = (await StartAsync(other, providerPrice: 3.50m)).ReviewId;

        await using var mine = TestAppDbContext.For(_options, BusinessA);
        var store = new EfCatalogueReviewStore(mine);

        Assert.Null(await store.FindReviewAsync(otherReviewId, CancellationToken.None));

        await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ApplyCatalogueReview(store, Actor(), new Clock(AppliedAt))
                .Handle(new ApplyCatalogueReviewRequest(otherReviewId), CancellationToken.None));

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Null(await verify.ProviderCatalogueReviews.AsNoTracking()
            .Where(review => review.Id == otherReviewId)
            .Select(review => review.AppliedAtUtc)
            .SingleAsync());
    }

    /// <summary>
    /// A local row of another business cannot be the target of this business's link. It is not in the
    /// tenant-filtered read, so the apply refuses it as "not a product of this business" - the same
    /// answer a product that does not exist at all gets - and nothing is written on either side.
    /// </summary>
    [Fact]
    public async Task Another_business_product_cannot_be_linked()
    {
        await SeedProductAsync(BusinessB, 60, "B chips", 4.00m);

        await using var mine = TestAppDbContext.For(_options, BusinessA);
        var review = await StartAsync(mine, providerPrice: 3.50m);
        var item = Assert.Single(review.Products);

        var refusal = await Assert.ThrowsAsync<DomainValidationException>(() =>
            new ApplyCatalogueReview(
                    new EfCatalogueReviewStore(mine), Actor(), new Clock(AppliedAt))
                .Handle(
                    new ApplyCatalogueReviewRequest(
                        review.ReviewId,
                        [new CatalogueReviewDecisionRequest(item.ItemId, CatalogueReviewAction.LinkExisting, 60)]),
                    CancellationToken.None));

        Assert.Contains("not a product of this business", refusal.Message, StringComparison.Ordinal);

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var untouched = await verify.Products.AsNoTracking().SingleAsync();
        Assert.Equal((BusinessB, "B chips", (string?)null), (untouched.BusinessId, untouched.Name, untouched.ExternalProductId));
    }

    /// <summary>
    /// The sale-resolution adapter is scoped the same way: another business's unresolved sales
    /// carrying the same provider product id are invisible to this business's run, so one operator's
    /// catalogue link can never attribute another operator's revenue.
    /// </summary>
    [Fact]
    public async Task Another_business_unresolved_sales_are_neither_listed_nor_resolved()
    {
        await SeedProductAsync(BusinessA, 50, "A chips", 3.00m, providerProductId: 100);
        await SeedProductAsync(BusinessB, 60, "B chips", 4.00m, providerProductId: 100);

        await using (var seed = TestAppDbContext.For(_options, BusinessB))
        {
            seed.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 1,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = 1,
                ProductId = null,
                NayaxProductId = 100,
                ProviderProductIdIsRaw = true,
                MachineAuthorizationTime = StartedAt,
            });
            await seed.SaveChangesAsync();
        }

        await using var mine = TestAppDbContext.For(_options, BusinessA);
        var store = new EfLinkedProviderSaleResolutionStore(mine);

        Assert.Empty(await store.ListAsync(CancellationToken.None));

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Null(await verify.NayaxSales.AsNoTracking()
            .Select(sale => sale.ProductId)
            .SingleAsync());
    }

    private async Task<CatalogueReviewDto> StartAsync(AppDbContext db, decimal providerPrice) =>
        await new StartCatalogueReview(Nayax(providerPrice), new EfCatalogueReviewStore(db), new Clock(StartedAt))
            .Handle(CancellationToken.None);

    /// <summary>
    /// One operator catalogue, identical for both businesses: the same provider product id and the
    /// same group id, which is the legitimate overlap this test exists for.
    /// </summary>
    private static INayaxLynxClient Nayax(decimal providerPrice)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new NayaxProduct
                {
                    NayaxProductId = 100,
                    ProductName = "Nayax chips",
                    ProductDefaultRetailPrice = providerPrice,
                    ProductGroupId = 10,
                },
            ]);
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxProductGroup { ProductGroupID = 10, ProductGroupName = "Snacks" }]);
        return nayax.Object;
    }

    private static FakeAuthenticatedActorAccessor Actor()
    {
        Assert.True(ActorIdentity.TryCreate(
            "33333333-3333-3333-3333-333333333333",
            "11111111-1111-1111-1111-111111111111",
            out var actor));
        return FakeAuthenticatedActorAccessor.Identified(actor!);
    }

    private async Task LinkAsync(int businessId, long localRowId, decimal providerPrice)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        var review = await StartAsync(db, providerPrice);
        var category = Assert.Single(review.Categories);
        var product = Assert.Single(review.Products);

        await new ApplyCatalogueReview(
                new EfCatalogueReviewStore(db), Actor(), new Clock(AppliedAt))
            .Handle(
                new ApplyCatalogueReviewRequest(
                    review.ReviewId,
                    [
                        new CatalogueReviewDecisionRequest(category.ItemId, CatalogueReviewAction.CreateNew),
                        new CatalogueReviewDecisionRequest(
                            product.ItemId,
                            CatalogueReviewAction.LinkExisting,
                            localRowId,
                            [
                                new CatalogueReviewFieldDecisionRequest(
                                    NegotiableCatalogueField.UnitPrice, ProviderFieldDecision.TakeProvider),
                            ]),
                    ]),
                CancellationToken.None);
    }

    private async Task SeedProductAsync(
        int businessId, long localRowId, string name, decimal price, long? providerProductId = null)
    {
        await using var setup = TestAppDbContext.For(_options, businessId);
        setup.Products.Add(new Product
        {
            Id = localRowId,
            Name = name,
            UnitPrice = price,
            RestockTo = 0,
            ExternalProductId = providerProductId is { } provider
                ? NayaxExternalId.From(provider)
                : null,
        });
        await setup.SaveChangesAsync();
    }

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = StartedAt,
    };

    private sealed class Clock(DateTime utcNow) : Inventory.Application.Time.IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
