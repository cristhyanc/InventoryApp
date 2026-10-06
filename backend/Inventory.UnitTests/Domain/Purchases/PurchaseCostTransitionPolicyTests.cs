using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

public class PurchaseCostTransitionPolicyTests
{
    [Fact]
    public void FindConflictingBaseline_returns_null_when_purchase_date_is_after_every_cutoff()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 1, 1) };

        var conflict = PurchaseCostTransitionPolicy.FindConflictingBaseline([1], new DateTime(2026, 2, 1), cutoffs);

        Assert.Null(conflict);
    }

    [Fact]
    public void FindConflictingBaseline_returns_the_product_whose_cutoff_the_date_does_not_pass()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 2, 1) };

        var conflict = PurchaseCostTransitionPolicy.FindConflictingBaseline([1], new DateTime(2026, 2, 1), cutoffs);

        Assert.Equal((1L, new DateTime(2026, 2, 1)), conflict);
    }

    [Fact]
    public void FindConflictingBaseline_ignores_a_product_with_no_baseline()
    {
        var conflict = PurchaseCostTransitionPolicy.FindConflictingBaseline(
            [1], new DateTime(2026, 1, 1), new Dictionary<long, DateTime>());

        Assert.Null(conflict);
    }

    [Fact]
    public void HasPreservedMovement_is_false_when_every_movement_is_after_its_cutoff()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 1, 1) };

        Assert.False(PurchaseCostTransitionPolicy.HasPreservedMovement([(1L, new DateTime(2026, 1, 2))], cutoffs));
    }

    [Fact]
    public void HasPreservedMovement_is_true_for_a_movement_at_or_before_its_cutoff()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 1, 1) };

        Assert.True(PurchaseCostTransitionPolicy.HasPreservedMovement([(1L, new DateTime(2026, 1, 1))], cutoffs));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    public void IsPreservedChangeAllowed_only_allows_an_unchanged_date_and_items(bool dateChanged, bool itemsChanged, bool expected)
    {
        Assert.Equal(expected, PurchaseCostTransitionPolicy.IsPreservedChangeAllowed(dateChanged, itemsChanged));
    }

    [Fact]
    public void FindProtectedMovement_returns_the_first_movement_at_or_before_its_cutoff()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 1, 1) };
        var movements = new[]
        {
            (MovementId: 10, ProductId: 1L, EffectiveAt: new DateTime(2026, 1, 2)),
            (MovementId: 11, ProductId: 1L, EffectiveAt: new DateTime(2026, 1, 1)),
        };

        var protectedMovement = PurchaseCostTransitionPolicy.FindProtectedMovement(movements, cutoffs);

        Assert.Equal((11, 1L, new DateTime(2026, 1, 1)), protectedMovement);
    }

    [Fact]
    public void FindProtectedMovement_returns_null_when_none_are_protected()
    {
        var cutoffs = new Dictionary<long, DateTime> { [1] = new DateTime(2026, 1, 1) };
        var movements = new[] { (MovementId: 10, ProductId: 1L, EffectiveAt: new DateTime(2026, 1, 2)) };

        Assert.Null(PurchaseCostTransitionPolicy.FindProtectedMovement(movements, cutoffs));
    }
}
