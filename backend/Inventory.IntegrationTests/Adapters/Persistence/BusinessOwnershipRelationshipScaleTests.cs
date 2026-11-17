using System.Data.Common;
using System.Diagnostics;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// How the save-time ownership check behaves at scale (issue #1282). The rules it enforces are
/// unchanged - <see cref="BusinessDataIsolationTests"/> is still the relational proof of what is
/// allowed and what is refused - and these tests pin down the two things that make the check usable
/// on a batch write: <em>how many statements</em> it makes, and <em>how its cost grows</em>.
///
/// <para>Why it matters, from audit #1226 finding PERF-01: one <c>SaveChangesAsync</c> of N new
/// rows, each with one tenant-owned foreign key, grew about four times for every doubling of N -
/// 0.45 s at 500 rows, 1.5 s at 1,000, 5.7 s at 2,000 and <strong>26.0 s at 4,000</strong>, measured
/// on a freshly migrated SQLite file at audit SHA <c>0db9f507</c>. A save that slow holds SQLite's
/// single write lock long enough that every other business's write in the process waits behind it
/// and fails on the busy timeout. The old shape was quadratic twice over: the principal of every
/// foreign key was looked for by scanning the whole change tracker (and each
/// <c>ChangeTracker.Entries&lt;T&gt;()</c> call ran change detection again), and an untracked
/// principal cost one database query <em>per foreign key</em>.</para>
///
/// <para>The hard checks here are the command counts and the two-business save; the timing ceilings
/// are deliberately generous, so a slow runner alone does not make them fail. They run on real
/// SQLite - in memory for the behavioural and command-count tests, and on a real file for the
/// timing ones, as the audit measured them.</para>
///
/// <para>Business A is 1 and business B is 2 throughout, as in the isolation tests.</para>
/// </summary>
public class BusinessOwnershipRelationshipScaleTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;

    /// <summary>
    /// The batch size <c>BusinessOwnershipEnforcer</c> loads untracked principals in. One statement
    /// per principal entity type per save is the rule; a save that needs more distinct ids than this
    /// is split into fixed-size batches, so the statement count stays bounded by
    /// <c>ceil(distinct ids / this)</c> rather than by the number of rows.
    /// </summary>
    private const int UntrackedPrincipalBatchSize = 1_000;

    /// <summary>The audit's own worst case: one save of this many new rows.</summary>
    private const int AuditRowCount = 4_000;

    /// <summary>
    /// The sizes the audit measured, so the test reports the same series it is judged against.
    /// </summary>
    private static readonly int[] MeasuredRowCounts = [500, 1_000, 2_000, AuditRowCount];

    /// <summary>
    /// The absolute ceiling for the audit's 4,000-row case. The measured baseline was 26.0 s, so
    /// this is comfortably under it while leaving a slow runner a wide margin.
    /// </summary>
    private static readonly TimeSpan FourThousandRowCeiling = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Roughly linear: doubling the rows may not much more than double the time. Three is the
    /// generous version of two; the baseline grew by about four and a half (5.7 s to 26.0 s).
    /// </summary>
    private const double AllowedGrowthPerDoubling = 3.0;

    /// <summary>
    /// Below this, a ratio says more about scheduling noise and SQLite's own fixed costs than about
    /// how the check scales, so the growth comparison is only applied above it.
    /// </summary>
    private static readonly TimeSpan GrowthComparisonFloor = TimeSpan.FromSeconds(3);

    private readonly ITestOutputHelper _output;
    private readonly List<string> _databaseFiles = [];

    public BusinessOwnershipRelationshipScaleTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var file in _databaseFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temporary file is not worth failing a test over.
            }
        }

        GC.SuppressFinalize(this);
    }

    #region Behaviour at scale, with two businesses

    /// <summary>
    /// The relational two-business proof for a batch write (AGENTS.md § Tenant ownership: any change
    /// to enforcement needs one). One save mixes all three ways a principal can be reached - a row
    /// already in the change tracker, a row being inserted in this same save, and a row that is only
    /// in the database - and every one of them belongs to the caller's own business, so the save
    /// succeeds and every new row is stamped.
    /// </summary>
    [Fact]
    public async Task One_large_save_mixing_tracked_and_untracked_principals_of_the_callers_business_succeeds()
    {
        await using var connection = await OpenSharedMemoryAsync();
        var options = OptionsFor(connection);
        var trackedCategoryId = SeedCategory(options, BusinessA, "A Tracked Shelf");
        var untrackedCategoryId = SeedCategory(options, BusinessA, "A Untracked Shelf");
        SeedCategory(options, BusinessB, "B Shelf");

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            AddMixedRows(a, trackedCategoryId, untrackedCategoryId, rowsPerPrincipalKind: 1_000);

            await a.SaveChangesAsync();
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        var products = await verify.Products.ToListAsync();
        Assert.Equal(3_000, products.Count);
        Assert.All(products, product => Assert.Equal(BusinessA, product.BusinessId));
        Assert.Equal(1_000, products.Count(product => product.CategoryId == trackedCategoryId));
        Assert.Equal(1_000, products.Count(product => product.CategoryId == untrackedCategoryId));

        // The thousand rows pointing at the category created in the same save all found it.
        var createdCategory = await verify.Categories.SingleAsync(category => category.Name == "A Created Shelf");
        Assert.Equal(1_000, products.Count(product => product.CategoryId == createdCategory.Id));
        Assert.Equal(BusinessA, createdCategory.BusinessId);
    }

    /// <summary>
    /// The same save with one row pointing at business B's category is refused, and nothing at all
    /// is written - not the 3,000 legitimate rows, not the category the save created. One
    /// <c>SaveChanges</c> is one transaction, and the refusal happens before it opens.
    /// </summary>
    [Fact]
    public async Task The_same_save_with_one_row_pointing_at_the_other_business_is_refused_and_writes_nothing()
    {
        await using var connection = await OpenSharedMemoryAsync();
        var options = OptionsFor(connection);
        var trackedCategoryId = SeedCategory(options, BusinessA, "A Tracked Shelf");
        var untrackedCategoryId = SeedCategory(options, BusinessA, "A Untracked Shelf");
        var otherBusinessCategoryId = SeedCategory(options, BusinessB, "B Shelf");

        await using (var a = TestAppDbContext.For(options, BusinessA))
        {
            AddMixedRows(a, trackedCategoryId, untrackedCategoryId, rowsPerPrincipalKind: 1_000);
            a.Products.Add(NewProduct("Smuggled", otherBusinessCategoryId));

            var exception = await Assert.ThrowsAsync<CrossBusinessAccessException>(() => a.SaveChangesAsync());
            Assert.Contains("Category", exception.Message, StringComparison.Ordinal);
        }

        await using var verify = TestAppDbContext.Unrestricted(options);
        Assert.Empty(await verify.Products.ToListAsync());
        Assert.Equal(3, await verify.Categories.CountAsync());
    }

    #endregion

    #region Statements per save

    /// <summary>
    /// 4,000 rows all pointing at the same untracked principal: exactly one statement over the
    /// principal's table, because the save asks about one distinct id.
    /// </summary>
    [Fact]
    public async Task Four_thousand_rows_pointing_at_one_untracked_principal_cost_one_statement()
    {
        await using var connection = await OpenSharedMemoryAsync();
        var categoryId = SeedCategory(OptionsFor(connection), BusinessA, "A Shelf");

        var commands = new List<string>();
        await using (var a = TestAppDbContext.For(OptionsFor(connection, new CommandRecordingInterceptor(commands)), BusinessA))
        {
            for (var row = 0; row < AuditRowCount; row++)
            {
                a.Products.Add(NewProduct($"Product {row}", categoryId));
            }

            await a.SaveChangesAsync();
        }

        Assert.Equal(1, CategoryStatements(commands));

        await using var verify = TestAppDbContext.Unrestricted(OptionsFor(connection));
        Assert.Equal(AuditRowCount, await verify.Products.CountAsync());
    }

    /// <summary>
    /// 4,000 rows each pointing at a <em>different</em> untracked principal: the statement count is
    /// bounded by the fixed batch size, not by the number of rows. Under the old shape this was
    /// 4,000 statements, one per foreign key.
    /// </summary>
    [Fact]
    public async Task Four_thousand_rows_pointing_at_four_thousand_untracked_principals_cost_one_statement_per_batch()
    {
        await using var connection = await OpenSharedMemoryAsync();
        var categoryIds = SeedCategories(OptionsFor(connection), BusinessA, AuditRowCount);

        var commands = new List<string>();
        await using (var a = TestAppDbContext.For(OptionsFor(connection, new CommandRecordingInterceptor(commands)), BusinessA))
        {
            for (var row = 0; row < AuditRowCount; row++)
            {
                a.Products.Add(NewProduct($"Product {row}", categoryIds[row]));
            }

            await a.SaveChangesAsync();
        }

        var expectedBatches = (AuditRowCount + UntrackedPrincipalBatchSize - 1) / UntrackedPrincipalBatchSize;
        var statements = CategoryStatements(commands);
        _output.WriteLine($"{AuditRowCount} distinct untracked principals resolved in {statements} statement(s)");
        Assert.InRange(statements, 1, expectedBatches);

        await using var verify = TestAppDbContext.Unrestricted(OptionsFor(connection));
        Assert.Equal(AuditRowCount, await verify.Products.CountAsync());
    }

    #endregion

    #region Timing on a real SQLite file

    /// <summary>
    /// The audit's own case, on a real SQLite file: one save of 4,000 new rows that all point at the
    /// same untracked principal. Baseline at audit SHA <c>0db9f507</c>: 0.45 s / 1.5 s / 5.7 s /
    /// 26.0 s for 500 / 1,000 / 2,000 / 4,000 rows.
    /// </summary>
    [Fact]
    public async Task A_save_of_four_thousand_rows_sharing_one_untracked_principal_stays_roughly_linear()
    {
        var measurements = await MeasureSeriesAsync(distinctPrincipals: false);

        AssertRoughlyLinear(measurements);
    }

    /// <summary>
    /// The harder case: 4,000 new rows that each point at a different untracked principal, so the
    /// enforcer has 4,000 distinct ids to resolve rather than one.
    /// </summary>
    [Fact]
    public async Task A_save_of_four_thousand_rows_with_distinct_untracked_principals_stays_roughly_linear()
    {
        var measurements = await MeasureSeriesAsync(distinctPrincipals: true);

        AssertRoughlyLinear(measurements);
    }

    private async Task<Dictionary<int, TimeSpan>> MeasureSeriesAsync(bool distinctPrincipals)
    {
        var measurements = new Dictionary<int, TimeSpan>();

        foreach (var rows in MeasuredRowCounts)
        {
            var elapsed = await MeasureSaveAsync(rows, distinctPrincipals);
            measurements[rows] = elapsed;
            _output.WriteLine(
                $"{rows} rows, {(distinctPrincipals ? "one untracked principal each" : "one shared untracked principal")}: "
                    + $"{elapsed.TotalSeconds:0.000} s");
        }

        return measurements;
    }

    /// <summary>
    /// One save of <paramref name="rows"/> new products on its own SQLite <em>file</em>, timing the
    /// save alone. Only the save is measured: seeding the businesses, the schema and the principals
    /// is setup, and the audit measured one save.
    /// </summary>
    private async Task<TimeSpan> MeasureSaveAsync(int rows, bool distinctPrincipals)
    {
        var file = Path.Combine(Path.GetTempPath(), $"inventory-issue-1282-{Guid.NewGuid():N}.sqlite");
        _databaseFiles.Add(file);

        await using var connection = new SqliteConnection($"Data Source={file}");
        await connection.OpenAsync();
        var options = OptionsFor(connection);
        var categoryIds = SeedCategories(options, BusinessA, distinctPrincipals ? rows : 1);

        await using var a = TestAppDbContext.For(options, BusinessA);
        for (var row = 0; row < rows; row++)
        {
            a.Products.Add(NewProduct($"Product {row}", categoryIds[distinctPrincipals ? row : 0]));
        }

        var save = Stopwatch.StartNew();
        await a.SaveChangesAsync();
        save.Stop();

        return save.Elapsed;
    }

    private void AssertRoughlyLinear(Dictionary<int, TimeSpan> measurements)
    {
        var fourThousand = measurements[AuditRowCount];
        var twoThousand = measurements[2_000];

        Assert.True(
            fourThousand < FourThousandRowCeiling,
            $"One save of {AuditRowCount} rows took {fourThousand.TotalSeconds:0.000} s, over the "
                + $"{FourThousandRowCeiling.TotalSeconds:0} s ceiling (baseline at audit SHA 0db9f507: 26.0 s).");

        if (fourThousand <= GrowthComparisonFloor)
        {
            // Fast enough that the ratio would measure noise rather than growth.
            return;
        }

        Assert.True(
            fourThousand <= twoThousand * AllowedGrowthPerDoubling,
            $"Doubling 2,000 rows to {AuditRowCount} took {fourThousand.TotalSeconds:0.000} s against "
                + $"{twoThousand.TotalSeconds:0.000} s, more than {AllowedGrowthPerDoubling:0} times - "
                + "the cost of one save is growing faster than the rows do.");
    }

    #endregion

    #region Setup

    private static DbContextOptions<AppDbContext> OptionsFor(
        SqliteConnection connection,
        DbCommandInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection);
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static async Task<SqliteConnection> OpenSharedMemoryAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        EnsureDatabase(OptionsFor(connection));
        return connection;
    }

    /// <summary>
    /// The schema and the two synthetic businesses, created once per database. The file-backed
    /// timing runs each get a new file, so each of them creates both.
    /// </summary>
    private static void EnsureDatabase(DbContextOptions<AppDbContext> options)
    {
        using var setup = TestAppDbContext.Unrestricted(options);
        if (!setup.Database.EnsureCreated())
        {
            return;
        }

        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessB, Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = DateTime.UtcNow });
        setup.SaveChanges();
    }

    /// <summary>
    /// Seeds principals from outside the scoped path, so the save under test finds them in the
    /// database rather than in its own change tracker.
    /// </summary>
    private static List<long> SeedCategories(DbContextOptions<AppDbContext> options, int businessId, int count)
    {
        EnsureDatabase(options);

        using var setup = TestAppDbContext.Unrestricted(options);
        var categories = new List<Category>(count);
        for (var index = 0; index < count; index++)
        {
            categories.Add(new Category { BusinessId = businessId, Name = $"Shelf {index}" });
        }

        setup.Categories.AddRange(categories);
        setup.SaveChanges();

        return categories.Select(category => category.Id).ToList();
    }

    private static long SeedCategory(DbContextOptions<AppDbContext> options, int businessId, string name)
    {
        using var setup = TestAppDbContext.Unrestricted(options);
        var category = new Category { BusinessId = businessId, Name = name };
        setup.Categories.Add(category);
        setup.SaveChanges();
        return category.Id;
    }

    /// <summary>
    /// Rows reaching their principal all three ways at once: a category already loaded into this
    /// context, a category being inserted in this same save, and a category that is only in the
    /// database.
    /// </summary>
    private static void AddMixedRows(
        AppDbContext context,
        long trackedCategoryId,
        long untrackedCategoryId,
        int rowsPerPrincipalKind)
    {
        var tracked = context.Categories.Single(category => category.Id == trackedCategoryId);
        var created = new Category { Name = "A Created Shelf" };
        context.Categories.Add(created);

        for (var row = 0; row < rowsPerPrincipalKind; row++)
        {
            context.Products.Add(NewProduct($"Tracked {row}", tracked.Id));
            context.Products.Add(NewProduct($"Untracked {row}", untrackedCategoryId));
            context.Products.Add(new Product
            {
                Name = $"Created {row}",
                Sku = $"Created-{row}",
                QuantityInStock = 1,
                Category = created,
            });
        }
    }

    private static Product NewProduct(string name, long categoryId) => new()
    {
        Name = name,
        Sku = $"{name}-SKU",
        QuantityInStock = 1,
        CategoryId = categoryId,
    };

    /// <summary>
    /// How many statements this save made over the principals' table. The enforcer's lookup is the
    /// only thing that reads <c>Categories</c> during a save of products.
    /// </summary>
    private static int CategoryStatements(IEnumerable<string> commands) =>
        commands.Count(text => text.Contains("FROM \"Categories\"", StringComparison.Ordinal));

    private sealed class CommandRecordingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public CommandRecordingInterceptor(List<string> commands) => _commands = commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<object> ScalarExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result)
        {
            Record(command);
            return base.ScalarExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<object> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }

        /// <summary>
        /// Only the statements this test asks about are kept: a save of 4,000 rows also issues the
        /// inserts themselves, and holding every one of those command texts would measure memory
        /// rather than behaviour.
        /// </summary>
        private void Record(DbCommand command)
        {
            if (command.CommandText.Contains("\"Categories\"", StringComparison.Ordinal))
            {
                _commands.Add(command.CommandText);
            }
        }
    }

    #endregion
}
