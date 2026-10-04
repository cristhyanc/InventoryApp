using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Xunit;

namespace InventoryApi.Tests.Domain.Costing;

/// <summary>
/// The costing-repair rules (issue #359). A repair is the only costing-only historical acquisition
/// the ledger accepts, so every one of these checks is what keeps it from becoming a way to invent
/// inventory value: positive quantity, non-negative cost, a reason a human can audit, a placement
/// after the transition cutoff, and a placement the replay actually reaches before the sale it is
/// meant to cover.
/// </summary>
public class CostingRepairPolicyTests
{
    private static readonly DateTime EffectiveAt = new(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
    private const string Reason = "Opening stock for the 2025 cutover was never recorded.";

    [Fact]
    public void A_valid_repair_keeps_its_values_and_derives_its_total()
    {
        var repair = CostingRepairPolicy.Validate(EffectiveAt, 12, 1.25m, $"  {Reason}  ");

        Assert.Equal(EffectiveAt, repair.EffectiveAt);
        Assert.Equal(DateTimeKind.Utc, repair.EffectiveAt.Kind);
        Assert.Equal(12, repair.Quantity);
        Assert.Equal(1.25m, repair.UnitCost);
        Assert.Equal(15m, repair.TotalValue);
        Assert.Equal(Reason, repair.Reason);
    }

    [Fact]
    public void A_zero_cost_repair_is_allowed_because_free_stock_is_a_real_acquisition()
    {
        Assert.Equal(0m, CostingRepairPolicy.Validate(EffectiveAt, 1, 0m, Reason).TotalValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_non_positive_quantity_is_rejected(int quantity)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            CostingRepairPolicy.Validate(EffectiveAt, quantity, 1m, Reason));

        Assert.Equal("A costing repair must add a positive quantity.", exception.Message);
    }

    [Theory]
    [InlineData(-0.000001)]
    [InlineData(-5)]
    public void A_negative_unit_cost_is_rejected(double unitCost)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            CostingRepairPolicy.Validate(EffectiveAt, 1, (decimal)unitCost, Reason));

        Assert.Equal("A costing repair unit cost cannot be negative.", exception.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fix")]
    [InlineData("n/a")]
    [InlineData("N/A")]
    [InlineData("none")]
    [InlineData("correction")]
    [InlineData("Not applicable")]
    [InlineData("costing repair")]
    [InlineData("  Unknown  ")]
    public void An_empty_or_generic_reason_is_rejected(string? reason)
    {
        var exception = Assert.Throws<DomainValidationException>(() =>
            CostingRepairPolicy.Validate(EffectiveAt, 1, 1m, reason));

        Assert.Equal(
            "Record a specific reason for this costing repair: at least 10 characters, and not a placeholder.",
            exception.Message);
    }

    [Fact]
    public void A_local_effective_time_is_converted_to_the_utc_instant_it_names()
    {
        var local = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Local);

        var repair = CostingRepairPolicy.Validate(local, 1, 1m, Reason);

        Assert.Equal(DateTimeKind.Utc, repair.EffectiveAt.Kind);
        Assert.Equal(local.ToUniversalTime(), repair.EffectiveAt);
    }

    [Fact]
    public void An_unspecified_effective_time_is_read_as_utc_like_every_stored_stock_movement()
    {
        var unspecified = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Unspecified);

        var repair = CostingRepairPolicy.Validate(unspecified, 1, 1m, Reason);

        Assert.Equal(DateTimeKind.Utc, repair.EffectiveAt.Kind);
        Assert.Equal(unspecified.Ticks, repair.EffectiveAt.Ticks);
    }

    [Fact]
    public void A_repair_after_the_baseline_cutoff_is_accepted_and_no_baseline_accepts_any_time()
    {
        Assert.Null(Record.Exception(() => CostingRepairPolicy.EnsureAfterBaselineCutoff(EffectiveAt, EffectiveAt.AddTicks(-1))));
        Assert.Null(Record.Exception(() => CostingRepairPolicy.EnsureAfterBaselineCutoff(EffectiveAt, null)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(86400)]
    public void A_repair_at_or_before_the_baseline_cutoff_is_rejected_because_the_replay_ignores_it(int secondsBeforeCutoff)
    {
        var cutoffAt = EffectiveAt.AddSeconds(secondsBeforeCutoff);

        var exception = Assert.Throws<DomainValidationException>(() =>
            CostingRepairPolicy.EnsureAfterBaselineCutoff(EffectiveAt, cutoffAt));

        Assert.Equal(
            $"A costing repair must take effect after the product's inventory-cost transition cutoff "
                + $"({cutoffAt:yyyy-MM-dd HH:mm:ss}Z). The cost replay ignores everything at or before the cutoff.",
            exception.Message);
    }

    [Fact]
    public void A_repair_that_does_not_replay_before_the_sale_it_must_cover_is_rejected()
    {
        var sale = new CostReplaySale(987654, EffectiveAt);
        var repair = new CostReplayRepair(1, EffectiveAt.AddTicks(1), 1, 1m);

        var exception = Assert.Throws<DomainValidationException>(() =>
            CostingRepairPolicy.EnsureReplaysBeforeSale(repair, sale));

        Assert.Equal(
            "The repair does not replay before completed sale 987654, so it cannot cost that sale. "
                + "Choose an earlier effective time and preview again.",
            exception.Message);
    }

    [Fact]
    public void A_repair_at_or_before_the_sale_it_must_cover_is_accepted()
    {
        var sale = new CostReplaySale(987654, EffectiveAt);

        Assert.Null(Record.Exception(() =>
            CostingRepairPolicy.EnsureReplaysBeforeSale(new CostReplayRepair(1, EffectiveAt, 1, 1m), sale)));
        Assert.Null(Record.Exception(() =>
            CostingRepairPolicy.EnsureReplaysBeforeSale(new CostReplayRepair(1, EffectiveAt.AddTicks(-1), 1, 1m), sale)));
    }
}
