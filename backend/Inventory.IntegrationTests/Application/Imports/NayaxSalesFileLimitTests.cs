using System.IO.Compression;
using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Imports;
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
/// Issue #1300: one uploaded sales file cannot use up the server's memory. A file carrying more rows
/// than <see cref="NayaxSalesFilePolicy.MaxRows"/>, and an XLSX whose sheet data expands past
/// <see cref="NayaxSalesFilePolicy.MaxUncompressedSheetBytes"/>, are refused with the operator's own
/// sentence and nothing is written.
///
/// <para>The defect these are about: the import read the whole workbook into memory first - a 9.9 MB
/// CSV of 114,250 rows allocated about a gigabyte just to be read - and then held the database's
/// single write lock for minutes saving it, which every other write waits on and then fails after
/// its busy timeout. A cap is what makes the cost of one upload bounded; the refusal names the
/// longer-history import because splitting the file is what an operator can actually do about it.</para>
///
/// <para>Real SQLite, because "nothing is written" is a claim about a database, and the import's
/// save now runs inside the shared <c>BEGIN IMMEDIATE</c> transaction.</para>
/// </summary>
public class NayaxSalesFileLimitTests
{
    private const long MachineId = 942488501;

    private const string Header =
        "TransactionID,TransactionStatusId,MachineID,SettlementValue,PaymentMethod,ProductName,MachineName,AuthorizationDateTimeGMT";

    private const string GmtCell = "2026-10-04T12:30:00Z";

    /// <summary>
    /// The owner's limits of 14 November 2026, pinned as the named constants the refusal and the
    /// guard are written against. The message is derived from the row limit, so lowering the limit -
    /// which issue #1300 allows if the measured write-lock hold exceeds its budget - cannot leave
    /// the sentence claiming a number the import no longer enforces.
    /// </summary>
    [Fact]
    public void The_limits_are_the_owners_named_constants()
    {
        Assert.Equal(20_000, NayaxSalesFilePolicy.MaxRows);
        Assert.Equal(50L * 1024 * 1024, NayaxSalesFilePolicy.MaxUncompressedSheetBytes);
        Assert.Equal(10, NayaxSalesFilePolicy.MaxWriteLockHoldSeconds);
        Assert.Equal(
            "This file has more than 20,000 sales. For a longer history, export one file per "
                + "quarter, or use the sales history import.",
            NayaxSalesFilePolicy.TooLargeMessage);
    }

    /// <summary>
    /// <strong>The required row-limit case.</strong> One row past the limit refuses the whole file
    /// with the operator's sentence, and no sale is stored - not the 20,000 rows that were inside
    /// the limit either, because a half-imported file is worse than a refused one.
    /// </summary>
    [Fact]
    public async Task A_file_with_more_rows_than_the_limit_is_refused_and_imports_nothing()
    {
        await using var connection = await CreateSqliteAsync();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var db = Context(connection);
            await Import(db).Handle(Csv(Rows(NayaxSalesFilePolicy.MaxRows + 1)));
        });

        // The same exception type the unsupported-format rule raises, which is what
        // ImportsController.ImportNayaxSales answers 400 Bad Request with, carrying this message.
        Assert.Equal(NayaxSalesFilePolicy.TooLargeMessage, refusal.Message);
        await using var read = Context(connection);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// And the limit itself is not the refusal: a file of exactly
    /// <see cref="NayaxSalesFilePolicy.MaxRows"/> rows imports every one of them. A cap that refused
    /// the file it allows would be a different limit than the one the operator is told about.
    /// </summary>
    [Fact]
    public async Task A_file_of_exactly_the_limit_is_imported()
    {
        await using var connection = await CreateSqliteAsync();

        NayaxSalesImportResult upload;
        await using (var db = Context(connection))
            upload = await Import(db).Handle(Csv(Rows(NayaxSalesFilePolicy.MaxRows)));

        Assert.Equal(NayaxSalesFilePolicy.MaxRows, upload.Imported);
        await using var read = Context(connection);
        Assert.Equal(NayaxSalesFilePolicy.MaxRows, await read.NayaxSales.AsNoTracking().CountAsync());
    }

    /// <summary>
    /// <strong>The required XLSX expansion case.</strong> A workbook whose sheet data expands past
    /// the limit is refused from the package's own entry sizes, <em>before</em> the workbook is
    /// loaded - which the archive here proves, because it is not a readable workbook at all: if
    /// anything tried to open it, the failure would be ClosedXML's rather than this refusal.
    /// </summary>
    [Fact]
    public async Task An_xlsx_whose_sheet_data_expands_past_the_limit_is_refused_before_it_is_loaded()
    {
        await using var connection = await CreateSqliteAsync();
        var oversized = OversizedSheetPackage();

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await using var db = Context(connection);
            await Import(db).Handle(new NayaxSalesFileInput("sales.xlsx", () => new MemoryStream(oversized)));
        });

        Assert.Equal(NayaxSalesFilePolicy.TooLargeMessage, refusal.Message);
        await using var read = Context(connection);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The guard is a size check and not a readability check: an upload that is not an archive at
    /// all still fails the way it always has - ClosedXML's own <see cref="FileFormatException"/>,
    /// which the controller's <c>400</c> handling does not claim - rather than being reported to the
    /// operator as a file that is too large.
    /// </summary>
    [Fact]
    public async Task An_upload_that_is_not_a_workbook_still_fails_as_a_corrupt_upload()
    {
        await using var connection = await CreateSqliteAsync();
        var bytes = Encoding.UTF8.GetBytes("this is not a workbook");

        await using var db = Context(connection);
        var thrown = await Assert.ThrowsAnyAsync<Exception>(() =>
            Import(db).Handle(new NayaxSalesFileInput("sales.xlsx", () => new MemoryStream(bytes))));

        Assert.IsNotAssignableFrom<InvalidOperationException>(thrown);
    }

    /// <summary>
    /// The CSV conversion stops at the first line past the limit instead of converting the file and
    /// counting afterwards: the memory a huge upload costs is spent while it is being read, so a
    /// limit applied after the read would already have been paid. What is observable from outside
    /// is that the reader answers "beyond limits" with no rows at all.
    /// </summary>
    [Fact]
    public void The_reader_answers_a_file_past_the_row_limit_with_no_rows()
    {
        using var content = new MemoryStream(
            Encoding.UTF8.GetBytes(Rows(NayaxSalesFilePolicy.MaxRows + 1)));

        var read = new ClosedXmlNayaxSalesWorkbookReader().Read(
            content,
            "sales.csv",
            new NayaxSalesFileLimits(
                NayaxSalesFilePolicy.MaxRows, NayaxSalesFilePolicy.MaxUncompressedSheetBytes));

        Assert.True(read.ExceedsLimits);
        Assert.Empty(read.Rows);
    }

    /// <summary>
    /// A caller that states no limit is not capped (issue #1300): the sale timestamp repair preview
    /// reads an export for its authorization instants alone, imports nothing and writes nothing, so
    /// what it may read is its own decision rather than a side effect of the import's.
    /// </summary>
    [Fact]
    public void A_caller_with_no_limit_reads_a_file_past_the_import_limit()
    {
        using var content = new MemoryStream(
            Encoding.UTF8.GetBytes(Rows(NayaxSalesFilePolicy.MaxRows + 1)));

        var read = new ClosedXmlNayaxSalesWorkbookReader()
            .Read(content, "sales.csv", NayaxSalesFileLimits.None);

        Assert.False(read.ExceedsLimits);
        Assert.Equal(NayaxSalesFilePolicy.MaxRows + 1, read.Rows.Count);
    }

    /// <summary>
    /// A ZIP archive holding one worksheet part that expands past the limit, built directly rather
    /// than by saving a workbook of that many rows: what the guard reads is the archive's own
    /// declared entry size, so the bytes never have to be a real spreadsheet - and the entry's
    /// content is a repeated byte, which is why a 51 MB part is a small archive in memory.
    /// </summary>
    private static byte[] OversizedSheetPackage()
    {
        var package = new MemoryStream();
        using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var sheet = archive.CreateEntry("xl/worksheets/sheet1.xml").Open();
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)' ');
            for (var written = 0L;
                 written <= NayaxSalesFilePolicy.MaxUncompressedSheetBytes;
                 written += chunk.Length)
            {
                sheet.Write(chunk);
            }
        }

        return package.ToArray();
    }

    /// <summary>A file of <paramref name="count"/> complete, importable rows.</summary>
    private static string Rows(int count)
    {
        var csv = new StringBuilder(Header).Append('\n');
        for (var row = 1; row <= count; row++)
        {
            csv.Append(5_000_000 + row).Append(',')
                .Append(NayaxTransactionStatusIds.Completed).Append(',')
                .Append(MachineId)
                .Append(",3.50,Credit Card,Water 600ml,Machine A,")
                .Append(GmtCell)
                .Append('\n');
        }

        return csv.ToString();
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
        setup.Products.Add(new Product
        {
            Id = 10,
            Name = "Water 600ml",
            QuantityInStock = 100_000,
            AverageUnitCost = 1m,
        });
        await setup.SaveChangesAsync();
        return connection;
    }

    private static AppDbContext Context(SqliteConnection connection) =>
        TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
}
