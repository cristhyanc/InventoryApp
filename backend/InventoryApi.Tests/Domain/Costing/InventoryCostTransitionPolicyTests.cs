using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Xunit;
using PersistedBaselineSource = InventoryApi.Models.InventoryCostBaselineSource;

namespace InventoryApi.Tests.Domain.Costing;

/// <summary>
/// The deterministic inventory-cost transition rules (issue #298, child 4 of #149), moved unchanged
/// from the former <c>InventoryCostTransitionService</c>: machine-slot stock, the opening costing
/// position, and draft/stale-preview validation, each with the exact caller-facing message.
/// </summary>
public class InventoryCostTransitionPolicyTests
{
    [Fact]
    public void Baseline_source_mirrors_the_persisted_enum_member_for_member_and_value_for_value() =>
        Assert.Equal(
            Enum.GetValues<PersistedBaselineSource>().Select(x => (x.ToString(), (int)x)),
            Enum.GetValues<InventoryCostBaselineSource>().Select(x => (x.ToString(), (int)x)));

    [Fact]
    public void A_negative_opening_cost_is_rejected_and_zero_is_allowed()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureValidOpeningCost(-0.01m));

        Assert.Equal("The opening average unit cost cannot be negative.", exception.Message);
        InventoryCostTransitionPolicy.EnsureValidOpeningCost(0m);
    }

    [Fact]
    public void Machine_quantity_sums_par_minus_missing_stock_over_every_slot()
    {
        Assert.Equal(11, InventoryCostTransitionPolicy.MachineQuantity(10, "Machine A", [new(10, 4), new(8, 3)]));
        Assert.Equal(0, InventoryCostTransitionPolicy.MachineQuantity(10, "Machine A", []));
        Assert.Equal(0, InventoryCostTransitionPolicy.MachineQuantity(10, "Machine A", [new(5, 5)]));
        Assert.Equal(5, InventoryCostTransitionPolicy.MachineQuantity(10, "Machine A", [new(5, 0)]));
    }

    [Theory]
    [InlineData(null, 1)]
    [InlineData(5, null)]
    public void A_slot_missing_par_or_missing_stock_is_rejected(int? par, int? missing)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.MachineQuantity(10, "Machine A", [new(par, missing)]));

        Assert.Equal(
            "Nayax stock is incomplete for product 10 in machine Machine A: PAR and MissingStockByMDB are required.",
            exception.Message);
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(5, -1)]
    public void A_slot_outside_zero_to_par_is_rejected(int par, int missing)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.MachineQuantity(10, "7", [new(par, missing)]));

        Assert.Equal("Nayax stock is invalid for product 10 in machine 7.", exception.Message);
    }

    [Fact]
    public void Opening_is_home_plus_machines_valued_at_the_opening_cost_with_the_legacy_discrepancy()
    {
        var opening = InventoryCostTransitionPolicy.CalculateOpening(19, [6, 5], 1.25m, 31);

        Assert.Equal(11, opening.MachineStockQuantity);
        Assert.Equal(30, opening.OpeningCostingQuantity);
        Assert.Equal(37.50m, opening.InventoryValue);
        Assert.Equal(-12, opening.LegacyPhysicalDiscrepancy);
        Assert.Equal(
            "Legacy physical movement history replayed to 31, while verified home stock was 19; discrepancy -12 was retired at cutover without altering legacy movements.",
            opening.DataQualityNote);
    }

    [Fact]
    public void A_reconciled_legacy_replay_is_noted_as_reconciled()
    {
        var opening = InventoryCostTransitionPolicy.CalculateOpening(4, [], 2m, 4);

        Assert.Equal(0, opening.LegacyPhysicalDiscrepancy);
        Assert.Equal(8m, opening.InventoryValue);
        Assert.Equal("Legacy physical movement history reconciled at transition.", opening.DataQualityNote);
    }

    [Fact]
    public void An_opening_quantity_outside_int_range_overflows_rather_than_wrapping() =>
        Assert.Throws<OverflowException>(() =>
            InventoryCostTransitionPolicy.CalculateOpening(int.MaxValue, [1], 1m, 0));

    [Fact]
    public void A_draft_is_usable_until_it_expires_and_only_once()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        InventoryCostTransitionPolicy.EnsureDraftUsable(null, now, now);

        var expired = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureDraftUsable(null, now.AddTicks(-1), now));
        var applied = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureDraftUsable(now, now.AddMinutes(5), now));

        Assert.Equal("The transition preview expired. Run the preview again.", expired.Message);
        Assert.Equal("This transition preview has already been applied.", applied.Message);
    }

    [Fact]
    public void An_unchanged_preview_is_accepted() =>
        InventoryCostTransitionPolicy.EnsureUnchanged(State(), State());

    [Theory]
    [InlineData("home")]
    [InlineData("replay")]
    [InlineData("machine-quantity")]
    [InlineData("machine-set")]
    public void A_changed_inventory_input_rejects_the_preview(string change)
    {
        var current = change switch
        {
            "home" => State() with { HomeStockQuantity = 20 },
            "replay" => State() with { LegacyReplayedPhysicalQuantity = 30 },
            "machine-quantity" => State() with { MachineStocks = [new(1, 6), new(2, 4)] },
            _ => State() with { MachineStocks = [new(1, 6), new(3, 5)] },
        };

        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureUnchanged(State(), current));

        Assert.Equal("Inventory data changed after the preview. Run the preview again before confirming.", exception.Message);
    }

    [Fact]
    public void Machine_stock_order_does_not_matter() =>
        InventoryCostTransitionPolicy.EnsureUnchanged(State(), State() with { MachineStocks = [new(2, 5), new(1, 6)] });

    [Theory]
    [InlineData("machine-total")]
    [InlineData("opening")]
    [InlineData("value")]
    public void A_tampered_preview_calculation_is_rejected(string change)
    {
        var expected = change switch
        {
            "machine-total" => State() with { MachineStockQuantity = 12 },
            "opening" => State() with { OpeningCostingQuantity = 31 },
            _ => State() with { InventoryValue = 37.51m },
        };

        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureUnchanged(expected, State()));

        Assert.Equal("The confirmed transition values do not match the preview calculation.", exception.Message);
    }

    [Fact]
    public void A_batch_with_a_changed_product_list_is_rejected()
    {
        var added = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureBatchUnchanged([State()], [State(), State(productId: 20)]));
        var replaced = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureBatchUnchanged([State()], [State(productId: 20)]));

        Assert.Equal("The eligible product list changed after the preview. Run it again.", added.Message);
        Assert.Equal("The eligible product list changed after the preview. Run it again.", replaced.Message);
    }

    [Fact]
    public void A_batch_with_a_changed_average_cost_names_the_product()
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureBatchUnchanged(
                [State()],
                [State() with { AverageUnitCost = 1.30m, InventoryValue = 39m }]));

        Assert.Equal("The average unit cost for Snack changed after the preview. Run it again.", exception.Message);
    }

    [Fact]
    public void A_batch_applies_the_single_product_stale_check_to_every_product()
    {
        InventoryCostTransitionPolicy.EnsureBatchUnchanged([State(), State(productId: 20)], [State(productId: 20), State()]);

        var exception = Assert.Throws<DomainValidationException>(() =>
            InventoryCostTransitionPolicy.EnsureBatchUnchanged(
                [State(), State(productId: 20)],
                [State(), State(productId: 20) with { HomeStockQuantity = 1 }]));

        Assert.Equal("Inventory data changed after the preview. Run the preview again before confirming.", exception.Message);
    }

    private static InventoryCostTransitionState State(long productId = 10) =>
        new(productId, "Snack", 19, [new(1, 6), new(2, 5)], 11, 30, 1.25m, 37.50m, 31);
}
