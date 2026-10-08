using System.Globalization;
using Inventory.Domain.Exceptions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// Issue #472: the deterministic rules of the Nayax sale timestamp repair - which stored sale a
/// piece of authoritative source evidence repairs, which one it leaves visibly unresolved, and what
/// makes a previewed plan stale.
///
/// The cohorts are the operator's own 8 October 2026 evidence, sanitized: transaction 3564567268 on
/// machine 531595328 holds <c>2026-10-07 12:42:44.263</c> while its authoritative
/// <c>AuthorizationDateTimeGMT</c> is <c>2026-10-07T23:42:44.263</c>, eleven hours later, because the
/// live synchronization read an offset-free GMT value against the Sydney host's own offset. Of 152
/// matched records 28 already hold the authoritative instant and 124 are shifted, so a blanket
/// offset is wrong for the business as a whole - which is why every decision here is made per
/// transaction from its own evidence and never from an arithmetic difference.
/// </summary>
public class NayaxSaleTimestampRepairPolicyTests
{
    private const long TransactionId = 3564567268;
    private const long MachineId = 531595328;

    /// <summary>The stored instant the Sydney-hosted API wrote: eleven hours before the truth.</summary>
    private static readonly DateTime StoredUtc = new(2026, 10, 7, 12, 42, 44, 263, DateTimeKind.Utc);

    /// <summary>The authoritative instant, from <c>AuthorizationDateTimeGMT</c>.</summary>
    private static readonly DateTime AuthoritativeUtc = new(2026, 10, 7, 23, 42, 44, 263, DateTimeKind.Utc);

    [Fact]
    public void The_known_transaction_is_repaired_to_its_authoritative_GMT_instant()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide([Sale(StoredUtc)], [Evidence(AuthoritativeUtc)]);

        var decision = Assert.Single(decisions);
        Assert.Equal(NayaxSaleTimestampRepairOutcome.Repairable, decision.Outcome);
        Assert.Equal(AuthoritativeUtc, decision.RepairedInstantUtc);
        Assert.Equal(StoredUtc, decision.Sale.StoredInstantUtc);
        Assert.Null(decision.UnresolvedReason);
        Assert.Equal(NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi, decision.Evidence!.Source);
    }

    /// <summary>
    /// The mixed-cohort rule. The same evidence set holds an already-correct row, a wrongly shifted
    /// one and one whose stored value looks like a machine-local clock; each is decided from its own
    /// evidence, and the two that need no change are not moved by the other's difference.
    /// </summary>
    [Fact]
    public void Already_correct_local_looking_and_shifted_rows_are_decided_one_by_one()
    {
        var localLookingStored = new DateTime(2026, 10, 8, 10, 42, 44, 93, DateTimeKind.Utc);

        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [
                Sale(AuthoritativeUtc, transactionId: 1),
                Sale(StoredUtc, transactionId: 2),
                Sale(localLookingStored, transactionId: 3),
            ],
            [
                Evidence(AuthoritativeUtc, transactionId: 1),
                Evidence(AuthoritativeUtc, transactionId: 2),
                Evidence(AuthoritativeUtc, transactionId: 3),
            ]);

        Assert.Equal(
            [
                (1L, NayaxSaleTimestampRepairOutcome.AlreadyCorrect, (DateTime?)null),
                (2L, NayaxSaleTimestampRepairOutcome.Repairable, AuthoritativeUtc),
                (3L, NayaxSaleTimestampRepairOutcome.Repairable, AuthoritativeUtc),
            ],
            decisions.Select(x => (x.Sale.TransactionId, x.Outcome, x.RepairedInstantUtc)));
    }

    [Fact]
    public void A_sale_no_source_covers_is_left_unchanged_and_reported_unresolved()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide([Sale(StoredUtc)], []);

        var decision = Assert.Single(decisions);
        Assert.Equal(NayaxSaleTimestampRepairOutcome.Unresolved, decision.Outcome);
        Assert.Equal(NayaxSaleTimestampUnresolvedReason.NoSourceEvidence, decision.UnresolvedReason);
        Assert.Null(decision.RepairedInstantUtc);
        Assert.Null(decision.Evidence);
    }

    [Fact]
    public void Evidence_whose_authorization_value_could_not_be_read_repairs_nothing()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide([Sale(StoredUtc)], [Evidence(null)]);

        var decision = Assert.Single(decisions);
        Assert.Equal(NayaxSaleTimestampRepairOutcome.Unresolved, decision.Outcome);
        Assert.Equal(NayaxSaleTimestampUnresolvedReason.UnreadableEvidence, decision.UnresolvedReason);
        Assert.Null(decision.RepairedInstantUtc);
    }

    /// <summary>
    /// Two sources that disagree about one transaction's authorization instant are not a majority
    /// vote and not a "latest wins": the operator has to resolve the disagreement, so nothing is
    /// written and the row stays visibly unresolved.
    /// </summary>
    [Fact]
    public void Conflicting_evidence_for_one_transaction_repairs_nothing()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [Sale(StoredUtc)],
            [
                Evidence(AuthoritativeUtc),
                Evidence(AuthoritativeUtc.AddSeconds(1), source: NayaxSaleTimestampEvidenceSource.OperatorExport),
            ]);

        var decision = Assert.Single(decisions);
        Assert.Equal(NayaxSaleTimestampUnresolvedReason.ConflictingEvidence, decision.UnresolvedReason);
        Assert.Null(decision.RepairedInstantUtc);
    }

    [Fact]
    public void Repeated_identical_evidence_from_two_sources_is_not_a_conflict()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [Sale(StoredUtc)],
            [
                Evidence(AuthoritativeUtc),
                Evidence(AuthoritativeUtc, source: NayaxSaleTimestampEvidenceSource.OperatorExport),
            ]);

        Assert.Equal(NayaxSaleTimestampRepairOutcome.Repairable, Assert.Single(decisions).Outcome);
    }

    /// <summary>
    /// Transaction identity is verified, not assumed. A remote transaction id is unique only within
    /// the operator account that issued it, so evidence naming another machine is evidence about
    /// another sale and must never move this one.
    /// </summary>
    [Fact]
    public void Evidence_naming_a_different_machine_repairs_nothing()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [Sale(StoredUtc)],
            [Evidence(AuthoritativeUtc) with { MachineId = MachineId + 1 }]);

        var decision = Assert.Single(decisions);
        Assert.Equal(NayaxSaleTimestampUnresolvedReason.MachineMismatch, decision.UnresolvedReason);
        Assert.Null(decision.RepairedInstantUtc);
    }

    [Theory]
    [InlineData("3.00", NayaxSaleTimestampRepairOutcome.Repairable, null)]
    [InlineData("3.01", NayaxSaleTimestampRepairOutcome.Repairable, null)]
    [InlineData("2.99", NayaxSaleTimestampRepairOutcome.Repairable, null)]
    [InlineData("3.02", NayaxSaleTimestampRepairOutcome.Unresolved, NayaxSaleTimestampUnresolvedReason.AmountMismatch)]
    [InlineData("4.80", NayaxSaleTimestampRepairOutcome.Unresolved, NayaxSaleTimestampUnresolvedReason.AmountMismatch)]
    public void Evidence_is_identity_checked_against_the_stored_amount_within_a_cent(
        string evidenceAmount,
        NayaxSaleTimestampRepairOutcome outcome,
        NayaxSaleTimestampUnresolvedReason? reason)
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [Sale(StoredUtc)],
            [
                Evidence(AuthoritativeUtc) with
                {
                    SettlementValue = decimal.Parse(evidenceAmount, CultureInfo.InvariantCulture),
                },
            ]);

        var decision = Assert.Single(decisions);
        Assert.Equal(outcome, decision.Outcome);
        Assert.Equal(reason, decision.UnresolvedReason);
    }

    [Fact]
    public void Evidence_carrying_no_amount_is_still_usable()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide(
            [Sale(StoredUtc)],
            [Evidence(AuthoritativeUtc) with { SettlementValue = null }]);

        Assert.Equal(NayaxSaleTimestampRepairOutcome.Repairable, Assert.Single(decisions).Outcome);
    }

    /// <summary>
    /// Applying the same verified repair again is a no-op: the second preview sees the repaired
    /// instant, finds the evidence already agrees with it, and reports nothing left to write.
    /// </summary>
    [Fact]
    public void Re_deciding_after_a_repair_reports_nothing_left_to_do()
    {
        var first = NayaxSaleTimestampRepairPolicy.Decide([Sale(StoredUtc)], [Evidence(AuthoritativeUtc)]);
        var repaired = Sale(first.Single().RepairedInstantUtc!.Value);

        var second = NayaxSaleTimestampRepairPolicy.Decide([repaired], [Evidence(AuthoritativeUtc)]);

        Assert.Equal(NayaxSaleTimestampRepairOutcome.AlreadyCorrect, Assert.Single(second).Outcome);
    }

    [Fact]
    public void Evidence_for_a_transaction_this_business_does_not_hold_decides_nothing()
    {
        var decisions = NayaxSaleTimestampRepairPolicy.Decide([], [Evidence(AuthoritativeUtc)]);

        Assert.Empty(decisions);
    }

    [Fact]
    public void A_draft_may_be_applied_once_and_only_before_it_expires()
    {
        var now = new DateTime(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc);

        var applied = Assert.Throws<DomainValidationException>(() =>
            NayaxSaleTimestampRepairPolicy.EnsureDraftUsable(now.AddMinutes(-5), now.AddHours(1), now));
        var expired = Assert.Throws<DomainValidationException>(() =>
            NayaxSaleTimestampRepairPolicy.EnsureDraftUsable(null, now.AddTicks(-1), now));
        NayaxSaleTimestampRepairPolicy.EnsureDraftUsable(null, now, now);

        Assert.Equal("This sale timestamp repair preview has already been applied.", applied.Message);
        Assert.Equal(
            "The sale timestamp repair preview expired. Run the preview again and review it before applying.",
            expired.Message);
    }

    /// <summary>
    /// The stale-state rule: the previewed sales, their stored instants, machines, amounts and
    /// statuses are compared against the apply's own authoritative read, so a sale imported, moved,
    /// restatused or repaired in between is refused rather than repaired against a plan nobody
    /// approved.
    /// </summary>
    [Theory]
    [InlineData("instant")]
    [InlineData("machine")]
    [InlineData("amount")]
    [InlineData("status")]
    [InlineData("added")]
    [InlineData("removed")]
    public void A_previewed_plan_is_refused_when_the_stored_sales_changed_underneath_it(string change)
    {
        StoredNayaxSale[] previewed = [Sale(StoredUtc, transactionId: 1), Sale(StoredUtc, transactionId: 2)];
        StoredNayaxSale[] current = change switch
        {
            "instant" => [previewed[0] with { StoredInstantUtc = StoredUtc.AddHours(1) }, previewed[1]],
            "machine" => [previewed[0] with { MachineId = MachineId + 1 }, previewed[1]],
            "amount" => [previewed[0] with { SettlementValue = 4.80m }, previewed[1]],
            "status" => [previewed[0] with { TransactionStatusId = NayaxTransactionStatusIds.Refunded }, previewed[1]],
            "added" => [.. previewed, Sale(StoredUtc, transactionId: 3)],
            _ => [previewed[0]],
        };

        var exception = Assert.Throws<DomainValidationException>(() =>
            NayaxSaleTimestampRepairPolicy.EnsureStoredSalesUnchanged(previewed, current));

        Assert.Equal(
            "The stored Nayax sales changed after this preview, so nothing was repaired. "
                + "Run the preview again and review it before applying.",
            exception.Message);
    }

    [Fact]
    public void An_unchanged_plan_passes_whatever_order_the_sales_are_read_in()
    {
        StoredNayaxSale[] previewed = [Sale(StoredUtc, transactionId: 1), Sale(StoredUtc, transactionId: 2)];

        NayaxSaleTimestampRepairPolicy.EnsureStoredSalesUnchanged(previewed, [previewed[1], previewed[0]]);
    }

    private static StoredNayaxSale Sale(DateTime storedUtc, long transactionId = TransactionId) =>
        new(
            transactionId,
            MachineId,
            "Pavillion Right",
            3.00m,
            NayaxTransactionStatusIds.Completed,
            storedUtc,
            NayaxProductId: 77,
            ProductName: "Coke Zero");

    private static NayaxSaleTimestampEvidence Evidence(
        DateTime? authorizationInstantUtc,
        long transactionId = TransactionId,
        NayaxSaleTimestampEvidenceSource source = NayaxSaleTimestampEvidenceSource.NayaxLastSalesApi) =>
        new(transactionId, MachineId, authorizationInstantUtc, 3.00m, source, "lastSales 2026-10-08T02:00:00Z");
}
