using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Two businesses in one database, each in its own time zone (issue #499), over a real relational
/// SQLite connection.
///
/// Three things are under test, and all three are properties of the whole read-and-write path
/// rather than of the conversion arithmetic (which <c>ZonedBusinessCalendarTests</c> covers):
/// <list type="bullet">
///   <item>a business's zone comes back from its own record, so two businesses sharing a
///   deployment get isolated business-date boundaries - including on a daylight-saving transition
///   day in each of their two opposite daylight-saving calendars;</item>
///   <item>a zone the host cannot resolve is refused on the way in, by whichever path writes a
///   business, and nothing is stored;</item>
///   <item>a business whose stored zone has been corrupted outside the application fails closed on
///   read instead of silently reporting in another business's calendar.</item>
/// </list>
/// </summary>
public class PerBusinessTimeZonePersistenceTests : IDisposable
{
    private const int SydneyBusiness = 1;
    private const int NewYorkBusiness = 2;
    private const string Sydney = "Australia/Sydney";
    private const string NewYork = "America/New_York";

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public PerBusinessTimeZonePersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = SydneyBusiness, Name = "Vending Sydney", TimeZoneId = Sydney, CreatedAtUtc = CreatedAt },
            new Business { Id = NewYorkBusiness, Name = "Vending New York", TimeZoneId = NewYork, CreatedAtUtc = CreatedAt });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<string?> StoredTimeZoneOfAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        var profile = await new EfBusinessProfileStore(db)
            .FindAsync(BusinessId.From(businessId), CancellationToken.None);
        return profile?.TimeZoneId;
    }

    /// <summary>
    /// The calendar a request for <paramref name="businessId"/> would carry: the zone read from
    /// that business's own record, published into the per-request scope exactly as the API
    /// boundary's middleware publishes it.
    /// </summary>
    private async Task<IBusinessCalendar> CalendarForAsync(int businessId, DateTime nowUtc)
    {
        var timeZoneScope = new BusinessTimeZoneScope();
        if (await StoredTimeZoneOfAsync(businessId) is { Length: > 0 } timeZoneId)
        {
            timeZoneScope.Resolve(timeZoneId);
        }

        return new CurrentBusinessCalendar(new FakeClock(nowUtc), timeZoneScope);
    }

    [Fact]
    public async Task Each_business_reads_its_own_time_zone_and_never_the_others()
    {
        Assert.Equal(Sydney, await StoredTimeZoneOfAsync(SydneyBusiness));
        Assert.Equal(NewYork, await StoredTimeZoneOfAsync(NewYorkBusiness));
    }

    /// <summary>
    /// One instant, one deployment, two businesses: each gets the business date and the business-day
    /// UTC boundaries of its own calendar. 15:00 UTC on 14 July is already the 15th in Sydney and
    /// still the 14th in New York, which is the ordinary-day version of the boundary every report
    /// date filter is built from.
    /// </summary>
    [Fact]
    public async Task Two_businesses_get_isolated_business_dates_for_the_same_instant()
    {
        var nowUtc = new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc);

        var sydney = await CalendarForAsync(SydneyBusiness, nowUtc);
        var newYork = await CalendarForAsync(NewYorkBusiness, nowUtc);

        Assert.Equal(new DateTime(2026, 7, 15), sydney.Today);
        Assert.Equal(new DateTime(2026, 7, 14), newYork.Today);
        Assert.Equal(new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc),
            sydney.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
        Assert.Equal(new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc),
            newYork.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));
    }

    /// <summary>
    /// Each business's own daylight-saving transition day, resolved from its own stored zone: Sydney
    /// loses an hour on 4 October 2026 and New York loses one on 8 March 2026, and neither
    /// business's transition moves the other's business day by a second.
    /// </summary>
    [Theory]
    [InlineData(SydneyBusiness, 2026, 10, 4, 23)]
    [InlineData(SydneyBusiness, 2026, 4, 5, 25)]
    [InlineData(NewYorkBusiness, 2026, 3, 8, 23)]
    [InlineData(NewYorkBusiness, 2026, 11, 1, 25)]
    public async Task A_transition_day_is_the_right_length_for_the_business_whose_zone_it_belongs_to(
        int businessId, int year, int month, int day, int expectedHours)
    {
        var transitionDay = new DateTime(year, month, day);
        var calendar = await CalendarForAsync(businessId, new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));
        var otherBusinessId = businessId == SydneyBusiness ? NewYorkBusiness : SydneyBusiness;
        var otherCalendar = await CalendarForAsync(
            otherBusinessId, new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));

        var start = calendar.StartOfBusinessDayUtc(transitionDay);
        var nextStart = calendar.StartOfBusinessDayUtc(transitionDay.AddDays(1));

        Assert.Equal(expectedHours, (nextStart - start).TotalHours);
        Assert.Equal(transitionDay, calendar.ToBusinessDate(start));
        Assert.Equal(transitionDay, calendar.ToBusinessDate(nextStart.AddMilliseconds(-1)));

        // The other business's day is an ordinary 24 hours: one business's transition is not a
        // deployment-wide event.
        var otherStart = otherCalendar.StartOfBusinessDayUtc(transitionDay);
        Assert.Equal(24, (otherCalendar.StartOfBusinessDayUtc(transitionDay.AddDays(1)) - otherStart).TotalHours);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Australia/Sydeny")]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("AUS Eastern Standard Time")]
    public async Task A_business_cannot_be_created_with_a_time_zone_the_host_cannot_resolve(string timeZoneId)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        db.Businesses.Add(new Business
        {
            Id = 3,
            Name = "Vending Nowhere",
            TimeZoneId = timeZoneId,
            CreatedAtUtc = CreatedAt,
        });

        await Assert.ThrowsAsync<DomainValidationException>(() => db.SaveChangesAsync());

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Null(await verify.Businesses.AsNoTracking().SingleOrDefaultAsync(b => b.Id == 3));
    }

    [Fact]
    public async Task An_existing_businesss_time_zone_cannot_be_changed_to_an_unresolvable_one()
    {
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var business = await db.Businesses.SingleAsync(b => b.Id == SydneyBusiness);
            business.TimeZoneId = "Nowhere/At_All";

            await Assert.ThrowsAsync<DomainValidationException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(Sydney, await StoredTimeZoneOfAsync(SydneyBusiness));
    }

    /// <summary>
    /// The read path's own guard, for a value the write path could not have produced: a zone
    /// corrupted directly in the database. The request derives no business date at all rather than
    /// falling back to Sydney, to the host's zone, or to the other business's calendar.
    /// </summary>
    [Fact]
    public async Task A_business_whose_stored_zone_is_unresolvable_fails_closed_on_read()
    {
        await using (var corrupt = _connection.CreateCommand())
        {
            corrupt.CommandText =
                $"UPDATE Businesses SET TimeZoneId = 'Nowhere/At_All' WHERE Id = {NewYorkBusiness};";
            await corrupt.ExecuteNonQueryAsync();
        }

        var calendar = await CalendarForAsync(NewYorkBusiness, new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));

        var failure = Assert.Throws<BusinessTimeZoneUnavailableException>(() => calendar.Today);
        Assert.Equal("Nowhere/At_All", failure.TimeZoneId);

        // The other business is unaffected: one broken record is not a deployment-wide outage.
        var sydney = await CalendarForAsync(SydneyBusiness, new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc));
        Assert.Equal(new DateTime(2026, 7, 15), sydney.Today);
    }

    /// <summary>
    /// A business id another business's caller never produces still cannot be used to read that
    /// business's zone through a request: the id a request carries is the one tenancy resolution
    /// produced from its own membership, which is why this adapter takes a
    /// <see cref="BusinessId"/> and never a request value.
    /// </summary>
    [Fact]
    public async Task The_profile_store_answers_nothing_for_a_business_that_does_not_exist()
    {
        await using var db = TestAppDbContext.For(_options, SydneyBusiness);

        var profile = await new EfBusinessProfileStore(db)
            .FindAsync(BusinessId.From(99), CancellationToken.None);

        Assert.Null(profile);
    }
}
