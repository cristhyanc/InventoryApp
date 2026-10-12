using Inventory.Application.Businesses;
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
/// The business-settings store of issue #548 over a real relational SQLite connection, with two
/// businesses in the database throughout.
///
/// <para>Four things are properties of the whole read-and-write path rather than of the validation
/// arithmetic (which <c>BusinessSettingsPolicyTests</c> covers):</para>
/// <list type="bullet">
///   <item>a save writes the resolved business's row and no other business's, which is what keeps
///   this write - on one of the deliberately global tables, where no query filter can scope it -
///   confined to the caller's own business;</item>
///   <item>the central <c>SaveChanges</c> enforcers still decide a stored time zone and a stored
///   ABN, because the write goes through <c>SaveChanges</c> on a tracked entity and not through a
///   set-based update that would pass both;</item>
///   <item>the reported change set describes what the write replaced, including recording and
///   clearing a value that was not recorded before;</item>
///   <item>and a saved time zone re-derives business days for data that was already stored -
///   including the business's own daylight-saving transition - without a single stored instant
///   changing.</item>
/// </list>
/// </summary>
public class BusinessSettingsPersistenceTests : IDisposable
{
    private const int SydneyBusiness = 1;
    private const int NewYorkBusiness = 2;
    private const string Sydney = "Australia/Sydney";
    private const string NewYork = "America/New_York";
    private const string ValidAbn = "51824753556";

    private static readonly DateTime CreatedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public BusinessSettingsPersistenceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business
            {
                Id = SydneyBusiness,
                Name = "Vending Sydney",
                TimeZoneId = Sydney,
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            },
            new Business
            {
                Id = NewYorkBusiness,
                Name = "Vending New York",
                TimeZoneId = NewYork,
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_read_reports_the_business_as_it_is_stored_including_what_is_not_recorded()
    {
        var settings = await ReadAsync(SydneyBusiness);

        Assert.NotNull(settings);
        Assert.Equal("Vending Sydney", settings.Name);
        Assert.Equal(Sydney, settings.TimeZoneId);
        Assert.Null(settings.ContactEmail);
        Assert.Null(settings.Abn);
        Assert.Null(settings.GstRegistered);
    }

    [Fact]
    public async Task The_read_answers_nothing_for_a_business_that_does_not_exist()
    {
        Assert.Null(await ReadAsync(99));
    }

    /// <summary>
    /// A save writes the business the resolved id names, and the other business's row is byte for
    /// byte what it was - name, zone and every profile column. The id is the one tenancy resolution
    /// produced, which is why this adapter takes a <see cref="BusinessId"/> and never a request
    /// value.
    /// </summary>
    [Fact]
    public async Task A_save_writes_the_resolved_business_and_leaves_the_other_untouched()
    {
        var result = await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Renamed Sydney", "owner@example.com", ValidAbn, true, NewYork));

        Assert.NotNull(result);
        var saved = await ReadAsync(SydneyBusiness);
        Assert.Equal(new BusinessSettings("Renamed Sydney", "owner@example.com", ValidAbn, true, NewYork), saved);

        var other = await ReadAsync(NewYorkBusiness);
        Assert.Equal(new BusinessSettings("Vending New York", null, null, null, NewYork), other);
    }

    [Fact]
    public async Task A_save_against_a_business_that_does_not_exist_writes_nothing()
    {
        var result = await SaveAsync(
            99,
            new BusinessSettings("Nowhere", "owner@example.com", null, true, Sydney));

        Assert.Null(result);
        Assert.Equal("Vending Sydney", (await ReadAsync(SydneyBusiness))!.Name);
        Assert.Equal("Vending New York", (await ReadAsync(NewYorkBusiness))!.Name);
    }

    [Fact]
    public async Task The_reported_change_set_describes_what_the_write_replaced()
    {
        var recorded = await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Vending Sydney", "owner@example.com", ValidAbn, false, Sydney));

        Assert.Equal(
            [
                BusinessSettingsField.ContactEmail,
                BusinessSettingsField.Abn,
                BusinessSettingsField.GstRegistered,
            ],
            recorded!.ChangedFields);

        var cleared = await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Vending Sydney", "owner@example.com", null, false, Sydney));

        Assert.Equal([BusinessSettingsField.Abn], cleared!.ChangedFields);
        Assert.Null((await ReadAsync(SydneyBusiness))!.Abn);

        var unchanged = await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Vending Sydney", "owner@example.com", null, false, Sydney));

        Assert.Empty(unchanged!.ChangedFields);
    }

    /// <summary>
    /// The write goes through <c>SaveChanges</c>, so the central time-zone enforcer still decides
    /// which ids may be stored - a set-based update would have written straight past it. Nothing is
    /// saved, not even the settings that were valid.
    /// </summary>
    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("AUS Eastern Standard Time")]
    [InlineData("Australia/Sydeny")]
    public async Task A_time_zone_the_host_cannot_resolve_is_refused_and_stores_nothing(string timeZoneId)
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Renamed Sydney", "owner@example.com", ValidAbn, true, timeZoneId)));

        Assert.Equal(
            new BusinessSettings("Vending Sydney", null, null, null, Sydney),
            await ReadAsync(SydneyBusiness));
    }

    /// <summary>
    /// The same for the ABN enforcer. The Domain policy refuses a malformed ABN before this store
    /// is reached, so this is the second line: a value that arrived any other way is still refused
    /// on the way into the database.
    /// </summary>
    [Fact]
    public async Task An_ABN_that_is_not_one_is_refused_and_stores_nothing()
    {
        await Assert.ThrowsAsync<DomainValidationException>(() => SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Renamed Sydney", "owner@example.com", "51824753557", true, Sydney)));

        Assert.Equal(
            new BusinessSettings("Vending Sydney", null, null, null, Sydney),
            await ReadAsync(SydneyBusiness));
    }

    /// <summary>
    /// The criterion this slice turns on: changing the zone re-derives the business days of data
    /// that is already stored, and no stored instant changes.
    ///
    /// <para>One instant, 15:00 UTC on 14 July 2026, is the business date 15 July in Sydney and 14
    /// July in New York - the ordinary-day version of the boundary every report date filter is
    /// built from. The sale row the test stores keeps exactly the instant it was given; only the
    /// business day it falls on moves, which is what "no stored data changes" means
    /// concretely.</para>
    /// </summary>
    [Fact]
    public async Task A_saved_time_zone_re_derives_the_business_days_of_data_already_stored()
    {
        var storedInstant = new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc);
        await StoreSaleAsync(SydneyBusiness, storedInstant);

        var before = await CalendarForAsync(SydneyBusiness, storedInstant);
        Assert.Equal(new DateTime(2026, 7, 15), before.ToBusinessDate(storedInstant));
        Assert.Equal(
            new DateTime(2026, 7, 14, 14, 0, 0, DateTimeKind.Utc),
            before.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));

        await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Vending Sydney", "owner@example.com", null, true, NewYork));

        var after = await CalendarForAsync(SydneyBusiness, storedInstant);
        Assert.Equal(new DateTime(2026, 7, 14), after.ToBusinessDate(storedInstant));
        Assert.Equal(
            new DateTime(2026, 7, 15, 4, 0, 0, DateTimeKind.Utc),
            after.StartOfBusinessDayUtc(new DateTime(2026, 7, 15)));

        // The stored sale is untouched: the boundaries moved, the history did not.
        Assert.Equal(storedInstant, await StoredSaleInstantAsync(SydneyBusiness));
    }

    /// <summary>
    /// And the new zone's own daylight-saving calendar applies to past dates too, not only to dates
    /// after the change: 8 March 2026 is a 23-hour business day for a business that is now in New
    /// York, and was an ordinary 24-hour one while it was in Sydney.
    /// </summary>
    [Fact]
    public async Task The_new_zones_daylight_saving_calendar_applies_to_past_business_days()
    {
        var transitionDay = new DateTime(2026, 3, 8);
        var now = new DateTime(2026, 7, 14, 15, 0, 0, DateTimeKind.Utc);

        var before = await CalendarForAsync(SydneyBusiness, now);
        Assert.Equal(24, HoursIn(before, transitionDay));

        await SaveAsync(
            SydneyBusiness,
            new BusinessSettings("Vending Sydney", "owner@example.com", null, true, NewYork));

        var after = await CalendarForAsync(SydneyBusiness, now);
        Assert.Equal(23, HoursIn(after, transitionDay));

        // The other business, which was already in New York, is unaffected by the change: one
        // business's zone is not a deployment-wide setting.
        var otherBusiness = await CalendarForAsync(NewYorkBusiness, now);
        Assert.Equal(23, HoursIn(otherBusiness, transitionDay));
    }

    private static double HoursIn(IBusinessCalendar calendar, DateTime businessDate) =>
        (calendar.StartOfBusinessDayUtc(businessDate.AddDays(1))
            - calendar.StartOfBusinessDayUtc(businessDate)).TotalHours;

    private async Task<BusinessSettings?> ReadAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfBusinessSettingsStore(db)
            .FindAsync(BusinessId.From(businessId), CancellationToken.None);
    }

    private async Task<BusinessSettingsSaveResult?> SaveAsync(int businessId, BusinessSettings submitted)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await new EfBusinessSettingsStore(db)
            .SaveAsync(BusinessId.From(businessId), submitted, CancellationToken.None);
    }

    /// <summary>
    /// The calendar a request for <paramref name="businessId"/> would carry: the zone read from
    /// that business's own record, published into the per-request scope exactly as the API
    /// boundary's middleware publishes it.
    /// </summary>
    private async Task<IBusinessCalendar> CalendarForAsync(int businessId, DateTime nowUtc)
    {
        var timeZoneScope = new BusinessTimeZoneScope();
        if (await ReadAsync(businessId) is { TimeZoneId.Length: > 0 } settings)
        {
            timeZoneScope.Resolve(settings.TimeZoneId);
        }

        return new CurrentBusinessCalendar(new FakeClock(nowUtc), timeZoneScope);
    }

    /// <summary>One already-stored sale, so "data already stored" is a row and not a hypothesis.</summary>
    private async Task StoreSaleAsync(int businessId, DateTime machineAuthorizationTimeUtc)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 1,
            MachineID = 1,
            MachineAuthorizationTime = machineAuthorizationTimeUtc,
            TransactionStatusId = 12,
        });
        await db.SaveChangesAsync();
    }

    private async Task<DateTime?> StoredSaleInstantAsync(int businessId)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await db.NayaxSales
            .AsNoTracking()
            .Select(sale => sale.MachineAuthorizationTime)
            .SingleOrDefaultAsync();
    }
}
