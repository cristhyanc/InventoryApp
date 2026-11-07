using Inventory.Application.NayaxReconciliation;
using Inventory.Application.NayaxSaleReview;
using Inventory.Domain.Exceptions;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.NayaxSqs;
using InventoryApi.Tests.Application.Time;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Issue #1105's Owner decisions on a real SQLite database: the decision table applied inside one
/// write-locked transaction, the product resolution of "Use Nayax's values", the audit and the
/// pending rebuild each decision records, later evidence superseding a decision, and the race
/// between a decision and the hourly evidence write.
///
/// <para>Relational SQLite rather than the EF InMemory provider, because every decision is one
/// <c>BEGIN IMMEDIATE</c> transaction and the InMemory provider has none.</para>
/// </summary>
public class EfNayaxSaleReviewStoreTests
{
    private static readonly DateTime RunStarted = new(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecidedAt = new(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc);
    private static readonly NayaxSaleDecisionActor Owner = new("tenant-1", "owner-object-1");

    /// <summary>A second product, linked to a Nayax id whose number is also another product's local id.</summary>
    private const long OtherNayaxProductId = 4300002;
    private const long LinkedLocalProductId = 5002;

    // ---------------------------------------------------------------- the decision table

    [Fact]
    public async Task Keep_the_SQS_values_confirms_the_sale_audits_it_and_records_one_rebuild()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture);

        await using (var db = fixture.Context())
        {
            var applied = await Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues, "Till receipt matches"),
                Owner,
                CancellationToken.None);

            Assert.NotNull(applied);
            Assert.Equal(NayaxSqsVerification.OwnerConfirmed, applied.ResultingState);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId);
        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, sale.SqsVerification);
        Assert.Equal(3.5m, sale.SettlementValue);

        var audit = await read.NayaxSaleOwnerDecisionAudits.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.BusinessA, audit.BusinessId);
        Assert.Equal(NayaxSaleOwnerDecisionAuditKind.Decision, audit.Kind);
        Assert.Equal(NayaxSaleOwnerDecision.KeepSqsValues, audit.Decision);
        Assert.Equal(evidenceId, audit.EvidenceId);
        Assert.Equal("Till receipt matches", audit.Reason);
        Assert.Equal(Owner.ObjectId, audit.ActorObjectId);
        Assert.Equal(Owner.DirectoryTenantId, audit.ActorDirectoryTenantId);
        Assert.Equal(DecidedAt, audit.OccurredAtUtc);
        Assert.Equal(NayaxSqsVerification.Conflict, audit.PreviousState);
        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, audit.ResultingState);
        Assert.Equal(0m, audit.PreviousCountedAmount);
        Assert.Equal(3.5m, audit.ResultingCountedAmount);

        // Counting changed (a conflict did not count; a confirmed sale does), so its product replays.
        var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.ProductA, pending.ProductId);
        Assert.Equal(NayaxSqsSaleFixture.SaleInstant, pending.FromInstantUtc);
    }

    [Fact]
    public async Task Use_Nayax_values_resolves_the_local_product_through_the_link_map_not_the_raw_id()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await SeedOverlappingProductsAsync(fixture);
        var laterInstant = NayaxSqsSaleFixture.SaleInstant.AddMinutes(-30);
        var (saleId, evidenceId) = await ConflictingSaleAsync(
            fixture,
            Provider() with
            {
                SettlementValue = 4.5m,
                AuthorizationInstantUtc = laterInstant,
                NayaxProductId = OtherNayaxProductId,
                ProductName = "Fictional Cola 600ml",
            });

        await using (var db = fixture.Context())
        {
            var applied = await Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.UseNayaxValues, null),
                Owner,
                CancellationToken.None);
            Assert.Equal(NayaxSqsVerification.OwnerCorrected, applied!.ResultingState);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId);

        // The raw provider id and the resolved local product, changed together. The local product is
        // the one the link map names - never the decoy whose local id equals the raw Nayax id.
        Assert.Equal(OtherNayaxProductId, sale.NayaxProductId);
        Assert.True(sale.ProviderProductIdIsRaw);
        Assert.Equal(LinkedLocalProductId, sale.ProductId);
        Assert.NotEqual(OtherNayaxProductId, sale.ProductId);
        Assert.Equal(4.5m, sale.SettlementValue);
        Assert.Equal(laterInstant, sale.MachineAuthorizationTime);
        Assert.Equal(NayaxSqsVerification.OwnerCorrected, sale.SqsVerification);

        var audit = await read.NayaxSaleOwnerDecisionAudits.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxSqsSaleFixture.ProductA, audit.PreviousProductId);
        Assert.Equal(LinkedLocalProductId, audit.ResultingProductId);
        Assert.Equal(3.5m, audit.PreviousSettlementValue);
        Assert.Equal(4.5m, audit.ResultingSettlementValue);

        // Both products replay, from the earlier of the old and new instants.
        var pending = await read.PendingInventoryCostRebuilds.AsNoTracking().OrderBy(row => row.ProductId).ToListAsync();
        Assert.Equal([NayaxSqsSaleFixture.ProductA, LinkedLocalProductId], pending.ConvertAll(row => row.ProductId));
        Assert.All(pending, row => Assert.Equal(laterInstant, row.FromInstantUtc));
    }

    [Fact]
    public async Task Use_Nayax_values_falls_back_to_the_product_name_exactly_as_a_new_sale_does()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(
            fixture, Provider() with { SettlementValue = 4.5m, NayaxProductId = 4399999 });

        await using (var db = fixture.Context())
        {
            await Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.UseNayaxValues, null),
                Owner,
                CancellationToken.None);
        }

        await using var read = fixture.Context();
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId);
        Assert.Equal(NayaxSqsSaleFixture.ProductA, sale.ProductId);
        Assert.Equal(4399999, sale.NayaxProductId);
    }

    [Fact]
    public async Task Use_Nayax_values_is_refused_with_409_and_changes_nothing_when_the_product_is_not_linked()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(
            fixture,
            Provider() with { SettlementValue = 4.5m, NayaxProductId = 4399999, ProductName = "No such product anywhere" });

        await using (var db = fixture.Context())
        {
            var refused = await Assert.ThrowsAsync<DomainConflictException>(() => Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.UseNayaxValues, null),
                Owner,
                CancellationToken.None));
            Assert.Equal(EfNayaxSaleReviewStore.ProductNotLinkedMessage, refused.Message);
        }

        await AssertUnchangedAsync(fixture, saleId, NayaxSqsVerification.Conflict);
    }

    [Fact]
    public async Task This_sale_is_genuine_is_refused_on_a_conflict()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture);

        await using (var db = fixture.Context())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.Genuine, "It was real"),
                Owner,
                CancellationToken.None));
        }

        await AssertUnchangedAsync(fixture, saleId, NayaxSqsVerification.Conflict);
    }

    [Fact]
    public async Task Use_Nayax_values_is_refused_when_it_names_an_absence()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, _) = await ConflictingSaleAsync(fixture);
        var absenceId = await AddAbsenceAsync(fixture, saleId, NayaxSqsVerification.Conflict);

        await using (var db = fixture.Context())
        {
            await Assert.ThrowsAsync<DomainValidationException>(() => Store(db).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleId, NayaxSqsVerification.Conflict, absenceId, absenceId, NayaxSaleOwnerDecision.UseNayaxValues, null),
                Owner,
                CancellationToken.None));
        }

        await AssertUnchangedAsync(fixture, saleId, NayaxSqsVerification.Conflict);
    }

    [Theory]
    [InlineData(NayaxSaleOwnerDecision.DoNotCount, NayaxSqsVerification.OwnerExcluded)]
    [InlineData(NayaxSaleOwnerDecision.Genuine, NayaxSqsVerification.OwnerConfirmed)]
    public async Task A_sale_needing_investigation_may_be_decided_on_absence_evidence(
        NayaxSaleOwnerDecision decision, NayaxSqsVerification result)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);
        var absenceId = await AddAbsenceAsync(fixture, saleId, NayaxSqsVerification.NeedsInvestigation);

        await using (var db = fixture.Context())
        {
            var applied = await Store(db).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleId, NayaxSqsVerification.NeedsInvestigation, absenceId, absenceId, decision, "Checked the machine"),
                Owner,
                CancellationToken.None);
            Assert.Equal(result, applied!.ResultingState);
        }

        await using var read = fixture.Context();
        Assert.Equal(absenceId, (await read.NayaxSaleOwnerDecisionAudits.AsNoTracking().SingleAsync()).EvidenceId);

        // "This sale is genuine" changes nothing that counts, so nothing is replayed; "Don't count
        // this sale" removes it from the totals, so its product is.
        Assert.Equal(
            decision is NayaxSaleOwnerDecision.DoNotCount ? 1 : 0,
            await read.PendingInventoryCostRebuilds.CountAsync());
    }

    // ---------------------------------------------------------------- the expected-value guard

    [Fact]
    public async Task A_stale_state_is_refused_with_409_and_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture);

        await using (var db = fixture.Context())
        {
            await Assert.ThrowsAsync<DomainConflictException>(() => Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.NeedsInvestigation, evidenceId, NayaxSaleOwnerDecision.DoNotCount, "r"),
                Owner,
                CancellationToken.None));
        }

        await AssertUnchangedAsync(fixture, saleId, NayaxSqsVerification.Conflict);
    }

    [Fact]
    public async Task Stale_evidence_is_refused_with_409_and_writes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, firstEvidenceId) = await ConflictingSaleAsync(fixture);

        // Newer evidence arrived after the page was read.
        await using (var db = fixture.Context())
        {
            await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(1), Provider() with { SettlementValue = 5.5m }),
                CancellationToken.None);
        }

        await using (var db = fixture.Context())
        {
            await Assert.ThrowsAsync<DomainConflictException>(() => Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, firstEvidenceId, NayaxSaleOwnerDecision.KeepSqsValues, "r"),
                Owner,
                CancellationToken.None));
        }

        await AssertUnchangedAsync(fixture, saleId, NayaxSqsVerification.Conflict);
    }

    [Fact]
    public async Task A_business_can_neither_decide_another_business_sale_nor_name_its_evidence()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleA, evidenceA) = await ConflictingSaleAsync(fixture);
        var (saleB, evidenceB) = await ConflictingSaleAsync(fixture, businessId: NayaxSqsSaleFixture.BusinessB);

        await using (var asB = fixture.Context(NayaxSqsSaleFixture.BusinessB))
        {
            // Business A's sale is simply not found in business B's scope.
            Assert.Null(await Store(asB).DecideAsync(
                Request(saleA, NayaxSqsVerification.Conflict, evidenceA, NayaxSaleOwnerDecision.KeepSqsValues, "r"),
                Owner,
                CancellationToken.None));

            // Nor is business A's evidence, named against business B's own sale.
            await Assert.ThrowsAsync<DomainConflictException>(() => Store(asB).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleB, NayaxSqsVerification.Conflict, evidenceB, evidenceA, NayaxSaleOwnerDecision.KeepSqsValues, "r"),
                Owner,
                CancellationToken.None));

            var listed = await Store(asB).ListAsync(NeedsReview(), CancellationToken.None);
            Assert.Equal([saleB], listed.Items.Select(item => item.SaleId));
            Assert.Equal(1, listed.TotalCount);
        }

        await AssertUnchangedAsync(fixture, saleA, NayaxSqsVerification.Conflict);
        await AssertUnchangedAsync(fixture, saleB, NayaxSqsVerification.Conflict, NayaxSqsSaleFixture.BusinessB);
    }

    // ---------------------------------------------------------------- list and summary

    [Fact]
    public async Task The_list_and_summary_show_flagged_sales_and_the_decisions_the_table_allows()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (conflictId, comparisonId) = await ConflictingSaleAsync(fixture);
        var investigationId = await StoreQueueSaleAsync(fixture, transactionId: 4100000002);
        var absenceId = await AddAbsenceAsync(fixture, investigationId, NayaxSqsVerification.NeedsInvestigation);

        await using var db = fixture.Context();
        var store = Store(db);

        Assert.Equal(new NayaxSaleReviewSummary(2, 1), await store.SummaryAsync(CancellationToken.None));

        var items = (await store.ListAsync(NeedsReview(), CancellationToken.None)).Items.ToDictionary(item => item.SaleId);
        Assert.False(items[conflictId].IsCounted);
        Assert.Equal(comparisonId, items[conflictId].LatestComparison!.Id);
        Assert.Equal(
            [
                new NayaxSaleAvailableDecision(NayaxSaleOwnerDecision.UseNayaxValues, comparisonId),
                new NayaxSaleAvailableDecision(NayaxSaleOwnerDecision.KeepSqsValues, comparisonId),
                new NayaxSaleAvailableDecision(NayaxSaleOwnerDecision.DoNotCount, comparisonId),
            ],
            items[conflictId].AvailableDecisions);

        Assert.True(items[investigationId].IsCounted);
        Assert.Equal(12, items[investigationId].LatestAbsence!.ReturnedItemCount);
        Assert.Equal(
            [
                new NayaxSaleAvailableDecision(NayaxSaleOwnerDecision.DoNotCount, absenceId),
                new NayaxSaleAvailableDecision(NayaxSaleOwnerDecision.Genuine, absenceId),
            ],
            items[investigationId].AvailableDecisions);
    }

    /// <summary>
    /// A flagged sale older than every decided one is still on the review page: the two views are
    /// paged separately, and the needs-review view holds exactly what the Dashboard summary counts.
    /// </summary>
    [Fact]
    public async Task A_flagged_sale_older_than_every_decided_sale_is_reachable_and_every_page_can_be_read()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var flaggedId = await StoreQueueSaleAsync(fixture, transactionId: 4100000100);
        var decidedIds = new List<long>();
        for (var i = 1; i <= 5; i++)
        {
            decidedIds.Add(await StoreQueueSaleAsync(fixture, transactionId: 4100000100 + i));
        }

        await using (var db = fixture.Context())
        {
            var flagged = await db.NayaxSales.SingleAsync(row => row.Id == flaggedId);
            flagged.SqsVerification = NayaxSqsVerification.Conflict;
            flagged.MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant.AddDays(-30);
            var offset = 0;
            foreach (var sale in await db.NayaxSales.Where(row => decidedIds.Contains(row.Id)).OrderBy(row => row.Id).ToListAsync())
            {
                sale.SqsVerification = NayaxSqsVerification.OwnerConfirmed;
                sale.MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant.AddMinutes(++offset);
            }

            await db.SaveChangesAsync();
        }

        await using var read = fixture.Context();
        var store = Store(read);

        // The summary counts it, and the needs-review view lists it, on a page of one.
        Assert.Equal(new NayaxSaleReviewSummary(1, 1), await store.SummaryAsync(CancellationToken.None));
        var needsReview = await store.ListAsync(new NayaxSaleReviewQuery(NayaxSaleReviewView.NeedsReview, 0, 1), CancellationToken.None);
        Assert.Equal([flaggedId], needsReview.Items.Select(item => item.SaleId));
        Assert.Equal(1, needsReview.TotalCount);

        // The decided view pages through all five, newest first, with no sale twice or missed.
        var seen = new List<long>();
        for (var page = 0; page < 3; page++)
        {
            var decided = await store.ListAsync(new NayaxSaleReviewQuery(NayaxSaleReviewView.Decided, page, 2), CancellationToken.None);
            Assert.Equal(5, decided.TotalCount);
            seen.AddRange(decided.Items.Select(item => item.SaleId));
        }

        Assert.Equal(Enumerable.Reverse(decidedIds), seen);
        Assert.Empty((await store.ListAsync(new NayaxSaleReviewQuery(NayaxSaleReviewView.Decided, 3, 2), CancellationToken.None)).Items);
        Assert.Empty((await store.ListAsync(new NayaxSaleReviewQuery(NayaxSaleReviewView.Decided, int.MaxValue, 100), CancellationToken.None)).Items);
    }

    [Theory]
    [InlineData(-1, 25)]
    [InlineData(0, 0)]
    [InlineData(0, 101)]
    public async Task A_page_outside_the_allowed_range_is_refused(int pageIndex, int pageSize)
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        await using var db = fixture.Context();

        await Assert.ThrowsAsync<DomainValidationException>(() => Store(db).ListAsync(
            new NayaxSaleReviewQuery(NayaxSaleReviewView.NeedsReview, pageIndex, pageSize), CancellationToken.None));
    }

    // ---------------------------------------------------------------- costing and reconciliation

    [Fact]
    public async Task Excluding_a_sale_releases_exactly_one_unit_of_costing_inventory()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);
        var absenceId = await AddAbsenceAsync(fixture, saleId, NayaxSqsVerification.NeedsInvestigation);
        var before = await CostingQuantityAsync(fixture);

        await using (var db = fixture.Context())
        {
            await Store(db).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleId, NayaxSqsVerification.NeedsInvestigation, absenceId, absenceId, NayaxSaleOwnerDecision.DoNotCount, "Test vend"),
                Owner,
                CancellationToken.None);
            var drained = await NayaxSqsSaleFixture.Store(db).RebuildInventoryCostsAsync(CancellationToken.None);
            Assert.Empty(drained.Failures);
        }

        Assert.Equal(before + 1, await CostingQuantityAsync(fixture));

        await using var read = fixture.Context();
        Assert.Empty(await read.PendingInventoryCostRebuilds.ToListAsync());
    }

    [Fact]
    public async Task Keep_the_SQS_values_consumes_exactly_as_before_the_conflict()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);
        var consumed = await CostingQuantityAsync(fixture);
        var (_, evidenceId) = await ConflictOfAsync(fixture, saleId, Provider() with { SettlementValue = 4.5m });

        await using (var db = fixture.Context())
        {
            await Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues, "Receipt"),
                Owner,
                CancellationToken.None);
            await NayaxSqsSaleFixture.Store(db).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        Assert.Equal(consumed, await CostingQuantityAsync(fixture));
    }

    [Fact]
    public async Task The_audit_lists_a_reconciled_period_the_decision_changed_and_by_how_much()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);
        var absenceId = await AddAbsenceAsync(fixture, saleId, NayaxSqsVerification.NeedsInvestigation);

        await using (var seed = fixture.Context())
        {
            var file = new ImportedFile { FileName = "reimbursement.xlsx", FileHash = "hash-1", ImportedAt = RunStarted };
            file.Reimbursements.Add(new ImportedReimbursement
            {
                ReimbursementStartDate = new DateTime(2026, 11, 1),
                ReimbursementEndDate = new DateTime(2026, 11, 7),
                IsReimbursement = true,
            });
            seed.ImportedFiles.Add(file);
            await seed.SaveChangesAsync();
        }

        await using (var db = fixture.Context())
        {
            await Store(db).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleId, NayaxSqsVerification.NeedsInvestigation, absenceId, absenceId, NayaxSaleOwnerDecision.DoNotCount, "Test vend"),
                Owner,
                CancellationToken.None);
        }

        await using var read = fixture.Context();
        var audit = await read.NayaxSaleOwnerDecisionAudits.AsNoTracking().SingleAsync();
        Assert.Equal(new DateTime(2026, 11, 1), audit.ReconciledPeriodFrom);
        Assert.Equal(new DateTime(2026, 11, 7), audit.ReconciledPeriodTo);
        Assert.Equal(-3.5m, audit.ReconciledPeriodDifference);
    }

    // ---------------------------------------------------------------- later evidence

    [Fact]
    public async Task Evidence_that_repeats_what_the_decision_relied_on_changes_nothing()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var conflicting = Provider() with { SettlementValue = 4.5m };
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, conflicting);
        await DecideAsync(fixture, saleId, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues);

        await using (var db = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(2), conflicting),
                CancellationToken.None);
            Assert.False(written.StateChanged);
        }

        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, await VerificationOfAsync(fixture, saleId));
        await using var read = fixture.Context();
        Assert.Equal(1, await read.NayaxSaleOwnerDecisionAudits.CountAsync());
    }

    /// <summary>
    /// A, the Owner keeps the SQS values, a matching B, then A again: A is exactly what the Owner
    /// already considered, so the decision stands even though B is now the latest comparison. A
    /// genuinely new contradiction afterwards still supersedes it.
    /// </summary>
    [Fact]
    public async Task The_relied_on_contradiction_returning_after_a_match_never_supersedes_the_decision()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var a = Provider() with { SettlementValue = 4.5m };
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, a);
        await DecideAsync(fixture, saleId, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues);

        foreach (var (provider, hours) in new[] { (Provider(), 2), (a, 3) })
        {
            await using var db = fixture.Context();
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(hours), provider),
                CancellationToken.None);

            // Both are new to the history (each differs from the latest row), and neither moves the state.
            Assert.True(written.Written);
            Assert.False(written.StateChanged);
            Assert.Equal(NayaxSqsVerification.OwnerConfirmed, written.State);
        }

        await using (var read = fixture.Context())
        {
            Assert.Equal(NayaxSqsVerification.OwnerConfirmed, await VerificationOfAsync(fixture, saleId));
            Assert.Equal(1, await read.NayaxSaleOwnerDecisionAudits.CountAsync());

            // Only the decision's own rebuild: neither later comparison changed counting.
            Assert.Equal(1, await read.PendingInventoryCostRebuilds.CountAsync());
        }

        long newEvidenceId;
        await using (var db = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(4), Provider() with { SettlementValue = 6.5m }),
                CancellationToken.None);
            Assert.True(written.StateChanged);
            newEvidenceId = written.EvidenceId!.Value;
        }

        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));
        await using var after = fixture.Context();
        var superseded = await after.NayaxSaleOwnerDecisionAudits.AsNoTracking()
            .SingleAsync(row => row.Kind == NayaxSaleOwnerDecisionAuditKind.Superseded);
        Assert.Equal(newEvidenceId, superseded.EvidenceId);
    }

    [Fact]
    public async Task Matching_evidence_is_recorded_but_never_promotes_an_owner_decision_to_verified()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, Provider() with { SettlementValue = 4.5m });
        await DecideAsync(fixture, saleId, evidenceId, NayaxSaleOwnerDecision.UseNayaxValues);

        await using (var db = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(2), Provider() with { SettlementValue = 4.5m }),
                CancellationToken.None);
            Assert.True(written.Written);
            Assert.False(written.StateChanged);
        }

        Assert.Equal(NayaxSqsVerification.OwnerCorrected, await VerificationOfAsync(fixture, saleId));
    }

    [Fact]
    public async Task Contradicting_new_evidence_supersedes_the_decision_and_audits_it()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, Provider() with { SettlementValue = 4.5m });
        await DecideAsync(fixture, saleId, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues);

        long newEvidenceId;
        await using (var db = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(2), Provider() with { SettlementValue = 6.5m }),
                CancellationToken.None);
            Assert.True(written.StateChanged);
            newEvidenceId = written.EvidenceId!.Value;
        }

        Assert.Equal(NayaxSqsVerification.Conflict, await VerificationOfAsync(fixture, saleId));

        await using var read = fixture.Context();
        var superseded = await read.NayaxSaleOwnerDecisionAudits.AsNoTracking()
            .SingleAsync(row => row.Kind == NayaxSaleOwnerDecisionAuditKind.Superseded);
        Assert.Equal(newEvidenceId, superseded.EvidenceId);
        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, superseded.PreviousState);
        Assert.Equal(NayaxSqsVerification.Conflict, superseded.ResultingState);
        Assert.Null(superseded.Decision);

        // The sale stopped counting again, so its product replays again; the alert shows again.
        Assert.Equal(1, await read.PendingInventoryCostRebuilds.CountAsync());
        Assert.Equal(new NayaxSaleReviewSummary(1, 1), await Store(read).SummaryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_absence_never_supersedes_an_owner_decision()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var saleId = await StoreQueueSaleAsync(fixture);
        var absenceId = await AddAbsenceAsync(fixture, saleId, NayaxSqsVerification.NeedsInvestigation);
        await using (var db = fixture.Context())
        {
            await Store(db).DecideAsync(
                new NayaxSaleDecisionRequest(
                    saleId, NayaxSqsVerification.NeedsInvestigation, absenceId, absenceId, NayaxSaleOwnerDecision.Genuine, "Seen"),
                Owner,
                CancellationToken.None);
        }

        await using (var db = fixture.Context())
        {
            var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordAbsenceAsync(
                new NayaxSaleAbsenceEvidence(
                    saleId,
                    Guid.NewGuid(),
                    RunStarted.AddHours(3),
                    new NayaxProviderMachineWindow(NayaxSqsSaleFixture.MachineId, 20, NayaxSqsSaleFixture.SaleInstant.AddHours(-2), NayaxSqsSaleFixture.SaleInstant.AddHours(2)),
                    NayaxSqsSaleFixture.SaleInstant,
                    NayaxSqsSaleFixture.ReceivedAtUtc,
                    new NayaxSaleAbsenceObservation(true, true, true, NayaxSqsVerification.NeedsInvestigation)),
                CancellationToken.None);
            Assert.False(written.StateChanged);
        }

        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, await VerificationOfAsync(fixture, saleId));
    }

    // ---------------------------------------------------------------- the race (required)

    /// <summary>
    /// <strong>Evidence first.</strong> The hourly write commits new evidence after the page was read
    /// and before the decision; the decision is refused with 409 and writes nothing.
    /// </summary>
    [Fact]
    public async Task Race_when_the_evidence_commits_first_the_decision_is_refused()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, Provider() with { SettlementValue = 4.5m });

        await using (var evidenceWriter = fixture.SecondWriter())
        {
            await new EfNayaxSaleProviderEvidenceStore(evidenceWriter).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(1), Provider() with { SettlementValue = 6.5m }),
                CancellationToken.None);
        }

        await using (var db = fixture.Context())
        {
            await Assert.ThrowsAsync<DomainConflictException>(() => Store(db).DecideAsync(
                Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues, "r"),
                Owner,
                CancellationToken.None));
        }

        await using var read = fixture.Context();
        Assert.Equal(0, await read.NayaxSaleOwnerDecisionAudits.CountAsync());
        Assert.Equal(0, await read.PendingInventoryCostRebuilds.CountAsync());
    }

    /// <summary>
    /// <strong>Decision first.</strong> The decision commits, then the hourly write evaluates its
    /// evidence against the decided values: a repeat of what the decision relied on changes nothing,
    /// and a differing contradiction supersedes it.
    /// </summary>
    [Fact]
    public async Task Race_when_the_decision_commits_first_the_evidence_is_judged_against_the_decided_values()
    {
        await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync();
        var conflicting = Provider() with { SettlementValue = 4.5m };
        var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, conflicting);

        await DecideAsync(fixture, saleId, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues);

        await using (var evidenceWriter = fixture.SecondWriter())
        {
            var repeat = await new EfNayaxSaleProviderEvidenceStore(evidenceWriter).RecordComparisonAsync(
                new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(1), conflicting),
                CancellationToken.None);
            Assert.False(repeat.StateChanged);
        }

        Assert.Equal(NayaxSqsVerification.OwnerConfirmed, await VerificationOfAsync(fixture, saleId));

        await using var read = fixture.Context();
        Assert.Equal(1, await read.NayaxSaleOwnerDecisionAudits.CountAsync());
        Assert.Equal(1, await read.PendingInventoryCostRebuilds.CountAsync());
    }

    /// <summary>
    /// <strong>Truly concurrent.</strong> A decision and an evidence write for the same sale run at
    /// once on two connections to the same file. Whichever order SQLite's write lock gives them, no
    /// decision is applied against evidence it did not see, and an applied decision has exactly one
    /// audit row and one pending rebuild.
    /// </summary>
    [Fact]
    public async Task Race_run_concurrently_the_two_writes_are_strictly_ordered()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await using var fixture = await NayaxSqsSaleFixture.OnDiskAsync(busyTimeoutSeconds: 30);
            var (saleId, evidenceId) = await ConflictingSaleAsync(fixture, Provider() with { SettlementValue = 4.5m });

            await using var decisionDb = fixture.Context();
            await using var evidenceDb = fixture.SecondWriter();

            using var start = new SemaphoreSlim(0, 2);
            var decision = Task.Run(async () =>
            {
                await start.WaitAsync();
                try
                {
                    return await Store(decisionDb).DecideAsync(
                        Request(saleId, NayaxSqsVerification.Conflict, evidenceId, NayaxSaleOwnerDecision.KeepSqsValues, "r"),
                        Owner,
                        CancellationToken.None);
                }
                catch (DomainConflictException)
                {
                    return null;
                }
            });
            var evidence = Task.Run(async () =>
            {
                await start.WaitAsync();
                return await new EfNayaxSaleProviderEvidenceStore(evidenceDb).RecordComparisonAsync(
                    new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted.AddHours(1), Provider() with { SettlementValue = 6.5m }),
                    CancellationToken.None);
            });
            start.Release(2);
            var applied = await decision;
            var written = await evidence;

            await using var read = fixture.Context();
            var audits = await read.NayaxSaleOwnerDecisionAudits.AsNoTracking().OrderBy(row => row.Id).ToListAsync();
            var state = await VerificationOfAsync(fixture, saleId);

            if (applied is null)
            {
                // The evidence committed first: nothing of the decision was written.
                Assert.Empty(audits);
                Assert.Equal(NayaxSqsVerification.Conflict, state);
                Assert.False(written.StateChanged);
            }
            else
            {
                // The decision committed first, and the differing contradiction then superseded it.
                Assert.Equal(
                    [NayaxSaleOwnerDecisionAuditKind.Decision, NayaxSaleOwnerDecisionAuditKind.Superseded],
                    audits.ConvertAll(row => row.Kind));
                Assert.Equal(evidenceId, audits[0].EvidenceId);
                Assert.True(written.StateChanged);
                Assert.Equal(NayaxSqsVerification.Conflict, state);
                Assert.Equal(1, await read.PendingInventoryCostRebuilds.CountAsync());
            }
        }
    }

    // ---------------------------------------------------------------- the pinned predicate

    /// <summary>
    /// The EF-translatable completed-sale predicate and the domain rule say the same thing for every
    /// state, as <see cref="NayaxSaleCountingPolicy"/> requires.
    /// </summary>
    [Fact]
    public async Task The_EF_completed_sale_predicate_matches_the_domain_rule_for_every_state()
    {
        await using var fixture = await NayaxSqsSaleFixture.InMemoryAsync();
        var states = Enum.GetValues<NayaxSqsVerification>().Cast<NayaxSqsVerification?>().Append(null).ToList();
        var transactionId = 4100000100L;

        await using (var seed = fixture.Context())
        {
            foreach (var state in states)
            {
                foreach (var status in new int?[] { 12, 250, null })
                {
                    seed.NayaxSales.Add(new NayaxSales
                    {
                        TransactionID = transactionId++,
                        TransactionStatusId = status,
                        MachineID = NayaxSqsSaleFixture.MachineId,
                        SettlementValue = 1m,
                        MachineAuthorizationTime = NayaxSqsSaleFixture.SaleInstant,
                        Provider = ExternalProvider.Nayax,
                        SqsVerification = state,
                    });
                }
            }

            await seed.SaveChangesAsync();
        }

        var predicate = (System.Linq.Expressions.Expression<Func<NayaxSales, bool>>)typeof(EfNayaxSaleReviewStore).Assembly
            .GetType("Inventory.Infrastructure.Data.EfNayaxSalesQueries", throwOnError: true)!
            .GetProperty("CompletedSalePredicate")!
            .GetValue(null)!;

        await using var read = fixture.Context();
        var counted = (await read.NayaxSales.AsNoTracking().Where(predicate).Select(row => row.Id).ToListAsync()).ToHashSet();
        var all = await read.NayaxSales.AsNoTracking().ToListAsync();

        Assert.All(all, sale => Assert.Equal(
            NayaxSaleCountingPolicy.IsCountedSale(sale.TransactionStatusId, sale.SqsVerification),
            counted.Contains(sale.Id)));
    }

    // ---------------------------------------------------------------- helpers

    private static NayaxSaleReviewQuery NeedsReview() =>
        new(NayaxSaleReviewView.NeedsReview, 0, NayaxSaleReviewQuery.MaxPageSize);

    private static EfNayaxSaleReviewStore Store(AppDbContext db) =>
        new(db, new EfNayaxProductLinkStore(db), new FakeClock(DecidedAt));

    private static NayaxSaleDecisionRequest Request(
        long saleId, NayaxSqsVerification state, long evidenceId, NayaxSaleOwnerDecision decision, string? reason) =>
        new(saleId, state, evidenceId, evidenceId, decision, reason);

    private static async Task DecideAsync(
        NayaxSqsSaleFixture fixture, long saleId, long evidenceId, NayaxSaleOwnerDecision decision)
    {
        await using var db = fixture.Context();
        Assert.NotNull(await Store(db).DecideAsync(
            Request(saleId, NayaxSqsVerification.Conflict, evidenceId, decision, "A reason"),
            Owner,
            CancellationToken.None));
    }

    private static NayaxProviderSaleValues Provider() => new(
        MachineId: NayaxSqsSaleFixture.MachineId,
        SettlementValue: 3.5m,
        AuthorizationInstantUtc: NayaxSqsSaleFixture.SaleInstant,
        CurrencyCode: null,
        NayaxProductId: NayaxSqsSaleFixture.NayaxProductId,
        ProductName: "Fictional Spring Water 600ml");

    /// <summary>A queue sale the business's own lastSales contradicted (by default, on the amount).</summary>
    private static async Task<(long SaleId, long EvidenceId)> ConflictingSaleAsync(
        NayaxSqsSaleFixture fixture, NayaxProviderSaleValues? provider = null, int businessId = NayaxSqsSaleFixture.BusinessA)
    {
        var saleId = await StoreQueueSaleAsync(fixture, businessId);
        return await ConflictOfAsync(fixture, saleId, provider ?? Provider() with { SettlementValue = 4.5m }, businessId);
    }

    private static async Task<(long SaleId, long EvidenceId)> ConflictOfAsync(
        NayaxSqsSaleFixture fixture, long saleId, NayaxProviderSaleValues provider, int businessId = NayaxSqsSaleFixture.BusinessA)
    {
        await using var db = fixture.Context(businessId);
        var written = await new EfNayaxSaleProviderEvidenceStore(db).RecordComparisonAsync(
            new NayaxSaleComparisonEvidence(saleId, Guid.NewGuid(), RunStarted, provider), CancellationToken.None);

        Assert.Equal(NayaxSqsVerification.Conflict, written.State);

        // The contradiction stopped the sale counting and staged its product's replay; drained here so
        // each test reads only the rebuilds its own decision staged.
        await NayaxSqsSaleFixture.Store(db).RebuildInventoryCostsAsync(CancellationToken.None);
        return (saleId, written.EvidenceId!.Value);
    }

    /// <summary>An absence row naming what lastSales returned for the machine, with the sale in <paramref name="state"/>.</summary>
    private static async Task<long> AddAbsenceAsync(NayaxSqsSaleFixture fixture, long saleId, NayaxSqsVerification state)
    {
        await using var db = fixture.Context();
        var sale = await db.NayaxSales.SingleAsync(row => row.Id == saleId);
        sale.SqsVerification = state;
        var row = new NayaxSaleProviderEvidence
        {
            NayaxSaleId = saleId,
            RunId = Guid.NewGuid(),
            ObservedAtUtc = RunStarted.AddMinutes(30),
            Kind = NayaxSaleProviderEvidenceKind.Absence,
            ResultingState = state,
            ReadMachineId = NayaxSqsSaleFixture.MachineId,
            ReturnedItemCount = 12,
            ReturnedEarliestInstantUtc = NayaxSqsSaleFixture.SaleInstant.AddHours(-1),
            ReturnedLatestInstantUtc = NayaxSqsSaleFixture.SaleInstant.AddHours(1),
            SaleAuthorizationInstantUtc = NayaxSqsSaleFixture.SaleInstant,
            SaleReceivedViaSqsAtUtc = NayaxSqsSaleFixture.ReceivedAtUtc,
            Bracketed = true,
            OutsideReceiptGrace = true,
        };
        db.NayaxSaleProviderEvidences.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>
    /// A second product linked to <see cref="OtherNayaxProductId"/>, and a decoy whose own local id is
    /// that same number: the case that proves the resolver, not the raw id, chose the product.
    /// </summary>
    private static async Task SeedOverlappingProductsAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        db.Products.Add(new Product
        {
            Id = LinkedLocalProductId,
            Name = "Fictional Cola 600ml",
            ExternalProductId = NayaxExternalId.From(OtherNayaxProductId),
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
        });
        db.Products.Add(new Product
        {
            Id = OtherNayaxProductId,
            Name = "Decoy whose local id equals the Nayax id",
            QuantityInStock = 10,
            CostingQuantity = 10,
            InventoryValue = 20m,
            AverageUnitCost = 2m,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<long> StoreQueueSaleAsync(
        NayaxSqsSaleFixture fixture,
        int businessId = NayaxSqsSaleFixture.BusinessA,
        long transactionId = NayaxSqsSaleFixture.TransactionId)
    {
        await using (var db = fixture.Context(businessId))
        {
            await NayaxSqsSaleFixture.Store(db).PersistSqsSaleAsync(
                NayaxSqsSaleFixture.SqsSale(
                    suppliedStatusId: 12, transactionId: transactionId, messageId: $"message-{businessId}-{transactionId}"),
                CancellationToken.None);
            await NayaxSqsSaleFixture.Store(db).RebuildInventoryCostsAsync(CancellationToken.None);
        }

        await using var read = fixture.Context(businessId);
        return (await read.NayaxSales.AsNoTracking().SingleAsync(row => row.TransactionID == transactionId)).Id;
    }

    private static async Task<int?> CostingQuantityAsync(NayaxSqsSaleFixture fixture)
    {
        await using var db = fixture.Context();
        return (await db.Products.AsNoTracking().SingleAsync(row => row.Id == NayaxSqsSaleFixture.ProductA)).CostingQuantity;
    }

    private static async Task AssertUnchangedAsync(
        NayaxSqsSaleFixture fixture, long saleId, NayaxSqsVerification state, int businessId = NayaxSqsSaleFixture.BusinessA)
    {
        await using var read = fixture.Context(businessId);
        var sale = await read.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId);
        Assert.Equal(state, sale.SqsVerification);
        Assert.Equal(3.5m, sale.SettlementValue);
        Assert.Equal(NayaxSqsSaleFixture.NayaxProductId, sale.NayaxProductId);
        Assert.Equal(0, await read.NayaxSaleOwnerDecisionAudits.CountAsync());
        Assert.Equal(0, await read.PendingInventoryCostRebuilds.CountAsync());
    }

    private static async Task<NayaxSqsVerification?> VerificationOfAsync(NayaxSqsSaleFixture fixture, long saleId)
    {
        await using var db = fixture.Unrestricted();
        return (await db.NayaxSales.AsNoTracking().SingleAsync(row => row.Id == saleId)).SqsVerification;
    }
}
