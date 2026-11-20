using System.Data.Common;
using System.Text;
using Inventory.Application.Costing;
using Inventory.Application.Imports;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1300: the uploaded sales import reads what the business already holds in one pass over the
/// file's transaction identifiers, in fixed-size batches - never one query per row.
///
/// <para>One query per row is what made the measured import quadratic: 4,000 rows took 30 seconds
/// and 8,000 took 124. It also meant 20,000 round trips inside the write-locked save, which is time
/// every other writer in the application spends waiting for the lock.</para>
///
/// <para>The statements are counted from the connection itself, over real SQLite, because "how many
/// queries" is not observable from the port's answer.</para>
/// </summary>
public class NayaxSalesImportLookupBatchingTests
{
    private const int Rows = 1_200;
    private const long MachineId = 942488501;

    [Fact]
    public async Task The_stored_sales_of_a_file_are_read_in_batches_rather_than_one_query_per_row()
    {
        var statements = new List<string>();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new CommandTextInterceptor(statements))
            .Options;

        await using (var setup = TestAppDbContext.Unrestricted(options))
            await setup.Database.EnsureCreatedAsync();

        NayaxSalesImportResult upload;
        await using (var db = TestAppDbContext.Unrestricted(options))
        {
            statements.Clear();
            upload = await Import(db).Handle(File());
        }

        Assert.Equal(Rows, upload.Imported);

        // Every statement this import makes over the sales table is the stored-sale lookup: the rows
        // resolve to no product, so nothing costs anything from the ledger. One statement per 500
        // identifiers is three of them for this file; one per row would be 1,200.
        var lookups = statements.Count(
            text => text.Contains("FROM \"NayaxSales\"", StringComparison.Ordinal));
        Assert.InRange(lookups, 1, 3);
    }

    /// <summary>
    /// A file of <see cref="Rows"/> rows that name no product: the identity facts the import needs
    /// and nothing that would make the costing read a ledger, so what is counted is the lookup
    /// alone.
    /// </summary>
    private static NayaxSalesFileInput File()
    {
        var csv = new StringBuilder(
            "TransactionID,TransactionStatusId,MachineID,SettlementValue,AuthorizationDateTimeGMT")
            .Append('\n');
        for (var row = 1; row <= Rows; row++)
        {
            csv.Append(6_000_000 + row).Append(',')
                .Append(NayaxTransactionStatusIds.Completed).Append(',')
                .Append(MachineId)
                .Append(",3.50,2026-10-04T12:30:00Z")
                .Append('\n');
        }

        var content = Encoding.UTF8.GetBytes(csv.ToString());
        return new NayaxSalesFileInput("sales.csv", () => new MemoryStream(content));
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

    private sealed class CommandTextInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _statements;

        public CommandTextInterceptor(List<string> statements) => _statements = statements;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            _statements.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _statements.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
