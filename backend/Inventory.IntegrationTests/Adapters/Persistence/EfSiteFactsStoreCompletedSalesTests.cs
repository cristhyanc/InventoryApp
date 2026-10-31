using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The completed-sales read behind the site dashboard's sales figures, over relational SQLite
/// (issue #1012). Its lower bound is the first instant of the earliest period the site row
/// publishes, so the bound is <b>inclusive</b>: a sale authorized at exactly that instant belongs to
/// that period, and a read that excluded it would make the period's own first instant unreachable.
/// The filter is otherwise unchanged - only the caller's machines, and only completed sales.
/// </summary>
public class EfSiteFactsStoreCompletedSalesTests : IDisposable
{
    private const int BusinessId = 1;
    private const long MachineId = 10;

    private static readonly DateTime From = new(2023, 9, 30, 14, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public EfSiteFactsStoreCompletedSalesTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business
        {
            Id = BusinessId,
            Name = "Vending Co",
            Status = BusinessStatus.Active,
            CreatedAtUtc = From,
        });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The bound itself: the sale authorized at exactly it is returned, and the one a millisecond
    /// earlier is not.
    /// </summary>
    [Fact]
    public async Task The_read_includes_a_sale_at_exactly_its_lower_bound()
    {
        await SeedAsync(
            Transaction(1, 7m, From, NayaxTransactionStatusIds.Completed),
            Transaction(2, 1000m, From.AddMilliseconds(-1), NayaxTransactionStatusIds.Completed));

        var sales = await ReadAsync();

        Assert.Equal(7m, Assert.Single(sales).SettlementValue);
    }

    [Fact]
    public async Task The_read_returns_only_completed_sales()
    {
        await SeedAsync(
            Transaction(1, 7m, From.AddHours(1), NayaxTransactionStatusIds.Completed),
            Transaction(2, 1000m, From.AddHours(1), NayaxTransactionStatusIds.Refunded),
            Transaction(3, 1000m, From.AddHours(1), NayaxTransactionStatusIds.PendingSettlementNotFinal),
            Transaction(4, 1000m, From.AddHours(1), NayaxTransactionStatusIds.CancelledOrDeclined26));

        var sales = await ReadAsync();

        Assert.Equal(7m, Assert.Single(sales).SettlementValue);
    }

    [Fact]
    public async Task The_read_returns_only_the_requested_machines()
    {
        await SeedAsync(
            Transaction(1, 7m, From.AddHours(1), NayaxTransactionStatusIds.Completed),
            Transaction(2, 1000m, From.AddHours(1), NayaxTransactionStatusIds.Completed, machineId: 99));

        var sales = await ReadAsync();

        Assert.Equal(MachineId, Assert.Single(sales).MachineId);
    }

    private async Task SeedAsync(params NayaxSales[] transactions)
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        seed.NayaxSales.AddRange(transactions);
        await seed.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<Inventory.Application.Sites.SiteCompletedSaleFact>> ReadAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessId);
        return await new EfSiteFactsStore(db).GetRecentCompletedSalesAsync([MachineId], From, CancellationToken.None);
    }

    private static NayaxSales Transaction(
        long transactionId, decimal settlementValue, DateTime authorizedAtUtc, int statusId, long machineId = MachineId) =>
        new()
        {
            BusinessId = BusinessId,
            TransactionID = transactionId,
            MachineID = machineId,
            SettlementValue = settlementValue,
            PaymentMethod = "Credit",
            MachineAuthorizationTime = authorizedAtUtc,
            TransactionStatusId = statusId,
        };
}
