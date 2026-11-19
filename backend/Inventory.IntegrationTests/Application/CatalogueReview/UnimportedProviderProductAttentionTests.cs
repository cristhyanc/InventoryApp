using Inventory.Application.CatalogueReview;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.CatalogueReview;

/// <summary>
/// The Dashboard's "Nayax products are selling but aren't in your products" read (issue #1352),
/// against a relational database and the real <c>Australia/Sydney</c> calendar.
///
/// <para><strong>Relational, and with the real zoned calendar, deliberately.</strong> Three of the
/// things asserted here are properties of the database and of the time zone rather than of any one
/// call: that the count is of <em>distinct</em> provider ids, that the central tenant query filter -
/// and not a predicate of the adapter's own - is what keeps another business's sales out, and that
/// the window's first instant is the start of a Sydney calendar date whose UTC offset may differ from
/// today's. None of those could be told apart from a lucky in-memory grouping or a fixed-offset
/// subtraction.</para>
///
/// <para>Two businesses, both in Sydney, with the clock pinned: business A is the caller throughout
/// and business B exists only so that its sales can be proved invisible.</para>
/// </summary>
public sealed class UnimportedProviderProductAttentionTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    private const long Monster = 901;
    private const long CokeZero = 902;
    private const long Chips = 903;
    private const long Water = 904;

    /// <summary>14:00 on 18 November 2026 in Sydney (AEDT, UTC+11), so "today" is the 18th.</summary>
    private static readonly DateTime NowUtc = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// 14:00 on 20 October 2026 in Sydney, chosen so the thirty-date window reaches back to 21
    /// September - before Sydney moved its clocks forward on 4 October 2026 - and therefore starts in
    /// AEST (UTC+10) while today is in AEDT (UTC+11).
    /// </summary>
    private static readonly DateTime DaylightSavingNowUtc = new(2026, 10, 20, 3, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;

    public UnimportedProviderProductAttentionTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(Options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(Business(BusinessA, "Vending A"), Business(BusinessB, "Vending B"));
        setup.SaveChanges();
    }

    private DbContextOptions<AppDbContext> Options { get; }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The core case: one Nayax sale in the window whose provider product id no product carries. The
    /// count is one and the example is named from the sale's own stored product name.
    /// </summary>
    [Fact]
    public async Task A_sale_of_a_provider_product_no_product_carries_is_counted_and_named()
    {
        await SeedAsync(Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1)));

        var attention = await ReadAsync();

        Assert.Equal(1, attention.UnimportedProductCount);
        var example = Assert.Single(attention.Examples);
        Assert.Equal(NayaxExternalId.From(Monster), example.NayaxProductId);
        Assert.Equal("Monster Original", example.ProductName);
    }

    /// <summary>
    /// The example's name is the <em>latest</em> qualifying sale's, because that is the provider's
    /// most recent name for the product the operator is about to import.
    /// </summary>
    [Fact]
    public async Task The_example_takes_the_latest_sales_stored_name()
    {
        await SeedAsync(
            Unresolved(1, Monster, "Monster", NowUtc.AddDays(-5)),
            Unresolved(2, Monster, "Monster Original 500ml", NowUtc.AddDays(-1)));

        var attention = await ReadAsync();

        Assert.Equal("Monster Original 500ml", Assert.Single(attention.Examples).ProductName);
    }

    /// <summary>Two sales of one provider product are one missing product, not two.</summary>
    [Fact]
    public async Task Two_sales_of_the_same_provider_product_count_once()
    {
        await SeedAsync(
            Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-3)),
            Unresolved(2, Monster, "Monster Original", NowUtc.AddDays(-2)));

        var attention = await ReadAsync();

        Assert.Equal(1, attention.UnimportedProductCount);
        Assert.Single(attention.Examples);
    }

    /// <summary>
    /// What clears the alert is a <em>product</em> carrying the provider's id - the link or create
    /// Import products performs. The old sales stay unresolved, because attributing them is the
    /// Owner's separate step, and the count is nevertheless zero: there is nothing left to import.
    /// </summary>
    [Fact]
    public async Task A_product_that_carries_the_provider_id_clears_the_count_with_the_sales_still_unresolved()
    {
        await SeedAsync(Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1)));

        await using (var link = Scoped())
        {
            link.Products.Add(Product("Monster Original", Monster));
            await link.SaveChangesAsync();
        }

        var attention = await ReadAsync();

        Assert.Equal(0, attention.UnimportedProductCount);
        Assert.Empty(attention.Examples);

        await using var verify = Scoped();
        Assert.All(await verify.NayaxSales.AsNoTracking().ToListAsync(), sale => Assert.Null(sale.ProductId));
    }

    /// <summary>
    /// A retired product still carries the provider's id, so the product <em>is</em> imported and the
    /// alert must not ask for it again. Reactivating it is a different decision from importing it.
    /// </summary>
    [Fact]
    public async Task A_retired_product_that_carries_the_provider_id_clears_the_count()
    {
        await SeedAsync(Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1)));

        await using (var retire = Scoped())
        {
            var product = Product("Monster Original", Monster);
            product.IsActive = false;
            retire.Products.Add(product);
            await retire.SaveChangesAsync();
        }

        Assert.Equal(0, (await ReadAsync()).UnimportedProductCount);
    }

    /// <summary>
    /// Every qualifying condition, one disqualified sale at a time. Each of these sales is in the
    /// window and carries a provider id nothing local holds, and not one of them is evidence that a
    /// product is missing: a sale that already has a local product is not waiting on an import, a
    /// provenance-unknown number may be either a provider id or the local product id the old
    /// synchronization wrote, a sale of another provider says nothing about Nayax's catalogue, and a
    /// sale with no provider product id names nothing to import.
    /// </summary>
    [Fact]
    public async Task A_sale_that_fails_any_one_condition_is_not_counted()
    {
        var alreadyResolved = Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1));
        var provenanceUnknown = Unresolved(2, CokeZero, "Coke Zero 600ml", NowUtc.AddDays(-1));
        provenanceUnknown.ProviderProductIdIsRaw = false;
        var otherProvider = Unresolved(3, Chips, "Chips", NowUtc.AddDays(-1));
        // Nayax is the only declared provider, so "not Nayax" is written as the undeclared stored
        // value the enum deliberately leaves unreachable by name: a stored zero is not a provider,
        // and this read must not treat one as Nayax's.
        otherProvider.Provider = (ExternalProvider)0;
        var noProviderProduct = Unresolved(4, null, "Unnamed", NowUtc.AddDays(-1));

        await using (var setup = Scoped())
        {
            // A local product the first sale is attributed to. It carries no provider id, so the
            // sale's own NayaxProductId is one no product holds - and the sale still has a product.
            var product = Product("Something else", externalProductId: null);
            setup.Products.Add(product);
            await setup.SaveChangesAsync();
            alreadyResolved.ProductId = product.Id;
            setup.NayaxSales.AddRange(alreadyResolved, provenanceUnknown, otherProvider, noProviderProduct);
            await setup.SaveChangesAsync();
        }

        var attention = await ReadAsync();

        Assert.Equal(0, attention.UnimportedProductCount);
        Assert.Empty(attention.Examples);
    }

    /// <summary>
    /// The window is thirty Sydney calendar dates including today: the first instant of the thirtieth
    /// date back is counted, the last instant of the date before it is not, and a sale earlier today
    /// is counted.
    /// </summary>
    [Fact]
    public async Task The_window_is_thirty_sydney_dates_including_today()
    {
        // 00:00:00 Sydney on 20 October 2026, which is today - 29.
        var firstInstant = new DateTime(2026, 10, 19, 13, 0, 0, DateTimeKind.Utc);

        await SeedAsync(
            Unresolved(1, Monster, "Monster Original", firstInstant),
            Unresolved(2, CokeZero, "Coke Zero 600ml", firstInstant.AddSeconds(-1)),
            Unresolved(3, Chips, "Chips", NowUtc.AddHours(-2)));

        var attention = await ReadAsync();

        Assert.Equal(2, attention.UnimportedProductCount);
        Assert.Equal(
            [NayaxExternalId.From(Monster), NayaxExternalId.From(Chips)],
            attention.Examples.Select(example => example.NayaxProductId).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The same boundary across a daylight-saving change. The window's first date, 21 September 2026,
    /// is in AEST (UTC+10) while today, 20 October, is in AEDT (UTC+11): its midnight is
    /// <c>20 September 14:00Z</c>, not <c>21 September 03:00Z</c>, so a subtraction of hours from
    /// "now" would wrongly drop the first hour of that date.
    /// </summary>
    [Fact]
    public async Task The_window_crosses_a_daylight_saving_change_without_losing_its_first_date()
    {
        // 00:00:00 Sydney on 21 September 2026 (AEST, UTC+10), which is today - 29.
        var firstInstant = new DateTime(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc);

        await SeedAsync(
            Unresolved(1, Monster, "Monster Original", firstInstant),
            Unresolved(2, CokeZero, "Coke Zero 600ml", firstInstant.AddSeconds(-1)));

        var attention = await ReadAsync(DaylightSavingNowUtc);

        Assert.Equal(1, attention.UnimportedProductCount);
        Assert.Equal(
            NayaxExternalId.From(Monster), Assert.Single(attention.Examples).NayaxProductId);
    }

    /// <summary>
    /// At most three products are named, most recently sold first, and the count still reports every
    /// one of them so the alert can say "and N more".
    /// </summary>
    [Fact]
    public async Task At_most_three_products_are_named_most_recently_sold_first()
    {
        await SeedAsync(
            Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1)),
            Unresolved(2, CokeZero, "Coke Zero 600ml", NowUtc.AddDays(-2)),
            Unresolved(3, Chips, "Chips", NowUtc.AddDays(-3)),
            Unresolved(4, Water, "Water 600ml", NowUtc.AddDays(-4)));

        var attention = await ReadAsync();

        Assert.Equal(4, attention.UnimportedProductCount);
        Assert.Equal(
            [NayaxExternalId.From(Monster), NayaxExternalId.From(CokeZero), NayaxExternalId.From(Chips)],
            attention.Examples.Select(example => example.NayaxProductId));
    }

    /// <summary>
    /// A sale that stored no product name is still a product to import; the name is simply not known,
    /// and is answered as <see langword="null"/> rather than as an invented one.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_stored_name_is_counted_with_no_name()
    {
        var nameless = Unresolved(1, Monster, "Monster Original", NowUtc.AddDays(-1));
        nameless.ProductName = null;

        await SeedAsync(nameless);

        var attention = await ReadAsync();

        Assert.Equal(1, attention.UnimportedProductCount);
        Assert.Null(Assert.Single(attention.Examples).ProductName);
    }

    /// <summary>
    /// Another business's sales are never counted, and this is what the central query filter is for:
    /// the adapter carries no business predicate of its own, so a context scoped to business A simply
    /// cannot see business B's rows - nor is business B's own product allowed to clear business A's
    /// alert.
    /// </summary>
    [Fact]
    public async Task Another_businesses_sales_and_products_are_never_counted()
    {
        await using (var other = TestAppDbContext.For(Options, BusinessB))
        {
            other.Products.Add(Product("Coke Zero 600ml", CokeZero));
            other.NayaxSales.AddRange(
                Unresolved(10, Monster, "Monster Original", NowUtc.AddDays(-1)),
                Unresolved(11, CokeZero, "Coke Zero 600ml", NowUtc.AddDays(-1)));
            await other.SaveChangesAsync();
        }

        await SeedAsync(Unresolved(1, CokeZero, "Coke Zero 600ml", NowUtc.AddDays(-1)));

        var attention = await ReadAsync();

        Assert.Equal(1, attention.UnimportedProductCount);
        Assert.Equal(NayaxExternalId.From(CokeZero), Assert.Single(attention.Examples).NayaxProductId);
    }

    /// <summary>A business with nothing waiting is answered zero and no example, never a failure.</summary>
    [Fact]
    public async Task A_business_with_nothing_waiting_is_answered_zero()
    {
        var attention = await ReadAsync();

        Assert.Equal(0, attention.UnimportedProductCount);
        Assert.Empty(attention.Examples);
    }

    private async Task<UnimportedProviderProductAttention> ReadAsync(DateTime? nowUtc = null)
    {
        await using var db = Scoped();
        var clock = new FixedClock(nowUtc ?? NowUtc);

        return await new GetUnimportedProviderProductAttention(
                new EfUnimportedProviderProductReader(db),
                clock,
                ZonedBusinessCalendar.ForTimeZone(clock, "Australia/Sydney"))
            .Handle(CancellationToken.None);
    }

    private async Task SeedAsync(params NayaxSales[] sales)
    {
        await using var setup = Scoped();
        setup.NayaxSales.AddRange(sales);
        await setup.SaveChangesAsync();
    }

    /// <summary>A context acting as business A, exactly as a scoped request would.</summary>
    private AppDbContext Scoped() => TestAppDbContext.For(Options, BusinessA);

    private static Business Business(int id, string name) =>
        new()
        {
            Id = id,
            Name = name,
            TimeZoneId = "Australia/Sydney",
            Status = BusinessStatus.Active,
            SalesProvider = ExternalProvider.Nayax,
            CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    private static Product Product(string name, long? externalProductId) =>
        new()
        {
            Name = name,
            ExternalProductId = externalProductId is null
                ? null
                : NayaxExternalId.From(externalProductId.Value),
            RestockTo = 0,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    /// <summary>
    /// A stored Nayax sale with no local product, carrying the raw provider product id - the exact
    /// state the sales synchronization leaves a sale of a product this business never imported in.
    /// </summary>
    private static NayaxSales Unresolved(
        long transactionId, long? nayaxProductId, string? productName, DateTime authorizedAtUtc) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = NayaxTransactionStatusIds.Completed,
            MachineID = 1,
            ProductId = null,
            Provider = ExternalProvider.Nayax,
            NayaxProductId = nayaxProductId,
            ProviderProductIdIsRaw = true,
            ProductName = productName,
            MachineAuthorizationTime = authorizedAtUtc,
        };

    private sealed class FixedClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
