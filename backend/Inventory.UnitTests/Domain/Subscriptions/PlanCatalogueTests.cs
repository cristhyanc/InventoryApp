using Inventory.Domain.Subscriptions;
using Xunit;

namespace Inventory.UnitTests.Domain.Subscriptions;

/// <summary>
/// The Free/Starter/Pro catalogue of issue #966, with the prices and limits agreed on #965.
///
/// <para>The figures are asserted literally and in minor units, because that is the one thing about
/// a catalogue that must not drift: a price is what a customer is charged and a limit is what a
/// business is told it has, so each is pinned here rather than left to be read off the
/// implementation. The comparison rules beside them are what "the higher of the two records" is
/// built on.</para>
/// </summary>
public class PlanCatalogueTests
{
    private const long Gigabyte = 1024L * 1024L * 1024L;

    [Fact]
    public void Free_is_free_with_one_gigabyte_and_no_connected_machines()
    {
        var plan = PlanCatalogue.For(SubscriptionPlan.Free);

        Assert.Equal(0, plan.MonthlyPriceMinorUnits);
        Assert.Equal("AUD", plan.CurrencyCode);
        Assert.Equal(Gigabyte, plan.AttachmentStorageLimitBytes);
        Assert.Equal(0, plan.IncludedConnectedMachines);
        // No connected machines to add to, which is a different fact from "they are free".
        Assert.Null(plan.AdditionalConnectedMachinePriceMinorUnits);
    }

    [Fact]
    public void Starter_is_seven_dollars_with_twenty_gigabytes_and_no_connected_machines()
    {
        var plan = PlanCatalogue.For(SubscriptionPlan.Starter);

        Assert.Equal(700, plan.MonthlyPriceMinorUnits);
        Assert.Equal("AUD", plan.CurrencyCode);
        Assert.Equal(20 * Gigabyte, plan.AttachmentStorageLimitBytes);
        Assert.Equal(0, plan.IncludedConnectedMachines);
        Assert.Null(plan.AdditionalConnectedMachinePriceMinorUnits);
    }

    [Fact]
    public void Pro_is_fifteen_dollars_with_twenty_gigabytes_five_machines_and_two_dollars_each_after()
    {
        var plan = PlanCatalogue.For(SubscriptionPlan.Pro);

        Assert.Equal(1500, plan.MonthlyPriceMinorUnits);
        Assert.Equal("AUD", plan.CurrencyCode);
        Assert.Equal(20 * Gigabyte, plan.AttachmentStorageLimitBytes);
        Assert.Equal(5, plan.IncludedConnectedMachines);
        Assert.Equal(200, plan.AdditionalConnectedMachinePriceMinorUnits);
    }

    /// <summary>
    /// Every declared plan has exactly one entry, and the catalogue holds no entry for anything
    /// else: a plan with no price is a plan nobody can be charged for, and an entry with no plan is
    /// a price nothing applies to.
    /// </summary>
    [Fact]
    public void Every_declared_plan_has_exactly_one_entry()
    {
        Assert.Equal(SubscriptionPlans.All, PlanCatalogue.All.Select(plan => plan.Plan).ToArray());
        Assert.Equal(PlanCatalogue.All.Count, PlanCatalogue.All.DistinctBy(plan => plan.Plan).Count());
    }

    /// <summary>Prices rise with the plan, which is what makes "the higher plan" and "more expensive" the same order.</summary>
    [Fact]
    public void The_catalogue_is_ordered_from_least_to_most_expensive()
    {
        var prices = PlanCatalogue.All.Select(plan => plan.MonthlyPriceMinorUnits).ToList();

        Assert.Equal(prices.Order().ToList(), prices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void A_plan_this_code_does_not_declare_has_no_catalogue_entry(int undeclared)
    {
        var plan = (SubscriptionPlan)undeclared;

        Assert.False(SubscriptionPlans.IsDeclared(plan));
        Assert.Throws<ArgumentOutOfRangeException>(() => PlanCatalogue.For(plan));
    }

    /// <summary>
    /// Zero is declared by no plan, so an unfilled column and a default-constructed enum are both a
    /// plan nothing entitles rather than the cheapest one.
    /// </summary>
    [Fact]
    public void No_declared_plan_is_zero() =>
        Assert.DoesNotContain((SubscriptionPlan)0, SubscriptionPlans.All);

    [Theory]
    [InlineData(SubscriptionPlan.Free, SubscriptionPlan.Starter, SubscriptionPlan.Starter)]
    [InlineData(SubscriptionPlan.Starter, SubscriptionPlan.Pro, SubscriptionPlan.Pro)]
    [InlineData(SubscriptionPlan.Pro, SubscriptionPlan.Free, SubscriptionPlan.Pro)]
    [InlineData(SubscriptionPlan.Pro, SubscriptionPlan.Pro, SubscriptionPlan.Pro)]
    public void The_higher_of_two_plans_is_the_more_entitled_one(
        SubscriptionPlan first,
        SubscriptionPlan second,
        SubscriptionPlan expected) =>
        Assert.Equal(expected, SubscriptionPlans.Higher(first, second));

    [Fact]
    public void A_plan_is_at_least_itself_and_every_plan_below_it()
    {
        Assert.True(SubscriptionPlans.IsAtLeast(SubscriptionPlan.Pro, SubscriptionPlan.Pro));
        Assert.True(SubscriptionPlans.IsAtLeast(SubscriptionPlan.Pro, SubscriptionPlan.Starter));
        Assert.False(SubscriptionPlans.IsAtLeast(SubscriptionPlan.Starter, SubscriptionPlan.Pro));
    }

    /// <summary>
    /// An undeclared plan has no position in the catalogue, so it is refused rather than compared as
    /// if it were the lowest or the highest.
    /// </summary>
    [Fact]
    public void An_undeclared_plan_cannot_be_compared() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SubscriptionPlans.Higher((SubscriptionPlan)0, SubscriptionPlan.Free));
}
