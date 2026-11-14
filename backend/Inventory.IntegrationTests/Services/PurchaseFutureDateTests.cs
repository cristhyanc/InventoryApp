using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.Time;
using Inventory.Domain.Purchases;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Time;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Services;

/// <summary>
/// Issue #1246: a purchase is never dated later than today in the business's own time zone, on a
/// create and on an edit alike.
///
/// <para>The regression this is about: a purchase of 12 dated a week ahead showed 12 in stock, but a
/// refill or a write-off <em>today</em> then failed with "becomes physically negative", because the
/// cost replay orders today's movement before the purchase's. Nothing on any screen could repair it
/// except correcting the date.</para>
///
/// <para>The calendar here is the real <see cref="ZonedBusinessCalendar"/> for
/// <c>Australia/Sydney</c> over a fixed clock, which is what makes the midnight boundary a real one:
/// for the eleven hours after Sydney midnight the business's date and the host's UTC date are
/// different calendar days, and a purchase dated the Sydney date must still be accepted then. The
/// policy's own day comparison is <c>InventoryApi.Tests.Domain.Purchases.PurchaseDatePolicyTests</c>
/// and the orchestration over a fake store is
/// <c>InventoryApi.Tests.Application.Purchases.PurchaseDateUseCaseTests</c>; this file is the real
/// SQLite write, so "nothing is written" means no purchase row, no stock movement and no stored
/// document.</para>
/// </summary>
public sealed class PurchaseFutureDateTests : IDisposable
{
    private const long ProductId = 1;
    private const int SupplierId = 1;

    /// <summary>Sydney 13:00 on 14 November 2026 (AEDT, UTC+11): the business day is 14 November.</summary>
    private static readonly DateTime SydneyMiddayUtc = new(2026, 11, 14, 2, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Sydney 00:30 on 15 November 2026, while the UTC date is still 14 November: the boundary the
    /// acceptance criteria name.
    /// </summary>
    private static readonly DateTime JustAfterSydneyMidnightUtc = new(2026, 11, 14, 13, 30, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly string _documentRoot;

    public PurchaseFutureDateTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();

        _documentRoot = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(_documentRoot);
    }

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_documentRoot)) Directory.Delete(_documentRoot, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A date a week ahead, as a slipped date picker produces it, and the same "tomorrow" the Add
    /// Purchase form itself sends for a Sydney browser - midnight Sydney, which is 13:00 UTC the day
    /// before. Both are refused, and the refusal leaves no purchase, no stock movement and no stored
    /// receipt behind.
    /// </summary>
    [Theory]
    [InlineData("2026-11-21T00:00:00Z")]
    [InlineData("2026-11-15T00:00:00Z")]
    [InlineData("2026-11-14T13:00:00Z")]
    public async Task A_purchase_dated_after_the_sydney_date_is_refused_and_writes_nothing(string purchaseDate)
    {
        await SeedProductAndSupplierAsync();

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                UploadUseCase(db, SydneyMiddayUtc).Handle(
                    FileInput(),
                    Fields(DateTime.Parse(purchaseDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal)),
                    [new PurchaseItemInput(ProductId, 12m, 1.50m)],
                    CancellationToken.None));

            Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await verify.Receipts.ToListAsync());
        Assert.Empty(await verify.StockAdjustments.ToListAsync());
        Assert.Equal(0, (await verify.Products.SingleAsync()).QuantityInStock);
        Assert.Empty(Directory.GetFiles(_documentRoot, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// The midnight boundary of the acceptance criteria: at 00:30 in Sydney the business date is
    /// already 15 November while the host's UTC date is still the 14th, and a purchase dated the
    /// 15th is today's purchase. Both encodings of that date are accepted - Sydney midnight (13:00
    /// UTC the day before, which is what the form sends) and the same calendar date as a UTC
    /// midnight instant, which is an hour-of-day still ahead of the clock but the same business day.
    /// </summary>
    [Theory]
    [InlineData("2026-11-14T13:00:00Z")]
    [InlineData("2026-11-15T00:00:00Z")]
    public async Task A_date_equal_to_the_sydney_date_is_created_while_the_utc_date_is_the_previous_day(
        string purchaseDate)
    {
        await SeedProductAndSupplierAsync();
        var submitted = DateTime.Parse(purchaseDate, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var record = await UploadUseCase(db, JustAfterSydneyMidnightUtc).Handle(
                FileInput(),
                Fields(submitted),
                [new PurchaseItemInput(ProductId, 12m, 1.50m)],
                CancellationToken.None);

            Assert.NotNull(record);
            Assert.Equal(submitted, record!.PurchaseDate);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(submitted, (await verify.Receipts.SingleAsync()).PurchaseDate);
        Assert.Equal(12, (await verify.Products.SingleAsync()).QuantityInStock);
    }

    /// <summary>
    /// The day after the business's own date is still refused at that same boundary, so "the UTC
    /// date is behind" never widens the rule by a day: 13:00 UTC on 15 November is Sydney midnight
    /// on the 16th.
    /// </summary>
    [Fact]
    public async Task The_day_after_the_sydney_date_is_still_refused_at_the_boundary()
    {
        await SeedProductAndSupplierAsync();

        await using var db = TestAppDbContext.Unrestricted(_options);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UploadUseCase(db, JustAfterSydneyMidnightUtc).Handle(
                FileInput(),
                Fields(new DateTime(2026, 11, 15, 13, 0, 0, DateTimeKind.Utc)),
                [new PurchaseItemInput(ProductId, 12m, 1.50m)],
                CancellationToken.None));

        Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        Assert.Empty(await db.Receipts.ToListAsync());
    }

    /// <summary>
    /// The decision recorded on the issue: <em>every</em> save needs a date of today or earlier, so
    /// an existing future-dated purchase - one the adapter wrote before this rule existed, which is
    /// how it is seeded here - cannot be saved at all until its date is corrected. An edit that
    /// submits no date (the stored date would stand) and an edit that submits the stored date back
    /// (what the Edit form sends while the date is untouched) are both refused, and neither changes
    /// anything; the same edit with the date corrected saves, and moves the purchase's own restock
    /// movement with it.
    /// </summary>
    [Fact]
    public async Task An_existing_future_dated_purchase_is_refused_until_its_date_is_corrected()
    {
        await SeedProductAndSupplierAsync();
        var futureDate = new DateTime(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        var purchaseId = await SeedFutureDatedPurchaseAsync(futureDate);

        // No date submitted: the stored future date is the one that would stand, so it is refused.
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EditUseCase(db, SydneyMiddayUtc).Handle(
                    purchaseId, Fields(null, "Corrected title"), null, CancellationToken.None));

            Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        }

        // The stored date submitted back unchanged: refused for the same reason.
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                EditUseCase(db, SydneyMiddayUtc).Handle(
                    purchaseId, Fields(futureDate, "Corrected title"), null, CancellationToken.None));

            Assert.Equal(PurchaseDatePolicy.AfterTodayMessage, error.Message);
        }

        await using (var refused = TestAppDbContext.Unrestricted(_options))
        {
            var unchanged = await refused.Receipts.SingleAsync();
            Assert.Equal("Future restock", unchanged.Title);
            Assert.Equal(futureDate, unchanged.PurchaseDate);
            Assert.Equal(futureDate, (await refused.StockAdjustments.SingleAsync()).EffectiveAt);
        }

        // The one way forward: correct the date. Today in Sydney is accepted and saves the edit.
        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var updated = await EditUseCase(db, SydneyMiddayUtc).Handle(
                purchaseId, Fields(SydneyMiddayUtc, "Corrected title"), null, CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal(SydneyMiddayUtc, updated!.PurchaseDate);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var corrected = await verify.Receipts.SingleAsync();
        Assert.Equal("Corrected title", corrected.Title);
        Assert.Equal(SydneyMiddayUtc, corrected.PurchaseDate);
        Assert.Equal(SydneyMiddayUtc, (await verify.StockAdjustments.SingleAsync()).EffectiveAt);
    }

    /// <summary>
    /// An ordinary edit of an ordinary purchase is untouched: the stored date is in the past, no
    /// date is submitted, and the edit saves.
    /// </summary>
    [Fact]
    public async Task An_edit_that_submits_no_date_saves_when_the_stored_date_is_not_after_today()
    {
        await SeedProductAndSupplierAsync();
        var pastDate = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
        var purchaseId = await SeedFutureDatedPurchaseAsync(pastDate);

        await using (var db = TestAppDbContext.Unrestricted(_options))
        {
            var updated = await EditUseCase(db, SydneyMiddayUtc).Handle(
                purchaseId, Fields(null, "Corrected title"), null, CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal(pastDate, updated!.PurchaseDate);
        }

        await using var verify = TestAppDbContext.Unrestricted(_options);
        var saved = await verify.Receipts.SingleAsync();
        Assert.Equal("Corrected title", saved.Title);
        Assert.Equal(pastDate, saved.PurchaseDate);
    }

    /// <summary>
    /// The purchase the audit expected to find none of: written straight through the adapter, which
    /// carries no date rule of its own, exactly as the create used to write it.
    /// </summary>
    private async Task<int> SeedFutureDatedPurchaseAsync(DateTime purchaseDate)
    {
        await using var db = TestAppDbContext.Unrestricted(_options);
        var purchase = await new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed)
            .CreateAsync(
                new PurchaseFields("Future restock", null, null, null, null, purchaseDate, SupplierId),
                [new PurchaseItemInput(ProductId, 12m, 1.50m)],
                file: null,
                supplierOrderCompletion: null,
                attachmentReservation: null,
                CancellationToken.None);

        return purchase.Id;
    }

    private async Task SeedProductAndSupplierAsync()
    {
        await using var seed = TestAppDbContext.Unrestricted(_options);
        seed.Products.Add(new Product { Id = ProductId, Name = "Coke" });
        seed.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            ProductId = ProductId,
            CutoffAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            HomeStockQuantity = 0,
            CostSource = InventoryCostBaselineSource.ManualAuthoritative,
        });
        seed.Suppliers.Add(new Supplier { Id = SupplierId, Name = "Costco" });
        await seed.SaveChangesAsync();
    }

    private UploadPurchase UploadUseCase(AppDbContext db, DateTime nowUtc) =>
        TestPurchaseUseCases.Upload(
            new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed),
            Documents(),
            new FakeClock(nowUtc),
            calendar: SydneyCalendar(nowUtc));

    private UpdatePurchase EditUseCase(AppDbContext db, DateTime nowUtc) =>
        TestPurchaseUseCases.Update(
            new EfPurchaseStore(db, TestCostingUseCases.Rebuild(db), TestClocks.Fixed),
            SydneyCalendar(nowUtc));

    private static IBusinessCalendar SydneyCalendar(DateTime nowUtc) =>
        ZonedBusinessCalendar.ForTimeZone(new FakeClock(nowUtc), "Australia/Sydney");

    /// <summary>
    /// Real document storage under this test's own temporary folder, so "nothing is written" can be
    /// asserted about the receipt's bytes as well as about the database.
    /// </summary>
    private IDocumentStorage Documents() =>
        new FileSystemDocumentStorage(new FileSystemDocumentStorageOptions
        {
            ContentRootPath = _documentRoot,
            WebRootPath = _documentRoot,
        });

    private static PurchaseFields Fields(DateTime? purchaseDate, string title = "Weekly restock") =>
        new(title, null, null, null, null, purchaseDate, SupplierId);

    private static PurchaseFileInput FileInput() =>
        new("receipt.jpg", "image/jpeg", 3, () => new MemoryStream([1, 2, 3]));
}
