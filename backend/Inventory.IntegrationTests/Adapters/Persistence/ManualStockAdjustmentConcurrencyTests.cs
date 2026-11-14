using Inventory.Application.Stock;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two manual stock adjustments of one product at the same instant (issue #1241).
///
/// <para>"Does storage hold what this adjustment takes?" is a check-then-write rule, and before this
/// issue the adjustment staged its movement - and therefore read the quantity it was checked against
/// - before the transaction opened. Two simultaneous write-offs of 4 against a stock of 5 both read
/// 5, both computed a new stock of 1, and the loser's movement was then refused by the cost replay
/// as "becomes physically negative": a <c>500</c> about costing data quality, for an operator whose
/// adjustment simply no longer fit. The read, the sufficiency check and the write now happen inside
/// one <c>SqliteWriteLockTransaction</c> (<c>BEGIN IMMEDIATE</c>), so the loser is told the
/// established not-enough-stock sentence against the quantity the winner left.</para>
///
/// <para>It runs against a real SQLite <strong>file</strong> with one connection per writer, because
/// that is the only way to have two genuine writers: a shared in-memory database is one connection,
/// and the EF InMemory provider has no locking at all. The two requests are released together at a
/// barrier, so the window the rule has to close is actually opened. It is the same shape as
/// <see cref="PickListRefillConcurrencyTests"/> (issue #743), for the same reason.</para>
/// </summary>
public sealed class ManualStockAdjustmentConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;
    private const decimal OpeningUnitCost = 2m;

    private static readonly DateTime RecordedAt = new(2026, 11, 13, 2, 30, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-manual-adjustments-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public ManualStockAdjustmentConcurrencyTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.Add(new Business
        {
            Id = BusinessA,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = RecordedAt,
        });
        db.SaveChanges();
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
    /// The acceptance criterion with teeth: two write-offs that each fit on their own but together
    /// take more than the shelf holds. Exactly one may be recorded, and the loser has to be told the
    /// same thing it would have been told sequentially - the established not-enough-stock sentence
    /// against the quantity the winner left, mapped to <c>400</c> - rather than a costing
    /// data-quality failure.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_adjustments_that_together_exceed_stock_leave_exactly_one()
    {
        SeedProduct(5);

        var answers = await RaceAsync(
            () => AdjustAsync(-4, DomainStock.StockAdjustmentReason.Damaged),
            () => AdjustAsync(-4, DomainStock.StockAdjustmentReason.Damaged));

        var recorded = Assert.Single(answers.Where(answer => answer.Record is not null));
        var refused = Assert.Single(answers.Where(answer => answer.Failure is not null));

        Assert.Equal(-4, recorded.Record!.QuantityChange);
        Assert.Equal(1, recorded.Record.QuantityAfter);

        // Refused against the quantity the winner actually left, not the one it started from.
        var insufficient = Assert.IsType<InsufficientStockException>(refused.Failure);
        Assert.Equal("Not enough products in stock. Available stock: 1", insufficient.Message);

        var (quantityInStock, movements) = Read();
        Assert.Equal(1, quantityInStock);
        Assert.Equal(1, movements);
    }

    /// <summary>
    /// Two adjustments that both fit are both recorded: the write lock serialises them, it does not
    /// refuse one, and the second is applied to the quantity the first left rather than to the one
    /// they both started from.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_adjustments_that_both_fit_are_both_recorded()
    {
        SeedProduct(10);

        var answers = await RaceAsync(
            () => AdjustAsync(-4, DomainStock.StockAdjustmentReason.Damaged),
            () => AdjustAsync(-4, DomainStock.StockAdjustmentReason.Damaged));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Equal([2, 6], answers.Select(answer => answer.Record!.QuantityAfter).Order());

        var (quantityInStock, movements) = Read();
        Assert.Equal(2, quantityInStock);
        Assert.Equal(2, movements);
    }

    /// <summary>What one adjustment answered: the movement it recorded, or the exception it was refused with.</summary>
    private sealed record AdjustAnswer(StockAdjustmentRecord? Record, Exception? Failure);

    private async Task<AdjustAnswer> AdjustAsync(int quantityChange, DomainStock.StockAdjustmentReason reason)
    {
        await using var db = TestAppDbContext.For(Options(), BusinessA);
        try
        {
            var record = await new AdjustStock(
                    new EfStockAdjustmentStore(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
                    new EfLocalMachineStore(db))
                .Handle(
                    ProductId,
                    new ManualStockAdjustmentInput(quantityChange, reason, "note", null, null, null),
                    CancellationToken.None);
            return new AdjustAnswer(record, null);
        }
        catch (Exception failure)
        {
            return new AdjustAnswer(null, failure);
        }
    }

    /// <summary>Releases both adjustments at the same instant and returns what each of them answered.</summary>
    private static async Task<AdjustAnswer[]> RaceAsync(params Func<Task<AdjustAnswer>>[] adjustments)
    {
        using var barrier = new Barrier(adjustments.Length);

        return await Task.WhenAll(adjustments.Select(adjustment => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await adjustment();
        })));
    }

    /// <summary>
    /// Seeds the product with an opening costed Restock, so the cost rebuild each adjustment triggers
    /// has a real history to replay and a known average cost to write the write-off off at.
    /// </summary>
    private void SeedProduct(int quantity)
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        db.Products.Add(new Product { Id = ProductId, Name = "Coke Zero", QuantityInStock = quantity });
        db.StockAdjustments.Add(new StockAdjustment
        {
            ProductId = ProductId,
            QuantityChange = quantity,
            QuantityAfter = quantity,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = OpeningUnitCost,
            EffectiveAt = RecordedAt.AddDays(-1),
        });
        db.SaveChanges();
    }

    /// <summary>The stored storage quantity, and how many write-off movements were recorded.</summary>
    private (int QuantityInStock, int Movements) Read()
    {
        using var db = TestAppDbContext.Unrestricted(Options());
        return (
            db.Products.AsNoTracking().Single(product => product.Id == ProductId).QuantityInStock,
            db.StockAdjustments.AsNoTracking().Count(adjustment =>
                adjustment.ProductId == ProductId && adjustment.Reason == StockAdjustmentReason.Damaged));
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;
}
