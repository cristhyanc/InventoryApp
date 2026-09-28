using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The deterministic Sync Restock rules (issue #183): resolving a parsed alert to a local product,
/// reading its storage impact, detecting a plausible manual duplicate, and deciding whether it may
/// be applied. These are pure rules with no EF Core, HTTP, or Nayax dependency.
/// </summary>
public class NayaxMachineStockSyncPoliciesTests
{
    private const long MachineId = 900;
    private static readonly DateTime EventTime = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static IReadOnlyDictionary<int, long> Mdb(params (int Mdb, long NayaxProductId)[] positions) =>
        positions.ToDictionary(p => p.Mdb, p => p.NayaxProductId);

    private static IReadOnlyDictionary<long, NayaxStockSyncProduct> Catalogue(
        params NayaxStockSyncProduct[] products) =>
        products.ToDictionary(p => p.Id);

    #region Matching

    [Fact]
    public void Matches_by_machine_and_mdb_when_the_name_agrees()
    {
        var resolution = NayaxMachineStockMatchPolicy.Resolve(
            MachineId,
            new ParsedNayaxStockAdjustment(13, "25g Nobby's Beef Jerky Hot", 2),
            Mdb((13, 200)),
            Catalogue(new NayaxStockSyncProduct(200, "25g Nobby's Beef Jerky Hot", 20)));

        Assert.Equal(NayaxStockEventMatchStatus.Matched, resolution.MatchStatus);
        Assert.Equal(200, resolution.MatchedProductId);
        Assert.Null(resolution.NeedsReviewReason);
    }

    [Fact]
    public void Tolerates_case_whitespace_and_a_parenthetical_suffix_in_the_alert_name()
    {
        var resolution = NayaxMachineStockMatchPolicy.Resolve(
            MachineId,
            new ParsedNayaxStockAdjustment(7, "coke 375ML", 1),
            Mdb((7, 200)),
            Catalogue(new NayaxStockSyncProduct(200, "Coke 375mL (3.50)", 10)));

        Assert.Equal(NayaxStockEventMatchStatus.Matched, resolution.MatchStatus);
    }

    [Fact]
    public void An_unknown_mdb_needs_review_and_resolves_no_product()
    {
        var resolution = NayaxMachineStockMatchPolicy.Resolve(
            MachineId,
            new ParsedNayaxStockAdjustment(99, "Coke 375mL", 1),
            Mdb((7, 200)),
            Catalogue(new NayaxStockSyncProduct(200, "Coke 375mL", 10)));

        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, resolution.MatchStatus);
        Assert.Null(resolution.MatchedProductId);
        Assert.Contains("MDB 99", resolution.NeedsReviewReason);
    }

    [Fact]
    public void An_mdb_mapped_to_an_unknown_local_product_needs_review()
    {
        var resolution = NayaxMachineStockMatchPolicy.Resolve(
            MachineId,
            new ParsedNayaxStockAdjustment(7, "Coke 375mL", 1),
            Mdb((7, 999)),
            Catalogue(new NayaxStockSyncProduct(200, "Coke 375mL", 10)));

        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, resolution.MatchStatus);
        Assert.Null(resolution.MatchedProductId);
        Assert.Contains("999", resolution.NeedsReviewReason);
    }

    [Fact]
    public void A_material_name_mismatch_needs_review_but_still_reports_the_mapped_product()
    {
        var resolution = NayaxMachineStockMatchPolicy.Resolve(
            MachineId,
            new ParsedNayaxStockAdjustment(7, "Sprite 375mL", 1),
            Mdb((7, 200)),
            Catalogue(new NayaxStockSyncProduct(200, "Coke 375mL", 10)));

        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, resolution.MatchStatus);
        Assert.Equal(200, resolution.MatchedProductId);
        Assert.Contains("mismatch", resolution.NeedsReviewReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unparsed_alert_needs_review_and_keeps_the_parser_failure_reason()
    {
        var resolution = NayaxMachineStockMatchPolicy.Unparsed("EventData is empty.");

        Assert.Equal(NayaxStockEventMatchStatus.NeedsReview, resolution.MatchStatus);
        Assert.Null(resolution.MatchedProductId);
        Assert.Equal("EventData is empty.", resolution.NeedsReviewReason);
    }

    #endregion

    #region Storage impact

    [Theory]
    [InlineData(2, 20, false, null)]
    [InlineData(20, 20, false, null)] // exactly available is not insufficient
    [InlineData(5, 4, true, 1)]
    public void A_positive_refill_reports_whether_storage_covers_it(
        int quantity, int available, bool insufficient, int? unaccounted)
    {
        var impact = NayaxMachineStockImpactPolicy.Evaluate(
            NayaxStockEventMatchStatus.Matched, quantity, available);

        Assert.True(impact.IsPositiveRefill);
        Assert.False(impact.IsDiscrepancy);
        Assert.Equal(insufficient, impact.IsInsufficientStorage);
        Assert.Equal(unaccounted, impact.UnaccountedDifference);
    }

    [Fact]
    public void A_negative_adjustment_is_a_discrepancy_and_never_a_refill()
    {
        var impact = NayaxMachineStockImpactPolicy.Evaluate(NayaxStockEventMatchStatus.Matched, -3, 10);

        Assert.False(impact.IsPositiveRefill);
        Assert.True(impact.IsDiscrepancy);
        Assert.False(impact.IsInsufficientStorage);
    }

    [Fact]
    public void A_needs_review_event_is_never_a_positive_refill()
    {
        var impact = NayaxMachineStockImpactPolicy.Evaluate(NayaxStockEventMatchStatus.NeedsReview, 5, 1);

        Assert.False(impact.IsPositiveRefill);
        Assert.False(impact.IsInsufficientStorage);
        Assert.Null(impact.UnaccountedDifference);
    }

    #endregion

    #region Duplicate detection

    [Fact]
    public void A_matching_manual_refill_inside_the_window_is_a_possible_duplicate()
    {
        var duplicate = NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
            200, 4, EventTime,
            [new ManualRefillEvidence(200, -4, EventTime.AddHours(-1))]);

        Assert.NotNull(duplicate);
    }

    [Fact]
    public void A_matching_manual_refill_outside_the_window_is_not_a_duplicate()
    {
        var duplicate = NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
            200, 4, EventTime,
            [new ManualRefillEvidence(200, -4, EventTime.AddDays(-30))]);

        Assert.Null(duplicate);
    }

    [Fact]
    public void A_nearby_manual_movement_of_another_product_or_quantity_is_not_a_duplicate()
    {
        var duplicate = NayaxMachineStockDuplicatePolicy.FindPossibleDuplicate(
            200, 4, EventTime,
            [
                new ManualRefillEvidence(201, -4, EventTime),   // different product
                new ManualRefillEvidence(200, -6, EventTime),   // different quantity
            ]);

        Assert.Null(duplicate);
    }

    #endregion

    #region Apply decision

    [Fact]
    public void A_validated_positive_refill_within_storage_may_be_applied()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20);

        Assert.Equal(NayaxStockEventApplyDecision.Apply, ruling.Decision);
    }

    [Fact]
    public void An_already_applied_event_is_never_applied_again()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Applied, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20);

        Assert.Equal(NayaxStockEventApplyDecision.AlreadyApplied, ruling.Decision);
    }

    [Fact]
    public void A_needs_review_event_reports_its_own_reason()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed,
            NayaxStockEventMatchStatus.NeedsReview,
            "MDB 99 is not a known position on machine 900.",
            null, 8, null);

        Assert.Equal(NayaxStockEventApplyDecision.NeedsReview, ruling.Decision);
        Assert.Equal("MDB 99 is not a known position on machine 900.", ruling.Message);
    }

    [Fact]
    public void A_negative_adjustment_is_never_applied_automatically()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, -3, 10);

        Assert.Equal(NayaxStockEventApplyDecision.NegativeDiscrepancy, ruling.Decision);
    }

    [Fact]
    public void A_refill_exceeding_storage_is_refused_whole_rather_than_partially_applied()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 5, 4);

        Assert.Equal(NayaxStockEventApplyDecision.InsufficientStorage, ruling.Decision);
        Assert.Contains("only 4 is available", ruling.Message);
    }

    [Fact]
    public void A_matched_product_that_no_longer_exists_is_reported_rather_than_applied()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 5, null);

        Assert.Equal(NayaxStockEventApplyDecision.MatchedProductMissing, ruling.Decision);
    }

    #endregion

    #region Duplicate resolution gate (issue #196)

    [Fact]
    public void A_flagged_possible_duplicate_is_never_applied_through_the_ordinary_path_until_resolved()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20,
            isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.None);

        Assert.Equal(NayaxStockEventApplyDecision.DuplicateRequiresResolution, ruling.Decision);
    }

    [Fact]
    public void An_event_reconciled_as_recorded_manually_is_never_applied_again()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20,
            isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.ReconciledManually);

        Assert.Equal(NayaxStockEventApplyDecision.ReconciledDuplicate, ruling.Decision);
    }

    [Fact]
    public void A_duplicate_explicitly_resolved_as_a_separate_restock_applies_normally()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20,
            isPossibleDuplicate: true, duplicateResolution: NayaxDuplicateResolution.AppliedAsSeparateRestock);

        Assert.Equal(NayaxStockEventApplyDecision.Apply, ruling.Decision);
    }

    [Fact]
    public void A_non_duplicate_event_is_unaffected_by_the_duplicate_gate()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed, NayaxStockEventMatchStatus.Matched, null, 200, 8, 20);

        Assert.Equal(NayaxStockEventApplyDecision.Apply, ruling.Decision);
    }

    #endregion
}
