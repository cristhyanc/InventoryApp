using System.Text;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Application.Tenancy;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SaleTimestampRepair;

/// <summary>
/// Orchestration of the Nayax sale timestamp repair use cases (issue #472): what the preview
/// reports, what it refuses, what the apply writes, what it refuses to write, and what it rolls
/// back.
///
/// The seeded situation is the operator's own 8 October 2026 defect, sanitized. Transaction
/// 3564567268 on machine 531595328 (Pavillion Right), $3.00, holds the instant
/// <c>2026-10-07 12:42:44.263</c> because the live synchronization read an offset-free
/// <c>AuthorizationDateTimeGMT</c> of <c>2026-10-07T23:42:44.263</c> against the Sydney host's own
/// UTC offset (the defect issue #471 stopped happening but did not repair). Under AEDT the stored
/// value reads as 7 October 23:42 in Sydney and the authoritative one as 8 October 10:42, so
/// repairing it moves $3.00 from one Sydney business day into the next.
///
/// These run on a real (non-InMemory) SQLite connection deliberately: the apply's all-or-nothing
/// guarantee is a database transaction, the stale-plan check is a re-read, and the repaired value is
/// what every report later reads back - none of which the InMemory provider proves. Several tests
/// read back through a fresh <see cref="AppDbContext"/> for the same reason: a rolled-back apply must
/// leave nothing behind in the database, not merely in a change tracker.
/// </summary>
public class NayaxSaleTimestampRepairTests : IDisposable
{
    private const int BusinessA = 1;
    private const int BusinessB = 2;
    private const long MachineId = 531595328;
    private const long KnownTransactionId = 3564567268;
    private const long ProductId = 10;

    /// <summary>The instant the Sydney-hosted API stored: eleven hours before the truth.</summary>
    private static readonly DateTime StoredUtc = new(2026, 10, 7, 12, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>The authoritative instant, from <c>AuthorizationDateTimeGMT</c>.</summary>
    private static readonly DateTime AuthoritativeUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    private static readonly DateTime SevenOctober = new(2026, 10, 7);
    private static readonly DateTime EightOctober = new(2026, 10, 8);
    private static readonly DateTime Now = new(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeClock _clock = new(Now);

    public NayaxSaleTimestampRepairTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.AddRange(
            new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = Now },
            new Business { Id = BusinessB, Name = "Vending B", CreatedAtUtc = Now });
        setup.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- preview

    /// <summary>
    /// Acceptance criterion: the known transaction is repaired to 2026-10-07T23:42:44.263Z and
    /// reports on 8 October in Sydney. The preview is where that is stated, per transaction, with the
    /// source that decided it - never as a difference between two stored values.
    /// </summary>
    [Fact]
    public async Task Preview_reports_each_transaction_with_its_old_and_new_instant_and_Sydney_business_date()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        Assert.Equal((1, 1, 1, 0, 0), (
            preview.EvidenceRecords, preview.SalesExamined, preview.Repairable, preview.AlreadyCorrect, preview.Unresolved));
        var row = Assert.Single(preview.Rows);
        Assert.Equal(KnownTransactionId, row.TransactionId);
        Assert.Equal(MachineId, row.MachineId);
        Assert.Equal(NayaxSaleTimestampRepairOutcome.Repairable, row.Outcome);
        Assert.Equal(StoredUtc, row.StoredInstantUtc);
        Assert.Equal(SevenOctober, row.StoredBusinessDate);
        Assert.Equal(AuthoritativeUtc, row.RepairedInstantUtc);
        Assert.Equal(EightOctober, row.RepairedBusinessDate);
        Assert.Equal(Inventory.Domain.Nayax.NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi, row.EvidenceSource);
        Assert.Equal("lastSales machine 531595328 read 2026-10-08T02:00:00Z", row.EvidenceReference);
        Assert.Null(row.UnresolvedReason);
        Assert.True(row.CompletedSale);
        Assert.Equal((StoredUtc, AuthoritativeUtc), (preview.ExaminedFromUtc, preview.ExaminedToUtc));
        Assert.Equal(Now.Add(NayaxSaleTimestampRepairPolicy.PreviewLifetime), preview.ExpiresAt);
    }

    /// <summary>
    /// Issue #472 requirement: determine the affected range from the evidence, not from an assumed
    /// single week - and report an unresolved transaction rather than guessing at it. The range here
    /// is the shifted sale's stored instant through its authoritative one, so a neighbouring sale no
    /// source covered is examined and reported unresolved, while one outside the range is not
    /// examined at all and stays untouched.
    /// </summary>
    [Fact]
    public async Task The_examined_range_comes_from_the_evidence_and_shows_the_sales_no_source_covered()
    {
        var insideRange = new DateTime(2026, 10, 7, 20, 0, 0, DateTimeKind.Utc);
        var outsideRange = new DateTime(2026, 10, 1, 20, 0, 0, DateTimeKind.Utc);
        Seed(
            BusinessA,
            Sale(KnownTransactionId, StoredUtc, 3.00m),
            Sale(2, insideRange, 90.90m),
            Sale(3, outsideRange, 100.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        Assert.Equal((StoredUtc, AuthoritativeUtc), (preview.ExaminedFromUtc, preview.ExaminedToUtc));
        Assert.Equal((1, 2, 1, 1), (
            preview.EvidenceRecords, preview.SalesExamined, preview.Repairable, preview.Unresolved));
        Assert.Equal([2L, KnownTransactionId], preview.Rows.Select(x => x.TransactionId).Order());
        var unresolved = preview.Rows.Single(x => x.TransactionId == 2);
        Assert.Equal(NayaxSaleTimestampUnresolvedReason.NoSourceEvidence, unresolved.UnresolvedReason);
        Assert.Null(unresolved.EvidenceSource);
    }

    [Fact]
    public async Task Preview_reports_the_revenue_each_Sydney_business_day_loses_and_gains()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        Assert.Equal(
            [(SevenOctober, 3.00m, 0m, -3.00m), (EightOctober, 0m, 3.00m, 3.00m)],
            preview.RevenueMovement.Select(x => (x.BusinessDate, x.AmountLeaving, x.AmountArriving, x.NetMovement)));
    }

    /// <summary>
    /// A sale whose instant moves inside its own Sydney day moves no revenue, and a sale that is not
    /// a completed sale moves none either - it is still re-dated and still reported, because a
    /// pending, refunded, cancelled or unknown-status row must stay visible.
    /// </summary>
    [Fact]
    public async Task Revenue_movement_counts_only_completed_sales_that_change_their_business_day()
    {
        Seed(
            BusinessA,
            Sale(1, StoredUtc, 3.00m),
            Sale(2, StoredUtc, 7.50m, status: NayaxTransactionStatusIds.Refunded));

        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(
                Item(1, StoredUtc.AddMinutes(5), 3.00m),
                Item(2, AuthoritativeUtc, 7.50m)));

        Assert.Equal(2, preview.Repairable);
        Assert.Empty(preview.RevenueMovement);
        Assert.Equal([true, false], preview.Rows.OrderBy(x => x.TransactionId).Select(x => x.CompletedSale));
    }

    /// <summary>
    /// Acceptance criterion: mixed already-correct, local-looking and wrongly shifted rows are
    /// handled by source evidence, never a blanket offset. The three stored values here differ by
    /// eleven hours, by nothing and by twenty-two hours from the one authoritative instant, and each
    /// is decided from its own evidence.
    /// </summary>
    [Fact]
    public async Task A_mixed_cohort_is_decided_per_transaction_and_never_by_a_global_offset()
    {
        var localLooking = new DateTime(2026, 10, 8, 10, 42, 44, 93, DateTimeKind.Utc);
        Seed(
            BusinessA,
            Sale(1, AuthoritativeUtc, 3.00m),
            Sale(2, StoredUtc, 3.00m),
            Sale(3, localLooking, 3.00m));

        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(
                Item(1, AuthoritativeUtc, 3.00m),
                Item(2, AuthoritativeUtc, 3.00m),
                Item(3, AuthoritativeUtc, 3.00m)));

        Assert.Equal(
            [
                (1L, NayaxSaleTimestampRepairOutcome.AlreadyCorrect, (DateTime?)null),
                (2L, NayaxSaleTimestampRepairOutcome.Repairable, AuthoritativeUtc),
                (3L, NayaxSaleTimestampRepairOutcome.Repairable, AuthoritativeUtc),
            ],
            preview.Rows.OrderBy(x => x.TransactionId).Select(x => (x.TransactionId, x.Outcome, x.RepairedInstantUtc)));
    }

    /// <summary>
    /// Acceptance criterion: missing, invalid and conflicting evidence are rejected or explicitly
    /// unresolved. Each of these leaves the stored instant exactly as it is and says why, so the
    /// operator knows which source to obtain next instead of seeing a silently skipped row.
    /// </summary>
    [Fact]
    public async Task Unresolvable_evidence_leaves_every_affected_sale_unchanged_and_says_why()
    {
        Seed(
            BusinessA,
            Sale(1, StoredUtc, 3.00m),
            Sale(2, StoredUtc, 3.00m),
            Sale(3, StoredUtc, 3.00m),
            Sale(4, StoredUtc, 3.00m),
            Sale(5, StoredUtc, 3.00m));

        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(
                // 1: no evidence at all - the rolling window no longer returns it.
                Item(2, null, 3.00m),
                Item(3, AuthoritativeUtc, 3.00m, machineId: MachineId + 1),
                Item(4, AuthoritativeUtc, 4.80m),
                Item(5, AuthoritativeUtc, 3.00m)),
            export: Export(
                "TransactionID,MachineID,SettlementValue,TransactionStatusId,AuthorizationDateTimeGMT",
                $"5,{MachineId},3.00,12,2026-10-07T23:42:45.000"));

        Assert.Equal((5, 0, 0, 5), (
            preview.SalesExamined, preview.Repairable, preview.AlreadyCorrect, preview.Unresolved));
        Assert.Equal(
            [
                (1L, (NayaxSaleTimestampUnresolvedReason?)NayaxSaleTimestampUnresolvedReason.NoSourceEvidence),
                (2L, NayaxSaleTimestampUnresolvedReason.UnreadableEvidence),
                (3L, NayaxSaleTimestampUnresolvedReason.MachineMismatch),
                (4L, NayaxSaleTimestampUnresolvedReason.AmountMismatch),
                (5L, NayaxSaleTimestampUnresolvedReason.ConflictingEvidence),
            ],
            preview.Rows.OrderBy(x => x.TransactionId).Select(x => (x.TransactionId, x.UnresolvedReason)));
        Assert.All(preview.Rows, row =>
        {
            Assert.Equal(StoredUtc, row.StoredInstantUtc);
            Assert.Null(row.RepairedInstantUtc);
        });
        Assert.Empty(preview.AffectedProducts);
    }

    /// <summary>
    /// Issue #472 requirement: the supplied workbook lacks <c>AuthorizationDateTimeGMT</c>, and an
    /// update time or a machine-local time is never substituted for one. An export without the column
    /// is therefore refused outright rather than quietly producing no evidence, which is the failure
    /// mode that would let an operator believe a source had been checked.
    /// </summary>
    [Fact]
    public async Task Preview_refuses_an_export_that_carries_no_authorization_time_column()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => PreviewAsync(
            BusinessA,
            LastSalesNayaxLynxClient.Empty(MachineId),
            includeLatestSalesApiEvidence: false,
            export: Export(
                "TransactionID,MachineID,SettlementValue,TransactionStatusId,MachineAuthorizationTime,Updated Date and Time (GMT)",
                $"{KnownTransactionId},{MachineId},3.00,12,8/10/2026 10:42:44 AM,8/10/2026 11:00:00 AM")));

        Assert.StartsWith(
            "The uploaded export has no AuthorizationDateTimeGMT column", exception.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Preview_refuses_a_request_that_names_no_source_at_all()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => PreviewAsync(
            BusinessA, LastSalesNayaxLynxClient.Empty(MachineId), includeLatestSalesApiEvidence: false));

        Assert.StartsWith("A sale timestamp repair needs at least one authoritative source", exception.Message, StringComparison.Ordinal);
        await AssertNothingWrittenAsync();
    }

    [Fact]
    public async Task Preview_changes_no_sale_and_writes_only_its_own_plan()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(StoredUtc, (await read.NayaxSales.AsNoTracking().SingleAsync()).MachineAuthorizationTime);
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
        var draft = await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().SingleAsync();
        Assert.Equal((preview.PreviewId, BusinessA, (DateTime?)null), (draft.Id, draft.BusinessId, draft.AppliedAt));
    }

    // ------------------------------------------------------------------ apply

    /// <summary>
    /// The write, and its limits. One column moves; the transaction id, amount, status, payment
    /// method and product mapping are untouched; no sale is added or removed, so no transaction is
    /// double counted and no physical stock movement is repeated; and the audit records what moved,
    /// from which source, under which preview and by which operator.
    /// </summary>
    [Fact]
    public async Task Apply_repairs_the_previewed_sale_records_the_audit_and_changes_nothing_else()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        var applied = await ApplyAsync(BusinessA, preview.PreviewId);

        Assert.Equal((1, 0, 0), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
        await using var read = TestAppDbContext.Unrestricted(_options);
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync();
        Assert.Equal(AuthoritativeUtc, sale.MachineAuthorizationTime);
        Assert.Equal(
            (KnownTransactionId, MachineId, 3.00m, NayaxTransactionStatusIds.Completed, "Credit Card", (long?)null),
            (sale.TransactionID, sale.MachineID, sale.SettlementValue, sale.TransactionStatusId, sale.PaymentMethod, sale.NayaxProductId));
        var audit = await read.NayaxSaleTimestampRepairs.AsNoTracking().SingleAsync();
        Assert.Equal((KnownTransactionId, MachineId, StoredUtc, AuthoritativeUtc), (
            audit.TransactionId, audit.MachineId, audit.PreviousInstantUtc, audit.RepairedInstantUtc));
        Assert.Equal((SevenOctober, EightOctober), (audit.PreviousBusinessDate, audit.RepairedBusinessDate));
        Assert.Equal(Inventory.Infrastructure.Models.NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi, audit.EvidenceSource);
        Assert.Equal("lastSales machine 531595328 read 2026-10-08T02:00:00Z", audit.EvidenceReference);
        Assert.Equal((preview.PreviewId, Now, BusinessA), (audit.PreviewId, audit.AppliedAt, audit.BusinessId));
        Assert.Equal(Operator.DirectoryTenantId, audit.AppliedByDirectoryTenantId);
        Assert.Equal(Operator.ObjectId, audit.AppliedByObjectId);
        Assert.True((await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().SingleAsync()).AppliedAt.HasValue);
    }

    [Fact]
    public async Task Apply_writes_nothing_for_an_unresolved_or_already_correct_row()
    {
        Seed(BusinessA, Sale(1, AuthoritativeUtc, 3.00m), Sale(2, StoredUtc, 3.00m));
        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(Item(1, AuthoritativeUtc, 3.00m), Item(2, null, 3.00m)));

        var applied = await ApplyAsync(BusinessA, preview.PreviewId);

        Assert.Equal((2, 0, 1, 1), (preview.SalesExamined, preview.Repairable, preview.AlreadyCorrect, preview.Unresolved));
        Assert.Equal(0, applied.SalesRepaired);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
        Assert.Equal(
            [AuthoritativeUtc, StoredUtc],
            (await read.NayaxSales.AsNoTracking().OrderBy(x => x.TransactionID).ToListAsync())
                .Select(x => x.MachineAuthorizationTime));
    }

    /// <summary>
    /// Acceptance criterion: applying the same verified repair again is a no-op. The second preview
    /// reads the repaired instant, finds the same source already agrees with it, and reports nothing
    /// left to do - so the apply writes no second audit row and the instant does not move again.
    /// </summary>
    [Fact]
    public async Task Applying_the_same_verified_repair_again_is_a_no_op()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var window = LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m));
        var first = await PreviewAsync(BusinessA, window);
        await ApplyAsync(BusinessA, first.PreviewId);

        var second = await PreviewAsync(BusinessA, window);
        var applied = await ApplyAsync(BusinessA, second.PreviewId);

        Assert.Equal((0, 1, 0), (second.Repairable, second.AlreadyCorrect, second.Unresolved));
        Assert.Equal(0, applied.SalesRepaired);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(AuthoritativeUtc, (await read.NayaxSales.AsNoTracking().SingleAsync()).MachineAuthorizationTime);
        Assert.Single(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The stale-state rule. The authoritative read, the comparison and the write are one operation,
    /// so a sale that was imported, re-timed, restatused or already repaired between the preview and
    /// the apply invalidates the plan instead of being repaired over.
    /// </summary>
    [Theory]
    [InlineData("instant")]
    [InlineData("status")]
    [InlineData("added")]
    public async Task Apply_rejects_a_plan_whose_stored_sales_changed_underneath_it(string change)
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(
                Item(KnownTransactionId, AuthoritativeUtc, 3.00m),
                Item(KnownTransactionId + 1, AuthoritativeUtc, 3.00m)));

        await using (var edit = TestAppDbContext.For(_options, BusinessA))
        {
            if (change == "added")
                edit.NayaxSales.Add(Sale(KnownTransactionId + 1, StoredUtc, 3.00m));
            else
            {
                var sale = await edit.NayaxSales.SingleAsync(x => x.TransactionID == KnownTransactionId);
                if (change == "instant")
                    sale.MachineAuthorizationTime = StoredUtc.AddHours(1);
                else
                    sale.TransactionStatusId = NayaxTransactionStatusIds.Refunded;
            }

            await edit.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessA, preview.PreviewId));

        Assert.Equal(NayaxSaleTimestampRepairPolicy.StalePlanMessage, exception.Message);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
        Assert.Null((await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().SingleAsync()).AppliedAt);
    }

    [Fact]
    public async Task Apply_rejects_a_second_confirmation_of_the_same_plan()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));
        await ApplyAsync(BusinessA, preview.PreviewId);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessA, preview.PreviewId));

        Assert.Equal("This sale timestamp repair preview has already been applied.", exception.Message);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Single(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Apply_rejects_an_expired_plan()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));
        _clock.UtcNow = preview.ExpiresAt.AddTicks(1);

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessA, preview.PreviewId));

        Assert.Equal(
            "The sale timestamp repair preview expired. Run the preview again and review it before applying.",
            exception.Message);
        await AssertNothingRepairedAsync();
    }

    [Fact]
    public async Task Apply_rejects_an_unknown_preview_and_requires_explicit_confirmation()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        var unknown = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessA, Guid.NewGuid()));
        var unconfirmed = await Assert.ThrowsAsync<DomainValidationException>(() =>
            ApplyAsync(BusinessA, preview.PreviewId, confirmed: false));

        Assert.Equal(ApplyNayaxSaleTimestampRepair.UnknownPreviewMessage, unknown.Message);
        Assert.Equal("Explicit confirmation is required to repair stored Nayax sale timestamps.", unconfirmed.Message);
        await AssertNothingRepairedAsync();
    }

    [Fact]
    public async Task Apply_refuses_a_caller_it_cannot_attribute_the_repair_to()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m)));

        await using (var db = TestAppDbContext.For(_options, BusinessA))
            await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
                TestSaleTimestampRepairUseCases
                    .Apply(db, FakeAuthenticatedActorAccessor.Unidentifiable(), _clock)
                    .Handle(new(preview.PreviewId, true)));

        await AssertNothingRepairedAsync();
    }

    // -------------------------------------------------------- tenant isolation

    /// <summary>
    /// Two businesses may legitimately hold the same remote <c>TransactionID</c>, because it is
    /// unique only within the operator account that issued it. A repair therefore examines and moves
    /// only the caller's own sale, through the central tenant query filters, and another business's
    /// preview does not exist for it at all.
    /// </summary>
    [Fact]
    public async Task A_repair_never_examines_moves_or_confirms_another_businesss_data()
    {
        Seed(BusinessA, Sale(KnownTransactionId, StoredUtc, 3.00m));
        Seed(BusinessB, Sale(KnownTransactionId, StoredUtc, 3.00m));
        var window = LiveWindow(Item(KnownTransactionId, AuthoritativeUtc, 3.00m));

        var preview = await PreviewAsync(BusinessA, window);
        var foreign = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessB, preview.PreviewId));
        await ApplyAsync(BusinessA, preview.PreviewId);

        Assert.Equal(1, preview.SalesExamined);
        Assert.Equal(ApplyNayaxSaleTimestampRepair.UnknownPreviewMessage, foreign.Message);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            [(BusinessA, AuthoritativeUtc), (BusinessB, StoredUtc)],
            (await read.NayaxSales.AsNoTracking().OrderBy(x => x.BusinessId).ToListAsync())
                .Select(x => (x.BusinessId, x.MachineAuthorizationTime)));
        Assert.Equal(BusinessA, (await read.NayaxSaleTimestampRepairs.AsNoTracking().SingleAsync()).BusinessId);
    }

    // ---------------------------------------------------------------- costing

    /// <summary>
    /// Acceptance criterion: the cost rebuild starts at the earliest affected old or new instant and
    /// does not duplicate inventory movements. The two repaired sales move in opposite directions -
    /// one later, one earlier - so the replay has to start before both of their old and new
    /// positions, or the sale that moved later would leave stale COGS behind at the position it left.
    /// </summary>
    [Fact]
    public async Task Apply_replays_each_affected_product_from_the_earlier_of_the_old_and_new_instants()
    {
        var restockAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var movesLater = new DateTime(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);
        var movesEarlier = new DateTime(2026, 10, 6, 9, 0, 0, DateTimeKind.Utc);
        SeedCostedProduct(restockAt, cutoffAt: restockAt.AddHours(-1));
        Seed(
            BusinessA,
            Sale(1, movesLater, 3.00m, productId: ProductId),
            Sale(2, movesEarlier, 3.00m, productId: ProductId));
        var preview = await PreviewAsync(
            BusinessA,
            LiveWindow(
                Item(1, movesLater.AddHours(5), 3.00m),
                Item(2, movesEarlier.AddHours(-5), 3.00m)));

        // Sale 1 leaves 02:00 and sale 2 arrives at 04:00, so the replay restarts at 02:00: the
        // position the sale that moved later vacated, which is earlier than anything the sale that
        // moved earlier reaches.
        var product = Assert.Single(preview.AffectedProducts);
        Assert.Equal((ProductId, "Coke Zero", movesLater, true), (
            product.ProductId, product.ProductName, product.RebuildFromUtc, product.RebuildPlanned));

        var applied = await ApplyAsync(BusinessA, preview.PreviewId);

        Assert.Equal((2, 1, 2), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.All(await read.NayaxSales.AsNoTracking().ToListAsync(), sale =>
        {
            Assert.Equal(2m, sale.UnitCostAtSale);
            Assert.Equal(SaleCostingStatus.Costed, sale.CostingStatus);
            Assert.Equal(SaleCostSource.InventoryLedger, sale.CostSource);
        });
        // One restock in, two sales out: the physical movement history is untouched by the repair,
        // so nothing is replayed into a second movement and the costing position is consumed once.
        Assert.Single(await read.StockAdjustments.AsNoTracking().ToListAsync());
        var stored = await read.Products.AsNoTracking().SingleAsync();
        Assert.Equal((8, 16m, 2m), (stored.CostingQuantity, stored.InventoryValue, stored.AverageUnitCost));
    }

    /// <summary>
    /// A sale at or before a product's inventory-cost transition baseline cutoff is covered by that
    /// baseline, so replaying it would recost history the transition owns. The preview says so rather
    /// than replaying it silently - the same gate the uploaded sales import and the latest-sales
    /// synchronization apply.
    /// </summary>
    [Fact]
    public async Task A_products_costing_is_not_replayed_from_at_or_before_its_transition_baseline_cutoff()
    {
        var saleAt = new DateTime(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);
        SeedCostedProduct(restockAt: saleAt.AddDays(-5), cutoffAt: saleAt.AddHours(1));
        Seed(BusinessA, Sale(1, saleAt, 3.00m, productId: ProductId));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(1, saleAt.AddHours(5), 3.00m)));

        var product = Assert.Single(preview.AffectedProducts);
        var applied = await ApplyAsync(BusinessA, preview.PreviewId);

        Assert.Equal((true, saleAt, false), (product.HasTransitionBaseline, product.RebuildFromUtc, product.RebuildPlanned));
        Assert.Equal(saleAt.AddHours(1), product.TransitionCutoffAt);
        Assert.Equal((1, 0, 0), (applied.SalesRepaired, applied.ProductsRebuilt, applied.RecostedSales));
    }

    /// <summary>
    /// Rollback semantics. A product whose cost history cannot be replayed aborts the whole apply:
    /// no instant is repaired, no audit row is kept, the plan stays unapplied and the operator is
    /// told which product has to be fixed first. Nothing invents an opening cost to make the replay
    /// succeed, and a failed replay is never reported as a successful repair.
    /// </summary>
    [Fact]
    public async Task Apply_rolls_everything_back_when_an_affected_products_cost_history_cannot_be_replayed()
    {
        var saleAt = new DateTime(2026, 10, 6, 2, 0, 0, DateTimeKind.Utc);
        // The situation a costing repair exists for: the product entered the transition with an
        // opening costing quantity of zero while stock was still in the machines, so its completed
        // sale has no costed stock to consume and every write to it fails on the same fatal issue.
        await using (var setup = TestAppDbContext.Unrestricted(_options))
        {
            setup.Products.Add(new Product { Id = ProductId, BusinessId = BusinessA, Name = "Coke Zero", QuantityInStock = 10 });
            setup.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
            {
                BusinessId = BusinessA,
                ProductId = ProductId,
                CutoffAt = saleAt.AddDays(-1),
                HomeStockQuantity = 10,
                OpeningCostingQuantity = 0,
                InventoryValue = 0m,
                AverageUnitCost = 0m,
            });
            await setup.SaveChangesAsync();
        }

        Seed(BusinessA, Sale(1, saleAt, 3.00m, productId: ProductId));
        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(1, saleAt.AddHours(5), 3.00m)));

        var exception = await Assert.ThrowsAsync<DomainValidationException>(() => ApplyAsync(BusinessA, preview.PreviewId));

        Assert.StartsWith(
            "Nothing was repaired: the inventory cost of Coke Zero (product 10) could not be replayed from "
                + "2026-10-06T02:00:00Z.",
            exception.Message,
            StringComparison.Ordinal);
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(saleAt, (await read.NayaxSales.AsNoTracking().SingleAsync()).MachineAuthorizationTime);
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
        Assert.Null((await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().SingleAsync()).AppliedAt);
    }

    // -------------------------------------------- day, week and DST boundaries

    /// <summary>
    /// The week boundary. Sydney weeks start on Monday, and daylight saving began on Sunday
    /// 4 October 2026, so Monday 5 October starts at 2026-10-04T13:00Z. A sale stored an hour before
    /// that boundary and authorized an hour after it moves out of Sunday - and out of last week -
    /// into Monday, which is exactly the "fewer sales last week, more today" symptom reversed.
    /// </summary>
    [Fact]
    public async Task A_repair_can_move_a_sale_across_the_Sydney_week_boundary()
    {
        var storedUtc = new DateTime(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);
        var authoritativeUtc = new DateTime(2026, 10, 4, 23, 0, 0, DateTimeKind.Utc);
        Seed(BusinessA, Sale(1, storedUtc, 3.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(1, authoritativeUtc, 3.00m)));

        var row = Assert.Single(preview.Rows);
        Assert.Equal(new DateTime(2026, 10, 4), row.StoredBusinessDate);
        Assert.Equal(new DateTime(2026, 10, 5), row.RepairedBusinessDate);
    }

    /// <summary>
    /// Both daylight-saving transitions, read by the platform timezone database rather than a fixed
    /// <c>+10</c>/<c>+11</c> assumption: the 23-hour day when AEDT began on 4 October 2026, and both
    /// passes of the repeated hour when AEDT ended on 5 April 2026. The second pass is the one a
    /// fixed offset gets wrong.
    /// </summary>
    [Theory]
    // AEDT begins: 16:00Z on 3 October is 02:00 AEST on 4 October, the instant the clocks jump.
    [InlineData("2026-10-03T15:59:00Z", "2026-10-03T16:01:00Z", "2026-10-04", "2026-10-04")]
    // Across the new midnight: 12:59Z is 23:59 AEDT on 4 October, 13:01Z is 00:01 on 5 October.
    [InlineData("2026-10-04T12:59:00Z", "2026-10-04T13:01:00Z", "2026-10-04", "2026-10-05")]
    // AEDT ends on 5 April 2026: 15:59Z on 4 April is 02:59 AEDT, the first pass of the repeated
    // hour; 16:01Z is 02:01 AEST, the second pass. Both are 5 April in Sydney.
    [InlineData("2026-04-04T15:59:00Z", "2026-04-04T16:01:00Z", "2026-04-05", "2026-04-05")]
    // And across that day's own midnight, which is 13:00Z while AEDT still applies.
    [InlineData("2026-04-04T12:59:00Z", "2026-04-04T13:01:00Z", "2026-04-04", "2026-04-05")]
    public async Task Business_dates_either_side_of_a_daylight_saving_transition_come_from_the_timezone_database(
        string storedUtc,
        string authoritativeUtc,
        string storedBusinessDate,
        string repairedBusinessDate)
    {
        var stored = DateTime.Parse(storedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var authoritative = DateTime.Parse(authoritativeUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Seed(BusinessA, Sale(1, stored, 3.00m));

        var preview = await PreviewAsync(BusinessA, LiveWindow(Item(1, authoritative, 3.00m)));

        var row = Assert.Single(preview.Rows);
        Assert.Equal(DateTime.Parse(storedBusinessDate, null), row.StoredBusinessDate);
        Assert.Equal(DateTime.Parse(repairedBusinessDate, null), row.RepairedBusinessDate);
    }

    // ------------------------------------------------------------------ setup

    private static ActorIdentity Operator => Identity("11111111-1111-1111-1111-111111111111");

    private static ActorIdentity Identity(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate("33333333-3333-3333-3333-333333333333", objectId, out var actor));
        return actor!;
    }

    private async Task<NayaxSaleTimestampRepairPreview> PreviewAsync(
        int businessId,
        LastSalesNayaxLynxClient nayax,
        bool includeLatestSalesApiEvidence = true,
        NayaxSalesFileInput? export = null,
        NayaxSaleTimestampReconciliationRequest? reconciliation = null)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await TestSaleTimestampRepairUseCases.Preview(db, nayax, _clock)
            .Handle(new(includeLatestSalesApiEvidence, reconciliation), export);
    }

    private async Task<NayaxSaleTimestampRepairApplied> ApplyAsync(
        int businessId,
        Guid previewId,
        bool confirmed = true)
    {
        await using var db = TestAppDbContext.For(_options, businessId);
        return await TestSaleTimestampRepairUseCases
            .Apply(db, FakeAuthenticatedActorAccessor.Identified(Operator), _clock)
            .Handle(new(previewId, confirmed));
    }

    private void Seed(int businessId, params NayaxSales[] sales)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        foreach (var sale in sales)
        {
            sale.BusinessId = businessId;
            db.NayaxSales.Add(sale);
        }

        db.SaveChanges();
    }

    /// <summary>
    /// A product with ten units of costed stock at $2, acquired by a restock after its transition
    /// baseline cutoff, so the replay has a complete history to cost the sales from.
    /// </summary>
    private void SeedCostedProduct(DateTime restockAt, DateTime cutoffAt)
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        db.Products.Add(new Product
        {
            Id = ProductId,
            BusinessId = BusinessA,
            Name = "Coke Zero",
            QuantityInStock = 10,
            AverageUnitCost = 2m,
        });
        db.InventoryCostTransitionBaselines.Add(new InventoryCostTransitionBaseline
        {
            BusinessId = BusinessA,
            ProductId = ProductId,
            CutoffAt = cutoffAt,
            HomeStockQuantity = 0,
            OpeningCostingQuantity = 0,
            InventoryValue = 0m,
            AverageUnitCost = 0m,
        });
        db.StockAdjustments.Add(new StockAdjustment
        {
            BusinessId = BusinessA,
            ProductId = ProductId,
            QuantityChange = 10,
            QuantityAfter = 10,
            Reason = StockAdjustmentReason.Restock,
            UnitCost = 2m,
            EffectiveAt = restockAt,
        });
        db.SaveChanges();
    }

    private static NayaxSales Sale(
        long transactionId,
        DateTime storedUtc,
        decimal settlementValue,
        int? status = NayaxTransactionStatusIds.Completed,
        long? productId = null) =>
        new()
        {
            TransactionID = transactionId,
            TransactionStatusId = status,
            MachineID = MachineId,
            MachineName = "Pavillion Right",
            SettlementValue = settlementValue,
            PaymentMethod = "Credit Card",
            ProductName = productId is null ? "Unknown" : "Coke Zero",
            NayaxProductId = productId,
            MachineAuthorizationTime = storedUtc,
        };

    private static LastSalesNayaxLynxClient LiveWindow(params NayaxLastSalesReport[] sales) =>
        new(sales.GroupBy(sale => sale.MachineID).ToDictionary(group => group.Key, group => group.ToList()));

    private static NayaxLastSalesReport Item(
        long transactionId,
        DateTime? authorizationGmt,
        decimal settlementValue,
        long? machineId = null) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = machineId ?? MachineId,
            MachineName = "Pavillion Right",
            SettlementValue = settlementValue,
            AuthorizationDateTimeGmt = authorizationGmt is { } gmt ? new DateTimeOffset(gmt, TimeSpan.Zero) : null,
        };

    private static NayaxSalesFileInput Export(params string[] lines) =>
        new("transactions.csv", () => new MemoryStream(Encoding.UTF8.GetBytes(string.Join('\n', lines))));

    private async Task AssertNothingWrittenAsync()
    {
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().ToListAsync());
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
    }

    private async Task AssertNothingRepairedAsync()
    {
        await using var read = TestAppDbContext.Unrestricted(_options);
        Assert.Empty(await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync());
        Assert.All(
            await read.NayaxSaleTimestampRepairPreviewDrafts.AsNoTracking().ToListAsync(),
            draft => Assert.Null(draft.AppliedAt));
    }
}
