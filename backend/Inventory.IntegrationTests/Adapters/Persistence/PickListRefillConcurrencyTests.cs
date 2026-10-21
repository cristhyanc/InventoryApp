using Inventory.Application.PickList;
using Inventory.Domain.Stock;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

// Both layers declare this enum (the shared wire vocabulary documented on
// StockAdjustmentResponseMapper); the persisted rows this test writes and reads carry the
// persistence one.
using StockAdjustmentReason = Inventory.Infrastructure.Models.StockAdjustmentReason;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The batch refill under real concurrency (issue #743).
///
/// <para>This is the test the design exists for. "Is there enough in storage?" and "has this key
/// already recorded a refill?" are both check-then-write rules, and a check-then-write rule that is
/// not serialised is not a rule: two pickers both read "enough stock" and both deduct, leaving
/// storage reporting units that are not there, and two copies of one submission both read "no
/// refill under this key" and both record one, deducting the shelf twice for one pick. Neither
/// outcome is cosmetic - they are the two ways a refill endpoint silently corrupts an inventory
/// quantity.</para>
///
/// <para>It therefore runs against a real SQLite <strong>file</strong> with two independent
/// connections, because that is the only way to have two genuine writers: a shared in-memory
/// database is one connection, and InMemory has no locking at all. The two requests are released
/// together at a barrier, so the window the rules have to close is actually opened. It is the same
/// shape as <see cref="BusinessMembershipWriteTransactionTests"/> (issue #522), for the same
/// reason.</para>
/// </summary>
public class PickListRefillConcurrencyTests : IDisposable
{
    private const int BusinessA = 1;
    private const long ProductId = 100;

    private static readonly DateTime RecordedAt = new(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-pick-list-refills-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public PickListRefillConcurrencyTests()
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

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>
    /// Seeds the product with an opening costed Restock, so the cost rebuild each refill triggers
    /// has a real history to replay.
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
            UnitCost = 2m,
            EffectiveAt = RecordedAt.AddDays(-1),
        });
        db.SaveChanges();
    }

    private static RecordPickListRefill UseCaseFor(AppDbContext db) =>
        new(
            new EfPickListRefillStore(
                db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db)),
            new EfPickListStorageStockStore(db),
            new EfLocalMachineStore(db),
            new FakeClock(RecordedAt));

    /// <summary>
    /// Runs the refills released at the same instant and returns each one's answer - the attempt it
    /// produced, or the exception it threw.
    /// </summary>
    private async Task<(PickListRefillAttempt? Attempt, Exception? Failure)[]> RaceAsync(
        params PickListRefillInput[] requests)
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
                    return (await UseCaseFor(db).Handle(request, CancellationToken.None), (Exception?)null);
                }
                catch (Exception exception)
                {
                    return ((PickListRefillAttempt?)null, exception);
                }
            }))
            .ToArray();

        return await Task.WhenAll(attempts);
    }

    private static PickListRefillInput Input(string key, int quantity) =>
        new(key, null, null, [new PickListRefillRequestRow(ProductId, quantity)]);

    private (int QuantityInStock, int RefillCount, int MovementCount) Read()
    {
        using var db = TestAppDbContext.For(Options(), BusinessA);
        return (
            db.Products.AsNoTracking().Single(product => product.Id == ProductId).QuantityInStock,
            db.PickListRefills.AsNoTracking().Count(),
            db.StockAdjustments.AsNoTracking()
                .Count(adjustment => adjustment.Reason == StockAdjustmentReason.MachineRefill));
    }

    /// <summary>
    /// Two copies of one submission - a client that retried before the first response arrived -
    /// deduct the shelf once. One of them records it and the other is answered with the recorded
    /// result; which is which is a race, and both answers are the same refill.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_submissions_of_one_key_record_once()
    {
        const string key = "one-submission-sent-twice";
        SeedProduct(10);

        var answers = await RaceAsync(Input(key, 3), Input(key, 3));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.Single(answers.Where(answer => answer.Attempt!.Outcome == PickListRefillOutcome.Recorded));
        Assert.Single(answers.Where(answer => answer.Attempt!.Outcome == PickListRefillOutcome.Replayed));

        // Both callers were told about the same refill, movement ids included. Compared field by
        // field because the record's own equality compares its row list by reference, and the
        // replayed answer reads its rows back from the database.
        var first = answers[0].Attempt!.Record!;
        var second = answers[1].Attempt!.Record!;
        Assert.Equal(first.IdempotencyKey, second.IdempotencyKey);
        Assert.Equal(first.MachineId, second.MachineId);
        Assert.Equal(first.Notes, second.Notes);
        Assert.Equal(first.RecordedAtUtc, second.RecordedAtUtc);
        Assert.Equal(first.Rows, second.Rows);

        var (quantityInStock, refills, movements) = Read();
        Assert.Equal(7, quantityInStock);
        Assert.Equal(1, refills);
        Assert.Equal(1, movements);
    }

    /// <summary>
    /// The acceptance criterion with teeth: two different requests that each pass the storage check
    /// on their own, but together take more than the shelf holds. Exactly one may succeed, and the
    /// loser has to be told the same thing it would have been told sequentially - the established
    /// not-enough-stock sentence, against the quantity the winner left - and must leave nothing
    /// behind.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_refills_that_together_exceed_storage_leave_exactly_one()
    {
        SeedProduct(5);

        var answers = await RaceAsync(Input("first-pick", 4), Input("second-pick", 4));

        Assert.All(answers, answer => Assert.Null(answer.Failure));

        var recorded = Assert.Single(
            answers.Where(answer => answer.Attempt!.Outcome == PickListRefillOutcome.Recorded));
        var rejected = Assert.Single(
            answers.Where(answer => answer.Attempt!.Outcome == PickListRefillOutcome.Rejected));

        Assert.Equal(4, Assert.Single(recorded.Attempt!.Record!.Rows).QuantityTaken);

        // Refused against the quantity the winner actually left, not the one it started from.
        Assert.Equal(
            PickListRefillPolicy.NotEnoughStockMessage(1),
            Assert.Single(rejected.Attempt!.Rejection!.Rows).Message);

        var (quantityInStock, refills, movements) = Read();
        Assert.Equal(1, quantityInStock);
        Assert.Equal(1, refills);
        Assert.Equal(1, movements);
    }

    /// <summary>
    /// Two refills that both fit are both recorded: the write lock serialises them, it does not
    /// refuse one. A picker waiting for another picker's transaction is not an error.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_refills_that_both_fit_are_both_recorded()
    {
        SeedProduct(10);

        var answers = await RaceAsync(Input("first-pick", 4), Input("second-pick", 4));

        Assert.All(answers, answer => Assert.Null(answer.Failure));
        Assert.All(
            answers,
            answer => Assert.Equal(PickListRefillOutcome.Recorded, answer.Attempt!.Outcome));

        var (quantityInStock, refills, movements) = Read();
        Assert.Equal(2, quantityInStock);
        Assert.Equal(2, refills);
        Assert.Equal(2, movements);
    }
}
