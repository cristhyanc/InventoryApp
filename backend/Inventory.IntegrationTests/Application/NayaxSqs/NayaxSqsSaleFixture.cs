using Inventory.Application.Costing;
using Inventory.Application.SalesSync;
using Inventory.Application.Time;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Tests.Application.NayaxSqs;

/// <summary>
/// The world the Nayax SQS sales tests of issue #1103 run in: two businesses, each with the product
/// the fixture message names, over <strong>relational SQLite</strong>.
///
/// <para>A real SQLite database rather than the EF InMemory provider, deliberately and for three
/// reasons the InMemory provider cannot show: the deduplication is a unique index, the save is a
/// <c>BEGIN IMMEDIATE</c> transaction, and the whole point of several of these cases is what two
/// writers do to one file (AGENTS.md § Database and migrations - "EF Core InMemory tests do not prove
/// relational behavior").</para>
///
/// <para>Business A owns machine <c>4200001</c> and product <c>5001</c>, whose recorded Nayax product
/// id is the one <see cref="NayaxSqsRealMessage"/> carries. Business B owns a product of its own at a
/// deliberately different unit cost, and - in the isolation cases - the <em>same</em> machine number,
/// which is the shape that tells "attributed by the sender" apart from "attributed by the machine".</para>
/// </summary>
internal sealed class NayaxSqsSaleFixture : IAsyncDisposable
{
    /// <summary>The business whose sender the fixture message is attributed to.</summary>
    public const int BusinessA = 1;

    /// <summary>A second business, for the isolation cases.</summary>
    public const int BusinessB = 2;

    /// <summary>Business A's product, which records the Nayax product id the message carries.</summary>
    public const long ProductA = 5001;

    /// <summary>Business B's own product.</summary>
    public const long ProductB = 6001;

    /// <summary>The Nayax machine id the fixture message names.</summary>
    public const long MachineId = 4200001;

    /// <summary>The Nayax product id the fixture message's product entry carries.</summary>
    public const long NayaxProductId = 4300001;

    /// <summary>The transaction id the fixture message carries.</summary>
    public const long TransactionId = 4100000001;

    /// <summary>The instant the fixture message's GMT authorization time means.</summary>
    public static readonly DateTime SaleInstant = new(2026, 11, 5, 2, 42, 17, DateTimeKind.Utc);

    /// <summary>The transition baseline cutoff, before the sale, so the replay actually runs.</summary>
    public static readonly DateTime CutoffAt = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>When the reader accepted the message.</summary>
    public static readonly DateTime ReceivedAtUtc = new(2026, 11, 5, 2, 42, 20, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly string? _databasePath;
    private readonly string _connectionString;

    private NayaxSqsSaleFixture(
        SqliteConnection connection, string? databasePath, string connectionString)
    {
        _connection = connection;
        _databasePath = databasePath;
        _connectionString = connectionString;
        Options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
    }

    public DbContextOptions<AppDbContext> Options { get; }

    /// <summary>A fixture on one shared in-memory database: enough for everything but contention.</summary>
    public static Task<NayaxSqsSaleFixture> InMemoryAsync() =>
        CreateAsync(databasePath: null, busyTimeoutSeconds: 5);

    /// <summary>
    /// A fixture on a real file. The file is what makes a second connection a second <em>writer</em>,
    /// which is the only way SQLite's own locking can be exercised at all - a shared in-memory
    /// database has one connection and therefore no contention to have.
    /// </summary>
    /// <param name="busyTimeoutSeconds">
    /// How long a writer waits for the lock. Generous for the case about two writers serialising, and
    /// deliberately short for the case about a writer giving up.
    /// </param>
    public static Task<NayaxSqsSaleFixture> OnDiskAsync(int busyTimeoutSeconds = 5) =>
        CreateAsync(
            Path.Combine(Path.GetTempPath(), $"nayax-sqs-{Guid.NewGuid():N}.db"), busyTimeoutSeconds);

    /// <summary>
    /// A context on a <strong>second connection</strong> to the same file, which is what makes it a
    /// second writer. Only a file-backed fixture has one; an in-memory one shares a single connection
    /// and would be the same writer under another name.
    /// </summary>
    public AppDbContext SecondWriter(int businessId = BusinessA)
    {
        if (_databasePath is null)
        {
            throw new InvalidOperationException(
                "A second writer needs a file-backed fixture: use NayaxSqsSaleFixture.OnDiskAsync().");
        }

        return TestAppDbContext.For(
            new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options,
            businessId);
    }

    /// <summary>
    /// A raw connection to the same file, for the one case that has to hold SQLite's write lock from
    /// outside the application entirely.
    /// </summary>
    public SqliteConnection RawConnection()
    {
        if (_databasePath is null)
        {
            throw new InvalidOperationException(
                "A second connection needs a file-backed fixture: use NayaxSqsSaleFixture.OnDiskAsync().");
        }

        return new SqliteConnection(_connectionString);
    }

    private static async Task<NayaxSqsSaleFixture> CreateAsync(
        string? databasePath, int busyTimeoutSeconds)
    {
        var connectionString = databasePath is null
            ? "Data Source=:memory:"
            : $"Data Source={databasePath};Default Timeout={busyTimeoutSeconds}";

        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        var fixture = new NayaxSqsSaleFixture(connection, databasePath, connectionString);

        await using (var setup = TestAppDbContext.Unrestricted(fixture.Options))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Businesses.AddRange(
                NayaxBusiness(BusinessA, "Fictional Vending A"),
                NayaxBusiness(BusinessB, "Fictional Vending B"));
            await setup.SaveChangesAsync();
        }

        await using (var seedA = TestAppDbContext.For(fixture.Options, BusinessA))
        {
            seedA.Products.Add(CostedProduct(
                ProductA, "Fictional Spring Water 600ml", averageUnitCost: 2m, NayaxProductId));
            seedA.InventoryCostTransitionBaselines.Add(Baseline(ProductA, openingValue: 20m));
            await seedA.SaveChangesAsync();
        }

        await using (var seedB = TestAppDbContext.For(fixture.Options, BusinessB))
        {
            seedB.Products.Add(CostedProduct(
                ProductB, "Another business's water", averageUnitCost: 9m, NayaxProductId));
            seedB.InventoryCostTransitionBaselines.Add(Baseline(ProductB, openingValue: 90m));
            await seedB.SaveChangesAsync();
        }

        return fixture;
    }

    /// <summary>A context acting as one business, exactly as a request or a reader scope would.</summary>
    public AppDbContext Context(int businessId = BusinessA) =>
        TestAppDbContext.For(Options, businessId);

    /// <summary>A context that sees every business, for arranging and reading back from outside.</summary>
    public AppDbContext Unrestricted() => TestAppDbContext.Unrestricted(Options);

    /// <summary>
    /// The real store over <paramref name="db"/>: the real costing use case, the real inventory-cost
    /// replay and the real provider-id lookup, exactly as the composition root wires them.
    /// </summary>
    public static EfLatestNayaxSalesStore Store(
        AppDbContext db, IRebuildProductCost? rebuild = null, IClock? clock = null)
    {
        var replay = rebuild ?? TestCostingUseCases.Rebuild(db);

        return new EfLatestNayaxSalesStore(
            db,
            TestCostingUseCases.CostSale(db, replay),
            replay,
            new EfNayaxProductLinkStore(db),
            clock ?? new FakeClock(ReceivedAtUtc));
    }

    /// <summary>
    /// One queued message, as the store is asked to save it. <c>saleInstant</c> is when the sale was
    /// authorised, for the cases about a second sale arriving <em>earlier</em> than one already
    /// recorded - the only direction a pending replay's instant ever moves.
    /// </summary>
    public static NayaxSqsSaleInput SqsSale(
        int suppliedStatusId,
        decimal settlementValue = 3.5m,
        string messageId = "message-1",
        long transactionId = TransactionId,
        long? nayaxProductId = NayaxProductId,
        DateTime? receivedAtUtc = null,
        string productName = "Fictional Spring Water 600ml",
        DateTime? saleInstant = null) =>
        new(
            new Inventory.Application.Nayax.NayaxLastSalesReport
            {
                TransactionID = transactionId,
                MachineID = MachineId,
                MachineName = "Fictional Depot Machine 1",
                SettlementValue = settlementValue,
                PaymentMethod = "Credit Card",
                ProductName = productName,
                NayaxProductId = nayaxProductId,
                TransactionStatusId = suppliedStatusId,
                AuthorizationDateTimeGmt = new DateTimeOffset(saleInstant ?? SaleInstant, TimeSpan.Zero),
                ProductCostPrice = 1.2m,
            },
            suppliedStatusId,
            messageId,
            receivedAtUtc ?? ReceivedAtUtc);

    /// <summary>One <c>lastSales</c> item, which supplies no status at all.</summary>
    public static Inventory.Application.Nayax.NayaxLastSalesReport LastSalesItem(
        decimal settlementValue = 3.5m,
        long transactionId = TransactionId,
        long? nayaxProductId = NayaxProductId) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = MachineId,
            MachineName = "Fictional Depot Machine 1",
            SettlementValue = settlementValue,
            PaymentMethod = "Credit Card",
            ProductName = "Fictional Spring Water 600ml",
            NayaxProductId = nayaxProductId,
            AuthorizationDateTimeGmt = new DateTimeOffset(SaleInstant, TimeSpan.Zero),
            ProductCostPrice = 1.2m,
        };

    /// <summary>
    /// Records <paramref name="senderId"/> as that business's enabled SQS sender (issue #1101).
    /// </summary>
    /// <param name="businessId">The business the sender belongs to.</param>
    /// <param name="senderId">The AWS IAM user id, or <c>null</c> for a row whose id was cleared.</param>
    /// <param name="isEnabled">Whether the mapping is in force.</param>
    /// <param name="lastMessageReceivedAtUtc">
    /// When its queue last delivered, for the cases that read it back (issue #1106). Normally written
    /// by the reader itself; arranged here so a test need not deliver a message to have one.
    /// </param>
    /// <param name="lastReconciliationSucceededAtUtc">
    /// When its hourly reconciliation last fully succeeded, for the same reason (issue #1104's
    /// column, issue #1106's reader).
    /// </param>
    public async Task RecordSenderAsync(
        int businessId,
        string? senderId,
        bool isEnabled = true,
        DateTime? lastMessageReceivedAtUtc = null,
        DateTime? lastReconciliationSucceededAtUtc = null)
    {
        await using var db = Unrestricted();
        db.BusinessNayaxSqsSenders.Add(new BusinessNayaxSqsSender
        {
            BusinessId = businessId,
            AwsSenderId = senderId,
            IsEnabled = isEnabled,
            LastMessageReceivedAtUtc = lastMessageReceivedAtUtc,
            LastReconciliationSucceededAtUtc = lastReconciliationSucceededAtUtc,
            Revision = 1,
            CreatedAtUtc = ReceivedAtUtc,
            UpdatedAtUtc = ReceivedAtUtc,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Moves a business to <paramref name="status"/>, for the lifecycle gate.</summary>
    public async Task SetBusinessStatusAsync(int businessId, BusinessStatus status)
    {
        await using var db = Unrestricted();
        var business = await db.Businesses.SingleAsync(row => row.Id == businessId);
        business.Status = status;
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();

        if (_databasePath is null)
        {
            return;
        }

        // Pooling keeps the file handle open, so it has to be released before the file can go.
        SqliteConnection.ClearAllPools();

        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // A leftover temporary file is not worth failing a test over.
        }
    }

    private static Product CostedProduct(
        long id, string name, decimal averageUnitCost, long? externalProductId) =>
        new()
        {
            Id = id,
            Name = name,
            ExternalProductId = externalProductId is null
                ? null
                : NayaxExternalId.From(externalProductId.Value),
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = averageUnitCost * 10,
            AverageUnitCost = averageUnitCost,
        };

    private static InventoryCostTransitionBaseline Baseline(long productId, decimal openingValue) =>
        new()
        {
            ProductId = productId,
            HomeStockQuantity = 10,
            OpeningCostingQuantity = 10,
            InventoryValue = openingValue,
            AverageUnitCost = openingValue / 10,
            CutoffAt = CutoffAt,
        };

    private static Business NayaxBusiness(int id, string name) => new()
    {
        Id = id,
        Name = name,
        Status = BusinessStatus.Active,
        SalesProvider = ExternalProvider.Nayax,
        CreatedAtUtc = CutoffAt,
    };
}
