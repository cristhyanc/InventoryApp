using Inventory.Application.PlatformDiagnostics;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.PlatformDiagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.PlatformDiagnostics;

/// <summary>
/// The SQLite enforcement half of the platform diagnostics API (issue #336), against a real
/// relational database created by the real migrations - so the table and column names these tests
/// permit and deny are the physical ones a deployed database has, not a test double's.
///
/// What is being proved here is that the boundary is SQLite's and not a text filter's: a forbidden
/// column is unreachable through a join, a correlated subquery, an alias and an expression alike;
/// a write, a DDL statement, a <c>PRAGMA</c> and an <c>ATTACH</c> are refused and change nothing;
/// a costly permitted query that produces no first row is interrupted at the deadline and leaves
/// the connection clean for the next one; and a column added to a permitted table after this code
/// was written is inaccessible without anybody editing the enforcement.
/// </summary>
public sealed class SqliteDiagnosticsQueryExecutorTests : IDisposable
{
    private const int BusinessAId = 1;
    private const int BusinessBId = 2;

    /// <summary>
    /// More rows than the hard 500-row maximum, so the real cap can be exercised rather than only
    /// a tightened one. Categories are the cheapest permitted table to seed at this size.
    /// </summary>
    private const int SeededCategoriesPerBusiness = 300;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"inventory-diagnostics-{Guid.NewGuid():N}.db");

    private readonly string _connectionString;

    public SqliteDiagnosticsQueryExecutorTests()
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString();

        using var db = TestAppDbContext.Unrestricted(Options());
        db.Database.Migrate();

        db.Businesses.AddRange(
            new Business { Id = BusinessAId, Name = "Business A", IsActive = true, CreatedAtUtc = DateTime.UtcNow },
            new Business { Id = BusinessBId, Name = "Business B", IsActive = true, CreatedAtUtc = DateTime.UtcNow });

        foreach (var businessId in new[] { BusinessAId, BusinessBId })
        {
            for (var index = 0; index < SeededCategoriesPerBusiness; index++)
            {
                db.Categories.Add(new Category { BusinessId = businessId, Name = $"Category {businessId}-{index}" });
            }

            db.Products.Add(new Product { BusinessId = businessId, Name = $"Product {businessId}" });
        }

        // A membership row exists so the tests that prove it is unreachable are proving something:
        // a denial over an empty table would pass for the wrong reason.
        db.BusinessMemberships.Add(new BusinessMembership
        {
            BusinessId = BusinessAId,
            DirectoryTenantId = "AAAAAAAA-0000-0000-0000-000000000001",
            ObjectId = "BBBBBBBB-0000-0000-0000-000000000002",
            IsActive = true,
        });

        db.SaveChanges();
    }

    #region Permitted reads

    /// <summary>
    /// The endpoint's reason to exist: an ownership read that spans every business. The tenant query
    /// filters are not involved - this is a separate read-only connection, not an unscoped
    /// <c>AppDbContext</c> - so both businesses' rows are visible, which is exactly the documented
    /// exception and nothing more.
    /// </summary>
    [Fact]
    public async Task A_permitted_query_reads_identity_columns_across_every_business()
    {
        var result = await ExecuteAsync("SELECT BusinessId, Id FROM Products ORDER BY BusinessId, Id");

        Assert.Equal(DiagnosticsQueryOutcome.Succeeded, result.Outcome);
        Assert.Equal(["BusinessId", "Id"], result.Columns);
        Assert.Equal(2, result.Rows.Count);
        Assert.Equal(["1", "2"], result.Rows.Select(row => row[0]));
    }

    /// <summary>
    /// The orphan investigation the surface was chosen for: a left join from a child to its parent,
    /// across four of the seven permitted tables.
    /// </summary>
    [Fact]
    public async Task The_orphan_investigation_the_surface_exists_for_is_permitted()
    {
        var result = await ExecuteAsync(
            """
            SELECT sa.Id, sa.BusinessId, p.BusinessId
            FROM StockAdjustments sa
            LEFT JOIN Products p ON p.Id = sa.ProductId
            WHERE p.Id IS NULL OR p.BusinessId <> sa.BusinessId
            """);

        Assert.Equal(DiagnosticsQueryOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM Products")]
    [InlineData("SELECT BusinessId, count(*) FROM Categories GROUP BY BusinessId")]
    [InlineData("SELECT max(Id), min(Id), sum(Id), avg(Id), total(Id) FROM Products")]
    [InlineData("SELECT coalesce(SupplierId, -1), ifnull(CategoryId, -1), typeof(Id) FROM Products")]
    [InlineData("WITH owned AS (SELECT Id, BusinessId FROM Products) SELECT count(*) FROM owned")]
    public async Task The_permitted_aggregate_and_null_handling_functions_work(string sql)
    {
        var result = await ExecuteAsync(sql);

        AssertSucceeded(result);
        Assert.NotEmpty(result.Rows);
    }

    /// <summary>
    /// A rowid reference that really is the internal row id is the same integer as <c>Id</c> on
    /// these tables, because <c>Id</c> is their <c>INTEGER PRIMARY KEY</c> - and SQLite reports it
    /// to the authorizer under that column's name, which is why all three spellings read as
    /// <c>Products.Id</c> and are allowed on the surface's own terms. No spelling is permitted by
    /// name; see
    /// <see cref="A_column_that_shadows_a_rowid_spelling_is_refused_like_any_other_new_column"/>.
    /// </summary>
    [Theory]
    [InlineData("rowid")]
    [InlineData("oid")]
    [InlineData("_rowid_")]
    public async Task The_rowid_spellings_of_a_permitted_integer_key_are_the_same_value_as_Id(string spelling)
    {
        var result = await ExecuteAsync($"SELECT {spelling}, Id FROM Products ORDER BY Id");

        AssertSucceeded(result);
        Assert.All(result.Rows, row => Assert.Equal(row[0], row[1]));
    }

    #endregion

    #region The data surface cannot be escaped

    [Theory]
    [InlineData("SELECT Name FROM Products")]
    [InlineData("SELECT Sku FROM Products")]
    [InlineData("SELECT UnitPrice FROM Products")]
    [InlineData("SELECT AverageUnitCost, InventoryValue FROM Products")]
    [InlineData("SELECT Name FROM Businesses")]
    [InlineData("SELECT Email FROM Suppliers")]
    [InlineData("SELECT Notes FROM StockAdjustments")]
    [InlineData("SELECT StoredFileName FROM Receipts")]
    [InlineData("SELECT * FROM Products")]
    public async Task An_unlisted_column_on_a_permitted_table_is_refused(string sql)
    {
        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenSchemaAccess, result.DenialReason);
        Assert.Empty(result.Rows);
    }

    [Theory]
    [InlineData("SELECT ObjectId FROM BusinessMemberships")]
    [InlineData("SELECT Id FROM NayaxSales")]
    [InlineData("SELECT Id FROM ImportedFiles")]
    [InlineData("SELECT Id FROM OperatingExpenses")]
    [InlineData("SELECT name FROM sqlite_master")]
    [InlineData("SELECT Id FROM InventoryCostRepairs")]
    public async Task An_unlisted_table_is_refused(string sql)
    {
        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenSchemaAccess, result.DenialReason);
    }

    /// <summary>
    /// The heart of "not just regex": every one of these reaches a forbidden table or column
    /// somewhere other than the obvious place in the select list - behind a join, a correlated
    /// subquery, an <c>IN</c> list, an alias, a <c>CASE</c> expression, an <c>ORDER BY</c>, a
    /// <c>HAVING</c> clause and a <c>WITH</c> clause. SQLite reports each as the same
    /// <c>SQLITE_READ</c> on the same physical column, so none of them get past the authorizer.
    /// </summary>
    [Theory]
    [InlineData("SELECT p.Id FROM Products p JOIN BusinessMemberships m ON m.BusinessId = p.BusinessId")]
    [InlineData("SELECT Id FROM Products WHERE BusinessId IN (SELECT BusinessId FROM BusinessMemberships)")]
    [InlineData("SELECT Id, (SELECT Name FROM Businesses b WHERE b.Id = Products.BusinessId) AS owner FROM Products")]
    [InlineData("SELECT p.Id AS anything FROM Products p ORDER BY p.Name")]
    [InlineData("SELECT CASE WHEN Name IS NULL THEN 0 ELSE 1 END FROM Products")]
    [InlineData("SELECT BusinessId FROM Products GROUP BY BusinessId HAVING max(UnitPrice) > 0")]
    [InlineData("WITH identities AS (SELECT ObjectId FROM BusinessMemberships) SELECT * FROM identities")]
    [InlineData("SELECT Id FROM Products UNION ALL SELECT Id FROM NayaxSales")]
    [InlineData("SELECT Id FROM Products WHERE EXISTS (SELECT 1 FROM BusinessMemberships)")]
    public async Task A_join_subquery_alias_or_expression_cannot_reach_outside_the_surface(string sql)
    {
        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenSchemaAccess, result.DenialReason);
        Assert.Empty(result.Rows);
    }

    /// <summary>
    /// The fail-closed guarantee a future schema change rests on. The column is added to a
    /// <em>permitted</em> table by a writable connection, exactly as a migration would add it, and
    /// is then unreachable with no change to the allow-list, the authorizer or this adapter.
    /// </summary>
    [Fact]
    public async Task A_column_added_to_a_permitted_table_after_the_fact_stays_inaccessible()
    {
        await AlterSchemaAsync("ALTER TABLE Products ADD COLUMN DiagnosticsProbe TEXT");

        var refused = await ExecuteAsync("SELECT DiagnosticsProbe FROM Products");
        var stillWorks = await ExecuteAsync("SELECT Id FROM Products");

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, refused.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenSchemaAccess, refused.DenialReason);
        Assert.Contains("DiagnosticsProbe", refused.Message, StringComparison.Ordinal);
        AssertSucceeded(stillWorks);
    }

    /// <summary>
    /// The same fail-closed guarantee for the three names that are not ordinary column names.
    /// SQLite treats <c>rowid</c>, <c>oid</c> and <c>_rowid_</c> as the internal row identifier
    /// only while no declared column carries that name; the moment a migration adds one, the same
    /// spelling means that column instead. An authorizer that permitted the names rather than the
    /// resolved access would therefore hand out a brand-new unlisted column - a real, reachable
    /// hole, because <c>ALTER TABLE Products ADD COLUMN oid TEXT</c> is an ordinary migration. Each
    /// name is added exactly as a migration would add it and must then be refused like any other
    /// new column.
    /// </summary>
    [Theory]
    [InlineData("rowid")]
    [InlineData("oid")]
    [InlineData("_rowid_")]
    public async Task A_column_that_shadows_a_rowid_spelling_is_refused_like_any_other_new_column(
        string columnName)
    {
        await AlterSchemaAsync($"ALTER TABLE Products ADD COLUMN {columnName} TEXT");

        var refused = await ExecuteAsync($"SELECT {columnName} FROM Products");
        var stillWorks = await ExecuteAsync("SELECT Id, BusinessId FROM Products");

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, refused.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenSchemaAccess, refused.DenialReason);
        Assert.Contains(columnName, refused.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(refused.Rows);
        AssertSucceeded(stillWorks);
    }

    [Theory]
    [InlineData("SELECT load_extension('evil')")]
    [InlineData("SELECT sqlite_version()")]
    [InlineData("SELECT Id FROM Products WHERE sqlite_source_id() <> ''")]
    [InlineData("SELECT randomblob(16)")]
    [InlineData("SELECT zeroblob(1000000)")]
    [InlineData("SELECT Id FROM Products WHERE Id IN (SELECT Id FROM Products WHERE Id LIKE '1%')")]
    public async Task A_function_outside_the_permitted_set_is_refused(string sql)
    {
        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenOperation, result.DenialReason);
    }

    #endregion

    #region Nothing can write, and nothing did

    /// <summary>
    /// Every write and DDL shape, refused - and then the database is counted to prove the refusal
    /// was a refusal and not a silent success. The read-only connection mode, <c>query_only</c> and
    /// the authorizer are three independent reasons each of these cannot run; this asserts the
    /// outcome they produce together.
    /// </summary>
    [Theory]
    [InlineData("DELETE FROM Products")]
    [InlineData("UPDATE Products SET BusinessId = 2")]
    [InlineData("INSERT INTO Categories (BusinessId, Name) VALUES (1, 'x')")]
    [InlineData("DROP TABLE Products")]
    [InlineData("CREATE TABLE diagnostics_scratch (x INTEGER)")]
    [InlineData("ALTER TABLE Products ADD COLUMN injected INTEGER")]
    [InlineData("CREATE INDEX ix_diagnostics ON Products (Id)")]
    [InlineData("CREATE VIEW diagnostics_view AS SELECT Name FROM Products")]
    [InlineData("CREATE TRIGGER t AFTER INSERT ON Products BEGIN DELETE FROM Products; END")]
    [InlineData("REPLACE INTO Categories (BusinessId, Name) VALUES (1, 'x')")]
    public async Task A_write_or_DDL_statement_is_refused_and_mutates_nothing(string sql)
    {
        var before = await CountsAsync();

        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenOperation, result.DenialReason);
        Assert.Equal(before, await CountsAsync());
    }

    /// <summary>
    /// A second statement smuggled behind a read is refused shape-first by the use case, so this
    /// asserts the layer underneath it: even handed straight to the adapter, the write does not
    /// happen.
    /// </summary>
    [Fact]
    public async Task A_second_statement_behind_a_read_mutates_nothing_even_at_the_adapter()
    {
        var before = await CountsAsync();

        await ExecuteAsync("SELECT Id FROM Products; DELETE FROM Products");

        Assert.Equal(before, await CountsAsync());
    }

    [Theory]
    [InlineData("PRAGMA table_info('Products')")]
    [InlineData("PRAGMA query_only = OFF")]
    [InlineData("PRAGMA writable_schema = ON")]
    [InlineData("ATTACH DATABASE 'other.db' AS other")]
    [InlineData("DETACH DATABASE main")]
    [InlineData("VACUUM")]
    [InlineData("BEGIN TRANSACTION")]
    public async Task A_pragma_attach_or_transaction_control_statement_is_refused(string sql)
    {
        var result = await ExecuteAsync(sql);

        Assert.Equal(DiagnosticsQueryOutcome.Rejected, result.Outcome);
        Assert.Equal(DiagnosticsQueryDenialReason.ForbiddenOperation, result.DenialReason);
        Assert.Empty(result.Rows);
    }

    /// <summary>
    /// A refusal has to be refused for the <em>right</em> reason, which is only visible in the
    /// message: it names the exact table, column or function SQLite denied. Without this, a
    /// denial caused by some unrelated mistake in the authorizer would look identical to the
    /// surface being enforced.
    /// </summary>
    [Fact]
    public async Task A_refusal_names_the_identifier_that_was_denied()
    {
        var column = await ExecuteAsync("SELECT Name FROM Products");
        var table = await ExecuteAsync("SELECT ObjectId FROM BusinessMemberships");
        var function = await ExecuteAsync("SELECT sqlite_version()");

        Assert.Contains("Products.Name", column.Message, StringComparison.Ordinal);
        Assert.Contains("BusinessMemberships", table.Message, StringComparison.Ordinal);
        Assert.Contains("sqlite_version", function.Message, StringComparison.Ordinal);
    }

    #endregion

    #region Limits

    [Fact]
    public async Task Reading_stops_at_the_five_hundred_row_hard_maximum_and_says_it_was_truncated()
    {
        var result = await ExecuteAsync("SELECT Id, BusinessId FROM Categories ORDER BY Id");

        Assert.Equal(DiagnosticsQueryOutcome.Truncated, result.Outcome);
        Assert.Equal(DiagnosticsTruncationReason.RowLimit, result.TruncationReason);
        Assert.Equal(PlatformDiagnosticsQueryLimits.HardMaxRows, result.Rows.Count);
        Assert.Equal(
            SeededCategoriesPerBusiness * 2,
            await ScalarAsync("SELECT count(*) FROM Categories"));
    }

    [Fact]
    public async Task Reading_stops_at_the_serialised_byte_budget_and_says_it_was_truncated()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(
            maxResponseBytes: PlatformDiagnosticsQueryLimits.ResponseEnvelopeReserveBytes + 200);

        var result = await ExecuteAsync("SELECT Id, BusinessId FROM Categories ORDER BY Id", limits);

        Assert.Equal(DiagnosticsQueryOutcome.Truncated, result.Outcome);
        Assert.Equal(DiagnosticsTruncationReason.ResponseByteLimit, result.TruncationReason);
        Assert.InRange(result.Rows.Count, 1, PlatformDiagnosticsQueryLimits.HardMaxRows - 1);
    }

    /// <summary>
    /// The byte cap is counted against the budget, not guessed from the row count: the rows that
    /// were accepted must actually fit in it.
    /// </summary>
    [Fact]
    public async Task The_rows_returned_under_a_byte_cap_fit_inside_the_budget()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(
            maxResponseBytes: PlatformDiagnosticsQueryLimits.ResponseEnvelopeReserveBytes + 400);

        var result = await ExecuteAsync("SELECT Id, BusinessId FROM Categories ORDER BY Id", limits);

        var budget = new DiagnosticsResponseBudget(limits);
        budget.ChargeColumns(result.Columns);
        Assert.All(result.Rows, row => Assert.True(budget.TryCharge(row)));
        Assert.True(budget.SpentBytes <= limits.RowByteBudget);
    }

    #endregion

    #region Interruption

    /// <summary>
    /// The timeout requirement in full: a <em>permitted</em> query that is simply expensive, whose
    /// aggregate produces no first row until it has finished, is interrupted inside SQLite at the
    /// deadline - and the next query works, so the interrupted one left nothing holding the
    /// database.
    /// </summary>
    [Fact]
    public async Task A_costly_permitted_query_that_yields_no_first_row_is_interrupted_at_the_deadline()
    {
        var limits = PlatformDiagnosticsQueryLimits.Create(maxDuration: TimeSpan.FromMilliseconds(400));
        var start = DateTime.UtcNow;

        var result = await ExecuteAsync(CostlyQuery, limits);
        var elapsed = DateTime.UtcNow - start;

        Assert.True(
            result.Outcome == DiagnosticsQueryOutcome.TimedOut,
            $"Expected a timeout but got {result.Outcome} ({result.DenialReason}): {result.Message}");
        Assert.Empty(result.Rows);

        // Interrupted, not merely abandoned: the call returns promptly rather than after the query
        // would have finished on its own, which for this cartesian product is far longer.
        Assert.True(elapsed < TimeSpan.FromSeconds(20), $"The query took {elapsed} to be interrupted.");

        var afterwards = await ExecuteAsync("SELECT Id FROM Products ORDER BY Id", limits);
        AssertSucceeded(afterwards);
        Assert.Equal(2, afterwards.Rows.Count);
    }

    [Fact]
    public async Task A_cancelled_request_interrupts_the_running_query()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));

        var result = await ExecuteAsync(CostlyQuery, PlatformDiagnosticsQueryLimits.Default, cancellation.Token);

        Assert.True(
            result.Outcome == DiagnosticsQueryOutcome.Cancelled,
            $"Expected a cancellation but got {result.Outcome} ({result.DenialReason}): {result.Message}");
        Assert.Empty(result.Rows);
    }

    [Fact]
    public async Task A_request_cancelled_before_it_starts_runs_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await ExecuteAsync(
            "SELECT Id FROM Products",
            PlatformDiagnosticsQueryLimits.Default,
            cancellation.Token);

        Assert.Equal(DiagnosticsQueryOutcome.Cancelled, result.Outcome);
    }

    /// <summary>
    /// A cartesian product over a permitted table, filtered so it returns a single aggregate row
    /// and only after every combination has been visited. Nothing about it is forbidden - that is
    /// the point: the interruption has to work for a query the surface allows.
    /// </summary>
    private const string CostlyQuery =
        """
        SELECT count(*)
        FROM Categories a, Categories b, Categories c, Categories d
        WHERE a.Id + b.Id + c.Id + d.Id < 0
        """;

    #endregion

    #region Harness

    private DbContextOptions<AppDbContext> Options() =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;

    /// <summary>
    /// Asserts success and, when it fails, reports the adapter's own message - which for a refusal
    /// names the exact table, column or function SQLite denied. Without it a failure here says only
    /// "expected Succeeded, actual Rejected", which is the least useful half of the answer.
    /// </summary>
    private static void AssertSucceeded(DiagnosticsQueryExecution result) =>
        Assert.True(
            result.Outcome == DiagnosticsQueryOutcome.Succeeded,
            $"Expected the query to succeed but it was {result.Outcome} "
                + $"({result.DenialReason}): {result.Message}");

    private Task<DiagnosticsQueryExecution> ExecuteAsync(
        string sql,
        PlatformDiagnosticsQueryLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var executor = new SqliteDiagnosticsQueryExecutor(
            new SqliteDiagnosticsOptions { ConnectionString = _connectionString },
            limits ?? PlatformDiagnosticsQueryLimits.Default);

        return executor.ExecuteAsync(sql, cancellationToken);
    }

    /// <summary>
    /// Row counts of every permitted table plus the schema's own object count, read outside the
    /// diagnostics path. This is the "nothing was mutated" evidence for the write and DDL tests.
    /// </summary>
    private async Task<string> CountsAsync()
    {
        var counts = new List<string>();

        foreach (var table in PlatformDiagnosticsDataSurface.Tables.Select(table => table.Table))
        {
            counts.Add($"{table}={await ScalarAsync($"SELECT count(*) FROM {table}")}");
        }

        counts.Add($"objects={await ScalarAsync("SELECT count(*) FROM sqlite_master")}");
        counts.Add($"productColumns={await ScalarAsync("SELECT count(*) FROM pragma_table_info('Products')")}");

        return string.Join(", ", counts);
    }

    /// <summary>
    /// Changes the schema on a separate writable connection, the way a migration would, so a test
    /// can prove the enforcement reacts to a schema it was not written against.
    /// </summary>
    private async Task AlterSchemaAsync(string sql)
    {
        await using var writable = new SqliteConnection(_connectionString);
        await writable.OpenAsync();
        await using var command = writable.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    #endregion
}
