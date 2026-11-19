using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Application.Commissions;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Shared;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Gst;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational SQLite tests: bookkeeping's card/cash split, COGS completeness, and date-range
/// filtering depend on SQL translation and ordering, not just in-memory LINQ semantics.
/// </summary>
public class EfBookkeepingReportFactsProviderTests
{
    private static async Task<(SqliteConnection Connection, DbContextOptions<AppDbContext> Options)> CreateSqliteAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var setup = TestAppDbContext.Unrestricted(options);
        await setup.Database.EnsureCreatedAsync();
        return (connection, options);
    }

    private static IGetSiteCommissionReport EmptyCommissions()
    {
        var mock = new Mock<IGetSiteCommissionReport>();
        mock.Setup(x => x.HandleBusinessPeriod(It.IsAny<SiteCommissionBusinessPeriod>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((SiteCommissionBusinessPeriod period, long? _, CancellationToken _) =>
                new SiteCommissionReport(period.FirstBusinessDate, period.LastBusinessDate, []));
        return mock.Object;
    }

    [Fact]
    public async Task Splits_card_and_cash_sales_and_reports_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales
            {
                TransactionID = 1,
                MachineID = 10,
                MachineName = "Machine A",
                SettlementValue = 10m,
                PaymentMethod = "Credit Card",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = 4m
            },
            new NayaxSales
            {
                TransactionID = 2,
                MachineID = 10,
                MachineName = "Machine A",
                SettlementValue = 5m,
                PaymentMethod = "Cash",
                MachineAuthorizationTime = new DateTime(2025, 8, 1),
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                CostOfGoodsSold = null
            });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(15m, facts.GrossSales);
        Assert.Equal(10m, facts.CardSales);
        Assert.Equal(1, facts.CardTransactions);
        Assert.Equal(5m, facts.CashSales);
        Assert.Equal(1, facts.CashTransactions);
        Assert.False(facts.IsCogsComplete);
        Assert.Equal(1, facts.UncostedTransactionCount);
        Assert.Equal(5m, facts.UncostedSalesAmount);
        Assert.Equal(4m, facts.PartialCostOfGoods);
    }

    [Fact]
    public async Task Date_range_is_inclusive_of_both_boundary_days_and_excludes_the_day_after()
    {
        // Sydney is AEST (UTC+10) in August, so Sydney midnight on 1/2/3 August 2025 is
        // 14:00 UTC on the preceding day - not UTC midnight. Issue #1250: the day boundary these
        // reports select by is the business (Sydney) date, not the UTC date of the instant.
        var sydney = new FixedSydneyTime(new DateTime(2025, 8, 10)).Calendar;
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 1m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 7, 31, 13, 59, 0, DateTimeKind.Utc), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 2m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 7, 31, 14, 0, 0, DateTimeKind.Utc), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 4m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1, 14, 0, 0, DateTimeKind.Utc), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 4, MachineID = 10, SettlementValue = 8m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 2, 14, 0, 0, DateTimeKind.Utc), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db, sydney), EmptyCommissions(), sydney);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 2), null, CancellationToken.None);

        Assert.Equal(6m, facts.GrossSales);
    }

    [Fact]
    public async Task Machine_filter_scopes_sales_to_the_selected_machine_only()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed },
            new NayaxSales { TransactionID = 2, MachineID = 20, SettlementValue = 30m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
    }

    /// <summary>
    /// Issue #1255's fixture, end to end over SQLite: the adapter groups the period's completed
    /// sales by the GST rule stored on the product each one is attributed to, and the report takes
    /// one eleventh of the taxable part of that. Only the explicitly GST-free product's sales leave
    /// the taxable total; the product nobody has given a rule stays taxable and is reported on its
    /// own. The whole report is asserted rather than only the facts, because the figure the owner
    /// reads is what this rule is about.
    /// </summary>
    [Fact]
    public async Task Gst_on_sales_follows_each_products_gst_rule_and_a_product_with_no_rule_stays_taxable()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var taxable = new Product { Name = "Chips", GstRule = GstClassification.Taxable };
        var gstFree = new Product { Name = "Bottled water", GstRule = GstClassification.GstFree };
        var noRule = new Product { Name = "Never configured" };
        db.Products.AddRange(taxable, gstFree, noRule);
        await db.SaveChangesAsync();
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 11m, productId: taxable.Id),
            Sale(2, NayaxTransactionStatusIds.Completed, 3m, productId: gstFree.Id),
            Sale(3, NayaxTransactionStatusIds.Completed, 22m, productId: noRule.Id));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);
        var report = await new GetBookkeepingReport(provider).Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        // The groups are summed from the same rows as the gross total, so they add up to it.
        Assert.Equal(36m, facts.GrossSales);
        Assert.Equal(36m, facts.SalesByProductGstRule.Sum(x => x.Sales));
        Assert.Equal(36m, report.Sales);
        Assert.Equal(33m, report.TaxableSales);
        Assert.Equal(3m, report.GstFreeSales);
        Assert.Equal(22m, report.NoGstRuleSales);
        Assert.Equal(3.00m, report.GstOnSales);
    }

    /// <summary>
    /// A sale the writer could not attribute to a local product has no rule to read, so it stays
    /// taxable and is counted in the no-rule figure. Nothing here re-derives a product from a
    /// provider id or a name (issue #758): the sale's own <c>ProductId</c> is the association.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_resolved_product_is_taxable_and_counted_as_having_no_gst_rule()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var gstFree = new Product { Name = "Bottled water", GstRule = GstClassification.GstFree };
        db.Products.Add(gstFree);
        await db.SaveChangesAsync();
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 11m),
            Sale(2, NayaxTransactionStatusIds.Completed, 3m, productId: gstFree.Id));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var report = await new GetBookkeepingReport(provider).Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(11m, report.TaxableSales);
        Assert.Equal(11m, report.NoGstRuleSales);
        Assert.Equal(3m, report.GstFreeSales);
        Assert.Equal(1m, report.GstOnSales);
    }

    /// <summary>
    /// The rule is read at report time and no history of it is kept (issue #1255), so configuring a
    /// product's rule changes what every period's report says about that product's past sales. That
    /// is the documented behaviour, stated on the report help, and this pins it.
    /// </summary>
    [Fact]
    public async Task The_rule_stored_when_the_report_runs_is_the_one_used()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var product = new Product { Name = "Bottled water" };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        db.NayaxSales.Add(Sale(1, NayaxTransactionStatusIds.Completed, 11m, productId: product.Id));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);
        var filter = new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1));
        var useCase = new GetBookkeepingReport(provider);

        var beforeTheRule = await useCase.Handle(filter, CancellationToken.None);
        product.GstRule = GstClassification.GstFree;
        await db.SaveChangesAsync();
        var afterTheRule = await useCase.Handle(filter, CancellationToken.None);

        Assert.Equal(1m, beforeTheRule.GstOnSales);
        Assert.Equal(11m, beforeTheRule.NoGstRuleSales);
        Assert.Equal(0m, afterTheRule.GstOnSales);
        Assert.Equal(11m, afterTheRule.GstFreeSales);
        Assert.Equal(0m, afterTheRule.NoGstRuleSales);
    }

    /// <summary>
    /// Issue #1255's second fixture: a thousand small sales across a taxable and a no-rule product.
    /// The GST is taken once from the period's taxable total, so there is no per-sale rounding to
    /// accumulate - a rounded eleventh per sale would drift by up to half a cent each time.
    /// </summary>
    [Fact]
    public async Task A_thousand_small_sales_report_the_gst_of_the_period_total_with_no_rounding_drift()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var taxable = new Product { Name = "Chips", GstRule = GstClassification.Taxable };
        var noRule = new Product { Name = "Never configured" };
        db.Products.AddRange(taxable, noRule);
        await db.SaveChangesAsync();
        for (var transaction = 1; transaction <= 1000; transaction++)
            db.NayaxSales.Add(Sale(transaction, NayaxTransactionStatusIds.Completed, 1.10m,
                productId: transaction % 2 == 0 ? taxable.Id : noRule.Id));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var report = await new GetBookkeepingReport(provider).Handle(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)), CancellationToken.None);

        Assert.Equal(1100m, report.Sales);
        Assert.Equal(1100m, report.TaxableSales);
        Assert.Equal(550m, report.NoGstRuleSales);
        Assert.Equal(0m, report.GstFreeSales);
        Assert.Equal(100.00m, report.GstOnSales);
    }

    [Fact]
    public async Task Non_completed_sales_are_excluded_from_totals_and_cogs_completeness()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 99m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.PendingSettlementNotFinal, CostOfGoodsSold = null });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(10m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    [Fact]
    public async Task Status_diagnostics_count_every_row_in_scope_before_the_completed_sale_filter()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.PendingSettlementNotFinal, 20m),
            Sale(3, NayaxTransactionStatusIds.PendingBatch, 21m),
            Sale(4, NayaxTransactionStatusIds.Refunded, 30m),
            Sale(5, NayaxTransactionStatusIds.CancelledOrDeclined26, 40m),
            Sale(6, NayaxTransactionStatusIds.CashlessCancelledProductNotDispensed, 41m),
            Sale(7, 9999, 50m),
            Sale(8, null, 60m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(2, facts.PendingTransactionCount);
        Assert.Equal(1, facts.RefundedTransactionCount);
        Assert.Equal(2, facts.DeclinedOrCancelledTransactionCount);
        Assert.Equal(1, facts.UnknownStatusTransactionCount);
        Assert.Equal(1, facts.MissingStatusTransactionCount);
        // The diagnostics never widen what the totals include: only the completed sale counts.
        Assert.Equal(10m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    [Fact]
    public async Task Status_diagnostics_are_scoped_to_the_selected_machine_and_date_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, null, 10m, machineId: 10),
            Sale(2, null, 10m, machineId: 20),
            Sale(3, null, 10m, machineId: 10, authorizationTime: new DateTime(2025, 8, 2)),
            Sale(4, 9999, 10m, machineId: 20));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(1, facts.MissingStatusTransactionCount);
        Assert.Equal(0, facts.UnknownStatusTransactionCount);
    }

    [Fact]
    public async Task A_period_whose_rows_are_all_completed_and_costed_reports_no_status_or_cost_problem()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.Completed, 5m, costOfGoodsSold: 2m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0, facts.MissingStatusTransactionCount);
        Assert.Equal(0, facts.UnknownStatusTransactionCount);
        Assert.Equal(0, facts.PendingTransactionCount);
        Assert.Equal(0, facts.RefundedTransactionCount);
        Assert.Equal(0, facts.DeclinedOrCancelledTransactionCount);
        Assert.True(facts.IsCogsComplete);
    }

    /// <summary>
    /// Issue #804: the completed-transaction count this report now publishes is the count of the
    /// very rows its own totals are summed from - approved status 12 only, with every other
    /// outcome excluded exactly as it is from gross sales. A completed sale whose payment method
    /// classifies as neither card nor cash is still one of them, so the count is not the card and
    /// cash counts added together.
    /// </summary>
    [Fact]
    public async Task Transaction_count_counts_completed_sales_only_including_an_unknown_payment_method()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            new NayaxSales { TransactionID = 1, MachineID = 10, SettlementValue = 10m, PaymentMethod = "Credit Card", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 4m },
            new NayaxSales { TransactionID = 2, MachineID = 10, SettlementValue = 5m, PaymentMethod = "Cash", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 2m },
            new NayaxSales { TransactionID = 3, MachineID = 10, SettlementValue = 6m, PaymentMethod = "Mystery Tender", MachineAuthorizationTime = new DateTime(2025, 8, 1), TransactionStatusId = NayaxTransactionStatusIds.Completed, CostOfGoodsSold = 3m },
            Sale(4, NayaxTransactionStatusIds.PendingSettlementNotFinal, 99m),
            Sale(5, NayaxTransactionStatusIds.Refunded, 99m),
            Sale(6, NayaxTransactionStatusIds.CancelledOrDeclined26, 99m),
            Sale(7, 9999, 99m),
            Sale(8, null, 99m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(3, facts.TransactionCount);
        Assert.Equal(1, facts.CardTransactions);
        Assert.Equal(1, facts.CashTransactions);
        Assert.Equal(1, facts.UnknownTransactions);
        Assert.Equal(21m, facts.GrossSales);
    }

    [Fact]
    public async Task Transaction_count_is_scoped_to_the_selected_machine_and_date_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 10),
            Sale(2, NayaxTransactionStatusIds.Completed, 10m, machineId: 10),
            Sale(3, NayaxTransactionStatusIds.Completed, 10m, machineId: 20),
            Sale(4, NayaxTransactionStatusIds.Completed, 10m, machineId: 10, authorizationTime: new DateTime(2025, 8, 2)));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), 10, CancellationToken.None);

        Assert.Equal(2, facts.TransactionCount);
        Assert.Equal(20m, facts.GrossSales);
    }

    [Fact]
    public async Task A_period_with_no_completed_sale_reports_a_zero_transaction_count()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.NayaxSales.Add(Sale(1, NayaxTransactionStatusIds.PendingSettlementNotFinal, 99m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0, facts.TransactionCount);
        Assert.Equal(0m, facts.GrossSales);
    }

    private static NayaxSales Sale(
        long transactionId, int? transactionStatusId, decimal settlementValue,
        long machineId = 10, DateTime? authorizationTime = null, decimal? costOfGoodsSold = null,
        long? productId = null) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId,
            PaymentMethod = "Credit Card",
            SettlementValue = settlementValue,
            MachineAuthorizationTime = authorizationTime ?? new DateTime(2025, 8, 1),
            TransactionStatusId = transactionStatusId,
            CostOfGoodsSold = costOfGoodsSold,
            // The local product the sale is attributed to (issue #758); null is unresolved, which is
            // a real state and the one every other test here leaves it in.
            ProductId = productId
        };

    /// <summary>
    /// Issue #805: this adapter's imported-settlement summary is what the report's actual
    /// reimbursement and its reconciliation status rest on (the reporting dashboard's adapter read
    /// the identical shared query until that issue removed the duplicate). An imported
    /// reimbursement is matched on its own covered reimbursement period, not on a payout or import
    /// date, so this is relational coverage of that overlap predicate and of the FK-backed
    /// navigation the shared query reads it through.
    /// </summary>
    [Fact]
    public async Task An_imported_reimbursement_covering_the_period_reports_its_net_settlement()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), 94m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.True(facts.ImportedHasNetSettlement);
        Assert.Equal(94m, facts.ImportedNetSettlement);
    }

    /// <summary>
    /// A reimbursement whose covered period lies outside the requested range is a different
    /// period's settlement and must not be matched into this one, which is what leaves the report
    /// reporting <c>Pending</c> rather than reconciling against somebody else's month.
    /// </summary>
    [Fact]
    public async Task An_imported_reimbursement_covering_another_period_is_not_matched()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2025, 9, 1), new DateTime(2025, 9, 30), 94m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.False(facts.ImportedContainsRows);
        Assert.False(facts.ImportedHasNetSettlement);
        Assert.Equal(0m, facts.ImportedNetSettlement);
    }

    /// <summary>
    /// A settlement import that really did settle nothing is a row that exists and a total of zero.
    /// The report compares it like any other amount; only the absence of a row is <c>Pending</c>.
    /// </summary>
    [Fact]
    public async Task An_imported_reimbursement_of_zero_still_reports_that_rows_exist()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), 0m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31), null, CancellationToken.None);

        Assert.True(facts.ImportedContainsRows);
        Assert.Equal(0m, facts.ImportedNetSettlement);
    }

    /// <summary>
    /// Issue #1253: a payout the requested range only <em>overlaps</em> settles days outside that
    /// range, so its whole total is not this range's actual reimbursement. The issue's own fixture:
    /// one payout for 1-7 September 2026 of 680.75, asked about 7 September alone. Nothing lies
    /// wholly inside that day, so there is no actual reimbursement at all - and the partly covered
    /// payout is reported separately with its own period dates and total instead of being summed
    /// into a figure the day never earned (AGENTS.md § Reimbursements and reconciliation: "Do not
    /// force an overlapping or partial reimbursement into a different requested reporting period").
    /// </summary>
    [Fact]
    public async Task A_payout_the_range_only_overlaps_is_listed_separately_and_is_no_part_of_the_actual()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2026, 9, 7), new DateTime(2026, 9, 7), null, CancellationToken.None);

        Assert.False(facts.ImportedHasNetSettlement);
        Assert.Equal(0m, facts.ImportedNetSettlement);
        // The settlement data itself still overlaps these dates, so the fee figures stay unverified
        // rather than missing: that answer is unchanged and is a different question (issue #731).
        Assert.True(facts.ImportedContainsRows);
        Assert.Empty(facts.ImportedFullyCoveredPeriods);
        var partlyCovered = Assert.Single(facts.ImportedPartlyCoveredPeriods);
        Assert.Equal(new DateTime(2026, 9, 1), partlyCovered.PeriodStart);
        Assert.Equal(new DateTime(2026, 9, 7), partlyCovered.PeriodEnd);
        Assert.Equal(680.75m, partlyCovered.Total);
    }

    /// <summary>
    /// Issue #1253, the calendar-month case: September 2026 holds one payout it covers in full and
    /// one that straddles the month end. Only the first is the month's actual reimbursement; the
    /// straddling payout is reported with its own dates and is part of no total, so a month report
    /// cannot claim money October settles.
    /// </summary>
    [Fact]
    public async Task A_calendar_month_counts_only_the_payout_it_covers_and_lists_the_one_straddling_its_end()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m));
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 28), new DateTime(2026, 10, 4), 600m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), null, CancellationToken.None);

        Assert.True(facts.ImportedHasNetSettlement);
        Assert.Equal(680.75m, facts.ImportedNetSettlement);
        Assert.Equal(new DateTime(2026, 9, 1), Assert.Single(facts.ImportedFullyCoveredPeriods).PeriodStart);
        var straddling = Assert.Single(facts.ImportedPartlyCoveredPeriods);
        Assert.Equal(new DateTime(2026, 9, 28), straddling.PeriodStart);
        Assert.Equal(new DateTime(2026, 10, 4), straddling.PeriodEnd);
        Assert.Equal(600m, straddling.Total);
    }

    /// <summary>
    /// The machine-filtered branch of the same query answers the same two questions over the payouts
    /// that have a device row for the selected machine (issue #1253): the month-end straddler is no
    /// part of its actual reimbursement either.
    /// </summary>
    [Fact]
    public async Task The_machine_filtered_branch_applies_the_same_wholly_inside_rule()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m, machineNumber: "10"));
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 28), new DateTime(2026, 10, 4), 600m, machineNumber: "10"));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 30), 10, CancellationToken.None);

        Assert.True(facts.ImportedMachineFilterMatched);
        Assert.True(facts.ImportedHasNetSettlement);
        Assert.Equal(680.75m, facts.ImportedNetSettlement);
        Assert.Equal(new DateTime(2026, 9, 28), Assert.Single(facts.ImportedPartlyCoveredPeriods).PeriodStart);
    }

    /// <summary>
    /// The same payout asked about the period it actually covers is wholly inside the range, so it
    /// is the range's actual reimbursement exactly as before, labelled with its own period dates.
    /// </summary>
    [Fact]
    public async Task A_payout_wholly_inside_the_range_is_the_actual_reimbursement_and_carries_its_period()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        db.ImportedFiles.Add(ImportedFileWith(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), 680.75m));
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2026, 9, 1), new DateTime(2026, 9, 7), null, CancellationToken.None);

        Assert.True(facts.ImportedHasNetSettlement);
        Assert.Equal(680.75m, facts.ImportedNetSettlement);
    }

    private static ImportedFile ImportedFileWith(DateTime from, DateTime to, decimal total, string? machineNumber = null)
    {
        var file = new ImportedFile { FileName = "settlement.xml", FileHash = $"{from:yyyyMMdd}-{total}", ImportedAt = DateTime.UtcNow };
        var reimbursement = new ImportedReimbursement
        {
            ReimbursementStartDate = from,
            ReimbursementEndDate = to,
            Total = total
        };
        // The device row is what a machine-filtered report matches a payout on, by the provider's
        // own machine number.
        if (machineNumber is not null)
            reimbursement.Devices.Add(new ImportedReimbursementDevice { MachineNumber = machineNumber });
        file.Reimbursements.Add(reimbursement);
        return file;
    }

    [Fact]
    public async Task Receipt_delivery_and_package_costs_are_zero_when_no_receipts_are_in_range()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1), null, CancellationToken.None);

        Assert.Equal(0m, facts.ReceiptDeliveryCost);
        Assert.Equal(0m, facts.ReceiptPackageCost);
        Assert.Equal(0m, facts.GrossSales);
        Assert.True(facts.IsCogsComplete);
    }

    /// <summary>
    /// The machine filter is still the <em>provider's</em> machine number (issue #780 deliberately
    /// left the report filters alone), but an expense now records the local machine row. So the
    /// number has to be resolved through <c>LocalMachine.ExternalMachineId</c> before the expenses
    /// are narrowed, and the whole-business and machine-filtered totals have to be exactly what
    /// they were before the repoint: 36 and 11.
    /// </summary>
    [Fact]
    public async Task Operating_expenses_are_filtered_by_the_providers_machine_number_through_the_local_machine()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });
        var machine10 = new LocalMachine { BusinessId = 1, Name = "Machine 10", ExternalMachineId = "10" };
        var machine11 = new LocalMachine { BusinessId = 1, Name = "Machine 11", ExternalMachineId = "11" };
        db.Machines.AddRange(machine10, machine11);
        db.NayaxSales.AddRange(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 10, authorizationTime: date, costOfGoodsSold: 4m),
            Sale(2, NayaxTransactionStatusIds.Completed, 20m, machineId: 11, authorizationTime: date, costOfGoodsSold: 8m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.AddRange(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 11m, GstAmount = 1m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine11.Id, TotalAmount = 22m, GstAmount = 2m },
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, TotalAmount = 3m, GstAmount = 0m });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var wholeBusiness = await provider.GetFactsAsync(date, date, null, CancellationToken.None);
        var machineTen = await provider.GetFactsAsync(date, date, 10, CancellationToken.None);

        Assert.Equal(36m, wholeBusiness.OperatingExpensesTotal);
        Assert.Equal(11m, machineTen.OperatingExpensesTotal);
        Assert.Equal(1m, machineTen.OperatingExpensesGst);
    }

    /// <summary>
    /// A provider machine this business has recorded no machine for narrows the report to no
    /// expenses at all. Falling back to the whole business's would report another machine's costs,
    /// and a shared sales row set, against the one machine that was asked about.
    /// </summary>
    [Fact]
    public async Task A_machine_filter_with_no_local_machine_reports_no_operating_expenses()
    {
        await using var connection = (await CreateSqliteAsync()).Connection;
        await using var db = TestAppDbContext.Unrestricted(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var date = new DateTime(2025, 8, 1);
        db.Businesses.Add(new Business
        {
            Id = 1,
            Name = "Vending A",
            Status = BusinessStatus.Active,
            CreatedAtUtc = DateTime.UtcNow,
            SalesProvider = ExternalProvider.Nayax,
        });
        var machine10 = new LocalMachine { BusinessId = 1, Name = "Machine 10", ExternalMachineId = "10" };
        db.Machines.Add(machine10);
        db.NayaxSales.Add(
            Sale(1, NayaxTransactionStatusIds.Completed, 10m, machineId: 99, authorizationTime: date, costOfGoodsSold: 4m));
        await db.SaveChangesAsync();
        db.OperatingExpenses.Add(
            new OperatingExpense { BusinessId = 1, ExpenseDate = date, MachineId = machine10.Id, TotalAmount = 11m, GstAmount = 1m });
        await db.SaveChangesAsync();
        var provider = new EfBookkeepingReportFactsProvider(db, TestFinancialUseCases.ProcessingFees(db), EmptyCommissions(), TestFinancialUseCases.TrivialCalendar);

        var facts = await provider.GetFactsAsync(date, date, 99, CancellationToken.None);

        Assert.Equal(0m, facts.OperatingExpensesTotal);
        Assert.Equal(0m, facts.OperatingExpensesGst);
        Assert.Empty(facts.OperatingExpensesByCategory);
    }
}
