using InventoryApi.Data;
using Inventory.Application.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// The tenant boundary around Nayax sales imports (issue #64).
///
/// A Nayax <c>TransactionID</c> is a remote identifier owned by Nayax, not by this application,
/// so two operator accounts can legitimately report the same number. The import de-duplicates by
/// that value, which makes it the one place where a shared external id could make one business
/// overwrite another business's financial row.
///
/// <see cref="Adapters.Persistence.BusinessScopedUniquenessTests"/> proves the database permits
/// the collision and keeps the two rows apart. These tests prove the *import path* does the right
/// thing with it: the existing-row lookup is tenant-scoped, so a colliding id is an insert for
/// the importing business rather than an update of somebody else's sale.
///
/// Business A is 1 and business B is 2, matching the other tenancy tests.
/// </summary>
public sealed class NayaxImportTenantIsolationTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long SharedTransactionId = 5150;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public NayaxImportTenantIsolationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The headline case: business A already holds the transaction id that business B is about to
    /// import. B must get its own row, and A's settlement value must be untouched - an update here
    /// would silently rewrite another business's revenue.
    /// </summary>
    [Fact]
    public async Task Importing_a_transaction_id_another_business_already_has_creates_a_new_row()
    {
        await SeedSaleAsync(BusinessA, settlementValue: 5m);

        await using (var b = TestAppDbContext.For(_options, BusinessB))
        {
            var result = await CreateImportService(b).ImportNayaxSalesFromExcelAsync(
                SalesCsv(SharedTransactionId, settlementValue: 99m));

            Assert.Equal(1, result.Imported);
            Assert.Equal(0, result.Updated);
        }

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        var sales = await unrestricted.NayaxSales
            .Where(sale => sale.TransactionID == SharedTransactionId)
            .OrderBy(sale => sale.BusinessId)
            .ToListAsync();

        Assert.Equal(2, sales.Count);
        Assert.Equal(BusinessA, sales[0].BusinessId);
        Assert.Equal(5m, sales[0].SettlementValue);
        Assert.Equal(BusinessB, sales[1].BusinessId);
        Assert.Equal(99m, sales[1].SettlementValue);
    }

    /// <summary>
    /// The same import run against the owning business must still update in place, otherwise the
    /// isolation above would have been bought by breaking de-duplication for everybody.
    /// </summary>
    [Fact]
    public async Task Importing_a_transaction_id_the_same_business_already_has_updates_it()
    {
        await SeedSaleAsync(BusinessA, settlementValue: 5m);

        await using (var a = TestAppDbContext.For(_options, BusinessA))
        {
            var result = await CreateImportService(a).ImportNayaxSalesFromExcelAsync(
                SalesCsv(SharedTransactionId, settlementValue: 42m));

            Assert.Equal(0, result.Imported);
            Assert.Equal(1, result.Updated);
        }

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        var sale = Assert.Single(await unrestricted.NayaxSales.ToListAsync());
        Assert.Equal(BusinessA, sale.BusinessId);
        Assert.Equal(42m, sale.SettlementValue);
    }

    /// <summary>
    /// Import de-duplication by file hash is also per business: the same file imported by two
    /// businesses is two imports, not a duplicate. One business must never be told its own import
    /// already happened because another business imported the same bytes first.
    /// </summary>
    [Fact]
    public async Task An_imported_file_recorded_by_another_business_is_not_seen_as_a_duplicate()
    {
        await using (var a = TestAppDbContext.For(_options, BusinessA))
        {
            a.ImportedFiles.Add(new ImportedFile { FileName = "statement.xml", FileHash = "shared-hash" });
            await a.SaveChangesAsync();
        }

        await using (var b = TestAppDbContext.For(_options, BusinessB))
        {
            Assert.Empty(await b.ImportedFiles.Where(file => file.FileHash == "shared-hash").ToListAsync());

            b.ImportedFiles.Add(new ImportedFile { FileName = "statement.xml", FileHash = "shared-hash" });
            await b.SaveChangesAsync();
        }

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, await unrestricted.ImportedFiles.CountAsync(file => file.FileHash == "shared-hash"));
    }

    private async Task SeedSaleAsync(int businessId, decimal settlementValue)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = SharedTransactionId,
            MachineID = 1,
            NayaxProductId = 999,
            ProductName = "Snack",
            SettlementValue = settlementValue,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineAuthorizationTime = new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
    }

    private static IFormFile SalesCsv(long transactionId, decimal settlementValue)
    {
        var content =
            "TransactionID,TransactionStatusId,MachineID,NayaxProductId,SettlementValue,ProductName,MachineAuthorizationTime\n"
            + $"{transactionId},12,1,999,{settlementValue},Snack,2/9/2026 2:30:00 PM";
        var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        return new FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }

    private static ImportService CreateImportService(AppDbContext db)
    {
        var rebuild = new InventoryCostRebuildService(db);
        return new ImportService(
            db,
            Mock.Of<IWebHostEnvironment>(),
            Mock.Of<ILogger<ImportService>>(),
            Mock.Of<INayaxLynxClient>(),
            new SaleCostingService(db, rebuild),
            rebuild);
    }
}
