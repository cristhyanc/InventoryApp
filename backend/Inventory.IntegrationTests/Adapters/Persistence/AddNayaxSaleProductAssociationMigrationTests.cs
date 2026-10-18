using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Upgrade test for issue #758's two-step data migration: <c>AddNayaxSaleProductAssociation</c>,
/// which gives every sale its own local-product column and resolves what the recorded ids already
/// say, and <c>ResolveNayaxSaleProductsByName</c>, which performs the one-off audited name step and
/// holds the failure gate.
///
/// What a reviewer cannot read off the SQL, and what each test here is for: every sale that is
/// costed today must come out attributed to the same product with the same cost and COGS; a sale
/// whose two candidate products disagree must come out <em>unresolved and conflicting</em>, even
/// when its name would match exactly one product, because nothing may choose between two recorded
/// identifiers; the same disagreement on a sale that carries a cost must stop the release outright
/// rather than silently drop that sale from every future replay; and the whole thing must work the
/// same over a row the latest-sales synchronization wrote (which stored the local product id in
/// <c>NayaxProductId</c>) and a row the uploaded export wrote (which stored the raw provider id).
/// </summary>
public class AddNayaxSaleProductAssociationMigrationTests
{
    private const string PreviousMigration = "20261018034928_AddProviderIdentityColumns";
    private const string AssociationMigration = "AddNayaxSaleProductAssociation";
    private const string NameMigration = "ResolveNayaxSaleProductsByName";

    /// <summary>
    /// One business, four products, and sales written by both paths.
    ///
    /// <list type="bullet">
    ///   <item>5001 "Chips" carries the Nayax id 5001 - the ordinary shape, where #732's backfill
    ///   recorded the key the catalogue import used.</item>
    ///   <item>5002 "Locally created" has no provider id at all: a product created in the app.</item>
    ///   <item>7100 "Imported bar" carries the Nayax id 777, which is deliberately not its own key,
    ///   and 777 "Unrelated snack" is a different local product whose key happens to equal it. That
    ///   is the collision step 1 has to notice.</item>
    /// </list>
    /// </summary>
    private const string Seed = """
        INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered, SalesProvider)
            VALUES (1, 'Vending A', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL, 1);
        INSERT INTO Products (Id, Name, Sku, Description, UnitPrice, AverageUnitCost, QuantityInStock,
                              LowStockThreshold, RestockTo, IsActive, Unit, CreatedAt, UpdatedAt,
                              BusinessId, CategoryId, CostingQuantity, InventoryValue, GstRule,
                              ExternalProductId)
            VALUES (5001, 'Chips', NULL, NULL, 3.50, 1.10, 12, 2, 20, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, NULL, 12, 13.20, 0, '5001'),
                   (5002, 'Locally created', NULL, NULL, 2.00, 1.00, 4, 1, 10, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, NULL, 4, 4.00, 0, NULL),
                   (7100, 'Imported bar', NULL, NULL, 2.50, 1.00, 4, 1, 10, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, NULL, 4, 4.00, 0, '777'),
                   (777, 'Unrelated snack', NULL, NULL, 1.50, 0.50, 9, 1, 10, 1, 'unit',
                    '2026-01-01 00:00:00', '2026-01-02 00:00:00', 1, NULL, 9, 4.50, 0, NULL);
        """;

    /// <summary>A sale as the pre-#758 schema holds it: no ProductId column exists yet.</summary>
    private static string Sale(
        long transactionId,
        long? nayaxProductId,
        string? productName,
        decimal? cost = null) =>
        $"""
        INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID,
                                NayaxProductId, ProductName, SettlementValue, MachineAuthorizationTime,
                                UnitCostAtSale, CostOfGoodsSold, CostingStatus, CostSource)
            VALUES (1, {transactionId}, 12, 7,
                    {(nayaxProductId?.ToString() ?? "NULL")},
                    {(productName is null ? "NULL" : $"'{productName.Replace("'", "''")}'")},
                    3.50, '2026-02-01 00:00:00',
                    {(cost?.ToString() ?? "NULL")}, {(cost?.ToString() ?? "NULL")},
                    {(cost is null ? 0 : 1)}, {(cost is null ? 0 : 1)});
        """;

    [Fact]
    public async Task Step_one_resolves_a_sale_whose_direct_and_external_candidates_agree()
    {
        await using var fixture = await MigratedAsync(
            // Sync-written: the local product id it had matched, which is also product 5001's own
            // recorded Nayax id, so the two candidates agree.
            Sale(900, nayaxProductId: 5001, productName: "Chips", cost: 1.10m)
            // Export-written: the raw Nayax id of a product whose key is the same number.
            + Sale(901, nayaxProductId: 5001, productName: "Chips (3.50)"));

        Assert.Equal(
            [(900L, (long?)5001L), (901L, 5001L)],
            await fixture.AssociationsAsync());

        // The migration left the old column exactly as it found it, and said nothing about what
        // kind of identifier it holds.
        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync(x => x.TransactionID == 900);
        Assert.Equal(5001, sale.NayaxProductId);
        Assert.False(sale.ProviderProductIdIsRaw);
        Assert.Equal(ExternalProvider.Nayax, sale.Provider);
    }

    /// <summary>
    /// Every sale that carried a cost before the migration carries the same cost, the same COGS and
    /// the same product afterwards. That is the whole point of a data-preserving backfill, and the
    /// one claim a reviewer cannot check by reading the SQL.
    /// </summary>
    [Fact]
    public async Task A_costed_sale_keeps_its_product_its_cost_and_its_COGS()
    {
        await using var fixture = await MigratedAsync(
            Sale(900, nayaxProductId: 5001, productName: "Chips", cost: 1.10m));

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(5001, sale.ProductId);
        Assert.Equal(1.10m, sale.UnitCostAtSale);
        Assert.Equal(1.10m, sale.CostOfGoodsSold);
        Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
    }

    /// <summary>
    /// The stored number names product 777 directly and product 7100 through its recorded provider
    /// id. There is no honest answer, so the sale stays unresolved and both candidates are recorded
    /// - <strong>even though its name matches exactly one product</strong>, which the name step
    /// must not use to settle an ambiguity an identifier already raised.
    /// </summary>
    [Fact]
    public async Task Disagreeing_candidates_on_an_uncosted_sale_are_conflicting_and_never_named_by_name()
    {
        await using var fixture = await MigratedAsync(
            Sale(902, nayaxProductId: 777, productName: "Imported bar"));

        Assert.Equal([(902L, (long?)null)], await fixture.AssociationsAsync());

        await using var db = fixture.Context();
        var conflict = await db.NayaxSaleProductConflicts.AsNoTracking().SingleAsync();
        Assert.Equal(902, conflict.TransactionId);
        Assert.Equal(777, conflict.DirectCandidateProductId);
        Assert.Equal(7100, conflict.ExternalCandidateProductId);
        Assert.Empty(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The same disagreement on a sale that carries a cost today stops the release: leaving it
    /// unresolved would drop it from every future cost replay while it still carries COGS. The
    /// first migration still applies - it is what creates the conflict record and the table an
    /// audited decision is written to - and only the second one refuses.
    /// </summary>
    [Fact]
    public async Task Disagreeing_candidates_on_a_costed_sale_stop_the_release()
    {
        await using var fixture = await SeededAsync(
            Sale(903, nayaxProductId: 777, productName: "Imported bar", cost: 1.00m));

        await fixture.MigrateAsync(AssociationMigration);
        var refused = await Assert.ThrowsAsync<SqliteException>(() => fixture.MigrateAsync(NameMigration));
        Assert.Contains("carries a historical cost today", refused.Message, StringComparison.Ordinal);

        // The conflict and its candidates survived the refusal, because they are the first
        // migration's work and it committed.
        await using var db = fixture.Context();
        Assert.Equal(903, (await db.NayaxSaleProductConflicts.AsNoTracking().SingleAsync()).TransactionId);
        Assert.Null((await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
    }

    /// <summary>
    /// ... and the release comes up once a human has recorded the decision, through the same
    /// audited operation the operator command uses. Nothing here guessed: the product was named.
    /// </summary>
    [Fact]
    public async Task The_release_starts_once_an_audited_decision_resolves_the_conflicting_sale()
    {
        await using var fixture = await SeededAsync(
            Sale(903, nayaxProductId: 777, productName: "Imported bar", cost: 1.00m));

        await fixture.MigrateAsync(AssociationMigration);
        await Assert.ThrowsAsync<SqliteException>(() => fixture.MigrateAsync(NameMigration));

        await using (var decide = fixture.Context())
        {
            var sale = await decide.NayaxSales.SingleAsync();
            decide.NayaxSaleProductResolutions.Add(new NayaxSaleProductResolution
            {
                NayaxSaleId = sale.Id,
                TransactionId = sale.TransactionID,
                NewProductId = 7100,
                Rule = NayaxSaleProductResolutionRule.OperatorDecision,
                CandidateProductIds = "777,7100",
                DecidedBy = "A. Operator",
                ResolvedAtUtc = new DateTime(2026, 10, 18, 0, 0, 0, DateTimeKind.Utc),
            });
            sale.ProductId = 7100;
            await decide.SaveChangesAsync();
        }

        await fixture.MigrateAsync(NameMigration);

        await using var db = fixture.Context();
        Assert.Equal(7100, (await db.NayaxSales.AsNoTracking().SingleAsync()).ProductId);
    }

    /// <summary>
    /// Step 2: a sale whose stored id named no product at all, and whose name matches exactly one
    /// product under the Domain rule - everything before the first parenthesis, trimmed,
    /// case-insensitive. Resolved, and recorded with the old value, the new value and the rule.
    /// </summary>
    [Fact]
    public async Task Step_two_resolves_an_unambiguous_name_through_an_audited_idempotent_step()
    {
        await using var fixture = await MigratedAsync(
            Sale(904, nayaxProductId: 424242, productName: "locally created (2.00)"));

        await using var db = fixture.Context();
        var sale = await db.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(5002, sale.ProductId);

        var resolution = await db.NayaxSaleProductResolutions.AsNoTracking().SingleAsync();
        Assert.Equal(sale.Id, resolution.NayaxSaleId);
        Assert.Equal(904, resolution.TransactionId);
        Assert.Null(resolution.PreviousProductId);
        Assert.Equal(5002, resolution.NewProductId);
        Assert.Equal(NayaxSaleProductResolutionRule.UniqueProductName, resolution.Rule);
        Assert.Null(resolution.DecidedBy);
    }

    /// <summary>
    /// An ambiguous name is not a match, and an absent one is not either. Both stay unresolved, and
    /// neither stops an uncosted release.
    /// </summary>
    [Fact]
    public async Task An_ambiguous_or_absent_name_on_an_uncosted_sale_stays_unresolved()
    {
        await using var fixture = await MigratedAsync(
            """
            INSERT INTO Products (Id, Name, UnitPrice, AverageUnitCost, QuantityInStock, LowStockThreshold,
                                  RestockTo, IsActive, CreatedAt, UpdatedAt, BusinessId, GstRule)
                VALUES (5003, 'Chips', 3.50, 1.00, 1, 1, 2, 1,
                        '2026-01-01 00:00:00', '2026-01-01 00:00:00', 1, 0);
            """
            // 'Chips' now names two products, so the name rule has no single answer.
            + Sale(905, nayaxProductId: null, productName: "Chips")
            + Sale(906, nayaxProductId: null, productName: null));

        Assert.Equal(
            [(905L, (long?)null), (906L, null)],
            await fixture.AssociationsAsync());

        await using var db = fixture.Context();
        Assert.Empty(await db.NayaxSaleProductResolutions.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Every row that existed is recorded as Nayax's and left provenance-unknown: before this
    /// release the column held a raw provider id on one path and a local product id on the other,
    /// so nothing may read a pre-migration value as a key of anything.
    /// </summary>
    [Fact]
    public async Task Existing_rows_are_recorded_as_Nayax_and_left_provenance_unknown()
    {
        await using var fixture = await MigratedAsync(
            Sale(900, nayaxProductId: 5001, productName: "Chips")
            + Sale(907, nayaxProductId: null, productName: null));

        await using var db = fixture.Context();
        var sales = await db.NayaxSales.AsNoTracking().OrderBy(x => x.TransactionID).ToListAsync();
        Assert.All(sales, sale => Assert.Equal(ExternalProvider.Nayax, sale.Provider));
        Assert.All(sales, sale => Assert.False(sale.ProviderProductIdIsRaw));
        Assert.Equal([5001L, null], sales.Select(sale => sale.NayaxProductId));
    }

    /// <summary>
    /// A sale can never name another business's product, and that is the database's guarantee
    /// rather than an application rule: the foreign key is on <c>(BusinessId, ProductId)</c>.
    /// </summary>
    [Fact]
    public async Task The_migrated_schema_refuses_a_sale_that_names_another_businesss_product()
    {
        await using var fixture = await MigratedAsync(Sale(900, nayaxProductId: 5001, productName: "Chips"));

        await fixture.ExecuteAsync(
            """
            INSERT INTO Businesses (Id, Name, CreatedAtUtc, TimeZoneId, Status, GstRegistered)
                VALUES (2, 'Vending B', '2026-01-01 00:00:00', 'Australia/Sydney', 1, NULL);
            """);

        var refused = await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync(
            """
            PRAGMA foreign_keys = ON;
            INSERT INTO NayaxSales (BusinessId, TransactionID, TransactionStatusId, MachineID, ProductId,
                                    Provider, ProviderProductIdIsRaw, SettlementValue,
                                    MachineAuthorizationTime, CostingStatus, CostSource)
                VALUES (2, 999, 12, 7, 5001, 1, 1, 1.00, '2026-02-01 00:00:00', 0, 0);
            """));
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deleting a product a sale references is restricted: never cascaded, never nulled. A sale is
    /// historical financial data, so the delete fails loudly instead of taking the sale with it or
    /// quietly detaching its COGS from any product.
    /// </summary>
    [Fact]
    public async Task Deleting_a_product_a_sale_references_is_restricted()
    {
        await using var fixture = await MigratedAsync(Sale(900, nayaxProductId: 5001, productName: "Chips"));

        var refused = await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync(
            """
            PRAGMA foreign_keys = ON;
            DELETE FROM Products WHERE Id = 5001;
            """));
        Assert.Contains("FOREIGN KEY", refused.Message, StringComparison.Ordinal);
    }

    private static async Task<MigrationFixture> SeededAsync(string sales)
    {
        var fixture = await MigrationFixture.AtAsync(PreviousMigration);
        await fixture.ExecuteAsync(Seed + sales);
        return fixture;
    }

    private static async Task<MigrationFixture> MigratedAsync(string sales)
    {
        var fixture = await SeededAsync(sales);
        await fixture.MigrateAsync(AssociationMigration);
        await fixture.MigrateAsync(NameMigration);
        return fixture;
    }

    private sealed class MigrationFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private MigrationFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options)
        {
            _connection = connection;
            Options = options;
        }

        public DbContextOptions<AppDbContext> Options { get; }

        public static async Task<MigrationFixture> AtAsync(string migration)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            var fixture = new MigrationFixture(connection, options);
            await fixture.MigrateAsync(migration);
            return fixture;
        }

        public async Task MigrateAsync(string target)
        {
            await using var db = TestAppDbContext.Unrestricted(Options);
            await db.GetService<IMigrator>().MigrateAsync(target);
        }

        public async Task ExecuteAsync(string sql)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public AppDbContext Context() => TestAppDbContext.Unrestricted(Options);

        /// <summary>Each sale's transaction id and the local product it came out attributed to.</summary>
        public async Task<List<(long TransactionId, long? ProductId)>> AssociationsAsync()
        {
            await using var db = Context();
            return await db.NayaxSales.AsNoTracking()
                .OrderBy(sale => sale.TransactionID)
                .Select(sale => ValueTuple.Create(sale.TransactionID, sale.ProductId))
                .ToListAsync();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }
}
