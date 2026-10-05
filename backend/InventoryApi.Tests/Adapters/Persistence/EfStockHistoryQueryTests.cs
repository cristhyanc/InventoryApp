using Inventory.Application.Stock;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainStock = Inventory.Domain.Stock;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The global, bounded, filterable stock-history query on <see cref="EfStockAdjustmentStore"/>
/// (issue #384), which the <c>/stock-history</c> page reads instead of asking for one product's
/// history at a time.
///
/// Relational (SQLite) rather than InMemory on purpose: everything asserted here is SQL behaviour -
/// ordering, <c>COUNT</c> against the unpaged filter, <c>LIMIT</c>/<c>OFFSET</c>, the join that
/// supplies the product name, and above all the central tenant query filter (issue #64), which this
/// adapter must rely on rather than adding a business predicate of its own.
///
/// The query reads the persisted <c>StockAdjustment</c> rows as they are: nothing here recalculates,
/// rewrites or synthesises a movement.
/// </summary>
public class EfStockHistoryQueryTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private static readonly DateTime Noon = new(2024, 6, 15, 2, 0, 0, DateTimeKind.Utc);

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static EfStockAdjustmentStore StoreFor(AppDbContext db) =>
        new(db, TestCostingUseCases.RecordMovement(db), TestCostingUseCases.Rebuild(db));

    private static StockHistoryFilter Filter(
        long? productId = null,
        DateTime? createdFromUtc = null,
        DateTime? createdBeforeUtc = null,
        DomainStock.StockAdjustmentReason? reason = null,
        long? machineId = null,
        DomainStock.StockAdjustmentSource? source = null,
        int skip = 0,
        int take = 50) =>
        new(productId, createdFromUtc, createdBeforeUtc, reason, machineId, source, skip, take);

    private static StockAdjustment Movement(
        long productId,
        DateTime createdAt,
        int quantityChange = 1,
        StockAdjustmentReason reason = StockAdjustmentReason.Restock,
        StockAdjustmentSource source = StockAdjustmentSource.Manual,
        long? machineId = null,
        decimal? unitCost = null,
        string? notes = null) =>
        new()
        {
            ProductId = productId,
            QuantityChange = quantityChange,
            QuantityAfter = quantityChange,
            Reason = reason,
            Source = source,
            MachineId = machineId,
            UnitCost = unitCost,
            Notes = notes,
            CreatedAt = createdAt,
            EffectiveAt = createdAt
        };

    /// <summary>
    /// Two products of business A with interleaved movements, plus business B's own product and
    /// movement, which must never appear in any result below.
    /// </summary>
    private static async Task SeedTwoBusinessesAsync(DbContextOptions<AppDbContext> options)
    {
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.AddRange(
                new Product { Id = 1, Name = "Coke", QuantityInStock = 10 },
                new Product { Id = 2, Name = "Chips", QuantityInStock = 10 });
            seed.StockAdjustments.AddRange(
                Movement(1, Noon.AddHours(-4), 10, StockAdjustmentReason.Restock, unitCost: 1.25m, notes: "first"),
                Movement(2, Noon.AddHours(-3), 6, StockAdjustmentReason.Restock, unitCost: 0.80m),
                Movement(1, Noon.AddHours(-2), -4, StockAdjustmentReason.MachineRefill, machineId: 9),
                Movement(2, Noon.AddHours(-1), -2, StockAdjustmentReason.MachineRefill, StockAdjustmentSource.Nayax, machineId: 11));
            await seed.SaveChangesAsync();
        }

        await using var seedB = TestAppDbContext.For(options, BusinessB);
        seedB.Products.Add(new Product { Id = 3, Name = "Other business product", QuantityInStock = 1 });
        seedB.StockAdjustments.Add(Movement(3, Noon, 1, StockAdjustmentReason.Restock, machineId: 9));
        await seedB.SaveChangesAsync();
    }

    [Fact]
    public async Task An_unfiltered_query_returns_every_product_newest_first_with_the_product_name()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).QueryHistoryAsync(Filter(), CancellationToken.None);

        Assert.Equal(4, result.TotalCount);
        Assert.Equal(
            [Noon.AddHours(-1), Noon.AddHours(-2), Noon.AddHours(-3), Noon.AddHours(-4)],
            result.Entries.Select(entry => entry.Movement.CreatedAt).ToArray());
        Assert.Equal(["Chips", "Coke", "Chips", "Coke"], result.Entries.Select(entry => entry.ProductName).ToArray());
    }

    [Fact]
    public async Task A_query_never_returns_another_businesss_products_or_movements()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using (var businessA = TestAppDbContext.For(options, BusinessA))
        {
            var result = await StoreFor(businessA).QueryHistoryAsync(Filter(), CancellationToken.None);

            Assert.Equal(4, result.TotalCount);
            Assert.DoesNotContain(3L, result.Entries.Select(entry => entry.Movement.ProductId));
            Assert.DoesNotContain("Other business product", result.Entries.Select(entry => entry.ProductName));
        }

        await using (var businessB = TestAppDbContext.For(options, BusinessB))
        {
            var result = await StoreFor(businessB).QueryHistoryAsync(Filter(), CancellationToken.None);

            Assert.Equal(1, result.TotalCount);
            Assert.Equal(3L, Assert.Single(result.Entries).Movement.ProductId);
        }

        // Asking for another business's product by id is still answered with nothing, not with that
        // business's history: the filter is applied inside the tenant-scoped query, not after it.
        await using (var businessA = TestAppDbContext.For(options, BusinessA))
        {
            var result = await StoreFor(businessA).QueryHistoryAsync(Filter(productId: 3), CancellationToken.None);

            Assert.Equal(0, result.TotalCount);
            Assert.Empty(result.Entries);
        }

        // A caller whose business could not be resolved reads nothing at all (fail closed).
        await using var denied = TestAppDbContext.Denied(options);
        var deniedResult = await StoreFor(denied).QueryHistoryAsync(Filter(), CancellationToken.None);
        Assert.Equal(0, deniedResult.TotalCount);
        Assert.Empty(deniedResult.Entries);
    }

    [Fact]
    public async Task The_product_filter_narrows_the_history_to_that_product()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).QueryHistoryAsync(Filter(productId: 1), CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.All(result.Entries, entry => Assert.Equal(1L, entry.Movement.ProductId));
        Assert.Equal([Noon.AddHours(-2), Noon.AddHours(-4)], result.Entries.Select(entry => entry.Movement.CreatedAt).ToArray());
    }

    /// <summary>
    /// The window is half-open on <c>CreatedAt</c>: the lower boundary instant is included and the
    /// upper one is not, which is what makes a Sydney day range (resolved to UTC by
    /// <c>ListStockHistory</c>) cover exactly its own days.
    /// </summary>
    [Fact]
    public async Task The_date_window_includes_its_start_instant_and_excludes_its_end_instant()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).QueryHistoryAsync(
            Filter(createdFromUtc: Noon.AddHours(-3), createdBeforeUtc: Noon.AddHours(-1)),
            CancellationToken.None);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal([Noon.AddHours(-2), Noon.AddHours(-3)], result.Entries.Select(entry => entry.Movement.CreatedAt).ToArray());
    }

    [Fact]
    public async Task The_reason_machine_and_source_filters_each_narrow_the_history()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = StoreFor(db);

        var refills = await store.QueryHistoryAsync(
            Filter(reason: DomainStock.StockAdjustmentReason.MachineRefill), CancellationToken.None);
        Assert.Equal(2, refills.TotalCount);
        Assert.All(refills.Entries, entry => Assert.Equal(DomainStock.StockAdjustmentReason.MachineRefill, entry.Movement.Reason));

        var machine9 = await store.QueryHistoryAsync(Filter(machineId: 9), CancellationToken.None);
        Assert.Equal(9L, Assert.Single(machine9.Entries).Movement.MachineId);

        var nayax = await store.QueryHistoryAsync(
            Filter(source: DomainStock.StockAdjustmentSource.Nayax), CancellationToken.None);
        Assert.Equal(DomainStock.StockAdjustmentSource.Nayax, Assert.Single(nayax.Entries).Movement.Source);

        var manual = await store.QueryHistoryAsync(
            Filter(source: DomainStock.StockAdjustmentSource.Manual), CancellationToken.None);
        Assert.Equal(3, manual.TotalCount);

        var combined = await store.QueryHistoryAsync(
            Filter(productId: 2, reason: DomainStock.StockAdjustmentReason.MachineRefill, machineId: 11),
            CancellationToken.None);
        Assert.Equal(1, combined.TotalCount);
        Assert.Equal(2L, Assert.Single(combined.Entries).Movement.ProductId);
    }

    /// <summary>
    /// A page returns at most <c>Take</c> rows from <c>Skip</c>, while <c>TotalCount</c> stays the
    /// count of the whole filtered history, so the page can say how much more there is without
    /// reading it. The rows are a partition of the ordered history: no row is served twice and none
    /// is skipped, which needs the deterministic tie-break the adapter applies - the two movements
    /// below share one <c>CreatedAt</c>.
    /// </summary>
    [Fact]
    public async Task A_bounded_page_returns_its_slice_while_the_total_counts_the_whole_filtered_history()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await using (var seed = TestAppDbContext.For(options, BusinessA))
        {
            seed.Products.Add(new Product { Id = 1, Name = "Coke", QuantityInStock = 10 });
            for (var index = 0; index < 5; index++)
            {
                seed.StockAdjustments.Add(Movement(1, Noon.AddHours(-index), index + 1));
            }

            // Two movements recorded in the same instant, so the ordering cannot rely on CreatedAt alone.
            seed.StockAdjustments.Add(Movement(1, Noon, 99));
            await seed.SaveChangesAsync();
        }

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = StoreFor(db);

        var first = await store.QueryHistoryAsync(Filter(take: 2), CancellationToken.None);
        var second = await store.QueryHistoryAsync(Filter(skip: 2, take: 2), CancellationToken.None);
        var third = await store.QueryHistoryAsync(Filter(skip: 4, take: 2), CancellationToken.None);
        var beyond = await store.QueryHistoryAsync(Filter(skip: 6, take: 2), CancellationToken.None);

        Assert.Equal(6, first.TotalCount);
        Assert.Equal(6, beyond.TotalCount);
        Assert.Equal(2, first.Entries.Count);
        Assert.Equal(2, second.Entries.Count);
        Assert.Equal(2, third.Entries.Count);
        Assert.Empty(beyond.Entries);

        var ids = first.Entries.Concat(second.Entries).Concat(third.Entries)
            .Select(entry => entry.Movement.Id)
            .ToArray();
        Assert.Equal(6, ids.Distinct().Count());
        Assert.Equal(ids.OrderByDescending(id => id).ToArray()[0], ids[0]);
    }

    [Fact]
    public async Task A_queried_movement_carries_the_persisted_costing_and_note_fields_unchanged()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);
        await SeedTwoBusinessesAsync(options);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var result = await StoreFor(db).QueryHistoryAsync(
            Filter(createdFromUtc: Noon.AddHours(-4), createdBeforeUtc: Noon.AddHours(-3)),
            CancellationToken.None);

        var entry = Assert.Single(result.Entries);
        Assert.Equal("Coke", entry.ProductName);
        Assert.Equal(10, entry.Movement.QuantityChange);
        Assert.Equal(10, entry.Movement.QuantityAfter);
        Assert.Equal(1.25m, entry.Movement.UnitCost);
        Assert.Equal("first", entry.Movement.Notes);
        Assert.Equal(DomainStock.StockAdjustmentReason.Restock, entry.Movement.Reason);
        Assert.Equal(DomainStock.StockAdjustmentSource.Manual, entry.Movement.Source);
    }
}
