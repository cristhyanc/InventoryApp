using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Purchases;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two copies of one purchase submission arriving at the same time (issue #1247).
///
/// <para>This is the test the design exists for. "Has this key already saved a purchase?" is a
/// check-then-write rule, and a check-then-write rule that is not serialised is not a rule: both
/// requests read "nothing under this key", both save, and the receipt's stock and inventory value
/// are counted twice - which is the doubled inventory the key exists to prevent, on a phone that
/// merely retried after a timeout.</para>
///
/// <para>It therefore runs against a real SQLite <strong>file</strong> with two independent
/// connections, because that is the only way to have two genuine writers: a shared in-memory
/// database is one connection, and InMemory has no locking at all. The two requests are released
/// together at a barrier, so the window the rule has to close is actually opened. It is the same
/// shape as <see cref="PickListRefillConcurrencyTests"/> (issue #743), for the same reason.</para>
/// </summary>
public class PurchaseCreateConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const int OpeningStock = 20;
    private const string Key = "7a0d1a9e-1f8c-4a5f-8a1f-2b3c4d5e6f70";

    private static readonly DateTime PurchaseDate = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-purchase-create-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public PurchaseCreateConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = PurchaseDate,
        });
        db.SaveChanges();
        SeedProduct();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The issue's third criterion: exactly one purchase and one set of stock movements are written,
    /// and the losing request is answered with the winning request's purchase rather than with an
    /// error - because the loser is a retry of the same submission and its operator is owed the
    /// purchase that was saved.
    /// </summary>
    [Fact]
    public async Task Two_identical_requests_under_one_key_save_one_purchase()
    {
        var answers = await RaceAsync(Request(Key), Request(Key));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        var ids = answers.Select(answer => answer.Purchase!.Id).Distinct().ToList();
        Assert.Single(ids);

        var state = Read();
        Assert.Equal(1, state.PurchaseCount);
        Assert.Equal(1, state.RestockMovementCount);
        // The receipt's four units, added once: a second purchase would have left 28 on the shelf.
        Assert.Equal(OpeningStock + 4, state.QuantityInStock);
        Assert.Equal(Key, state.StoredKey);
    }

    /// <summary>
    /// Two <em>different</em> purchases racing under one key: one is saved and the other is refused
    /// with the reused-key sentence, never saved as a second purchase. Which of them wins is the
    /// database's decision and is deliberately not asserted.
    /// </summary>
    [Fact]
    public async Task A_different_request_under_the_same_key_is_refused_rather_than_saved()
    {
        var answers = await RaceAsync(Request(Key), Request(Key, quantity: 7m));

        var saved = answers.Where(answer => answer.Purchase is not null).ToList();
        var refused = answers.Where(answer => answer.Failure is not null).ToList();
        Assert.Single(saved);
        var conflict = Assert.IsType<DomainConflictException>(Assert.Single(refused).Failure);
        Assert.Equal(PurchaseIdempotencyPolicy.KeyReusedMessage, conflict.Message);

        var state = Read();
        Assert.Equal(1, state.PurchaseCount);
        Assert.Equal(1, state.RestockMovementCount);
    }

    /// <summary>
    /// And without a key, two identical submissions are still two purchases - the behaviour every
    /// client that predates this issue has, unchanged by the lock the key takes.
    /// </summary>
    [Fact]
    public async Task Two_identical_requests_without_a_key_are_two_purchases()
    {
        var answers = await RaceAsync(Request(null), Request(null));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Equal(2, answers.Select(answer => answer.Purchase!.Id).Distinct().Count());

        var state = Read();
        Assert.Equal(2, state.PurchaseCount);
        Assert.Equal(2, state.RestockMovementCount);
        Assert.Equal(OpeningStock + 8, state.QuantityInStock);
    }

    /// <summary>
    /// Seeds the product with an opening costed Restock, so the cost rebuild each purchase triggers
    /// has a real history to replay.
    /// </summary>
    private void SeedProduct()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        db.Products.Add(new Product { Id = ProductId, Name = "Coke Zero", QuantityInStock = OpeningStock });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = OpeningStock,
            QuantityAfter = OpeningStock,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = PurchaseDate.AddDays(-1),
        });
        db.SaveChanges();
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>
    /// The create use case over a real adapter. Document storage is a bare double that is never
    /// reached: every purchase here is recorded without a document, which is also why nothing in
    /// this test depends on what it would do.
    /// </summary>
    private static UploadPurchase UseCaseFor(AppDbContext db) =>
        TestPurchaseUseCases.Upload(
            new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), new FakeClock(PurchaseDate)),
            new Mock<IDocumentStorage>().Object,
            new FakeClock(PurchaseDate));

    /// <summary>
    /// Runs the creates released at the same instant and returns each one's answer - the purchase it
    /// produced, or the exception it threw.
    /// </summary>
    private async Task<(PurchaseRecord? Purchase, Exception? Failure)[]> RaceAsync(
        params (string? Key, decimal Quantity)[] requests)
    {
        using var barrier = new Barrier(requests.Length);

        var attempts = requests
            .Select(request => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                try
                {
                    // Its own context on its own connection, the way two HTTP requests arrive.
                    await using var db = TestAppDbContext.For(Options(), BusinessA);
                    var purchase = await UseCaseFor(db).Handle(
                        null,
                        new PurchaseFields(
                            "Weekly restock", null, 30m, null, null, PurchaseDate, null, null, null, "INV-1"),
                        [new PurchaseItemInput(ProductId, request.Quantity, 1.10m)],
                        null,
                        request.Key,
                        CancellationToken.None);
                    return (purchase, (Exception?)null);
                }
                catch (Exception exception)
                {
                    return ((PurchaseRecord?)null, exception);
                }
            }))
            .ToArray();

        return await Task.WhenAll(attempts);
    }

    private static (string? Key, decimal Quantity) Request(string? key, decimal quantity = 4m) => (key, quantity);

    private (int PurchaseCount, int RestockMovementCount, int QuantityInStock, string? StoredKey) Read()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        return (
            db.Receipts.AsNoTracking().Count(),
            db.StockAdjustments.AsNoTracking().Count(movement => movement.ReceiptItemId.HasValue),
            db.Products.AsNoTracking().Single(product => product.Id == ProductId).QuantityInStock,
            db.Receipts.AsNoTracking().OrderBy(receipt => receipt.Id).Select(receipt => receipt.IdempotencyKey).FirstOrDefault());
    }
}
