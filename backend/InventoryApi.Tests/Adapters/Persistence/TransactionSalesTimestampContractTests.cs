using System.Text.Json;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Shared;
using Inventory.Application.Reporting.Transactions;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

using Inventory.Domain.FinancialConfiguration;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Reproduces the production defect the independent review of issue #232 found: Transaction Sales
/// can render <c>transactionDate</c> as the raw UTC clock value instead of Canberra local time even
/// though the template already uses <c>businessDateTime</c>.
///
/// Root cause, the same one issue #230 fixed for <see cref="StockAdjustment.CreatedAt"/>:
/// <see cref="NayaxSales.MachineAuthorizationTime"/> is a persisted true UTC instant (see
/// docs/architecture.md § Timezone and business calendar), but Microsoft's SQLite EF Core provider -
/// the only provider this API ever runs against, see Program.cs - does not round-trip
/// <see cref="DateTimeKind"/>: a value freshly queried back from the database (exactly what every
/// Transaction Sales request does, via <see cref="EfTransactionSalesReportFactsProvider"/>)
/// materialises with <see cref="DateTimeKind.Unspecified"/>. System.Text.Json then serialises it
/// without a trailing "Z"/offset, so the JSON instant is ambiguous and the Angular
/// <c>BusinessDateTimePipe</c> parses it as browser-local time instead of UTC - which, for an
/// operator whose browser is already set to Australia/Canberra, cancels out the pipe's own
/// UTC-to-Canberra conversion and reproduces exactly the "still shows the UTC clock value" symptom.
///
/// These tests use a real (non-InMemory) Sqlite provider deliberately: EF Core's InMemory provider
/// keeps the original CLR object and does not reproduce the Kind loss, so it would not catch this
/// regression.
///
/// The daily-report test guards the other half of the same contract, which issue #232 states
/// explicitly: restoring UTC identity on the persisted instant must not turn a value derived from
/// it as a business-calendar <em>date</em> into a UTC instant. A date-only field serialised with a
/// "Z" is parsed by the browser as midnight UTC and can render as the previous day.
/// </summary>
public class TransactionSalesTimestampContractTests
{
    private static readonly JsonSerializerOptions WebDefaults = new(JsonSerializerDefaults.Web);

    // 2026-06-15T00:00:00Z is Australian winter - outside daylight saving (AEST, UTC+10).
    private static readonly DateTime AestInstant = new(2026, 6, 15, 0, 0, 0, DateTimeKind.Utc);

    // 2026-01-15T00:00:00Z is Australian summer - inside daylight saving (AEDT, UTC+11).
    private static readonly DateTime AedtInstant = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> KnownInstants()
    {
        yield return new object[] { AestInstant };
        yield return new object[] { AedtInstant };
    }

    [Theory]
    [MemberData(nameof(KnownInstants))]
    public async Task Transaction_sales_row_preserves_the_UTC_instant_identity_of_the_authorization_time_across_a_real_Sqlite_round_trip(
        DateTime utcInstant)
    {
        await using var connection = await CreateSqliteAsync();
        await using (var setup = Context(connection))
        {
            setup.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                MachineName = "Alpha One",
                ProductName = "Water",
                PaymentMethod = "Credit Card",
                SettlementValue = 2.5m,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = utcInstant
            });
            await setup.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var useCase = new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(db));

        var report = await useCase.Handle(
            new TransactionSalesFilterDto(From: utcInstant.Date, To: utcInstant.Date),
            CancellationToken.None);

        var row = Assert.Single(report.Rows);

        // This is the actual boundary the frontend depends on: does a value freshly read back from
        // SQLite still carry an unambiguous UTC instant once serialized?
        var json = JsonSerializer.Serialize(row, WebDefaults);
        using var document = JsonDocument.Parse(json);
        var transactionDateJson = document.RootElement.GetProperty("transactionDate").GetString();

        Assert.NotNull(transactionDateJson);
        Assert.True(
            transactionDateJson!.EndsWith("Z", StringComparison.Ordinal) || transactionDateJson.Contains('+', StringComparison.Ordinal),
            $"Expected an unambiguous UTC instant (trailing 'Z' or an explicit offset) but got '{transactionDateJson}', " +
            "which the frontend would parse as browser-local time instead of the persisted UTC instant.");
        Assert.Equal(utcInstant, document.RootElement.GetProperty("transactionDate").GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Daily_report_dates_derived_from_the_authorization_time_stay_date_only_and_are_not_serialized_as_UTC_instants()
    {
        await using var connection = await CreateSqliteAsync();
        await using (var setup = Context(connection))
        {
            setup.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate { EffectiveFrom = new DateTime(2026, 1, 1), FeeExGst = 0.2m });
            setup.NayaxSales.Add(new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                PaymentMethod = "Credit Card",
                SettlementValue = 2.5m,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineAuthorizationTime = AestInstant,
                CostOfGoodsSold = 1m
            });
            await setup.SaveChangesAsync();
        }

        await using var db = Context(connection);
        var useCase = new GetDailyReport(new EfDailyReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db)));

        var report = await useCase.Handle(
            new ReportingFilterDto(From: AestInstant.Date, To: AestInstant.Date),
            CancellationToken.None);

        var json = JsonSerializer.Serialize(report, WebDefaults);
        using var document = JsonDocument.Parse(json);

        var rowDate = document.RootElement.GetProperty("rows")[0].GetProperty("date").GetString();
        AssertDateOnly(rowDate, "the daily report row's business-calendar date");

        // The same day value also reaches the response as the estimated-fee coverage start date.
        var estimatedFeeFromDate = document.RootElement
            .GetProperty("totals").GetProperty("nayaxProcessingFees").GetProperty("estimatedFeeFromDate").GetString();
        AssertDateOnly(estimatedFeeFromDate, "the estimated Nayax fee coverage start date");
    }

    private static void AssertDateOnly(string? value, string description)
    {
        Assert.NotNull(value);
        Assert.False(
            value!.EndsWith("Z", StringComparison.Ordinal) || value.Contains('+', StringComparison.Ordinal),
            $"Expected {description} to stay a date-only business-calendar value but got '{value}', " +
            "which the frontend would parse as a UTC instant and could render as the previous day.");
        Assert.StartsWith("2026-06-15T00:00:00", value, StringComparison.Ordinal);
    }

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
