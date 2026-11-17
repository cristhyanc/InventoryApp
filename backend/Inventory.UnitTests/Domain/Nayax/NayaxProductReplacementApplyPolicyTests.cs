using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// A product replacement (Event 510) is never a refill (issue #1325): the apply policy refuses it
/// whatever its match, quantity or storage, until applying a replacement exists (#1326). The
/// established settled answers still come first, so an already-recorded replacement replays as
/// reconciled.
/// </summary>
public class NayaxProductReplacementApplyPolicyTests
{
    [Fact]
    public void A_replacement_is_refused_even_if_it_looked_like_a_matched_refill()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed,
            NayaxStockEventMatchStatus.Matched,
            needsReviewReason: null,
            matchedProductId: 5,
            parsedQuantity: 4,
            availableStorageQuantity: 100,
            eventCode: NayaxMachineAlertEventCodes.MachineProductChanged);

        Assert.Equal(NayaxStockEventApplyDecision.ProductReplacementNotApplicable, ruling.Decision);
        Assert.Equal(NayaxMachineStockApplyPolicy.ProductReplacementMessage, ruling.Message);
    }

    [Fact]
    public void A_replacement_as_imported_is_refused_as_a_replacement_not_as_needs_review()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed,
            NayaxStockEventMatchStatus.NeedsReview,
            needsReviewReason: null,
            matchedProductId: null,
            parsedQuantity: null,
            availableStorageQuantity: null,
            eventCode: NayaxMachineAlertEventCodes.MachineProductChanged);

        Assert.Equal(NayaxStockEventApplyDecision.ProductReplacementNotApplicable, ruling.Decision);
    }

    [Fact]
    public void An_already_recorded_replacement_replays_as_reconciled()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed,
            NayaxStockEventMatchStatus.NeedsReview,
            needsReviewReason: null,
            matchedProductId: null,
            parsedQuantity: null,
            availableStorageQuantity: null,
            duplicateResolution: NayaxDuplicateResolution.ReconciledManually,
            eventCode: NayaxMachineAlertEventCodes.MachineProductChanged);

        Assert.Equal(NayaxStockEventApplyDecision.ReconciledDuplicate, ruling.Decision);
    }

    [Fact]
    public void A_501_refill_is_unchanged()
    {
        var ruling = NayaxMachineStockApplyPolicy.Decide(
            NayaxStockEventProcessingStatus.Unprocessed,
            NayaxStockEventMatchStatus.Matched,
            needsReviewReason: null,
            matchedProductId: 5,
            parsedQuantity: 4,
            availableStorageQuantity: 100);

        Assert.Equal(NayaxStockEventApplyDecision.Apply, ruling.Decision);
    }
}
