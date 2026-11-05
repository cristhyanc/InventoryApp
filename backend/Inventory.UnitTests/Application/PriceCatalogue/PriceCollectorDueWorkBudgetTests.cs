using Inventory.Application.PriceCatalogue;
using Inventory.Domain.Exceptions;
using Xunit;

namespace Inventory.UnitTests.Application.PriceCatalogue;

/// <summary>
/// How a <c>schedule=due</c> run's budget is filled (issue #1108): <c>PriceCollectorDueWork.Fill</c>
/// takes the pairs the write found due, in rotation order, and decides which of them this run
/// carries.
///
/// <para><strong>The budget is filled from due work, not from candidates.</strong> That is the whole
/// point of deciding it here rather than before the due check: a stretch of the rotation whose pairs
/// are all covered or in flight would otherwise exhaust the budget, leave the run with nothing to
/// issue, and - because the rotation cursor is read from the work actually issued - never move past
/// itself, so the pair owed a search just beyond that stretch would never be reached again.</para>
///
/// <para>The two bounds it keeps are the ones a <c>schedule=all</c> run keeps: a product is carried
/// only when <em>all</em> of its due pairs fit, so no published product is half-issued and every
/// result row can cite its work item, and the walk stops at the first product that does not fit so
/// the run is one contiguous stretch rather than whichever products happened to be small enough.
/// </para>
/// </summary>
public class PriceCollectorDueWorkBudgetTests
{
    [Fact]
    public void Due_work_that_fits_is_carried_whole_and_nothing_is_left_behind()
    {
        var due = Work((1, 10), (2, 10), (1, 11), (2, 11));

        var filled = DueWork(budget: 4).Fill(due);

        Assert.Equal(due, filled.WorkItems);
        Assert.False(filled.WorkItemLimitReached);
    }

    /// <summary>
    /// A product whose pairs do not all fit is not half-issued, and the walk stops there rather than
    /// skipping on to a smaller product behind it: the run stays one contiguous stretch of the due
    /// work, which is what the next run's cursor reads.
    /// </summary>
    [Fact]
    public void A_product_whose_pairs_do_not_all_fit_stops_the_walk()
    {
        var due = Work((1, 10), (2, 10), (1, 11), (2, 11), (1, 12));

        var filled = DueWork(budget: 3).Fill(due);

        Assert.Equal(Work((1, 10), (2, 10)), filled.WorkItems);
        Assert.True(filled.WorkItemLimitReached);
    }

    /// <summary>
    /// And when not even the first product fits, nothing is carried and the run says so - a product
    /// published with some of its pairs would be a product no result could be attributed to.
    /// </summary>
    [Fact]
    public void A_first_product_wider_than_the_budget_carries_nothing()
    {
        var due = Work((1, 10), (2, 10), (3, 10), (1, 11));

        var filled = DueWork(budget: 2).Fill(due);

        Assert.Empty(filled.WorkItems);
        Assert.True(filled.WorkItemLimitReached);
    }

    /// <summary>
    /// A waiting key is a product in exactly the same way a master product is, because a work item
    /// names one or the other and the rotation carries both.
    /// </summary>
    [Fact]
    public void A_waiting_key_is_counted_as_its_own_product()
    {
        List<NewPriceCollectorWorkItem> due =
        [
            new(1, null, "coke|375ml"),
            new(2, null, "coke|375ml"),
            new(1, null, "pepsi|375ml"),
        ];

        var filled = DueWork(budget: 2).Fill(due);

        Assert.Equal(due.Take(2), filled.WorkItems);
        Assert.True(filled.WorkItemLimitReached);
    }

    /// <summary>
    /// Nothing due is not a truncation: an empty list of due pairs fills nothing and leaves nothing
    /// behind, which is the answer that creates no run at all.
    /// </summary>
    [Fact]
    public void No_due_work_fills_nothing_and_leaves_nothing_behind()
    {
        var filled = DueWork(budget: 5).Fill([]);

        Assert.Empty(filled.WorkItems);
        Assert.False(filled.WorkItemLimitReached);
    }

    /// <summary>
    /// The budget is re-read through the Domain's own clamp, so a budget nobody could issue work
    /// with is refused here as it is on a <c>schedule=all</c> run rather than quietly issuing none.
    /// </summary>
    [Fact]
    public void A_budget_nothing_could_be_issued_with_is_refused()
    {
        Assert.Throws<DomainValidationException>(() => DueWork(budget: 0).Fill(Work((1, 10))));
    }

    private static PriceCollectorDueWork DueWork(int budget) =>
        new(new Dictionary<int, DateTime>(), budget);

    /// <summary>
    /// Due pairs in rotation order, each as <c>(supplier, master product)</c> - the order the work
    /// list publishes them in, which is why one product's pairs are consecutive.
    /// </summary>
    private static List<NewPriceCollectorWorkItem> Work(
        params (int SupplierPresetId, long MasterProductId)[] pairs) =>
        [.. pairs.Select(pair => new NewPriceCollectorWorkItem(
            pair.SupplierPresetId,
            pair.MasterProductId,
            WaitingKey: null))];
}
