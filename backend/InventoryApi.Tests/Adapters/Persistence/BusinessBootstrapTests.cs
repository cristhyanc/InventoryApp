using InventoryApi.Bootstrap;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The safety properties of the existing-data bootstrap (issue #64, checkpoint 3).
///
/// This is the one operation in the repository that rewrites ownership on real financial and
/// inventory history, so it is tested against a migrated relational database rather than a model
/// double: the backfill is raw SQL over physical tables, and nothing less would prove it.
///
/// Every test here seeds through the schema the checkpoint-2 migration produces - rows with
/// BusinessId = 0, owned by nobody - which is exactly the state a real deployment is in after
/// the schema is applied and before a human runs the bootstrap.
/// </summary>
public class BusinessBootstrapTests : IDisposable
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string SecondOid = "33333333-3333-3333-3333-333333333333";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly TimeProvider _clock = TimeProvider.System;

    public BusinessBootstrapTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        // Migrate rather than EnsureCreated: the bootstrap refuses to run against a schema with
        // pending migrations, and that refusal is one of the behaviours under test.
        using var db = TestAppDbContext.Unrestricted(_options);
        db.GetService<IMigrator>().Migrate();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static BusinessBootstrapOptions ValidOptions(params string[] objectIds)
    {
        var options = new BusinessBootstrapOptions { BusinessName = "Existing Vending Business" };
        foreach (var objectId in objectIds.Length == 0 ? [Oid] : objectIds)
        {
            options.Members.Add(new BusinessBootstrapMemberOptions { DirectoryTenantId = Tid, ObjectId = objectId });
        }

        return options;
    }

    private Task<BusinessBootstrapResult> RunAsync(BusinessBootstrapOptions options, bool dryRun)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        return new BusinessBootstrapper(db, options, _clock).RunAsync(dryRun, CancellationToken.None);
    }

    /// <summary>
    /// Seeds the pre-bootstrap world: a representative slice of unowned business data with known
    /// row counts and known money and stock values.
    /// </summary>
    private void SeedUnassignedBusinessData()
    {
        using var db = TestAppDbContext.Unrestricted(_options);

        var category = new Category { Name = "Snacks" };
        var supplier = new Supplier { Name = "Legacy Supplier" };
        db.Categories.Add(category);
        db.Suppliers.Add(supplier);
        db.SaveChanges();

        var chips = new Product
        {
            Name = "Chips",
            Sku = "CHIP-1",
            UnitPrice = 3.50m,
            AverageUnitCost = 1.25m,
            CostingQuantity = 40,
            InventoryValue = 50.00m,
            QuantityInStock = 42,
            CategoryId = category.Id,
            SupplierId = supplier.Id,
        };
        var bars = new Product
        {
            Name = "Bars",
            Sku = "BAR-1",
            UnitPrice = 2.75m,
            AverageUnitCost = 0.95m,
            CostingQuantity = 18,
            InventoryValue = 17.10m,
            QuantityInStock = 20,
        };
        db.Products.AddRange(chips, bars);
        db.SaveChanges();

        var purchase = new Purchase { Title = "March stock", TotalAmount = 120.45m, SupplierId = supplier.Id };
        purchase.Items.Add(new PurchaseItem { Product = chips, Quantity = 24m, UnitCost = 1.30m });
        purchase.Items.Add(new PurchaseItem { Product = bars, Quantity = 30m, UnitCost = 1.00m });
        db.Receipts.Add(purchase);

        db.StockAdjustments.AddRange(
            new StockAdjustment { Product = chips, QuantityChange = 24, QuantityAfter = 42, TotalCost = 31.20m },
            new StockAdjustment { Product = bars, QuantityChange = -4, QuantityAfter = 20, TotalCost = -3.80m });

        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 5001,
                MachineID = 7,
                SettlementValue = 3.50m,
                CostOfGoodsSold = 1.25m,
                MachineAuthorizationTime = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            },
            new NayaxSales
            {
                TransactionID = 5002,
                MachineID = 7,
                SettlementValue = 2.75m,
                CostOfGoodsSold = 0.95m,
                MachineAuthorizationTime = new DateTime(2026, 7, 1, 10, 0, 0, DateTimeKind.Utc),
            });

        db.OperatingExpenses.Add(new OperatingExpense
        {
            Description = "Insurance",
            AmountExGst = 100m,
            GstAmount = 10m,
            TotalAmount = 110m,
        });
        db.CommissionPayments.Add(new CommissionPayment { SiteId = 7, Amount = 42.50m });
        db.NayaxProcessingFeeRates.Add(new NayaxProcessingFeeRate
        {
            EffectiveFrom = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            FeeExGst = 0.19m,
        });

        db.SaveChanges();

        // The seeding contexts are unrestricted, so nothing stamped an owner. Confirm the
        // starting state really is "owned by nobody" before any test asserts a change from it.
        Assert.Equal(0, db.Products.Single(p => p.Name == "Chips").BusinessId);
    }

    private (long Products, long Sales, decimal InventoryValue, decimal Settlement, decimal PurchaseTotal) Snapshot()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        return (
            db.Products.Count(),
            db.NayaxSales.Count(),
            db.Products.Sum(p => p.InventoryValue ?? 0m),
            db.NayaxSales.Sum(s => s.SettlementValue),
            db.Receipts.Sum(r => r.TotalAmount ?? 0m));
    }

    #region It refuses rather than inventing an identity

    [Fact]
    public async Task Missing_configuration_refuses_and_changes_nothing()
    {
        SeedUnassignedBusinessData();
        var before = Snapshot();

        var result = await RunAsync(new BusinessBootstrapOptions(), dryRun: false);

        Assert.Equal(BusinessBootstrapOutcome.ConfigurationInvalid, result.Outcome);

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Businesses.ToList());
        Assert.Empty(db.BusinessMemberships.ToList());
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// A configured business with no members would be unreachable: nobody could sign in to it.
    /// The bootstrap must not quietly create one and leave the data stranded.
    /// </summary>
    [Fact]
    public async Task A_business_name_without_any_member_refuses()
    {
        SeedUnassignedBusinessData();

        var result = await RunAsync(new BusinessBootstrapOptions { BusinessName = "Vending Co" }, dryRun: false);

        Assert.Equal(BusinessBootstrapOutcome.ConfigurationInvalid, result.Outcome);
        Assert.Contains("will not invent an identity", result.Message, StringComparison.Ordinal);

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Businesses.ToList());
    }

    [Theory]
    [InlineData("", Oid)]
    [InlineData(Tid, "")]
    [InlineData("not-a-guid", Oid)]
    [InlineData(Tid, "not-a-guid")]
    public async Task A_malformed_entra_pair_refuses(string tid, string oid)
    {
        SeedUnassignedBusinessData();

        var options = new BusinessBootstrapOptions { BusinessName = "Vending Co" };
        options.Members.Add(new BusinessBootstrapMemberOptions { DirectoryTenantId = tid, ObjectId = oid });

        var result = await RunAsync(options, dryRun: false);

        Assert.Equal(BusinessBootstrapOutcome.ConfigurationInvalid, result.Outcome);

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Businesses.ToList());
    }

    /// <summary>
    /// The failure message is printed to an operator console, so it must name the position of the
    /// bad entry and never the identifier itself.
    /// </summary>
    [Fact]
    public async Task A_configuration_failure_message_never_echoes_the_entra_identifier()
    {
        var options = new BusinessBootstrapOptions { BusinessName = "Vending Co" };
        options.Members.Add(new BusinessBootstrapMemberOptions { DirectoryTenantId = Tid, ObjectId = "not-a-guid" });

        var result = await RunAsync(options, dryRun: false);

        Assert.DoesNotContain(Tid, result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Members[0]", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task More_than_one_existing_business_refuses_rather_than_choosing()
    {
        SeedUnassignedBusinessData();

        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Businesses.AddRange(
                new Business { Name = "First", CreatedAtUtc = DateTime.UtcNow },
                new Business { Name = "Second", CreatedAtUtc = DateTime.UtcNow });
            db.SaveChanges();
        }

        var result = await RunAsync(ValidOptions(), dryRun: false);

        Assert.Equal(BusinessBootstrapOutcome.AmbiguousExistingBusiness, result.Outcome);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(0, verify.Products.First().BusinessId);
    }

    #endregion

    #region Dry run

    [Fact]
    public async Task A_dry_run_reports_the_real_numbers_and_commits_nothing()
    {
        SeedUnassignedBusinessData();
        var before = Snapshot();

        var result = await RunAsync(ValidOptions(), dryRun: true);

        Assert.True(result.Succeeded);
        Assert.True(result.DryRun);
        Assert.True(result.TotalRowsAssigned > 0);
        Assert.All(result.Tables, table => Assert.Equal(0, table.UnassignedRowsAfter));

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(db.Businesses.ToList());
        Assert.Empty(db.BusinessBackfillAudits.ToList());
        Assert.Equal(0, db.Products.First().BusinessId);
        Assert.Equal(before, Snapshot());
    }

    /// <summary>
    /// The dry run is only useful if its numbers are the ones the apply produces. They are
    /// measured the same way, so they must match exactly.
    /// </summary>
    [Fact]
    public async Task A_dry_run_predicts_exactly_what_the_apply_assigns()
    {
        SeedUnassignedBusinessData();

        var dryRun = await RunAsync(ValidOptions(), dryRun: true);
        var applied = await RunAsync(ValidOptions(), dryRun: false);

        Assert.Equal(dryRun.TotalRowsAssigned, applied.TotalRowsAssigned);
        Assert.Equal(
            dryRun.Tables.Select(t => (t.TableName, t.RowsAssigned)),
            applied.Tables.Select(t => (t.TableName, t.RowsAssigned)));
    }

    #endregion

    #region Apply preserves the data

    [Fact]
    public async Task Applying_assigns_every_row_and_preserves_counts_and_financial_values()
    {
        SeedUnassignedBusinessData();
        var before = Snapshot();

        var result = await RunAsync(ValidOptions(), dryRun: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.BusinessCreated);
        Assert.Equal(1, result.MembershipsCreated);
        Assert.True(result.AllTotalsPreserved);
        Assert.All(result.Totals, total => Assert.Equal(total.Before, total.After));
        Assert.All(result.Tables, table => Assert.Equal(table.TotalRowsBefore, table.TotalRowsAfter));

        // Row counts and every money/stock value are byte-for-byte what they were.
        Assert.Equal(before, Snapshot());

        using var db = TestAppDbContext.Unrestricted(_options);
        var business = Assert.Single(db.Businesses.ToList());
        Assert.Equal("Existing Vending Business", business.Name);
        Assert.Equal(0, db.Products.Count(p => p.BusinessId == 0));
        Assert.Equal(2, db.Products.Count(p => p.BusinessId == business.Id));
        Assert.Equal(2, db.NayaxSales.Count(s => s.BusinessId == business.Id));
        Assert.Equal(2, db.ReceiptItems.Count(i => i.BusinessId == business.Id));
    }

    /// <summary>
    /// The point of the whole rollout: after the bootstrap, the configured actor's business can
    /// actually see its own data through the ordinary scoped path.
    /// </summary>
    [Fact]
    public async Task After_the_bootstrap_the_owning_business_can_read_its_data_through_the_scoped_path()
    {
        SeedUnassignedBusinessData();

        var result = await RunAsync(ValidOptions(), dryRun: false);
        Assert.True(result.Succeeded, result.Message);

        await using var scoped = TestAppDbContext.For(_options, result.BusinessId!.Value);

        Assert.Equal(2, scoped.Products.Count());
        Assert.Equal(2, scoped.NayaxSales.Count());
        Assert.Equal(3.50m, scoped.Products.Single(p => p.Name == "Chips").UnitPrice);
        Assert.Equal(50.00m, scoped.Products.Single(p => p.Name == "Chips").InventoryValue);
    }

    [Fact]
    public async Task Applying_writes_a_reviewable_audit_row_per_table()
    {
        SeedUnassignedBusinessData();

        var result = await RunAsync(ValidOptions(), dryRun: false);

        using var db = TestAppDbContext.Unrestricted(_options);
        var audits = db.BusinessBackfillAudits.ToList();

        Assert.Equal(result.Tables.Count, audits.Count);
        Assert.All(audits, audit => Assert.Equal(result.RunId, audit.RunId));
        Assert.All(audits, audit => Assert.Equal(result.BusinessId, audit.BusinessId));
        Assert.All(audits, audit => Assert.Equal(0, audit.UnassignedRowsAfter));
        Assert.Equal(
            result.TotalRowsAssigned,
            audits.Sum(audit => audit.RowsAssigned));
    }

    #endregion

    #region Restart safety and idempotence

    /// <summary>
    /// Re-running a completed bootstrap must be a no-op, not a second business, duplicate
    /// memberships, or re-stamped rows. This is what makes an interrupted run safe to simply run
    /// again.
    /// </summary>
    [Fact]
    public async Task Running_twice_assigns_nothing_the_second_time_and_creates_no_duplicates()
    {
        SeedUnassignedBusinessData();

        var first = await RunAsync(ValidOptions(), dryRun: false);
        var snapshotAfterFirst = Snapshot();

        var second = await RunAsync(ValidOptions(), dryRun: false);

        Assert.True(second.Succeeded, second.Message);
        Assert.False(second.BusinessCreated);
        Assert.Equal(0, second.MembershipsCreated);
        Assert.Equal(0, second.TotalRowsAssigned);
        Assert.Equal(snapshotAfterFirst, Snapshot());

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Single(db.Businesses.ToList());
        Assert.Single(db.BusinessMemberships.ToList());
        Assert.Equal(first.BusinessId, second.BusinessId);
    }

    /// <summary>
    /// A partially assigned database - the state left by a run interrupted midway - must be
    /// completed by a re-run, touching only what is still unassigned.
    /// </summary>
    [Fact]
    public async Task A_partially_assigned_database_is_completed_by_a_re_run()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);

        // Simulate an interrupted run: put one table back into the unassigned state.
        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Database.ExecuteSqlRaw("UPDATE Products SET BusinessId = 0;");
        }

        var result = await RunAsync(ValidOptions(), dryRun: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(2, result.Tables.Single(t => t.TableName == "Products").RowsAssigned);
        Assert.Equal(0, result.Tables.Single(t => t.TableName == "NayaxSales").RowsAssigned);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(0, verify.Products.Count(p => p.BusinessId == 0));
        Assert.Single(verify.Businesses.ToList());
    }

    /// <summary>
    /// A membership a human deliberately revoked must not be silently reactivated by re-running
    /// the bootstrap, or offboarding someone would be undone by routine maintenance.
    /// </summary>
    [Fact]
    public async Task A_revoked_membership_is_not_reactivated_by_a_re_run()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);

        RevokeEveryMembership();

        // Nothing is left unassigned after the first apply, so there is no data to strand and the
        // re-run is simply a no-op. What it must not do is quietly restore the revoked approval.
        var result = await RunAsync(ValidOptions(), dryRun: false);

        Assert.Equal(0, result.MembershipsCreated);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.False(Assert.Single(verify.BusinessMemberships.ToList()).IsActive);
    }

    /// <summary>
    /// The complement of the rule above. Keeping a revoked membership revoked is correct, but it
    /// must not be allowed to hand data to a business nobody can reach: if every configured actor
    /// matches only a revoked membership, assigning the rows would strand them behind an
    /// authorization boundary with no one on the other side.
    /// </summary>
    [Fact]
    public async Task Unassigned_rows_are_refused_when_no_active_membership_remains()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);

        RevokeEveryMembership();

        // Put the data back into the unassigned state, as an interrupted rollout would leave it.
        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Database.ExecuteSqlRaw("UPDATE Products SET BusinessId = 0;");
            db.Database.ExecuteSqlRaw("UPDATE NayaxSales SET BusinessId = 0;");
        }

        var result = await RunAsync(ValidOptions(), dryRun: false);

        Assert.Equal(BusinessBootstrapOutcome.NoActiveMembership, result.Outcome);
        Assert.Contains("nobody can access", result.Message, StringComparison.Ordinal);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, verify.Products.Count(p => p.BusinessId == 0));
        Assert.Equal(2, verify.NayaxSales.Count(s => s.BusinessId == 0));
        Assert.False(Assert.Single(verify.BusinessMemberships.ToList()).IsActive);
    }

    /// <summary>
    /// The guard is on the state the business will actually be in, so a dry run reports the same
    /// refusal rather than predicting a success the apply would not deliver.
    /// </summary>
    [Fact]
    public async Task A_dry_run_also_refuses_when_no_active_membership_remains()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);
        RevokeEveryMembership();

        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Database.ExecuteSqlRaw("UPDATE Products SET BusinessId = 0;");
        }

        var result = await RunAsync(ValidOptions(), dryRun: true);

        Assert.Equal(BusinessBootstrapOutcome.NoActiveMembership, result.Outcome);
    }

    /// <summary>
    /// Adding a second, active actor is the documented way out of the state above: the revoked
    /// membership stays revoked and the rollout can continue.
    /// </summary>
    [Fact]
    public async Task Configuring_another_actor_unblocks_a_business_whose_membership_was_revoked()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);
        RevokeEveryMembership();

        using (var db = TestAppDbContext.Unrestricted(_options))
        {
            db.Database.ExecuteSqlRaw("UPDATE Products SET BusinessId = 0;");
        }

        var result = await RunAsync(ValidOptions(Oid, SecondOid), dryRun: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(1, result.MembershipsCreated);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(0, verify.Products.Count(p => p.BusinessId == 0));
        Assert.False(verify.BusinessMemberships.Single(m => m.ObjectId == Oid.ToUpperInvariant()).IsActive);
        Assert.True(verify.BusinessMemberships.Single(m => m.ObjectId == SecondOid.ToUpperInvariant()).IsActive);
    }

    private void RevokeEveryMembership()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        foreach (var membership in db.BusinessMemberships.ToList())
        {
            membership.IsActive = false;
        }

        db.SaveChanges();
    }

    [Fact]
    public async Task Adding_a_second_configured_member_later_creates_only_the_new_membership()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(Oid), dryRun: false);

        var result = await RunAsync(ValidOptions(Oid, SecondOid), dryRun: false);

        Assert.Equal(1, result.MembershipsCreated);

        using var db = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, db.BusinessMemberships.Count());
    }

    #endregion

    #region Nothing happens by itself

    /// <summary>
    /// The defining property of this checkpoint: applying the schema assigns nothing. Migrations
    /// run on every deployment, so if ownership were assigned by one of them, a routine deploy
    /// would perform a high-risk production data operation unattended.
    /// </summary>
    [Fact]
    public void Applying_every_migration_assigns_no_ownership_and_creates_no_business()
    {
        SeedUnassignedBusinessData();

        using var db = TestAppDbContext.Unrestricted(_options);
        db.GetService<IMigrator>().Migrate();

        Assert.Empty(db.Businesses.ToList());
        Assert.Empty(db.BusinessMemberships.ToList());
        Assert.Empty(db.BusinessBackfillAudits.ToList());
        Assert.Equal(2, db.Products.Count(p => p.BusinessId == 0));
        Assert.Equal(2, db.NayaxSales.Count(s => s.BusinessId == 0));
    }

    #endregion

    #region Readiness reporting

    /// <summary>
    /// A freshly migrated database must not report itself as bootstrapped. It has no business and
    /// nobody who can sign in, so the rollout has not started - whatever the unassigned-row count
    /// happens to be.
    ///
    /// Note that "fresh" is not the same as "empty" here: the pre-existing
    /// <c>AddNayaxProcessingFeeRates</c> migration seeds a default fee rate, which is tenant-owned
    /// and therefore starts unassigned like any other legacy row. That is exactly why readiness
    /// cannot be inferred from the row count alone.
    /// </summary>
    [Fact]
    public void A_fresh_database_with_no_business_is_not_reported_as_bootstrapped()
    {
        using var db = TestAppDbContext.Unrestricted(_options);

        var state = TenantOwnershipReadiness.Inspect(db);

        Assert.Equal(0, state.BusinessCount);
        Assert.Equal(0, state.ActiveMembershipCount);
        Assert.False(state.IsReady);
    }

    /// <summary>
    /// The specific trap the old check fell into: a database with no unassigned rows at all, but
    /// also no business and no membership, must still be reported as not bootstrapped.
    /// </summary>
    [Fact]
    public void A_database_with_no_unassigned_rows_but_no_business_is_not_ready()
    {
        using var db = TestAppDbContext.Unrestricted(_options);

        // Clear the seeded fee rate so the unassigned count really is zero.
        db.Database.ExecuteSqlRaw("DELETE FROM NayaxProcessingFeeRates;");

        var state = TenantOwnershipReadiness.Inspect(db);

        Assert.Equal(0, state.UnassignedRows);
        Assert.Equal(0, state.BusinessCount);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void A_database_with_unassigned_rows_is_not_ready()
    {
        SeedUnassignedBusinessData();

        using var db = TestAppDbContext.Unrestricted(_options);
        var state = TenantOwnershipReadiness.Inspect(db);

        Assert.True(state.UnassignedRows > 0);
        Assert.False(state.IsReady);
    }

    /// <summary>
    /// Assigned data whose only membership has been revoked is not ready either: the rows have an
    /// owner, but no one can reach them.
    /// </summary>
    [Fact]
    public async Task Assigned_data_with_no_active_membership_is_not_ready()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);
        RevokeEveryMembership();

        using var db = TestAppDbContext.Unrestricted(_options);
        var state = TenantOwnershipReadiness.Inspect(db);

        Assert.Equal(0, state.UnassignedRows);
        Assert.Equal(1, state.BusinessCount);
        Assert.Equal(0, state.ActiveMembershipCount);
        Assert.False(state.IsReady);
    }

    [Fact]
    public async Task A_completed_bootstrap_is_reported_as_ready()
    {
        SeedUnassignedBusinessData();
        await RunAsync(ValidOptions(), dryRun: false);

        using var db = TestAppDbContext.Unrestricted(_options);
        var state = TenantOwnershipReadiness.Inspect(db);

        Assert.Equal(0, state.UnassignedRows);
        Assert.Equal(1, state.BusinessCount);
        Assert.Equal(1, state.ActiveMembershipCount);
        Assert.True(state.IsReady);
    }

    #endregion
}
