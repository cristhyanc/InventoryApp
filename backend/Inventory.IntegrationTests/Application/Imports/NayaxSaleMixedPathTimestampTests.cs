using System.Text;
using System.Text.Json;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #380 (owner decision on PR #392): one transaction reaching the database through both
/// ingestion paths - the latest-sales synchronization and an uploaded Nayax export - keeps the
/// authoritative instant it was given.
///
/// The latest-sales payload carries <c>AuthorizationDateTimeGMT</c>, so a synchronized sale holds a
/// true UTC instant. An uploaded export may not carry that column, and its own
/// <c>MachineAuthorizationTime</c> column has no published timezone contract. Such an export may
/// still enrich the stored sale (status, product, cost price) under the existing rules, but it must
/// never overwrite the stored instant with its unverified value, and a GMT value the export carries
/// but that cannot be read is not permission to fall back to that column either. Only a usable GMT
/// value may correct a stored instant, through the existing import and cost-rebuild path.
///
/// Real SQLite throughout: the instant is what every report later reads back.
/// </summary>
public class NayaxSaleMixedPathTimestampTests
{
    private const long TransactionId = 5001;
    private const long MachineId = 942488501;

    /// <summary>Sunday 4 October 2026 23:30 in Sydney (AEDT, +11): 12:30Z.</summary>
    private static readonly DateTime AuthoritativeUtc = new(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);

    /// <summary>The same sale's machine-local wall clock, as an export's own column writes it.</summary>
    private const string MachineLocalExportValue = "4/10/2026 11:30:00 PM";

    /// <summary>The machine-local ticks misread as an instant - eleven hours late.</summary>
    private static readonly DateTime MachineLocalTicksUtc = new(2026, 10, 4, 23, 30, 0, DateTimeKind.Utc);

    /// <summary>
    /// The regression: live sync stores the GMT instant, a legacy export without the GMT column
    /// re-imports the same transaction, and a later live sync sees it again. The instant stays the
    /// authoritative one throughout, the export's enrichment still lands, and there is one sale.
    /// </summary>
    [Fact]
    public async Task Live_sync_then_an_export_without_GMT_then_live_sync_keeps_the_authoritative_instant()
    {
        await using var connection = await CreateSqliteAsync();
        var payload = LastSales();

        await using (var db = Context(connection))
            await SyncStore(db).PersistLatestSalesAsync(payload, CancellationToken.None);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                "TransactionID,TransactionStatusId,MachineID,SettlementValue,ProductName,MachineAuthorizationTime\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},3.50,Water 600ml,{MachineLocalExportValue}"));

        await using (var db = Context(connection))
            await SyncStore(db).PersistLatestSalesAsync(payload, CancellationToken.None);

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(AuthoritativeUtc, sale.MachineAuthorizationTime);
        Assert.Equal("Water 600ml", sale.ProductName);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
    }

    /// <summary>
    /// A blank or unreadable GMT cell on a transaction already held: the stored instant is preserved
    /// and the row's other facts still enrich the sale. An unreadable authoritative value is never
    /// read as "no GMT, so use the machine-local column".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-an-instant")]
    [InlineData("31/31/2026 9:00:00 AM")]
    public async Task A_blank_or_malformed_GMT_value_never_overwrites_a_stored_instant(string gmtCell)
    {
        await using var connection = await CreateSqliteAsync();
        await using (var db = Context(connection))
            await SyncStore(db).PersistLatestSalesAsync(LastSales(), CancellationToken.None);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                "TransactionID,TransactionStatusId,MachineID,SettlementValue,ProductName,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Refunded},{MachineId},3.50,Water 600ml,{MachineLocalExportValue},{gmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(AuthoritativeUtc, sale.MachineAuthorizationTime);
        Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);
        Assert.Equal("Water 600ml", sale.ProductName);
    }

    /// <summary>
    /// A new transaction whose GMT value is present but unreadable is skipped, not imported at the
    /// unverified machine-local value: the export claimed an authoritative instant and it could not be
    /// read, so there is nothing trustworthy to store.
    /// </summary>
    [Fact]
    public async Task A_new_transaction_with_a_malformed_GMT_value_is_skipped_rather_than_imported_at_a_fallback()
    {
        await using var connection = await CreateSqliteAsync();

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                "TransactionID,TransactionStatusId,MachineID,SettlementValue,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},3.50,{MachineLocalExportValue},not-an-instant"));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 1), upload);
        await using var read = Context(connection);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A usable GMT value corrects an older stored instant - here the machine-local ticks an earlier
    /// sync stored as UTC - through the existing import path: the instant moves, the sale is
    /// re-costed, and the product's cost replay restarts from the earlier of the old and new instants
    /// so both positions in its history are replayed.
    /// </summary>
    [Fact]
    public async Task A_usable_GMT_value_corrects_an_older_incorrect_instant_and_replays_the_affected_product()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            SeedProductWithBaseline(seed);
            seed.NayaxSales.Add(StoredSale(MachineLocalTicksUtc));
            await seed.SaveChangesAsync();
        }

        var rebuild = new Mock<IRebuildProductCost>();
        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db, rebuild.Object).Handle(Csv(GmtExport()));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(AuthoritativeUtc, sale.MachineAuthorizationTime);
        rebuild.Verify(
            x => x.RebuildAsync(10, AuthoritativeUtc, false, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Replaying the correcting export is idempotent: the instant stays corrected, there is still one
    /// sale, and a later live sync of the same transaction changes nothing.
    /// </summary>
    [Fact]
    public async Task Replaying_a_correcting_export_and_a_later_live_sync_leave_the_corrected_instant_alone()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var seed = Context(connection))
        {
            SeedProductWithBaseline(seed);
            seed.NayaxSales.Add(StoredSale(MachineLocalTicksUtc));
            await seed.SaveChangesAsync();
        }

        await using (var db = Context(connection))
            await Import(db).Handle(Csv(GmtExport()));
        await using (var db = Context(connection))
            await Import(db).Handle(Csv(GmtExport()));
        await using (var db = Context(connection))
            await SyncStore(db).PersistLatestSalesAsync(LastSales(), CancellationToken.None);

        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(AuthoritativeUtc, sale.MachineAuthorizationTime);
    }

    /// <summary>
    /// A legacy export of a transaction not yet held still imports at its own
    /// <c>MachineAuthorizationTime</c> column, read exactly as earlier imports read it. Its timezone is
    /// unverified (Nayax publishes no export contract), so this is recorded as an open policy question
    /// in docs/architecture.md rather than converted with an invented timezone.
    /// </summary>
    [Fact]
    public async Task A_new_transaction_from_an_export_without_GMT_is_imported_at_its_own_unconverted_value()
    {
        await using var connection = await CreateSqliteAsync();

        await using (var db = Context(connection))
            await Import(db).Handle(Csv(
                "TransactionID,TransactionStatusId,MachineID,SettlementValue,MachineAuthorizationTime\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},3.50,{MachineLocalExportValue}"));

        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(new DateTime(2026, 10, 4, 23, 30, 0), sale.MachineAuthorizationTime);
    }

    private static string GmtExport() =>
        "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
        $"{TransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},10,3.50,Water,{MachineLocalExportValue},2026-10-04T12:30:00Z";

    private static void SeedProductWithBaseline(AppDbContext db)
    {
        db.Products.Add(new Product { Id = 10, Name = "Water", QuantityInStock = 10, AverageUnitCost = 1m });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = 10m,
            AverageUnitCost = 1m,
            CutoffAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
        });
    }

    private static NayaxSales StoredSale(DateTime instant) => new()
    {
        TransactionID = TransactionId,
        MachineID = MachineId,
        NayaxProductId = 10,
        ProductName = "Water",
        SettlementValue = 3.5m,
        TransactionStatusId = NayaxTransactionStatusIds.Completed,
        MachineAuthorizationTime = instant
    };

    private static List<NayaxLastSalesReport> LastSales() =>
        JsonSerializer.Deserialize<List<NayaxLastSalesReport>>($$"""
            [
              {
                "TransactionID": {{TransactionId}},
                "MachineID": {{MachineId}},
                "SettlementValue": 3.50,
                "PaymentMethod": "Credit Card",
                "ProductName": "Water",
                "AuthorizationDateTimeGMT": "2026-10-04T12:30:00.000Z",
                "MachineAuthorizationTime": "2026-10-04T23:30:00"
              }
            ]
            """)!;

    private static EfLatestNayaxSalesStore SyncStore(AppDbContext db)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new EfLatestNayaxSalesStore(db, TestCostingUseCases.CostSale(db, rebuild), rebuild);
    }

    private static ImportNayaxSales Import(AppDbContext db, IRebuildProductCost? rebuild = null)
    {
        var costRebuild = rebuild ?? TestCostingUseCases.Rebuild(db);
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, costRebuild),
            costRebuild);
    }

    private static NayaxSalesFileInput Csv(string content) =>
        new("sales.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    private static async Task<SqliteConnection> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var setup = Context(connection);
        await setup.Database.EnsureCreatedAsync();
        return connection;
    }

    private static AppDbContext Context(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}
