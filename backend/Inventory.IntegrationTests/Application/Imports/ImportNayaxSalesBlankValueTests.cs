using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.NayaxSqs;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// Issue #1298: a blank cell or a missing column in an uploaded Nayax sales file never overwrites a
/// stored sale, and a non-blank value the reader cannot read is an invalid row rather than a blank
/// one.
///
/// The file stays the source of truth for a transaction's status and amount - a value that is
/// present and valid still replaces the stored one in either direction - so what is pinned here is
/// only the difference between "the file says something else" and "the file says nothing at all".
/// Before this change the reader turned a blank or missing amount into <c>0</c> and a blank status
/// into <c>null</c>, and the update wrote both unconditionally, so uploading a file whose export
/// lacked those columns wiped a completed $3.50 sale to no status and $0 and dropped it out of
/// every report.
///
/// Real SQLite throughout, not EF InMemory: the stored amount and status are what Bookkeeping and
/// every other report read back, through the same relational column types and query translation.
/// </summary>
public class ImportNayaxSalesBlankValueTests
{
    private const long TransactionId = 7001;
    private const long MachineId = 942488501;
    private const long NewTransactionId = 7002;

    /// <summary>Sunday 4 October 2026 23:30 in Sydney (AEDT, +11): 12:30Z.</summary>
    private static readonly DateTime StoredInstant = new(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);

    private const string GmtCell = "2026-10-04T12:30:00Z";

    /// <summary>Every column the import reads, so a cell can be left blank one at a time.</summary>
    private const string FullHeader =
        "TransactionID,TransactionStatusId,MachineID,SettlementValue,PaymentMethod,ProductName,MachineName,AuthorizationDateTimeGMT";

    /// <summary>
    /// The reported case: a stored completed $3.50 sale and a file row whose status and amount cells
    /// are blank. The stored values stay, the row is still counted as updated, and the import says
    /// one row kept a stored value.
    /// </summary>
    [Fact]
    public async Task A_blank_status_and_amount_leave_the_stored_sale_unchanged()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{TransactionId},,{MachineId},,,,,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0, 1), upload);
        Assert.Empty(upload.InvalidRows);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(3.50m, sale.SettlementValue);
        Assert.Equal("Credit Card", sale.PaymentMethod);
        Assert.Equal("Water 600ml", sale.ProductName);
        Assert.Equal("Machine A", sale.MachineName);
        // The sale the kept name and status still belong to, and the rule Bookkeeping counts a sale by.
        Assert.Equal(10, sale.ProductId);
        Assert.True(NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification));
    }

    /// <summary>
    /// The same sale against a file exported without the status, amount and payment-method columns
    /// at all. A missing column says nothing about a transaction, exactly as a blank cell does.
    /// </summary>
    [Fact]
    public async Task An_export_without_the_status_and_amount_columns_leaves_them_unchanged()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                "TransactionID,MachineID,AuthorizationDateTimeGMT\n" +
                $"{TransactionId},{MachineId},{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0, 1), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(3.50m, sale.SettlementValue);
        Assert.Equal("Credit Card", sale.PaymentMethod);
        Assert.True(NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification));
    }

    /// <summary>
    /// An explicit <c>0</c> is a value the file states, not an absence: it replaces the stored
    /// amount, and nothing is reported as kept.
    /// </summary>
    [Fact]
    public async Task An_explicit_zero_amount_replaces_the_stored_amount()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},0,Credit Card,Water 600ml,Machine A,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0, 0), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(0m, sale.SettlementValue);
    }

    /// <summary>
    /// The file stays the source of truth for a status that is present: it replaces the stored one
    /// in either direction, completed to refunded and refunded back to completed.
    /// </summary>
    [Theory]
    [InlineData(NayaxTransactionStatusIds.Completed, NayaxTransactionStatusIds.Refunded)]
    [InlineData(NayaxTransactionStatusIds.Refunded, NayaxTransactionStatusIds.Completed)]
    public async Task A_status_the_file_states_replaces_the_stored_one(int storedStatus, int fileStatus)
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection, storedStatus);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{TransactionId},{fileStatus},{MachineId},4.25,Cash,Water 600ml,Machine A,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 1, 0, 0), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(fileStatus, sale.TransactionStatusId);
        Assert.Equal(4.25m, sale.SettlementValue);
        Assert.Equal("Cash", sale.PaymentMethod);
    }

    /// <summary>
    /// A non-blank amount the reader cannot read is never treated as blank: the row is not applied
    /// at all, the stored sale is untouched, and the result names the row and the transaction so an
    /// operator can find it in the file.
    /// </summary>
    [Theory]
    [InlineData("$3.50")]
    [InlineData("abc")]
    public async Task A_malformed_amount_is_reported_as_invalid_and_changes_nothing(string amountCell)
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{TransactionId},{NayaxTransactionStatusIds.Refunded},{MachineId},{amountCell},Cash,Other,Machine B,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 0, 0) { InvalidRows = [new(2, TransactionId)] }, upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(3.50m, sale.SettlementValue);
        Assert.Equal("Credit Card", sale.PaymentMethod);
        Assert.Equal("Machine A", sale.MachineName);
    }

    /// <summary>
    /// The same for a status that is not a number, and with a valid row beside it: a file is not
    /// refused as a whole, and the invalid row's own number is the one the file shows.
    /// </summary>
    [Fact]
    public async Task A_malformed_status_is_reported_as_invalid_while_the_valid_rows_import()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},1.50,Cash,Water 600ml,Machine A,{GmtCell}\n" +
                $"{TransactionId},not-a-status,{MachineId},9.99,Cash,Other,Machine B,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(1, 0, 0, 0) { InvalidRows = [new(3, TransactionId)] }, upload);
        await using var read = Context(connection);
        var sale = Assert.Single(
            await read.NayaxSales.AsNoTracking().Where(x => x.TransactionID == TransactionId).ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(3.50m, sale.SettlementValue);
    }

    /// <summary>
    /// A transaction this business does not hold yet has no stored amount to keep, and <c>$0</c> is
    /// not a reading of a blank cell: the row is skipped and counted, never stored as a zero sale.
    /// </summary>
    [Fact]
    public async Task A_new_sale_with_a_blank_amount_is_skipped_rather_than_stored_as_zero()
    {
        await using var connection = await CreateSqliteAsync();

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},,Cash,Water 600ml,Machine A,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 0, 1, 0), upload);
        await using var read = Context(connection);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The kept count is a count of rows, not of fields: a row that kept five stored values counts
    /// once, so the number an operator sees is "rows this file said nothing new about".
    /// </summary>
    [Fact]
    public async Task A_row_that_keeps_several_stored_values_is_counted_once()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);
        await SeedStoredSaleAsync(connection, transactionId: NewTransactionId);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{TransactionId},,{MachineId},,,,,{GmtCell}\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},2.00,Cash,Water 600ml,Machine A,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(0, 2, 0, 1), upload);
    }

    private static async Task SeedStoredSaleAsync(
        SqliteConnection connection,
        int storedStatus = NayaxTransactionStatusIds.Completed,
        long transactionId = TransactionId)
    {
        await using var db = Context(connection);
        if (!await db.Products.AnyAsync())
            db.Products.Add(new Product { Id = 10, Name = "Water 600ml", QuantityInStock = 10, AverageUnitCost = 1m });
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = transactionId,
            MachineID = MachineId,
            MachineName = "Machine A",
            ProductId = 10,
            ProductName = "Water 600ml",
            PaymentMethod = "Credit Card",
            SettlementValue = 3.50m,
            TransactionStatusId = storedStatus,
            MachineAuthorizationTime = StoredInstant
        });
        await db.SaveChangesAsync();
    }

    private static ImportNayaxSales Import(AppDbContext db)
    {
        var rebuild = Mock.Of<IRebuildProductCost>();
        return new ImportNayaxSales(
            new ClosedXmlNayaxSalesWorkbookReader(),
            new EfNayaxSalesImportStore(db),
            TestCostingUseCases.CostSale(db, rebuild),
            rebuild,
            InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself());
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
