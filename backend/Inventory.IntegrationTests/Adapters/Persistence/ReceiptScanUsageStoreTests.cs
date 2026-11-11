using Inventory.Application.PlatformReceiptScans;
using Inventory.Application.Time;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The monthly receipt-scan counter against real SQLite (issue #1164, sub-issue C of #975): the cap
/// under concurrency, the month boundary, tenant isolation, and the Super Admin's business-pinned
/// read of one business's count.
///
/// <para>Every writer here gets its own connection to a real database <strong>file</strong>, exactly
/// as <see cref="AttachmentStorageQuotaConcurrencyTests"/> does: the race the cap exists for cannot be
/// shown with a shared in-memory database - that is one connection - or with the EF InMemory
/// provider, which has no locking at all.</para>
/// </summary>
public sealed class ReceiptScanUsageStoreTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    /// <summary>November 2026, as <c>ReceiptScanQuotaPolicy</c> keys a month.</summary>
    private const int November = 202611;

    /// <summary>And December, so the boundary is crossed as arithmetic rather than by waiting.</summary>
    private const int December = 202612;

    private static readonly DateTime Now = new(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-receipt-scan-usage-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;
    private readonly FakeClock _clock = new(Now);

    public ReceiptScanUsageStoreTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();
        db.Businesses.AddRange(
            new Business
            {
                Id = BusinessA,
                Name = "Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Now,
            },
            new Business
            {
                Id = BusinessB,
                Name = "Second Vending Co",
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = Now,
            });
        db.SaveChanges();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_databasePath)) File.Delete(_databasePath);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A month with no row has made no scans, and the first one creates the row at <c>1</c> rather
    /// than leaving a month to be interpreted from an absence.
    /// </summary>
    [Fact]
    public async Task A_month_with_no_row_has_used_nothing_and_the_first_scan_creates_it()
    {
        Assert.Equal(0, await ReadUsedAsync(BusinessA, November));

        Assert.Equal(1, await CountAsync(BusinessA, November));
        Assert.Equal(1, await ReadUsedAsync(BusinessA, November));

        await using var verify = TestAppDbContext.Unrestricted(Options());
        var row = await verify.ReceiptScanUsages.AsNoTracking().SingleAsync();
        Assert.Equal(BusinessA, row.BusinessId);
        Assert.Equal(November, row.MonthKey);
        Assert.Equal(1, row.ScanCount);
        Assert.Equal(Now, row.CreatedAtUtc);
        Assert.Equal(Now, row.UpdatedAtUtc);
    }

    /// <summary>
    /// The issue's first and second criteria against the database: the cap is reached exactly at the
    /// cap, the refusal writes nothing, and a refusal does not move the count.
    /// </summary>
    [Fact]
    public async Task The_cap_is_reached_at_the_cap_and_a_refusal_writes_nothing()
    {
        await SeedAsync(BusinessA, November, PlanCatalogue.MonthlyReceiptScanLimit - 1);

        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, await CountAsync(BusinessA, November));
        Assert.Null(await CountAsync(BusinessA, November));
        Assert.Null(await CountAsync(BusinessA, November));

        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, await ReadUsedAsync(BusinessA, November));

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Single(await verify.ReceiptScanUsages.ToListAsync());
    }

    /// <summary>
    /// <strong>The issue's concurrency criterion.</strong> Two scans at once with one scan left:
    /// exactly one reaches the reader, because the read of the counter, its comparison with the cap
    /// and its increment all happen inside one <c>BEGIN IMMEDIATE</c> transaction. Without that write
    /// lock around the read both would see 99 and both would be allowed, taking the business to 101
    /// scans and 101 payments.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_scans_with_one_left_count_exactly_one()
    {
        await SeedAsync(BusinessA, November, PlanCatalogue.MonthlyReceiptScanLimit - 1);

        var (first, second) = await RaceAsync(
            () => CountAnswerAsync(BusinessA, November),
            () => CountAnswerAsync(BusinessA, November));

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);

        var counted = new[] { first.Counted, second.Counted }.Where(count => count is not null).ToList();
        Assert.Single(counted);
        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, counted[0]);

        // And the stored count never went past the cap, which is the thing that actually costs money.
        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, await ReadUsedAsync(BusinessA, November));
    }

    /// <summary>
    /// And the same race from an empty month: two scans at once both fit, and neither is lost to the
    /// other - the counter ends at two rather than at one, and there is still exactly one row.
    /// </summary>
    [Fact]
    public async Task Two_simultaneous_scans_with_room_are_both_counted_once_each()
    {
        var (first, second) = await RaceAsync(
            () => CountAnswerAsync(BusinessA, November),
            () => CountAnswerAsync(BusinessA, November));

        Assert.Null(first.Failure);
        Assert.Null(second.Failure);
        Assert.Equal([1, 2], new[] { first.Counted, second.Counted }.Order());
        Assert.Equal(2, await ReadUsedAsync(BusinessA, November));

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Single(await verify.ReceiptScanUsages.ToListAsync());
    }

    /// <summary>
    /// The issue's month-boundary criterion: a business that has used its whole November allowance
    /// starts December at zero, and November's count is left exactly as it was - a month that has
    /// passed keeps its count as the evidence of what was spent.
    /// </summary>
    [Fact]
    public async Task The_next_month_starts_again_at_zero_and_leaves_the_last_one_alone()
    {
        await SeedAsync(BusinessA, November, PlanCatalogue.MonthlyReceiptScanLimit);

        Assert.Null(await CountAsync(BusinessA, November));
        Assert.Equal(0, await ReadUsedAsync(BusinessA, December));
        Assert.Equal(1, await CountAsync(BusinessA, December));

        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, await ReadUsedAsync(BusinessA, November));
        Assert.Equal(1, await ReadUsedAsync(BusinessA, December));
    }

    /// <summary>
    /// The issue's tenant criterion: usage is per business, and another business's scans never count.
    /// One business at its cap does not stop the other from scanning, and neither can read the
    /// other's count at all - the store takes no business id, so what each one sees is the query
    /// filter's answer.
    /// </summary>
    [Fact]
    public async Task Usage_is_per_business_and_one_business_never_counts_against_another()
    {
        await SeedAsync(BusinessA, November, PlanCatalogue.MonthlyReceiptScanLimit);

        Assert.Null(await CountAsync(BusinessA, November));
        Assert.Equal(1, await CountAsync(BusinessB, November));

        Assert.Equal(PlanCatalogue.MonthlyReceiptScanLimit, await ReadUsedAsync(BusinessA, November));
        Assert.Equal(1, await ReadUsedAsync(BusinessB, November));

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Equal(
            [(BusinessA, PlanCatalogue.MonthlyReceiptScanLimit), (BusinessB, 1)],
            await verify.ReceiptScanUsages
                .AsNoTracking()
                .OrderBy(row => row.BusinessId)
                .Select(row => ValueTuple.Create(row.BusinessId, row.ScanCount))
                .ToListAsync());
    }

    /// <summary>
    /// One business cannot hold two counters for one month: the unique index over
    /// <c>(BusinessId, MonthKey)</c> is the backstop under the comparison, because a month with two
    /// counters is a month whose cap no single comparison can enforce.
    /// </summary>
    [Fact]
    public async Task A_month_cannot_come_to_hold_two_counters_for_one_business()
    {
        await SeedAsync(BusinessA, November, 1);

        await using var db = TestAppDbContext.Unrestricted(Options());
        db.ReceiptScanUsages.Add(new ReceiptScanUsage
        {
            BusinessId = BusinessA,
            MonthKey = November,
            ScanCount = 1,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    /// <summary>
    /// And the other half of that index: two businesses may each hold the same month, which is what
    /// makes the uniqueness per business rather than global (AGENTS.md § Tenant ownership and data
    /// isolation).
    /// </summary>
    [Fact]
    public async Task Two_businesses_may_each_hold_the_same_month()
    {
        await SeedAsync(BusinessA, November, 3);
        await SeedAsync(BusinessB, November, 4);

        await using var verify = TestAppDbContext.Unrestricted(Options());
        Assert.Equal(2, await verify.ReceiptScanUsages.CountAsync());
    }

    /// <summary>
    /// The Super Admin's own read (the issue's last criterion): one business's count for the month
    /// that business is in, under a <strong>denied</strong> scope where the central filter matches
    /// none of its rows. It answers about the business the route named and about no other.
    /// </summary>
    [Fact]
    public async Task The_platform_read_answers_for_the_named_business_only()
    {
        await SeedAsync(BusinessA, November, 7);
        await SeedAsync(BusinessB, November, 11);

        var first = await PlatformUsageAsync(BusinessA);
        var second = await PlatformUsageAsync(BusinessB);

        Assert.Equal(new PlatformReceiptScanUsage(BusinessA, November, 7), first);
        Assert.Equal(new PlatformReceiptScanUsage(BusinessB, November, 11), second);
    }

    /// <summary>
    /// A business that has never scanned has used none of its allowance, which is zero rather than an
    /// absent business - and an id that matches no business row is the absence, which the endpoint
    /// answers as a <c>404</c>.
    /// </summary>
    [Fact]
    public async Task The_platform_read_separates_no_scans_from_no_business()
    {
        Assert.Equal(new PlatformReceiptScanUsage(BusinessA, November, 0), await PlatformUsageAsync(BusinessA));
        Assert.Null(await PlatformUsageAsync(9_999));
    }

    /// <summary>
    /// And the month it answers for is the <strong>business's own</strong>. At 9am UTC on
    /// 30 November, Sydney is already on 30 November and so is Honolulu's 29th - but an hour after
    /// midnight Sydney on 1 December, Sydney is in December while UTC and Honolulu are still in
    /// November. The business in Sydney is answered for December and the one in Honolulu for
    /// November, from the same instant, which is what makes this the business's calendar month rather
    /// than the host's (AGENTS.md § Reporting dates and filters).
    /// </summary>
    [Fact]
    public async Task The_platform_read_uses_each_businesss_own_month()
    {
        await SetTimeZoneAsync(BusinessB, "Pacific/Honolulu");

        // 13:30 UTC on 30 November 2026 is 00:30 on 1 December in Sydney (UTC+11 in daylight saving)
        // and 03:30 on 30 November in Honolulu (UTC-10).
        _clock.UtcNow = new DateTime(2026, 11, 30, 13, 30, 0, DateTimeKind.Utc);

        await SeedAsync(BusinessA, December, 5);
        await SeedAsync(BusinessB, November, 6);

        Assert.Equal(new PlatformReceiptScanUsage(BusinessA, December, 5), await PlatformUsageAsync(BusinessA));
        Assert.Equal(new PlatformReceiptScanUsage(BusinessB, November, 6), await PlatformUsageAsync(BusinessB));
    }

    /// <summary>
    /// A business whose stored zone this host cannot resolve <strong>fails closed</strong> rather than
    /// being answered in <c>Australia/Sydney</c>, the host's zone or UTC: a count reported against
    /// the wrong month is a count reported against the wrong cap.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_time_zone_fails_closed_rather_than_guessing_a_month()
    {
        // Written around the write path on purpose: BusinessTimeZoneEnforcer refuses to store a zone
        // the host cannot resolve, so the only way to reach this state is the one a corrupted row or
        // a host with a different time-zone database would - which is exactly the state the read
        // path's own guard exists for.
        await CorruptTimeZoneAsync(BusinessA, "Nowhere/At_All");

        await Assert.ThrowsAsync<BusinessTimeZoneUnavailableException>(
            () => PlatformUsageAsync(BusinessA));
    }

    private async Task<int?> CountAsync(int businessId, int monthKey)
    {
        await using var db = TestAppDbContext.For(Options(), businessId);
        return await new EfReceiptScanUsageStore(db, _clock).TryCountScanAsync(
            monthKey, PlanCatalogue.MonthlyReceiptScanLimit, CancellationToken.None);
    }

    private async Task<CountAnswer> CountAnswerAsync(int businessId, int monthKey)
    {
        try
        {
            return new CountAnswer(await CountAsync(businessId, monthKey), null);
        }
        catch (Exception failure)
        {
            return new CountAnswer(null, failure);
        }
    }

    private async Task<int> ReadUsedAsync(int businessId, int monthKey)
    {
        await using var db = TestAppDbContext.For(Options(), businessId);
        return await new EfReceiptScanUsageStore(db, _clock).ReadUsedAsync(monthKey, CancellationToken.None);
    }

    /// <summary>
    /// The platform read, on a context with <strong>no</strong> business resolved - which is the
    /// scope a Super Admin request really carries, and the reason the adapter needs its one
    /// business-pinned filter-ignoring statement at all.
    /// </summary>
    private async Task<PlatformReceiptScanUsage?> PlatformUsageAsync(int businessId)
    {
        await using var db = TestAppDbContext.Denied(Options());
        return await new EfPlatformReceiptScanUsageStore(db, _clock).FindAsync(
            businessId, CancellationToken.None);
    }

    private async Task SeedAsync(int businessId, int monthKey, int scanCount)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        db.ReceiptScanUsages.Add(new ReceiptScanUsage
        {
            BusinessId = businessId,
            MonthKey = monthKey,
            ScanCount = scanCount,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetTimeZoneAsync(int businessId, string timeZoneId)
    {
        await using var db = TestAppDbContext.Unrestricted(Options());
        (await db.Businesses.SingleAsync(business => business.Id == businessId)).TimeZoneId = timeZoneId;
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A zone the write path would refuse, put there the way a corrupted row would be. Parameterised
    /// rather than interpolated, as every statement in these tests is.
    /// </summary>
    private async Task CorruptTimeZoneAsync(int businessId, string timeZoneId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Businesses SET TimeZoneId = $zone WHERE Id = $id;";
        command.Parameters.AddWithValue("$zone", timeZoneId);
        command.Parameters.AddWithValue("$id", businessId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Releases both operations at the same instant and returns what each answered.</summary>
    private static async Task<(T First, T Second)> RaceAsync<T>(Func<Task<T>> first, Func<Task<T>> second)
    {
        using var barrier = new Barrier(2);

        var answers = new[] { first, second }
            .Select(operation => Task.Run(async () =>
            {
                barrier.SignalAndWait();
                return await operation();
            }))
            .ToArray();

        var resolved = await Task.WhenAll(answers);
        return (resolved[0], resolved[1]);
    }

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>What one count attempt answered: the new count, or the failure.</summary>
    private sealed record CountAnswer(int? Counted, Exception? Failure);
}
