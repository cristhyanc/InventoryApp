using Inventory.Application.CatalogueReview;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
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

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// The relational world the provider catalogue review tests run in (issue #738).
///
/// <para><strong>Relational, not in-memory, and deliberately so.</strong> What these tests assert is
/// that an apply is atomic - a refused link commits <em>nothing</em> - that the links the database
/// refuses are refused, and that a provider id is unique per business among the rows that have one.
/// None of that is a property an in-memory provider has, so none of it could be told apart there from
/// a write that happened to be skipped.</para>
/// </summary>
public abstract class CatalogueReviewTestBase : IDisposable
{
    protected const int BusinessA = 1;

    protected static readonly DateTime StartedAt = new(2026, 10, 19, 2, 0, 0, DateTimeKind.Utc);
    protected static readonly DateTime AppliedAt = new(2026, 10, 19, 2, 5, 0, DateTimeKind.Utc);

    protected static readonly ActorIdentity Operator = Actor("11111111-1111-1111-1111-111111111111");

    private readonly SqliteConnection _connection;

    protected CatalogueReviewTestBase()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(Options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = StartedAt,
        });
        setup.SaveChanges();
    }

    protected DbContextOptions<AppDbContext> Options { get; }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A context acting as business A, exactly as a scoped request would.</summary>
    protected AppDbContext Scoped() => TestAppDbContext.For(Options, BusinessA);

    protected StartCatalogueReview Start(AppDbContext db, INayaxLynxClient nayax, DateTime? at = null) =>
        new(nayax, new EfCatalogueReviewStore(db), new FixedClock(at ?? StartedAt));

    protected ApplyCatalogueReview Apply(AppDbContext db, DateTime? at = null) =>
        new(
            new EfCatalogueReviewStore(db),
            FakeAuthenticatedActorAccessor.Identified(Operator),
            new FixedClock(at ?? AppliedAt));

    protected ResolveLinkedProviderSales ResolveSales(AppDbContext db, DateTime? at = null) =>
        new(
            new EfLinkedProviderSaleResolutionStore(db),
            TestCostingUseCases.Rebuild(db),
            FakeAuthenticatedActorAccessor.Identified(Operator),
            new FixedClock(at ?? AppliedAt));

    /// <summary>One operator catalogue, as the Lynx client would answer it.</summary>
    protected static INayaxLynxClient Nayax(
        IEnumerable<NayaxProduct> products, IEnumerable<NayaxProductGroup> groups)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(products.ToList());
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(groups.ToList());
        return nayax.Object;
    }

    protected static NayaxProduct Product(
        long nayaxProductId, string name, decimal? price = null, long? groupId = null) =>
        new()
        {
            NayaxProductId = nayaxProductId,
            ProductName = name,
            ProductDefaultRetailPrice = price,
            ProductGroupId = groupId,
        };

    protected static NayaxProductGroup Group(int groupId, string name) =>
        new() { ProductGroupID = groupId, ProductGroupName = name };

    /// <summary>The review entry for one provider product, by the provider id it carries.</summary>
    protected static CatalogueReviewProductDto ProductEntry(CatalogueReviewDto review, long nayaxProductId) =>
        Assert.Single(review.Products.Where(product =>
            product.ExternalId == NayaxExternalId.From(nayaxProductId)));

    /// <summary>The review entry for one provider group, by the provider id it carries.</summary>
    protected static CatalogueReviewCategoryDto CategoryEntry(CatalogueReviewDto review, long groupId) =>
        Assert.Single(review.Categories.Where(category =>
            category.ExternalId == NayaxExternalId.From(groupId)));

    protected static CatalogueReviewDecisionRequest LinkTo(long itemId, long localRowId) =>
        new(itemId, CatalogueReviewAction.LinkExisting, localRowId);

    protected static CatalogueReviewDecisionRequest Create(long itemId) =>
        new(itemId, CatalogueReviewAction.CreateNew);

    protected static CatalogueReviewDecisionRequest Price(
        long itemId, ProviderFieldDecision decision, CatalogueReviewAction action = CatalogueReviewAction.Defer,
        long? localRowId = null) =>
        new(
            itemId,
            action,
            localRowId,
            [new CatalogueReviewFieldDecisionRequest(NegotiableCatalogueField.UnitPrice, decision)]);

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate("33333333-3333-3333-3333-333333333333", objectId, out var actor));
        return actor!;
    }

    protected sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
