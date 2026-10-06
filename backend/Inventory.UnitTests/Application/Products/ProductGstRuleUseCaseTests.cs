using Inventory.Application.Gst;
using Inventory.Application.Products;
using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Products;

/// <summary>
/// The product GST rule use cases (issue #430): what a product's rule reads as before anybody
/// configures one, that a configured rule round-trips, and that a value outside the declared
/// vocabulary is refused without storing anything.
///
/// The rejection matters more than it looks. A rule arrives from a JSON body, where a C# enum is no
/// constraint at all: <c>{"gstRule": 999}</c> binds to a <see cref="GstClassification"/> no policy
/// describes. Stored, it would sit in the database as a rule the historical Preview/Apply workflow
/// (issue #433) would later have to interpret, and the obvious interpretation - "not Taxable, so no
/// GST" - is exactly the silent inference AGENTS.md § Purchase GST classification forbids.
/// </summary>
public class ProductGstRuleUseCaseTests
{
    private const long ProductId = 7;

    [Fact]
    public async Task A_product_nobody_configured_has_no_rule()
    {
        var store = new FakeProductStore([ProductId]);

        var rule = await new GetProductGstRule(store).Handle(ProductId, CancellationToken.None);

        Assert.Equal(GstRules.None, rule);
    }

    [Fact]
    public async Task An_unknown_product_has_no_rule_to_read_or_set()
    {
        var store = new FakeProductStore();

        Assert.Null(await new GetProductGstRule(store).Handle(ProductId, CancellationToken.None));
        Assert.Equal(
            GstRuleUpdateOutcome.NotFound,
            (await new SetProductGstRule(store).Handle(
                ProductId, GstClassification.Taxable, CancellationToken.None)).Outcome);
    }

    [Theory]
    [InlineData(GstClassification.Taxable)]
    [InlineData(GstClassification.GstFree)]
    [InlineData(GstClassification.Unknown)]
    public async Task A_supported_rule_is_stored_and_read_back(GstClassification rule)
    {
        var store = new FakeProductStore([ProductId]);

        var result = await new SetProductGstRule(store).Handle(ProductId, rule, CancellationToken.None);

        Assert.Equal(GstRuleUpdateOutcome.Success, result.Outcome);
        Assert.Null(result.ValidationError);
        Assert.Equal(rule, await new GetProductGstRule(store).Handle(ProductId, CancellationToken.None));
    }

    /// <summary>
    /// Clearing a rule is an ordinary, supported submission: <c>Unknown</c> is "no rule", a state of
    /// its own that must stay distinct from an explicit <c>GstFree</c> rule.
    /// </summary>
    [Fact]
    public async Task Setting_the_rule_back_to_none_clears_it()
    {
        var store = new FakeProductStore([ProductId]);
        var useCase = new SetProductGstRule(store);
        await useCase.Handle(ProductId, GstClassification.GstFree, CancellationToken.None);

        await useCase.Handle(ProductId, GstRules.None, CancellationToken.None);

        Assert.Equal(GstRules.None, await new GetProductGstRule(store).Handle(ProductId, CancellationToken.None));
    }

    [Theory]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(3)]
    public async Task An_undefined_rule_is_refused_and_stores_nothing(int value)
    {
        var store = new FakeProductStore([ProductId]);
        await new SetProductGstRule(store).Handle(ProductId, GstClassification.Taxable, CancellationToken.None);

        var result = await new SetProductGstRule(store).Handle(
            ProductId, (GstClassification)value, CancellationToken.None);

        Assert.Equal(GstRuleUpdateOutcome.Invalid, result.Outcome);
        Assert.Equal(GstRules.UnsupportedRuleMessage, result.ValidationError);
        Assert.Equal(
            GstClassification.Taxable,
            await new GetProductGstRule(store).Handle(ProductId, CancellationToken.None));
    }

    /// <summary>
    /// An unknown product and an undefined rule together: the rule is refused rather than reported
    /// as not-found, because an unsupported value is never stored whatever product it names, and a
    /// not-found answer would invite a caller to retry the same unsupported value elsewhere.
    /// </summary>
    [Fact]
    public async Task An_undefined_rule_is_refused_before_the_product_is_looked_up()
    {
        var store = new FakeProductStore();

        var result = await new SetProductGstRule(store).Handle(
            ProductId, (GstClassification)42, CancellationToken.None);

        Assert.Equal(GstRuleUpdateOutcome.Invalid, result.Outcome);
    }
}
