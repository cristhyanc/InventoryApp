using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
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
/// Issue #1300: one uploaded file may name the same transaction more than once, and each of its
/// rows is applied against what the row before it left - not against what the business held before
/// the file ran.
///
/// Since the stored sales of the whole file are read once, inside the write lock, that read is the
/// pass's <em>starting</em> state. Before this regression was fixed it was its only state, so a
/// second row for one transaction merged against a snapshot taken before the first row: a blank
/// amount or payment-method cell restored the value the earlier row had just written (undoing it,
/// and breaking issue #1298's rule that a blank cell changes nothing), and a second row naming a
/// transaction the file itself introduced staged a second insert of it - refused by the unique index
/// over <c>(BusinessId, TransactionID)</c> where it can see the row, and a double-counted sale
/// where it cannot. The per-row lookup this replaced could not get that wrong: it read the row
/// again, already updated.
///
/// Real reader, real store and real SQLite throughout: the final values are what every report reads
/// back, and the row count is what the unique index and Bookkeeping see.
/// </summary>
public class NayaxSalesImportRepeatedTransactionTests
{
    private const long StoredTransactionId = 8101;
    private const long NewTransactionId = 8102;
    private const long MachineId = 942488501;

    /// <summary>Sunday 4 October 2026 23:30 in Sydney (AEDT, +11): 12:30Z.</summary>
    private static readonly DateTime StoredInstant = new(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);

    private const string GmtCell = "2026-10-04T12:30:00Z";

    private const string FullHeader =
        "TransactionID,TransactionStatusId,MachineID,SettlementValue,PaymentMethod,ProductName,MachineName,AuthorizationDateTimeGMT";

    /// <summary>
    /// The reported case. A stored completed $3.50 sale paid by Credit Card at Machine A; the file
    /// names it twice, first stating a refund of $4.50 paid by EFTPOS at Machine B, then saying
    /// nothing about the status, the amount or any of the metadata. What the business ends up
    /// holding is the first row's values: the second row kept them, it did not restore the ones the
    /// first row replaced.
    /// </summary>
    [Fact]
    public async Task A_later_row_that_states_nothing_keeps_what_the_earlier_row_wrote()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{StoredTransactionId},{NayaxTransactionStatusIds.Refunded},{MachineId},4.50,EFTPOS,Water 1L,Machine B,{GmtCell}\n" +
                $"{StoredTransactionId},,{MachineId},,,,,{GmtCell}"));

        // Two rows updated one sale; only the second said nothing new, so only it is counted as
        // having kept a stored value.
        Assert.Equal(new NayaxSalesImportResult(0, 2, 0, 1), upload);
        Assert.Empty(upload.InvalidRows);

        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);
        Assert.Equal(4.50m, sale.SettlementValue);
        Assert.Equal("EFTPOS", sale.PaymentMethod);
        Assert.Equal("Water 1L", sale.ProductName);
        Assert.Equal("Machine B", sale.MachineName);
    }

    /// <summary>
    /// The same for the sale instant (issue #380's rule 2, applied to the file's own earlier row): a
    /// first row corrects the stored instant from its authoritative <c>AuthorizationDateTimeGMT</c>
    /// value, and a second row that carries no usable GMT value keeps the instant the sale now has
    /// rather than moving it back to the one it held before the file ran.
    /// </summary>
    [Fact]
    public async Task A_later_row_without_an_authoritative_instant_keeps_the_instant_the_earlier_row_set()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedStoredSaleAsync(connection);
        var corrected = new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                "TransactionID,MachineID,SettlementValue,MachineAuthorizationTime,AuthorizationDateTimeGMT\n" +
                $"{StoredTransactionId},{MachineId},4.50,,2026-10-05T01:00:00Z\n" +
                $"{StoredTransactionId},{MachineId},,4/10/2026 9:00:00 AM,"));

        // Both rows kept a stored value: this file carries no status, payment-method, product-name
        // or machine-name column at all, and the stored sale holds all four.
        Assert.Equal(new NayaxSalesImportResult(0, 2, 0, 2), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(corrected, DateTime.SpecifyKind(sale.MachineAuthorizationTime, DateTimeKind.Utc));
        Assert.Equal(4.50m, sale.SettlementValue);
    }

    /// <summary>
    /// A transaction the <em>file</em> introduces twice: the first row imports it, the second
    /// updates what that row staged. One sale exists, carrying the first row's amount and metadata
    /// because the second row stated none, and the import reports one imported and one updated row
    /// instead of importing the same transaction twice.
    /// </summary>
    [Fact]
    public async Task A_new_transaction_the_file_names_twice_is_imported_once_and_then_updated()
    {
        await using var connection = await CreateSqliteAsync();
        await SeedProductAsync(connection);

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},1.50,Cash,Water 600ml,Machine A,{GmtCell}\n" +
                $"{NewTransactionId},,{MachineId},,,,,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(1, 1, 0, 1), upload);
        Assert.Empty(upload.InvalidRows);

        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NewTransactionId, sale.TransactionID);
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(1.50m, sale.SettlementValue);
        Assert.Equal("Cash", sale.PaymentMethod);
        Assert.Equal("Water 600ml", sale.ProductName);
        Assert.Equal("Machine A", sale.MachineName);
        // The first row's association, resolved and persisted by this import (issue #758), still
        // names the product the kept name belongs to.
        Assert.Equal(10, sale.ProductId);
    }

    /// <summary>
    /// The file's last word about a transaction is what the business holds, whether or not it held
    /// the transaction before: a third row stating a new amount replaces the second row's kept one,
    /// and a transaction named three times is still one sale.
    /// </summary>
    [Fact]
    public async Task The_last_row_naming_a_transaction_decides_what_is_stored()
    {
        await using var connection = await CreateSqliteAsync();

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},1.50,Cash,Water 600ml,Machine A,{GmtCell}\n" +
                $"{NewTransactionId},,{MachineId},,,,,{GmtCell}\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Refunded},{MachineId},2.75,EFTPOS,Water 1L,Machine B,{GmtCell}"));

        Assert.Equal(new NayaxSalesImportResult(1, 2, 0, 1), upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Refunded, sale.TransactionStatusId);
        Assert.Equal(2.75m, sale.SettlementValue);
        Assert.Equal("EFTPOS", sale.PaymentMethod);
        Assert.Equal("Water 1L", sale.ProductName);
        Assert.Equal("Machine B", sale.MachineName);
    }

    /// <summary>
    /// A row the import applies nowhere leaves the transaction exactly as it was, so it cannot
    /// become the state a later row merges against: a first row imports the sale, an invalid row
    /// naming the same transaction changes nothing, and a third row with blank cells keeps the first
    /// row's values.
    /// </summary>
    [Fact]
    public async Task A_row_that_is_applied_nowhere_does_not_become_the_state_a_later_row_merges_against()
    {
        await using var connection = await CreateSqliteAsync();

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(
                FullHeader + "\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Completed},{MachineId},1.50,Cash,Water 600ml,Machine A,{GmtCell}\n" +
                $"{NewTransactionId},{NayaxTransactionStatusIds.Refunded},{MachineId},$9.99,EFTPOS,Water 1L,Machine B,{GmtCell}\n" +
                $"{NewTransactionId},,{MachineId},,,,,{GmtCell}"));

        Assert.Equal(
            new NayaxSalesImportResult(1, 1, 0, 1) { InvalidRows = [new(3, NewTransactionId)] }, upload);
        await using var read = Context(connection);
        var sale = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(NayaxTransactionStatusIds.Completed, sale.TransactionStatusId);
        Assert.Equal(1.50m, sale.SettlementValue);
        Assert.Equal("Cash", sale.PaymentMethod);
        Assert.Equal("Machine A", sale.MachineName);
    }

    /// <summary>The catalogue the import resolves a row's product name against.</summary>
    private static async Task SeedProductAsync(SqliteConnection connection)
    {
        await using var db = Context(connection);
        db.Products.Add(new Product { Id = 10, Name = "Water 600ml", QuantityInStock = 10, AverageUnitCost = 1m });
        await db.SaveChangesAsync();
    }

    private static async Task SeedStoredSaleAsync(SqliteConnection connection)
    {
        await SeedProductAsync(connection);
        await using var db = Context(connection);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = StoredTransactionId,
            MachineID = MachineId,
            MachineName = "Machine A",
            ProductId = 10,
            ProductName = "Water 600ml",
            PaymentMethod = "Credit Card",
            SettlementValue = 3.50m,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
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
            new EfNayaxSalesImportTransactions(db),
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
