using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Relational coverage for the manual-catalogue import guard (issue #736, parent #329 decision P2)
/// over the real <see cref="EfNayaxProductCatalogImportStore"/>, because what is asserted is that
/// <strong>nothing is committed</strong> when the guard refuses - which an in-memory provider,
/// having no transaction, could not tell apart from a write that happened to be skipped.
///
/// <para>The window the guard has to survive is the one the use case cannot avoid: the two Nayax
/// catalogue reads take as long as Nayax takes, and a person may create a manual product while they
/// are in flight. The check before those reads spends no Nayax call on a business that is already
/// manual; the check <em>inside the apply's transaction</em> is the one the import is decided
/// on.</para>
/// </summary>
public class ManualCatalogueImportGuardTests : IDisposable
{
    private const int BusinessA = 1;
    private static readonly DateTime ImportedAt = new(2026, 10, 19, 2, 15, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public ManualCatalogueImportGuardTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = DateTime.UtcNow,
        });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The business's catalogue is entirely provider-managed when the import starts, so the check
    /// before the remote reads allows it. A manual product is then created while the first Nayax
    /// read is in flight. The authoritative count inside the apply's own transaction sees it, the
    /// import is refused with the count in the message, and the database holds exactly the one
    /// manual product afterwards: no imported product, no imported category, no external id.
    /// </summary>
    [Fact]
    public async Task A_product_created_during_the_remote_read_refuses_the_import_and_commits_nothing()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var useCase = new ImportNayaxProductCatalog(
            NayaxThatCreatesAManualProductWhileItIsRead(), new EfNayaxProductCatalogImportStore(db), new FixedClock(ImportedAt));

        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => useCase.Handle(CancellationToken.None));

        Assert.Equal(ManualCatalogueImportPolicy.Refusal(1), refusal.Message);

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = Assert.Single(await verify.Products.AsNoTracking().ToListAsync());
        Assert.Equal("Hand-made bar", product.Name);
        Assert.Null(product.ExternalProductId);
        Assert.Empty(await verify.Categories.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The control: the same snapshot, into the same business, with nobody creating anything in
    /// between. The transaction the guard now runs in must not have made the ordinary import
    /// conditional on anything else - it commits exactly the rows it always did.
    /// </summary>
    [Fact]
    public async Task An_undisturbed_import_still_commits_its_catalogue()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var useCase = new ImportNayaxProductCatalog(
            Nayax(onProductsRead: null), new EfNayaxProductCatalogImportStore(db), new FixedClock(ImportedAt));

        await useCase.Handle(CancellationToken.None);

        await using var verify = TestAppDbContext.For(_options, BusinessA);
        var product = Assert.Single(await verify.Products.AsNoTracking().ToListAsync());
        Assert.Equal("Chips", product.Name);
        Assert.Equal(NayaxExternalId.From(100), product.ExternalProductId);
        var category = Assert.Single(await verify.Categories.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxExternalId.From(10), category.ExternalCategoryId);
    }

    /// <summary>
    /// A Nayax client whose products read inserts a manual product first - a second request
    /// committing a <c>POST /api/products</c> while this import waits on the operator's catalogue.
    /// It writes through its own <see cref="AppDbContext"/>, so the import's context never had it
    /// loaded and has to read it back to find out.
    /// </summary>
    private INayaxLynxClient NayaxThatCreatesAManualProductWhileItIsRead() =>
        Nayax(onProductsRead: () =>
        {
            using var other = TestAppDbContext.For(_options, BusinessA);
            other.Products.Add(new Product { Name = "Hand-made bar", UnitPrice = 2.50m, RestockTo = 0 });
            other.SaveChanges();
        });

    private static INayaxLynxClient Nayax(Action? onProductsRead)
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(client => client.GetProductsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                onProductsRead?.Invoke();
                return
                [
                    new NayaxProduct
                    {
                        NayaxProductId = 100,
                        ProductName = "Chips",
                        ProductDefaultRetailPrice = 3.50m,
                        ProductGroupId = 10,
                    },
                ];
            });
        nayax.Setup(client => client.GetProductGroupssAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new NayaxProductGroup { ProductGroupID = 10, ProductGroupName = "Snacks" }]);
        return nayax.Object;
    }

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
