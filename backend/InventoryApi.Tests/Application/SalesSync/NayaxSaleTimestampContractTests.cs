using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Application.Reporting.Transactions;
using Inventory.Infrastructure.Time;
using InventoryApi.Adapters.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Issue #380: the upstream timezone contract of the Nayax Lynx last-sales timestamps, and the
/// instant the latest-sales synchronization is therefore allowed to persist.
///
/// The contract is the published one for
/// <c>GET /v1/machines/{MachineID}/lastSales</c>
/// (https://devzone.nayax.com/reference/lynx/machines/get-last-sales-for-machine-by-machineid),
/// read through the Nayax documentation MCP server:
/// <list type="bullet">
///   <item><c>AuthorizationDateTimeGMT</c> - "The date and time when the transaction was authorized,
///   in GMT." A true instant, and the only authoritative one the payload carries.</item>
///   <item><c>MachineAuthorizationTime</c> - "The local date and time when the machine authorized the
///   transaction." Machine-local wall-clock time with no offset, so its ticks are <em>not</em> a UTC
///   instant and must never be persisted as one. The documented sample payload nevertheless prints it
///   with a trailing <c>Z</c> and equal to the GMT field, which is exactly why a <c>Z</c> on this
///   field proves nothing and the GMT field is read instead.</item>
/// </list>
/// Nayax's only machine timezone metadata is <c>MachineTimeZoneOffset</c> on the machine basic-info
/// endpoints - a bare <c>number</c> offset with no daylight-saving rule - so machine-local time
/// cannot be converted to an instant from the sales payload at all. Reading the GMT field is what
/// makes the conversion DST-correct without any fixed <c>+10</c>/<c>+11</c> assumption.
///
/// The production symptom these tests reproduce: Sydney daylight saving started on Sunday
/// 4 October 2026, so a 23:30 Sydney sale that evening is 12:30Z. Persisting the machine-local ticks
/// instead reads them as 23:30Z, which is 10:30 on Monday 5 October in Sydney - the sale moves out of
/// Sunday/last week and into Monday/today, which is "more sales today and fewer sales last week".
///
/// A real (non-InMemory) SQLite provider is used deliberately: the persisted instant is what every
/// report and dashboard later reads back, and the InMemory provider keeps the original CLR object.
/// </summary>
public class NayaxSaleTimestampContractTests
{
    /// <summary>
    /// Sunday 4 October 2026 23:30 in Sydney, the evening daylight saving started. AEDT is +11, so the
    /// instant is 12:30Z the same day.
    /// </summary>
    private static readonly DateTime SundayEveningUtc = new(2026, 10, 4, 12, 30, 0, DateTimeKind.Utc);

    private const string SundayEveningMachineLocal = "2026-10-04T23:30:00";

    /// <summary>
    /// Monday 5 October 2026 14:00 in Sydney (AEDT, +11), the first trading afternoon of the new week
    /// and after both the correct and the misread position of the Sunday-evening sale, so neither falls
    /// outside "today" merely by being later than now.
    /// </summary>
    private static readonly DateTime MondayAfternoonUtc = new(2026, 10, 5, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The documented response item, with the machine-local field deliberately *not* equal to the GMT
    /// field: that is the only shape in which the two can be told apart.
    /// </summary>
    private static string LastSalesPayload(
        string authorizationDateTimeGmt, string machineAuthorizationTime, long transactionId = 5001) => $$"""
        [
          {
            "TransactionID": {{transactionId}},
            "MachineID": 942488501,
            "MachineName": "Marshall Vending",
            "SettlementValue": 3.50,
            "PaymentMethod": "Credit Card",
            "ProductName": "Bottled Water",
            "ProductCostPrice": 1.10,
            "AuthorizationDateTimeGMT": "{{authorizationDateTimeGmt}}",
            "MachineAuthorizationTime": "{{machineAuthorizationTime}}",
            "SiteID": 2,
            "SiteName": "IL1"
          }
        ]
        """;

    private static List<NayaxLastSalesReport> Deserialize(string payload) =>
        JsonSerializer.Deserialize<List<NayaxLastSalesReport>>(payload)!;

    /// <summary>
    /// The defect itself: the sale the synchronization persists must be timestamped from the
    /// authoritative GMT field, never from the machine-local wall clock, whose ticks are an hour
    /// (AEST) or eleven hours (AEDT) away from the instant they are stored as.
    /// </summary>
    [Fact]
    public async Task Persisted_sale_instant_comes_from_the_authoritative_GMT_field_not_the_machine_local_field()
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize(LastSalesPayload("2026-10-04T12:30:00.000Z", SundayEveningMachineLocal));

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var persisted = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(SundayEveningUtc, persisted.MachineAuthorizationTime);
    }

    /// <summary>
    /// The production symptom, end to end over the persisted instant: the Sunday evening sale belongs
    /// to Sunday 4 October and to the week that ended with it, not to Monday 5 October. The dashboard
    /// window is the real one, resolved from a Monday-afternoon Sydney instant through the real
    /// <see cref="SydneyBusinessCalendar"/>.
    /// </summary>
    [Fact]
    public async Task A_Sunday_evening_sale_stays_in_Sunday_and_last_week_once_daylight_saving_has_started()
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize(LastSalesPayload("2026-10-04T12:30:00.000Z", SundayEveningMachineLocal));

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var persisted = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());

        var monday = new FixedSydneyTime(MondayAfternoonUtc);
        var window = monday.Window;

        Assert.Equal(new DateTime(2026, 10, 5), window.BusinessToday);
        Assert.Equal(new DateTime(2026, 10, 4), monday.Calendar.ToBusinessDate(persisted.MachineAuthorizationTime));
        Assert.False(
            Contains(window.Today, persisted.MachineAuthorizationTime),
            "A Sunday-evening sale must not be counted in Monday's 'today'.");
        Assert.False(
            Contains(window.CurrentWeek, persisted.MachineAuthorizationTime),
            "A Sunday-evening sale must not be counted in the new week to date.");
        Assert.True(
            Contains(window.LastWeek, persisted.MachineAuthorizationTime),
            "A Sunday-evening sale belongs to the week that ended with that Sunday.");
    }

    /// <summary>
    /// The same sale classified the way it was before the fix - machine-local ticks read as a UTC
    /// instant - to pin down that this really is the reported redistribution and not a window defect:
    /// 23:30 taken as 23:30Z is 10:30 on Monday in Sydney, so the sale lands in today and in the
    /// current week instead.
    /// </summary>
    [Fact]
    public void Reading_the_machine_local_ticks_as_an_instant_is_what_moved_the_sale_into_Monday()
    {
        var misread = DateTime.SpecifyKind(DateTime.Parse(
            SundayEveningMachineLocal, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var monday = new FixedSydneyTime(MondayAfternoonUtc);
        var window = monday.Window;

        Assert.Equal(new DateTime(2026, 10, 5), monday.Calendar.ToBusinessDate(misread));
        Assert.True(Contains(window.Today, misread));
        Assert.True(Contains(window.CurrentWeek, misread));
        Assert.False(Contains(window.LastWeek, misread));
    }

    /// <summary>
    /// Every DST case the Sydney business calendar has to classify the normalized instant through,
    /// taken from the IANA timezone database rather than a fixed offset: either side of the Sydney
    /// midnight that daylight saving began on, the first instant after the skipped hour, both passes
    /// of the repeated hour when daylight saving ended, and an ordinary AEST and AEDT trading day.
    /// </summary>
    [Theory]
    // Saturday 3 October 2026 23:30 Sydney, still AEST (+10): the last evening before the transition.
    [InlineData("2026-10-03T13:30:00.000Z", 2026, 10, 3)]
    // 00:30 on Sunday 4 October, Sydney midnight having passed while still AEST.
    [InlineData("2026-10-03T14:00:00.000Z", 2026, 10, 4)]
    [InlineData("2026-10-03T14:30:00.000Z", 2026, 10, 4)]
    // 03:00 on Sunday 4 October AEDT (+11): the first instant after the 02:00-03:00 hour Sydney skips.
    [InlineData("2026-10-03T16:00:00.000Z", 2026, 10, 4)]
    // Sunday 4 October 23:30 AEDT: the production symptom's own sale.
    [InlineData("2026-10-04T12:30:00.000Z", 2026, 10, 4)]
    // Monday 5 October 00:30 AEDT, the first business day of the new week.
    [InlineData("2026-10-04T13:30:00.000Z", 2026, 10, 5)]
    // Both passes of 02:30 on Sunday 5 April 2026, the ambiguous hour daylight saving ended in: AEDT
    // (+11) first, then AEST (+10). Two distinct instants, one Sydney business date.
    [InlineData("2026-04-04T15:30:00.000Z", 2026, 4, 5)]
    [InlineData("2026-04-04T16:30:00.000Z", 2026, 4, 5)]
    // An ordinary AEST day: 09:00 on Wednesday 12 August 2026.
    [InlineData("2026-08-11T23:00:00.000Z", 2026, 8, 12)]
    // An ordinary AEDT day: 09:00 on Wednesday 18 November 2026.
    [InlineData("2026-11-17T22:00:00.000Z", 2026, 11, 18)]
    public async Task The_persisted_instant_classifies_onto_the_Sydney_business_date_the_sale_happened_on(
        string authorizationDateTimeGmt, int year, int month, int day)
    {
        await using var connection = await CreateSqliteAsync();
        // The machine-local field is deliberately a different day from the GMT instant, so a path that
        // read it would classify onto the wrong date.
        var sales = Deserialize(LastSalesPayload(authorizationDateTimeGmt, "2000-01-01T00:00:00"));

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var persisted = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());

        Assert.Equal(
            DateTimeOffset.Parse(authorizationDateTimeGmt, System.Globalization.CultureInfo.InvariantCulture)
                .UtcDateTime,
            persisted.MachineAuthorizationTime);
        Assert.Equal(
            new DateTime(year, month, day),
            new FixedSydneyTime(persisted.MachineAuthorizationTime).Calendar
                .ToBusinessDate(persisted.MachineAuthorizationTime));
    }

    /// <summary>
    /// An instant that arrives carrying an explicit non-zero offset is the same instant, not a value to
    /// shift again: 23:30+11:00 on 4 October is 12:30Z, exactly what the <c>Z</c> form of the same sale
    /// stores. This is the "never converted twice" guarantee at the boundary - normalization is
    /// <see cref="DateTimeOffset.UtcDateTime"/>, which is idempotent by construction.
    /// </summary>
    [Theory]
    [InlineData("2026-10-04T12:30:00.000Z")]
    [InlineData("2026-10-04T12:30:00.000+00:00")]
    [InlineData("2026-10-04T23:30:00.000+11:00")]
    [InlineData("2026-10-04T22:30:00.000+10:00")]
    public async Task An_already_offset_aware_instant_is_stored_unchanged_rather_than_converted_again(
        string authorizationDateTimeGmt)
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize(LastSalesPayload(authorizationDateTimeGmt, SundayEveningMachineLocal));

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var persisted = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(SundayEveningUtc, persisted.MachineAuthorizationTime);
    }

    /// <summary>
    /// Re-encountering the same transaction - which the rolling last-sales window does on every
    /// refresh - must leave its stored instant exactly where it is, so no sale is ever shifted twice.
    /// </summary>
    [Fact]
    public async Task Re_synchronizing_the_same_transaction_neither_duplicates_nor_shifts_it()
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize(LastSalesPayload("2026-10-04T12:30:00.000Z", SundayEveningMachineLocal));

        await using (var db = Context(connection))
        {
            var store = Store(db);
            await store.PersistLatestSalesAsync(sales, CancellationToken.None);
            await store.PersistLatestSalesAsync(sales, CancellationToken.None);
        }

        await using (var again = Context(connection))
            await Store(again).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var persisted = Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
        Assert.Equal(SundayEveningUtc, persisted.MachineAuthorizationTime);
    }

    /// <summary>
    /// Transaction Sales reports the same transaction on the same Sydney calendar day the dashboard
    /// classified it into, because both read the one persisted instant: the report carries the sale's
    /// own instant through to <c>transactionDate</c> (which the Angular <c>BusinessDateTimePipe</c>
    /// renders in Sydney), rather than a separately derived date.
    /// </summary>
    [Fact]
    public async Task Transaction_sales_reports_the_sale_on_the_same_Sydney_day_the_dashboard_classifies_it_into()
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize(LastSalesPayload("2026-10-04T12:30:00.000Z", SundayEveningMachineLocal));

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        var report = await new GetTransactionSalesReport(new EfTransactionSalesReportFactsProvider(read))
            .Handle(
                new TransactionSalesFilterDto(From: new DateTime(2026, 10, 1), To: new DateTime(2026, 10, 7)),
                CancellationToken.None);

        var row = Assert.Single(report.Rows);
        var calendar = new FixedSydneyTime(SundayEveningUtc).Calendar;
        Assert.Equal(SundayEveningUtc, row.TransactionDate);
        Assert.Equal(new DateTime(2026, 10, 4), calendar.ToBusinessDate(row.TransactionDate));
    }

    /// <summary>
    /// Fail closed: a payload item carrying no authoritative GMT instant is not imported at the
    /// machine-local wall clock, nor at a defaulted instant - the same rule the uploaded import
    /// already applies to a row with no authorization time. The rolling last-sales window returns the
    /// transaction again on the next refresh, so nothing is lost by refusing to guess once.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_authoritative_GMT_instant_is_not_imported_at_a_guessed_time()
    {
        await using var connection = await CreateSqliteAsync();
        var sales = Deserialize($$"""
            [
              {
                "TransactionID": 5002,
                "MachineID": 942488501,
                "SettlementValue": 3.50,
                "MachineAuthorizationTime": "{{SundayEveningMachineLocal}}"
              }
            ]
            """);

        await using (var db = Context(connection))
            await Store(db).PersistLatestSalesAsync(sales, CancellationToken.None);

        await using var read = Context(connection);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    private static bool Contains(
        Inventory.Application.Machines.MachineDashboardPeriodUtc period, DateTime instant) =>
        instant >= period.StartUtc && instant <= period.EndUtc;

    private static EfLatestNayaxSalesStore Store(AppDbContext db)
    {
        var rebuild = TestCostingUseCases.Rebuild(db);
        return new EfLatestNayaxSalesStore(db, TestCostingUseCases.CostSale(db, rebuild), rebuild);
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
