using Inventory.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #307 moved <c>AppDbContext</c>, the EF entities and the EF migrations from
/// <c>InventoryApi</c> into <c>Inventory.Infrastructure</c>. That is a relocation, never a schema
/// change, and these tests are what makes the difference provable rather than asserted: a
/// deployment applies migrations by <c>MigrationId</c>, so a renamed, regenerated or re-namespaced
/// migration would make an existing production database either re-run schema operations or stop
/// matching the code - with no compiler error to warn anybody.
///
/// They are characterisation tests, not red-then-green ones: they held on <c>develop</c> before the
/// move and must still hold after it. See <c>AddNayaxMachineStockEventsMigrationTests</c> and the
/// other per-migration upgrade tests for the behaviour of individual migrations.
/// </summary>
public class MigrationRelocationTests
{
    /// <summary>
    /// Every migration that existed when the relocation happened, in order. Changing the namespace
    /// of a migration class does not change its <c>MigrationId</c> - the id comes from the
    /// <c>[Migration]</c> attribute - and this list is what proves that for all 38 of them at once.
    ///
    /// It is deliberately a prefix assertion rather than an equality one: a genuinely new migration
    /// appended by later work must not have to edit this list, but dropping, renaming or reordering
    /// any historical id must fail.
    /// </summary>
    private static readonly string[] MigrationIdsAtRelocation =
    [
        "20260707110636_InitialCreate",
        "20260723110733_RemoveUniqueName",
        "20260725102732_activeColmun",
        "20260728100150_eatbeforedate",
        "20260812091827_addmachineidcolumn",
        "20260902015611_receiptColumns",
        "20260902191900_AddNayaxSales",
        "20260902194500_AddNayaxProductIdToNayaxSales",
        "20260902220300_RemoveMachineNumberSiteFieldsFromNayaxSales",
        "20260903015645_AddImportedReimbursementFiles",
        "20260904030557_AddTransactionStatusId",
        "20260904091547_AddReceiptItems",
        "20260904094933_AddWeightedAverageInventoryCost",
        "20260904102447_AddHistoricalSaleCost",
        "20260904120646_AddOperatingExpenses",
        "20260904125340_otherexpenses",
        "20260906115551_newtbnayaxfees",
        "20260906213000_AddNayaxProcessingFeeRates",
        "20260906233500_RemoveNayaxSalesQuantity",
        "20260906233840_AddOperatingExpenseReceiptAttachments",
        "20260907105844_othermigr",
        "20260907114500_ConvertCommissionRatesToFractions",
        "20260907123300_RemoveProductMapped",
        "20260908034556_RenameOperatingExpenseAttachments",
        "20260908045058_AddHistoricalPerpetualInventoryCosting",
        "20260908074151_AddNayaxHistoricalTransactionCost",
        "20260908094148_AddInventoryCostTransitionBaseline",
        "20260913005745_AddSupplierOrders",
        "20260913011850_AddSupplierOrderReceiptAllocations",
        "20260915064737_AddProductRestockTo",
        "20260923110030_AddBusinessOwnershipModel",
        "20260923111712_AddBusinessOwnershipToTenantOwnedEntities",
        "20260923114130_ScopeUniqueConstraintsByBusiness",
        "20260923120151_AddBusinessBackfillAudit",
        "20260927140121_AddNayaxMachineStockEvents",
        "20260928012603_RenameNayaxMachineStockEventToEventLogContract",
        "20260928053917_AddNayaxMachineStockEventDuplicateResolution",
        "20261004064601_AddInventoryCostRepairs",
    ];

    private static DbContextOptions<AppDbContext> OptionsFor(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

    /// <summary>
    /// Acceptance criterion: no migration was regenerated or renamed. The ids EF discovers must
    /// still begin with exactly the historical sequence, in the same order.
    /// </summary>
    [Fact]
    public void Every_migration_keeps_its_migration_id()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));

        var discovered = db.GetService<IMigrationsAssembly>()
            .Migrations
            .Keys
            .ToArray();

        Assert.True(
            discovered.Length >= MigrationIdsAtRelocation.Length,
            $"Expected at least {MigrationIdsAtRelocation.Length} migrations, found {discovered.Length}. "
                + "A migration must never be deleted: EF applies schema history by MigrationId, so a "
                + "database that recorded a removed id can no longer be reasoned about.");

        Assert.Equal(
            MigrationIdsAtRelocation,
            discovered.Take(MigrationIdsAtRelocation.Length).ToArray());
    }

    /// <summary>
    /// The migrations must be discovered from the assembly that now owns them. EF resolves the
    /// migrations assembly from the <c>DbContext</c>'s own assembly by default, which is why the
    /// relocation needed no <c>MigrationsAssembly</c> configuration - this test is what would fail
    /// if the context and its migrations were ever split across two projects again.
    /// </summary>
    [Fact]
    public void Inventory_Infrastructure_is_the_migrations_assembly()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));

        var migrationsAssembly = db.GetService<IMigrationsAssembly>().Assembly;

        Assert.Equal(
            typeof(Inventory.Infrastructure.AssemblyMarker).Assembly,
            migrationsAssembly);
        Assert.Equal(typeof(AppDbContext).Assembly, migrationsAssembly);
    }

    /// <summary>
    /// Acceptance criterion: the model snapshot still describes the mapped model exactly, so
    /// <c>dotnet ef migrations add</c> would produce an empty migration. This is the in-test
    /// equivalent of <c>dotnet ef migrations has-pending-model-changes</c>, and it covers the risk
    /// specific to this change: the entity CLR namespace moved, and the snapshot has to agree with
    /// the relational model that results.
    /// </summary>
    [Fact]
    public void Model_snapshot_reports_no_pending_model_changes()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var db = TestAppDbContext.Unrestricted(OptionsFor(connection));

        Assert.False(
            db.Database.HasPendingModelChanges(),
            "AppDbContextModelSnapshot no longer matches the mapped model. Issue #307 relocates the "
                + "persistence types and must not change the schema: if this fails, the snapshot and "
                + "the model have genuinely diverged and the next `migrations add` would emit schema "
                + "operations nobody reviewed.");
    }

    /// <summary>
    /// Acceptance criterion: a database created by the pre-relocation migrations migrates with no
    /// new migration applied. Applying the full history is exactly what <c>develop</c>'s code does
    /// - the migration bodies are byte-identical - so the resulting schema is <c>develop</c>'s
    /// schema; what this proves is that the relocated code then considers it current, both by
    /// migration history and by model.
    /// </summary>
    [Fact]
    public void A_database_migrated_by_the_existing_history_has_nothing_left_to_apply()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = OptionsFor(connection);

        using (var migrated = TestAppDbContext.Unrestricted(options))
        {
            migrated.Database.Migrate();
        }

        using var verify = TestAppDbContext.Unrestricted(options);

        Assert.Empty(verify.Database.GetPendingMigrations());
        Assert.False(verify.Database.HasPendingModelChanges());

        // The history table records the ids, not the class namespaces, so a correctly relocated
        // migration set leaves the same audit trail a pre-relocation deployment would have.
        var applied = verify.Database.GetAppliedMigrations().ToArray();
        Assert.Equal(
            MigrationIdsAtRelocation,
            applied.Take(MigrationIdsAtRelocation.Length).ToArray());
    }

    /// <summary>
    /// Re-running <c>Migrate()</c> on an already-current database is a no-op. This is the shape
    /// normal Production startup takes (<c>DatabaseSchemaStartup</c>, issue #201): the relocation
    /// must not turn an idempotent startup into one that tries to re-apply schema work.
    /// </summary>
    [Fact]
    public void Migrating_an_already_current_database_applies_nothing()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        var options = OptionsFor(connection);

        using (var migrated = TestAppDbContext.Unrestricted(options))
        {
            migrated.Database.Migrate();
        }

        using var again = TestAppDbContext.Unrestricted(options);
        var appliedBefore = again.Database.GetAppliedMigrations().ToArray();

        again.Database.Migrate();

        Assert.Equal(appliedBefore, again.Database.GetAppliedMigrations().ToArray());
        Assert.Empty(again.Database.GetPendingMigrations());
    }
}
