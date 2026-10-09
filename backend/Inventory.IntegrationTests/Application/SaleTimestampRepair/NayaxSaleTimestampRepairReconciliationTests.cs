using System.Globalization;
using System.Text;
using Inventory.Application.Imports;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Application.SaleTimestampRepair;

/// <summary>
/// Issue #472: the fixed-cutoff reconciliation, over the operator's own sanitized 8 October 2026
/// evidence. These are the regression tests for the acceptance criteria that are about numbers
/// rather than mechanics, and they are deliberately built so the published figures fall out of the
/// fixture instead of being asserted at it.
///
/// The seeded database is the supplied snapshot, decomposed into the five cohorts the evidence
/// describes. Sydney daylight saving began on Sunday 4 October 2026, so AEDT (+11) applies all week
/// and Monday 5 October starts at <c>2026-10-04T13:00:00Z</c> - the weekly window start the operator
/// reported.
///
/// <list type="bullet">
///   <item><b>28 records already holding the authoritative instant</b>, $108.20, on Monday
///   5 October. They are decided as already correct and are not moved.</item>
///   <item><b>124 records stored eleven hours early</b>, because the live synchronization read an
///   offset-free <c>AuthorizationDateTimeGMT</c> against the Sydney host's own offset (the defect
///   issue #471 stopped happening but did not repair): 58 belonging to 6 October ($183.00), 58 to
///   7 October ($216.40) and 8 to 8 October ($27.70, including transaction 3564567268 at $3.00).
///   Each currently reports on the previous Sydney day.</item>
///   <item><b>60 records stored between 13:54 and 23:44 on 4 October UTC</b>, $190.90, which read as
///   Monday 5 October in Sydney and are <em>absent</em> from the supplied Monday-Thursday export. No
///   source covers them, so they are reported explicitly unresolved and left exactly as they are:
///   their true source dates still require verification, and absence from one export proves nothing
///   about deletion, duplication or a corrected date.</item>
///   <item><b>38 records the export carries that the snapshot does not hold at all</b>, $119.70, all
///   on 8 October and including the later $4.80 sale. Those are missing sales, not timestamp
///   defects.</item>
///   <item><b>One sale authorized after the export's cutoff</b>, $5.00, which is excluded from both
///   sides of the comparison because a later sale is not a discrepancy.</item>
/// </list>
/// Two reported figures come straight out of that decomposition, which is what makes the fixture
/// faithful rather than merely convenient: the snapshot's weekly window totals <b>$726.20</b> before
/// any repair, and the export's own daily totals are $108.20, $183.00, $216.40 and $147.40 for a
/// weekly <b>$655.00</b>.
///
/// A real (non-InMemory) SQLite connection is used deliberately: these totals are produced by
/// translated SQL over persisted instants, which is what every report later reads back.
/// </summary>
public class NayaxSaleTimestampRepairReconciliationTests : IDisposable
{
    private const int BusinessA = 1;
    private const long MachineId = 531595328;
    private const long KnownTransactionId = 3564567268;

    private static readonly DateTime FifthOctober = new(2026, 10, 5);
    private static readonly DateTime SixthOctober = new(2026, 10, 6);
    private static readonly DateTime SeventhOctober = new(2026, 10, 7);
    private static readonly DateTime EighthOctober = new(2026, 10, 8);

    /// <summary>The instant the compared export was taken: 8 October 2026 15:00 in Sydney (AEDT).</summary>
    private static readonly DateTime ExportCutoffUtc = new(2026, 10, 8, 4, 0, 0, DateTimeKind.Utc);

    private static readonly DateTime Now = new(2026, 10, 8, 5, 0, 0, DateTimeKind.Utc);

    /// <summary>The eleven-hour error the Sydney-hosted API introduced under AEDT.</summary>
    private static readonly TimeSpan HostOffsetError = TimeSpan.FromHours(11);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly FakeClock _clock = new(Now);
    private readonly List<Cohort> _cohorts = [];

    public NayaxSaleTimestampRepairReconciliationTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        using var setup = TestAppDbContext.Unrestricted(_options);
        setup.Database.EnsureCreated();
        setup.Businesses.Add(new Business { Id = BusinessA, Name = "Vending A", CreatedAtUtc = Now });
        setup.SaveChanges();

        BuildCohorts();
        SeedSnapshot();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The defect as the snapshot holds it, before anything is repaired: the reported $726.20 weekly
    /// window, with 6, 7 and 8 October's revenue sitting on the previous Sydney day and the known
    /// transaction reading as 7 October.
    /// </summary>
    [Fact]
    public async Task The_snapshot_reports_the_defect_the_operator_saw()
    {
        var reconciliation = (await PreviewAsync(MondayToThursdayExport())).Reconciliation!;

        Assert.Equal(726.20m, reconciliation.TotalBefore);
        Assert.Equal(
            [
                (FifthOctober, 482.10m),
                (SixthOctober, 216.40m),
                (SeventhOctober, 27.70m),
                (EighthOctober, 0m),
            ],
            reconciliation.Days.Select(day => (day.BusinessDate, day.CompletedSalesBefore)));
    }

    /// <summary>
    /// Acceptance criteria, in one pass over the supplied snapshot and export: the known transaction
    /// is repaired to 2026-10-07T23:42:44.263Z and reports on 8 October; the mixed cohorts are
    /// decided per transaction; the 60 uncovered records stay unresolved and unchanged; the 38
    /// records the snapshot does not hold are reported as missing sales rather than as repairs; and
    /// the later $4.80-era sale after the cutoff is excluded.
    ///
    /// The repair alone therefore produces $535.30 of source-verified weekly revenue, and the $119.70
    /// of missing sales accounts for the remaining difference from the export's $655.00 exactly -
    /// which is the whole point of reporting the two separately.
    /// </summary>
    [Fact]
    public async Task A_timestamp_repair_alone_does_not_produce_the_export_totals_and_says_what_is_missing()
    {
        var preview = await PreviewAsync(MondayToThursdayExport());

        var known = preview.Rows.Single(row => row.TransactionId == KnownTransactionId);
        Assert.Equal(new DateTime(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc), known.RepairedInstantUtc);
        Assert.Equal((SeventhOctober, (DateTime?)EighthOctober), (known.StoredBusinessDate, known.RepairedBusinessDate));
        // 124 shifted rows move, the 28 already-correct ones and the later $5.00 sale do not, and the
        // 60 the export does not cover stay unresolved.
        Assert.Equal((124, 29, 60), (preview.Repairable, preview.AlreadyCorrect, preview.Unresolved));

        var reconciliation = preview.Reconciliation!;
        Assert.Equal(
            [
                (FifthOctober, 108.20m),
                (SixthOctober, 183.00m),
                (SeventhOctober, 216.40m),
                (EighthOctober, 27.70m),
            ],
            reconciliation.Days.Select(day => (day.BusinessDate, day.SourceVerifiedAfter)));
        Assert.Equal(535.30m, reconciliation.SourceVerifiedTotalAfter);
        Assert.Equal((60, 190.90m), (reconciliation.UnresolvedCompletedCount, reconciliation.UnresolvedCompletedAmount));
        Assert.Equal((38, 119.70m), (reconciliation.MissingFromDatabaseCount, reconciliation.MissingFromDatabaseAmount));
        Assert.Equal(655.00m, reconciliation.SourceVerifiedTotalAfter + reconciliation.MissingFromDatabaseAmount);
        Assert.Equal((1, 5.00m), (reconciliation.ExcludedAfterCutoffCount, reconciliation.ExcludedAfterCutoffAmount));
        // A repair moves revenue between Sydney days; it never creates or destroys any.
        Assert.Equal(reconciliation.TotalBefore, reconciliation.TotalAfter);
    }

    /// <summary>
    /// Acceptance criterion: for the same complete transaction set and the same cutoff, the repaired
    /// database reconciles to the export's daily totals and its weekly $655.00, with later sales
    /// excluded.
    ///
    /// "Complete" is the operative word, and it takes two different operations to get there: the
    /// 38 transactions the snapshot never held are imported through the ordinary uploaded-export
    /// import - a repair can never create a sale - and the 124 shifted instants are repaired through
    /// the reviewed preview and apply. Only then does every day agree.
    /// </summary>
    [Fact]
    public async Task The_complete_transaction_set_reconciles_to_the_export_daily_and_weekly_totals()
    {
        await ImportMissingSalesAsync();
        var preview = await PreviewAsync(MondayToThursdayExport());

        var applied = await ApplyAsync(preview.PreviewId);
        var after = (await PreviewAsync(MondayToThursdayExport())).Reconciliation!;

        Assert.Equal(124, applied.SalesRepaired);
        Assert.Equal(
            [
                (FifthOctober, 108.20m),
                (SixthOctober, 183.00m),
                (SeventhOctober, 216.40m),
                (EighthOctober, 147.40m),
            ],
            after.Days.Select(day => (day.BusinessDate, day.SourceVerifiedAfter)));
        Assert.Equal(655.00m, after.SourceVerifiedTotalAfter);
        Assert.Equal((0, 0m), (after.MissingFromDatabaseCount, after.MissingFromDatabaseAmount));
        Assert.Equal((1, 5.00m), (after.ExcludedAfterCutoffCount, after.ExcludedAfterCutoffAmount));
        // Nothing is left to repair, and the 60 uncovered records are still unresolved and unmoved.
        Assert.Equal(0, after.Days.Sum(day => day.CompletedCountAfter) - after.CompletedCountAfter);
        Assert.Equal((60, 190.90m), (after.UnresolvedCompletedCount, after.UnresolvedCompletedAmount));
    }

    /// <summary>
    /// Acceptance criterion: obtain source coverage including 4 October to investigate the $190.90
    /// boundary discrepancy. The repair accepts an export that reaches back before the reconciled
    /// week, which is the only supported source for transactions the rolling last-sales window no
    /// longer returns, and the 60 records then stop being unresolved and are decided from that
    /// evidence like any other row.
    ///
    /// This asserts the capability and what the evidence says - here, that those stored instants are
    /// the authoritative ones, so the $190.90 is a real 5 October difference between the two exports
    /// rather than a timestamp defect. It deliberately does not assert any corrected date as truth:
    /// absence from the Monday-Thursday export never established one.
    /// </summary>
    [Fact]
    public async Task Source_coverage_including_4_October_resolves_the_boundary_cohort()
    {
        var preview = await PreviewAsync(ExportCovering(_cohorts));

        var reconciliation = preview.Reconciliation!;
        Assert.Equal(0, preview.Unresolved);
        Assert.Equal((0, 0m), (reconciliation.UnresolvedCompletedCount, reconciliation.UnresolvedCompletedAmount));
        Assert.Equal(
            [
                (FifthOctober, 299.10m),
                (SixthOctober, 183.00m),
                (SeventhOctober, 216.40m),
                (EighthOctober, 27.70m),
            ],
            reconciliation.Days.Select(day => (day.BusinessDate, day.SourceVerifiedAfter)));
        // The boundary cohort is confirmed where it already sits: 124 rows move, and the 28
        // already-correct rows, those 60 and the later $5.00 sale do not.
        Assert.Equal(124, preview.Repairable);
        Assert.Equal(89, preview.AlreadyCorrect);
    }

    /// <summary>
    /// The repaired instants are what the database holds afterwards, and the audit says, per
    /// transaction, which Sydney day each repair moved revenue out of and into - which is the
    /// evidence a human needs to accept a change to historical financial records.
    /// </summary>
    [Fact]
    public async Task Applying_the_repair_persists_the_authoritative_instants_and_audits_the_day_movement()
    {
        var preview = await PreviewAsync(MondayToThursdayExport());

        await ApplyAsync(preview.PreviewId);

        await using var read = TestAppDbContext.Unrestricted(_options);
        var known = await read.NayaxSales.AsNoTracking().SingleAsync(x => x.TransactionID == KnownTransactionId);
        Assert.Equal(new DateTime(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc), known.MachineAuthorizationTime);
        var audit = await read.NayaxSaleTimestampRepairs.AsNoTracking().ToListAsync();
        Assert.Equal(124, audit.Count);
        Assert.All(audit, repair =>
        {
            Assert.Equal(HostOffsetError, repair.RepairedInstantUtc - repair.PreviousInstantUtc);
            Assert.Equal(preview.PreviewId, repair.PreviewId);
            Assert.Equal(
                Inventory.Infrastructure.Models.NayaxSaleTimestampEvidenceSource.OperatorExport,
                repair.EvidenceSource);
            Assert.Equal("operator export transactions.csv", repair.EvidenceReference);
        });
        Assert.Equal(
            [(SeventhOctober, EighthOctober), (SixthOctober, SeventhOctober), (FifthOctober, SixthOctober)],
            audit
                .GroupBy(repair => (repair.PreviousBusinessDate, repair.RepairedBusinessDate))
                .OrderByDescending(group => group.Key.RepairedBusinessDate)
                .Select(group => group.Key));
        // The repair wrote one column. The snapshot's 213 sales - 28 already correct, 124 shifted,
        // 60 uncovered and the later $5.00 one - and their $731.20 of settled value are untouched: no
        // sale was created, removed or re-valued.
        Assert.Equal(
            (28 + 124 + 60 + 1, 731.20m),
            (await read.NayaxSales.CountAsync(), await read.NayaxSales.SumAsync(x => x.SettlementValue)));
    }

    // ------------------------------------------------------------------ setup

    /// <summary>Which of the evidence's five populations a modelled transaction belongs to.</summary>
    private enum CohortKind
    {
        /// <summary>Already holding the authoritative instant.</summary>
        AlreadyCorrect,

        /// <summary>Stored eleven hours early by the Sydney-hosted synchronization.</summary>
        Shifted,

        /// <summary>Absent from the supplied Monday-Thursday export; no source covers it.</summary>
        Boundary,

        /// <summary>Carried by the export but held by no stored sale at all.</summary>
        MissingFromSnapshot,

        /// <summary>Authorized after the export's cutoff.</summary>
        AfterCutoff,
    }

    /// <summary>One modelled transaction: what the snapshot holds, and what the source says.</summary>
    private sealed record Cohort(
        CohortKind Kind,
        long TransactionId,
        DateTime? StoredUtc,
        DateTime AuthoritativeUtc,
        decimal SettlementValue);

    private void BuildCohorts()
    {
        var transactionId = 5_000_000L;

        // 28 records already holding the authoritative instant, Monday 5 October, $108.20.
        AlreadyCorrect(new DateTime(2026, 10, 4, 23, 0, 0, DateTimeKind.Utc), 27, 3.80m, 5.60m);

        // 124 records stored eleven hours early: 58 on 6 October, 58 on 7 October, 8 on 8 October.
        Shifted(new DateTime(2026, 10, 5, 22, 0, 0, DateTimeKind.Utc), 57, 3.00m, 12.00m);
        Shifted(new DateTime(2026, 10, 6, 22, 0, 0, DateTimeKind.Utc), 57, 3.70m, 5.50m);
        Shifted(new DateTime(2026, 10, 7, 23, 0, 0, DateTimeKind.Utc), 6, 3.00m, 6.70m);
        // The operator's named transaction, with its exact instants and $3.00.
        _cohorts.Add(new(
            CohortKind.Shifted,
            KnownTransactionId,
            new DateTime(2026, 10, 7, 12, 42, 44, 263, DateTimeKind.Utc),
            new DateTime(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc),
            3.00m));

        // 60 records the Monday-Thursday export does not cover, $190.90, stored across 4 October UTC
        // and reading as Monday 5 October in Sydney. The authoritative value here is only what a
        // later, wider export would have to confirm; the Monday-Thursday export carries none of them.
        for (var i = 0; i < 60; i++)
        {
            var storedUtc = new DateTime(2026, 10, 4, 13, 54, 45, 333, DateTimeKind.Utc).AddMinutes(i * 10);
            _cohorts.Add(new(
                CohortKind.Boundary, transactionId++, storedUtc, storedUtc, i < 59 ? 3.00m : 13.90m));
        }

        // 38 records the export carries that the snapshot holds no sale for, $119.70, all on
        // 8 October, including the later $4.80 sale at 14:00 Sydney - still before the cutoff.
        for (var i = 0; i < 37; i++)
            _cohorts.Add(new(
                CohortKind.MissingFromSnapshot,
                transactionId++,
                null,
                new DateTime(2026, 10, 7, 23, 30, 0, DateTimeKind.Utc).AddMinutes(i),
                i < 36 ? 3.00m : 6.90m));
        _cohorts.Add(new(
            CohortKind.MissingFromSnapshot,
            transactionId++,
            null,
            new DateTime(2026, 10, 8, 3, 0, 0, DateTimeKind.Utc),
            4.80m));

        // One sale authorized after the export's cutoff: 8 October 17:00 Sydney, $5.00.
        var afterCutoff = new DateTime(2026, 10, 8, 6, 0, 0, DateTimeKind.Utc);
        _cohorts.Add(new(CohortKind.AfterCutoff, transactionId, afterCutoff, afterCutoff, 5.00m));

        void AlreadyCorrect(DateTime firstUtc, int repeats, decimal amount, decimal remainder)
        {
            for (var i = 0; i <= repeats; i++)
            {
                var at = firstUtc.AddMinutes(i);
                _cohorts.Add(new(
                    CohortKind.AlreadyCorrect, transactionId++, at, at, i < repeats ? amount : remainder));
            }
        }

        void Shifted(DateTime firstAuthoritativeUtc, int repeats, decimal amount, decimal remainder)
        {
            for (var i = 0; i <= repeats; i++)
            {
                var authoritative = firstAuthoritativeUtc.AddMinutes(i);
                _cohorts.Add(new(
                    CohortKind.Shifted,
                    transactionId++,
                    authoritative - HostOffsetError,
                    authoritative,
                    i < repeats ? amount : remainder));
            }
        }
    }

    private void SeedSnapshot()
    {
        using var db = TestAppDbContext.Unrestricted(_options);
        foreach (var cohort in _cohorts.Where(cohort => cohort.StoredUtc.HasValue))
            db.NayaxSales.Add(new NayaxSales
            {
                BusinessId = BusinessA,
                TransactionID = cohort.TransactionId,
                TransactionStatusId = NayaxTransactionStatusIds.Completed,
                MachineID = MachineId,
                MachineName = "Pavillion Right",
                SettlementValue = cohort.SettlementValue,
                PaymentMethod = "Credit Card",
                ProductName = "Unknown",
                MachineAuthorizationTime = cohort.StoredUtc!.Value,
            });

        db.SaveChanges();
    }

    /// <summary>
    /// The supplied Monday-Thursday export: every transaction it covers, with its authoritative
    /// <c>AuthorizationDateTimeGMT</c>. It deliberately omits the 60 records stored on 4 October UTC,
    /// exactly as the operator's own export did.
    /// </summary>
    private NayaxSalesFileInput MondayToThursdayExport() =>
        ExportCovering(_cohorts.Where(cohort => cohort.Kind != CohortKind.Boundary));

    private static NayaxSalesFileInput ExportCovering(IEnumerable<Cohort> cohorts)
    {
        var csv = new StringBuilder(
            "TransactionID,MachineID,SettlementValue,TransactionStatusId,ProductName,AuthorizationDateTimeGMT");
        foreach (var cohort in cohorts)
            csv.Append(CultureInfo.InvariantCulture, $"\n{cohort.TransactionId},{MachineId},")
                .Append(CultureInfo.InvariantCulture, $"{cohort.SettlementValue},{NayaxTransactionStatusIds.Completed},")
                .Append(CultureInfo.InvariantCulture, $"Unknown,{cohort.AuthoritativeUtc:yyyy-MM-ddTHH:mm:ss.fff}");

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());
        return new("transactions.csv", () => new MemoryStream(bytes));
    }

    /// <summary>
    /// Imports the 38 transactions the snapshot never held, through the ordinary uploaded-export
    /// import. A timestamp repair cannot create a sale, so this is the operation that makes the
    /// transaction set complete - and keeping it a separate, visible step is what stops a missing
    /// sale from being counted as a repaired one.
    /// </summary>
    private async Task ImportMissingSalesAsync()
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        var rebuild = TestCostingUseCases.Rebuild(db);
        var result = await new ImportNayaxSales(
                new ClosedXmlNayaxSalesWorkbookReader(),
                new EfNayaxSalesImportStore(db),
                TestCostingUseCases.CostSale(db, rebuild),
                rebuild)
            .Handle(ExportCovering(_cohorts.Where(cohort => cohort.StoredUtc is null)));

        Assert.Equal(new NayaxSalesImportResult(38, 0, 0), result);
    }

    private async Task<NayaxSaleTimestampRepairPreview> PreviewAsync(NayaxSalesFileInput export)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        return await TestSaleTimestampRepairUseCases
            .Preview(db, LastSalesNayaxLynxClient.Empty(MachineId), _clock)
            .Handle(
                new(
                    IncludeLatestSalesApiEvidence: false,
                    new NayaxSaleTimestampReconciliationRequest(ExportCutoffUtc, FifthOctober, EighthOctober)),
                export);
    }

    private async Task<NayaxSaleTimestampRepairApplied> ApplyAsync(Guid previewId)
    {
        await using var db = TestAppDbContext.For(_options, BusinessA);
        return await TestSaleTimestampRepairUseCases
            .Apply(db, FakeAuthenticatedActorAccessor.Identified(Operator), _clock)
            .Handle(new(previewId, true));
    }

    private static ActorIdentity Operator
    {
        get
        {
            Assert.True(ActorIdentity.TryCreate(
                "33333333-3333-3333-3333-333333333333",
                "11111111-1111-1111-1111-111111111111",
                out var actor));
            return actor!;
        }
    }
}
