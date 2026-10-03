using Inventory.Application.Imports;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// <see cref="EfImportedReimbursementStore"/> (issue #299) is the pending-XML import's only
/// touchpoint with persistence. These are relational SQLite tests, not InMemory ones, because
/// what they prove depends on real constraints and on the central tenant query filter: the
/// reimbursement graph persists with its relationships intact, the file-hash duplicate check is
/// scoped to the caller's business, and two businesses importing the same file neither block
/// nor see each other (AGENTS.md § Tenant ownership: uniqueness over an externally supplied
/// value is per business).
/// </summary>
public class EfImportedReimbursementStoreTests
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private static readonly DateTime ImportedAt = new(2026, 10, 3, 1, 30, 0, DateTimeKind.Utc);

    private static async Task<DbContextOptions<AppDbContext>> CreateSqliteAsync(SqliteConnection connection)
    {
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return options;
    }

    private static PendingReimbursementXmlFile File(
        string fileName = "august.xml", string contentHash = "HASH-A", string customerId = "C-1") =>
        new(fileName, contentHash, [new ImportedReimbursementFacts
        {
            ReportType = "reimbursement",
            CustomerId = customerId,
            CompanyName = "Vend Co",
            IsReimbursement = true,
            ReimbursementStartDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
            ReimbursementEndDate = new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            Total = 1234.56m,
            Devices = [new ImportedReimbursementDeviceFacts { HardwareSerial = "HW-1", NetAmount = 96.95m }],
            DevicePayments = [new ImportedDevicePaymentFacts { PaymentMethodDescription = "Credit Card", TotalSum = 90.40m }],
            Fees = [new ImportedFeeFacts { TotalSum = 10m, TotalSumWithVat = 11m, VatPercentage = 10m }],
            PaymentMethods = [new ImportedPaymentMethodFacts { PaymentMethodDescription = "Visa", IsNayaxReimbursement = true }],
        }]);

    [Fact]
    public async Task ImportAsync_persists_the_file_with_its_whole_reimbursement_graph()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await new EfImportedReimbursementStore(db).ImportAsync(File(), ImportedAt, CancellationToken.None);
        }

        await using var read = TestAppDbContext.For(options, BusinessA);
        var file = await read.ImportedFiles
            .Include(f => f.Reimbursements).ThenInclude(r => r.Devices)
            .Include(f => f.Reimbursements).ThenInclude(r => r.DevicePayments)
            .Include(f => f.Reimbursements).ThenInclude(r => r.Fees)
            .Include(f => f.Reimbursements).ThenInclude(r => r.PaymentMethods)
            .SingleAsync();

        Assert.Equal("august.xml", file.FileName);
        Assert.Equal("HASH-A", file.FileHash);
        Assert.Equal(ImportedAt, file.ImportedAt);

        var reimbursement = Assert.Single(file.Reimbursements);
        Assert.Equal(file.Id, reimbursement.ImportedFileId);
        Assert.Equal("C-1", reimbursement.CustomerId);
        Assert.True(reimbursement.IsReimbursement);
        Assert.Equal(1234.56m, reimbursement.Total);
        Assert.Equal("HW-1", Assert.Single(reimbursement.Devices).HardwareSerial);
        Assert.Equal(90.40m, Assert.Single(reimbursement.DevicePayments).TotalSum);
        var fee = Assert.Single(reimbursement.Fees);
        Assert.Equal(10m, fee.TotalSum);
        Assert.Equal(11m, fee.TotalSumWithVat);
        Assert.True(Assert.Single(reimbursement.PaymentMethods).IsNayaxReimbursement);
    }

    [Fact]
    public async Task HasFileWithContentHashAsync_answers_true_only_for_a_hash_this_business_imported()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using var db = TestAppDbContext.For(options, BusinessA);
        var store = new EfImportedReimbursementStore(db);
        Assert.False(await store.HasFileWithContentHashAsync("HASH-A", CancellationToken.None));

        await store.ImportAsync(File(), ImportedAt, CancellationToken.None);

        Assert.True(await store.HasFileWithContentHashAsync("HASH-A", CancellationToken.None));
        Assert.False(await store.HasFileWithContentHashAsync("HASH-B", CancellationToken.None));
    }

    /// <summary>
    /// Two businesses may legitimately receive the same reimbursement file. Business B must not
    /// be told its own first import is a duplicate because business A imported the same bytes,
    /// and the per-business unique index must allow both rows.
    /// </summary>
    [Fact]
    public async Task The_same_file_hash_imported_by_another_business_is_not_a_duplicate_here()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var businessA = TestAppDbContext.For(options, BusinessA))
        {
            await new EfImportedReimbursementStore(businessA)
                .ImportAsync(File(customerId: "A-1"), ImportedAt, CancellationToken.None);
        }

        await using (var businessB = TestAppDbContext.For(options, BusinessB))
        {
            var store = new EfImportedReimbursementStore(businessB);

            Assert.False(await store.HasFileWithContentHashAsync("HASH-A", CancellationToken.None));
            await store.ImportAsync(File(customerId: "B-1"), ImportedAt, CancellationToken.None);
            Assert.True(await store.HasFileWithContentHashAsync("HASH-A", CancellationToken.None));
        }

        await using var unrestricted = TestAppDbContext.Unrestricted(options);
        Assert.Equal(2, await unrestricted.ImportedFiles.CountAsync(f => f.FileHash == "HASH-A"));
    }

    /// <summary>
    /// The central tenant filter, exercised through this adapter: neither the imported file nor
    /// any row of its reimbursement graph is readable by the other business.
    /// </summary>
    [Fact]
    public async Task Another_businesss_imported_reimbursements_are_invisible_and_stamped_to_their_own_business()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var businessB = TestAppDbContext.For(options, BusinessB))
        {
            await new EfImportedReimbursementStore(businessB)
                .ImportAsync(File(fileName: "b.xml", contentHash: "HASH-B", customerId: "B-1"), ImportedAt, CancellationToken.None);
        }

        await using (var businessA = TestAppDbContext.For(options, BusinessA))
        {
            Assert.Empty(await businessA.ImportedFiles.ToListAsync());
            Assert.Empty(await businessA.ImportedReimbursements.ToListAsync());
            Assert.Empty(await businessA.ImportedReimbursementDevices.ToListAsync());
            Assert.Empty(await businessA.ImportedDevicePayments.ToListAsync());
            Assert.Empty(await businessA.ImportedFees.ToListAsync());
            Assert.Empty(await businessA.ImportedPaymentMethods.ToListAsync());
        }

        await using var unrestricted = TestAppDbContext.Unrestricted(options);
        Assert.Equal(BusinessB, (await unrestricted.ImportedFiles.SingleAsync()).BusinessId);
        Assert.Equal(BusinessB, (await unrestricted.ImportedReimbursements.SingleAsync()).BusinessId);
        Assert.Equal(BusinessB, (await unrestricted.ImportedReimbursementDevices.SingleAsync()).BusinessId);
        Assert.Equal(BusinessB, (await unrestricted.ImportedDevicePayments.SingleAsync()).BusinessId);
        Assert.Equal(BusinessB, (await unrestricted.ImportedFees.SingleAsync()).BusinessId);
        Assert.Equal(BusinessB, (await unrestricted.ImportedPaymentMethods.SingleAsync()).BusinessId);
    }

    /// <summary>
    /// A caller whose business could not be resolved reads nothing and writes nothing: the
    /// import fails closed rather than persisting unowned reimbursement data.
    /// </summary>
    [Fact]
    public async Task An_unresolved_business_imports_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using var denied = TestAppDbContext.Denied(options);
        var store = new EfImportedReimbursementStore(denied);

        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => store.ImportAsync(File(), ImportedAt, CancellationToken.None));

        await using var unrestricted = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await unrestricted.ImportedFiles.ToListAsync());
    }

    /// <summary>
    /// The write is one unit of work: a file whose reimbursement graph cannot be persisted
    /// leaves no half-imported file behind for the hash check to then treat as done.
    /// </summary>
    [Fact]
    public async Task A_second_import_of_the_same_hash_in_one_business_fails_without_persisting_a_second_file()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        var options = await CreateSqliteAsync(connection);

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await new EfImportedReimbursementStore(db).ImportAsync(File(), ImportedAt, CancellationToken.None);
        }

        await using (var db = TestAppDbContext.For(options, BusinessA))
        {
            await Assert.ThrowsAsync<DbUpdateException>(
                () => new EfImportedReimbursementStore(db).ImportAsync(File(), ImportedAt, CancellationToken.None));
        }

        await using var read = TestAppDbContext.For(options, BusinessA);
        Assert.Equal(1, await read.ImportedFiles.CountAsync());
        Assert.Equal(1, await read.ImportedReimbursements.CountAsync());
    }
}
