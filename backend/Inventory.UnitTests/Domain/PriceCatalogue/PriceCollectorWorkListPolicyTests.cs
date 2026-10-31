using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// What one collector run is asked to do (issue #924): which suppliers are due (every enabled,
/// collectable one, on every run, since 1 November 2026), and how much work a run may be handed.
/// </summary>
public class PriceCollectorWorkListPolicyTests
{
    #region Which suppliers are due

    /// <summary>
    /// An enabled supplier that something collects is due on every run, whenever it was last checked
    /// (owner decision, 1 November 2026): a run searches everywhere.
    /// </summary>
    [Fact]
    public void An_enabled_collectable_supplier_is_always_due() =>
        Assert.True(PriceCollectorWorkListPolicy.IsSupplierDue(isEnabled: true, hasConnector: true));

    /// <summary>
    /// A supplier nothing collects is never due. Costco is in that state and it is a real one: a route
    /// that cannot be taken is not work, and issuing it would be a work item no result could answer.
    /// </summary>
    [Fact]
    public void A_supplier_with_no_connector_is_never_due() =>
        Assert.False(PriceCollectorWorkListPolicy.IsSupplierDue(isEnabled: true, hasConnector: false));

    /// <summary>
    /// A disabled supplier is never due either. Disabling is how the platform stops suggesting a
    /// supplier, and a supplier it has stopped suggesting is not one to keep searching.
    /// </summary>
    [Fact]
    public void A_disabled_supplier_is_never_due() =>
        Assert.False(PriceCollectorWorkListPolicy.IsSupplierDue(isEnabled: false, hasConnector: true));

    #endregion

    #region How much work a run may carry

    /// <summary>
    /// The budget is the configured one while it is inside the reviewed maximum, and the maximum
    /// otherwise: no setting may widen the response past the bound this policy states.
    /// </summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(10, 10)]
    [InlineData(PriceCollectorWorkListPolicy.MaxWorkItemsPerRun, PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)]
    [InlineData(PriceCollectorWorkListPolicy.MaxWorkItemsPerRun + 1, PriceCollectorWorkListPolicy.MaxWorkItemsPerRun)]
    public void The_budget_is_the_configured_one_bounded_by_the_reviewed_maximum(int configured, int expected) =>
        Assert.Equal(expected, PriceCollectorWorkListPolicy.BudgetFor(configured));

    /// <summary>
    /// A non-positive budget is refused rather than read as "no work" or "unlimited": both are
    /// silently wrong, and a run issued with no work at all is not something to arrive at by
    /// accident.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_non_positive_budget_is_refused(int configured)
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => PriceCollectorWorkListPolicy.BudgetFor(configured));

        Assert.Equal(PriceCollectorWorkListPolicy.NonPositiveBudgetMessage, refusal.Message);
    }

    /// <summary>
    /// The two context bounds are separate numbers from the work-item one, and both are positive:
    /// they bound what travels <em>under</em> a product, which is the dimension a limit on work items
    /// cannot see at all.
    /// </summary>
    [Fact]
    public void The_context_bounds_are_their_own_positive_limits()
    {
        Assert.True(PriceCollectorWorkListPolicy.MaxListingsPerProduct > 0);
        Assert.True(PriceCollectorWorkListPolicy.MaxRefusedMasterProductsPerWaitingProduct > 0);
    }

    #endregion

    #region Where a bounded run continues from

    /// <summary>
    /// With no previous run there is no cursor, and the rotation starts at its beginning: every
    /// product comes after nothing.
    /// </summary>
    [Fact]
    public void With_no_cursor_every_product_comes_after_it()
    {
        Assert.True(PriceCollectorWorkListPolicy.MatchedProductComesAfter(
            masterProductId: 1, cursorMasterProductId: null, cursorWaitingKey: null));
        Assert.True(PriceCollectorWorkListPolicy.WaitingProductComesAfter("coca-cola|375ml", null));
    }

    /// <summary>
    /// With the previous run stopped at a matched product, the matched products after it are the ones
    /// with a higher key - the comparison is on the stored id, so a product added or removed between
    /// two runs cannot send the rotation back to the start.
    /// </summary>
    [Theory]
    [InlineData(4, false)]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public void After_a_matched_cursor_the_higher_ids_come_next(long candidate, bool comesAfter) =>
        Assert.Equal(
            comesAfter,
            PriceCollectorWorkListPolicy.MatchedProductComesAfter(
                candidate, cursorMasterProductId: 5, cursorWaitingKey: null));

    /// <summary>
    /// And every waiting product comes after a matched cursor, because the waiting products are
    /// issued after all of the matched ones.
    /// </summary>
    [Fact]
    public void Every_waiting_product_comes_after_a_matched_cursor() =>
        Assert.True(PriceCollectorWorkListPolicy.WaitingProductComesAfter(
            "coca-cola|375ml",
            cursorWaitingKey: null));

    /// <summary>
    /// With the previous run stopped among the waiting products, the waiting keys after it are the
    /// ordinally greater ones - and <strong>no</strong> matched product comes after it: the matched
    /// products are the start of the rotation and are reached by wrapping around, which is what makes
    /// the schedule a cycle rather than a one-way walk that ends.
    /// </summary>
    [Theory]
    [InlineData("alpine|375ml", false)]
    [InlineData("coca-cola|375ml", false)]
    [InlineData("zephyr|375ml", true)]
    public void After_a_waiting_cursor_the_greater_keys_come_next(string candidate, bool comesAfter)
    {
        Assert.Equal(
            comesAfter,
            PriceCollectorWorkListPolicy.WaitingProductComesAfter(candidate, "coca-cola|375ml"));

        Assert.False(PriceCollectorWorkListPolicy.MatchedProductComesAfter(
            masterProductId: long.MaxValue,
            cursorMasterProductId: null,
            cursorWaitingKey: "coca-cola|375ml"));
    }

    #endregion
}
