using System.Net;
using Inventory.Application.Machines;
using Inventory.Application.SalesSync;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Infrastructure.Nayax;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Application.SalesSync;

/// <summary>
/// Issue #471: the instant an offset-free Nayax GMT authorization timestamp is persisted at, through
/// the production path end to end - the real <see cref="NayaxLynxClient"/> over
/// <c>HttpClient</c> (so the real <c>ReadFromJsonAsync</c> serializer options), the real
/// <see cref="SyncLatestNayaxSales"/> use case, the real <see cref="EfLatestNayaxSalesStore"/>, and a
/// relational SQLite database. The EF Core InMemory provider would keep the original CLR object and
/// prove nothing about what a report later reads back.
///
/// The reproduction is the operator's sanitized live payload of 8 October 2026: the Nayax API
/// returned <c>"AuthorizationDateTimeGMT": "2026-10-07T23:42:44.263"</c> - offset-free, exactly as
/// the Nayax portal's own live sample response renders that field - and the Sydney-hosted API stored
/// <c>2026-10-07 12:42:44.263</c>, eleven hours early, because .NET's default
/// <see cref="DateTimeOffset"/> binding read the value against the host's time zone. The sale then
/// counted on the previous Sydney business day, which is the reported "today: Nayax $142.60 vs app
/// $114.90".
///
/// Issue #380 established which payload field the instant comes from; this is about what an
/// offset-free value in that field means. The timezone semantics are asserted here with the real
/// <c>Australia/Sydney</c> calendar, and the host-independence of the parse itself is covered without
/// mutating process-global time zone state by
/// <c>InventoryApi.Tests.Application.Nayax.NayaxLastSalesGmtTimestampTests</c>.
/// </summary>
public sealed class NayaxLiveSaleGmtTimestampSyncTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long MachineId = 531595328;
    private const long TransactionId = 3564567268;

    /// <summary>The operator's reported GMT value, offset-free as the live endpoint sent it.</summary>
    private const string OperatorGmt = "2026-10-07T23:42:44.263";

    /// <summary>The instant it names, and the one the database must hold.</summary>
    private static readonly DateTime OperatorInstantUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>
    /// What a Sydney host stored before this fix: the same wall clock read as AEDT (+11). The sale's
    /// Sydney date was 7 October instead of 8 October.
    /// </summary>
    private static readonly DateTime MisreadInstantUtc = OperatorInstantUtc - TimeSpan.FromHours(11);

    /// <summary>Thursday 8 October 2026, 14:00 in Sydney (AEDT): the afternoon the operator reported.</summary>
    private static readonly DateTime EightOctoberAfternoonUtc = new(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public NayaxLiveSaleGmtTimestampSyncTests()
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
    /// The acceptance case: the operator's exact payload, through the configured client and the
    /// production persistence path, holds the instant its GMT field names - and the Sydney business
    /// time the operator expects to see, 8 October 2026 10:42:44.263.
    /// </summary>
    [Fact]
    public async Task The_reported_payload_persists_the_instant_its_GMT_field_names()
    {
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));

        var persisted = await SingleSaleAsync(BusinessA);
        Assert.Equal(OperatorInstantUtc, persisted.MachineAuthorizationTime);

        var sydney = new FixedSydneyTime(OperatorInstantUtc).Calendar;
        Assert.Equal(new DateTime(2026, 10, 8), sydney.ToBusinessDate(persisted.MachineAuthorizationTime));
        Assert.Equal(
            new DateTime(2026, 10, 8, 10, 42, 44, 263),
            persisted.MachineAuthorizationTime + TimeSpan.FromHours(11));
    }

    /// <summary>
    /// The reported symptom over the real dashboard window: the sale counts in Sydney's 8 October,
    /// which the instant stored before the fix did not - it fell on 7 October, moving that revenue out
    /// of "today".
    /// </summary>
    [Fact]
    public async Task The_eight_October_sale_counts_in_Sydney_today_where_the_misread_instant_did_not()
    {
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));
        var persisted = await SingleSaleAsync(BusinessA);

        var window = new FixedSydneyTime(EightOctoberAfternoonUtc).Window;

        Assert.Equal(new DateTime(2026, 10, 8), window.BusinessToday);
        Assert.True(
            Contains(window.Today, persisted.MachineAuthorizationTime),
            "The 8 October sale must count in Sydney's 8 October.");
        Assert.False(
            Contains(window.Today, MisreadInstantUtc),
            "The instant stored before the fix fell on 7 October, which is the reported shortfall.");
        Assert.True(Contains(window.CurrentWeek, persisted.MachineAuthorizationTime));
    }

    /// <summary>
    /// The week-level symptom, at the Sydney week boundary: a Monday-morning sale belongs to the week
    /// to date. Read against an AEDT host it moved back into Sunday - the previous week - which is the
    /// reported "this week" shortfall.
    /// </summary>
    [Fact]
    public async Task A_Monday_morning_sale_counts_in_the_Sydney_week_to_date_where_the_misread_instant_did_not()
    {
        // Monday 5 October 2026, 09:00 in Sydney (AEDT, +11).
        await SyncAsync(BusinessA, LastSalesPayload("2026-10-04T22:00:00"));
        var persisted = await SingleSaleAsync(BusinessA);

        var window = new FixedSydneyTime(EightOctoberAfternoonUtc).Window;
        var misread = new DateTime(2026, 10, 4, 22, 0, 0, DateTimeKind.Utc) - TimeSpan.FromHours(11);

        Assert.True(
            Contains(window.CurrentWeek, persisted.MachineAuthorizationTime),
            "A Monday-morning sale belongs to the week to date.");
        Assert.False(Contains(window.LastWeek, persisted.MachineAuthorizationTime));
        Assert.True(
            Contains(window.LastWeek, misread),
            "Read against an AEDT host the same sale moved into the previous week.");
    }

    /// <summary>
    /// Every Sydney calendar case an offset-free GMT value has to classify through, taken from the
    /// IANA timezone database rather than a fixed offset: either side of the Sydney midnight daylight
    /// saving began on in October 2026, the first instant after the hour Sydney skips, the operator's
    /// own 8 October sale, and both passes of the repeated hour when daylight saving ended.
    /// </summary>
    [Theory]
    // Saturday 3 October 2026 23:30 Sydney, still AEST (+10).
    [InlineData("2026-10-03T13:30:00", 2026, 10, 3)]
    // Sunday 4 October 00:00 and 00:30 Sydney, AEST, after Sydney midnight.
    [InlineData("2026-10-03T14:00:00", 2026, 10, 4)]
    [InlineData("2026-10-03T14:30:00", 2026, 10, 4)]
    // Sunday 4 October 03:00 Sydney AEDT (+11): the first instant after the 02:00-03:00 hour Sydney skips.
    [InlineData("2026-10-03T16:00:00", 2026, 10, 4)]
    // Sunday 4 October 23:30 Sydney AEDT.
    [InlineData("2026-10-04T12:30:00", 2026, 10, 4)]
    // Monday 5 October 00:30 Sydney AEDT, the first business day of the new week.
    [InlineData("2026-10-04T13:30:00", 2026, 10, 5)]
    // The operator's 8 October sale: Sydney 10:42:44.263 on Thursday 8 October.
    [InlineData("2026-10-07T23:42:44.263", 2026, 10, 8)]
    // Both passes of 02:30 on Sunday 5 April 2026, the ambiguous hour daylight saving ended in.
    [InlineData("2026-04-04T15:30:00", 2026, 4, 5)]
    [InlineData("2026-04-04T16:30:00", 2026, 4, 5)]
    // An ordinary AEST day: 09:00 on Wednesday 15 July 2026.
    [InlineData("2026-07-14T23:00:00", 2026, 7, 15)]
    public async Task An_offset_free_GMT_value_classifies_onto_the_Sydney_business_date_it_happened_on(
        string authorizationDateTimeGmt, int year, int month, int day)
    {
        await SyncAsync(BusinessA, LastSalesPayload(authorizationDateTimeGmt));

        var persisted = await SingleSaleAsync(BusinessA);
        Assert.Equal(
            DateTime.SpecifyKind(
                DateTime.Parse(authorizationDateTimeGmt, System.Globalization.CultureInfo.InvariantCulture),
                DateTimeKind.Utc),
            persisted.MachineAuthorizationTime);
        Assert.Equal(
            new DateTime(year, month, day),
            new FixedSydneyTime(persisted.MachineAuthorizationTime).Calendar
                .ToBusinessDate(persisted.MachineAuthorizationTime));
    }

    /// <summary>
    /// The rolling last-sales window returns the same transaction on every refresh. Re-synchronizing
    /// it must leave one row holding exactly the same instant - neither duplicated nor shifted a
    /// second time - including across separate contexts.
    /// </summary>
    [Fact]
    public async Task Repeated_sync_keeps_one_transaction_at_one_instant()
    {
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));
        // The same transaction as the live endpoint also renders it, with an explicit offset: still
        // the same physical instant, and still the same single row.
        await SyncAsync(BusinessA, LastSalesPayload("2026-10-08T10:42:44.263+11:00"));

        var persisted = await SingleSaleAsync(BusinessA);
        Assert.Equal(OperatorInstantUtc, persisted.MachineAuthorizationTime);
    }

    /// <summary>
    /// A Nayax <c>TransactionID</c> is unique only within the operator account that issued it, so two
    /// businesses synchronizing the same transaction each get their own sale - at the correct instant -
    /// and neither can see or overwrite the other's. Repeating both syncs changes nothing.
    /// </summary>
    [Fact]
    public async Task Repeated_sync_for_two_businesses_keeps_their_sales_separate_and_correctly_timed()
    {
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));
        await SyncAsync(BusinessB, LastSalesPayload(OperatorGmt));
        await SyncAsync(BusinessA, LastSalesPayload(OperatorGmt));
        await SyncAsync(BusinessB, LastSalesPayload(OperatorGmt));

        var a = await SingleSaleAsync(BusinessA);
        var b = await SingleSaleAsync(BusinessB);
        Assert.Equal(OperatorInstantUtc, a.MachineAuthorizationTime);
        Assert.Equal(OperatorInstantUtc, b.MachineAuthorizationTime);
        Assert.NotEqual(a.Id, b.Id);

        await using var unrestricted = TestAppDbContext.Unrestricted(_options);
        var all = await unrestricted.NayaxSales.AsNoTracking()
            .Where(sale => sale.TransactionID == TransactionId)
            .OrderBy(sale => sale.BusinessId)
            .ToListAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal([BusinessA, BusinessB], all.Select(sale => sale.BusinessId));
        Assert.All(all, sale => Assert.Equal(OperatorInstantUtc, sale.MachineAuthorizationTime));
    }

    /// <summary>
    /// Fail closed, per item: an authoritative timestamp that is absent, null, blank, unreadable,
    /// structurally wrong, or malformed-but-parseable is not imported at a defaulted, invented or
    /// guessed instant, and not imported at the machine-local wall clock the same item carries. The
    /// rolling window offers the transaction again on the next refresh.
    /// </summary>
    [Theory]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": null, "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "not-an-instant", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": 1760000000, "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    // Malformed but parseable: a permissive parse would have persisted an invented instant for each
    // of these - midnight UTC for the date-only value, 10 July for the ambiguous slash date.
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "2026-10-07", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "07/10/2026", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "07/10/2026 23:42:44", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    [InlineData("""[{ "TransactionID": 3564567268, "MachineID": 531595328, "SettlementValue": 3.0, "AuthorizationDateTimeGMT": "Wed, 07 Oct 2026 23:42:44 GMT", "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }]""")]
    public async Task A_sale_whose_authoritative_timestamp_cannot_be_read_is_not_imported(string payload)
    {
        await SyncAsync(BusinessA, payload);

        await using var read = TestAppDbContext.For(_options, BusinessA);
        Assert.Empty(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// One unreadable item must not cost the refresh the items around it. Before this change an
    /// unreadable value threw out of the JSON reader, so a single bad item discarded every machine's
    /// sales for that refresh; now the item is skipped and its neighbours import at their own
    /// authoritative instants.
    /// </summary>
    [Fact]
    public async Task An_unreadable_item_does_not_discard_the_rest_of_the_refresh()
    {
        await SyncAsync(BusinessA, $$"""
            [
              { "TransactionID": 1, "MachineID": {{MachineId}}, "SettlementValue": 3.0,
                "AuthorizationDateTimeGMT": "not-an-instant",
                "MachineAuthorizationTime": "2026-10-08T10:42:44.093" },
              { "TransactionID": {{TransactionId}}, "MachineID": {{MachineId}}, "SettlementValue": 3.0,
                "AuthorizationDateTimeGMT": "{{OperatorGmt}}",
                "MachineAuthorizationTime": "2026-10-08T10:42:44.093" }
            ]
            """);

        var persisted = await SingleSaleAsync(BusinessA);
        Assert.Equal(TransactionId, persisted.TransactionID);
        Assert.Equal(OperatorInstantUtc, persisted.MachineAuthorizationTime);
    }

    /// <summary>
    /// The operator's sanitized live payload item (issue #471), with the machine-local field left at
    /// the value they reported so a path that read it would be visibly wrong.
    /// </summary>
    private static string LastSalesPayload(string authorizationDateTimeGmt) => $$"""
        [
          {
            "TransactionID": {{TransactionId}},
            "MachineID": {{MachineId}},
            "MachineName": "Pavillion Right",
            "SettlementValue": 3.0000,
            "AuthorizationDateTimeGMT": "{{authorizationDateTimeGmt}}",
            "MachineAuthorizationTime": "2026-10-08T10:42:44.093",
            "SettlementDateTimeGMT": "{{authorizationDateTimeGmt}}"
          }
        ]
        """;

    /// <summary>
    /// One complete latest-sales synchronization for one business, through the real client and the
    /// real use case, over a stub transport that answers the documented endpoints.
    /// </summary>
    private async Task SyncAsync(int businessId, string lastSalesPayload)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        var rebuild = TestCostingUseCases.Rebuild(db);
        var store = new EfLatestNayaxSalesStore(db, TestCostingUseCases.CostSale(db, rebuild), rebuild);
        await new SyncLatestNayaxSales(CreateClient(lastSalesPayload), store).Handle(CancellationToken.None);
    }

    private static NayaxLynxClient CreateClient(string lastSalesPayload)
    {
        // A deliberately unroutable host: this test must never reach a real Nayax endpoint, and the
        // real client is used so the production ReadFromJsonAsync serializer options are what bind
        // the payload.
        var handler = new StubNayaxHandler(lastSalesPayload, MachineId);
        var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://nayax.invalid/operational/v1/"),
        };
        var credentials = new FakeNayaxRequestCredentialProvider(
            "test-operator", "fake-token-not-a-real-credential");
        return new NayaxLynxClient(http, credentials, NullLogger<NayaxLynxClient>.Instance);
    }

    private async Task<NayaxSales> SingleSaleAsync(int businessId)
    {
        await using var read = TestAppDbContext.For(_options, businessId);
        return Assert.Single(await read.NayaxSales.AsNoTracking().ToListAsync());
    }

    private static bool Contains(MachineDashboardPeriodUtc period, DateTime instant) =>
        instant >= period.StartUtc && instant <= period.EndUtc;

    /// <summary>
    /// Answers the two endpoints one synchronization calls: the operator's machine list, and that
    /// machine's last sales.
    /// </summary>
    private sealed class StubNayaxHandler : HttpMessageHandler
    {
        private readonly string _lastSalesPayload;
        private readonly long _machineId;

        public StubNayaxHandler(string lastSalesPayload, long machineId)
        {
            _lastSalesPayload = lastSalesPayload;
            _machineId = machineId;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var body = path.EndsWith("/lastSales", StringComparison.Ordinal)
                ? _lastSalesPayload
                : $$"""[{ "MachineID": {{_machineId}}, "MachineName": "Pavillion Right" }]""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request,
            });
        }
    }
}
