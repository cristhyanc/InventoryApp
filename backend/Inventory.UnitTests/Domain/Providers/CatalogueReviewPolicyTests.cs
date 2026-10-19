using Inventory.Domain.Providers;
using Xunit;

namespace InventoryApi.Tests.Domain.Providers;

/// <summary>
/// The deterministic rules of the provider catalogue review (issue #738, parent #329 decision C1):
/// which links are refused, which provider values are proposed, and why a skipped value stays
/// skipped.
/// </summary>
public class CatalogueReviewPolicyTests
{
    [Fact]
    public void A_local_row_with_no_provider_id_may_be_linked()
    {
        Assert.Null(CatalogueReviewPolicy.LinkRefusal(
            ProviderCatalogueEntryKind.Product, "100", localRowProviderId: null, anotherRowCarriesIt: false));
    }

    [Fact]
    public void A_local_row_that_already_carries_a_different_provider_id_is_refused()
    {
        var refusal = CatalogueReviewPolicy.LinkRefusal(
            ProviderCatalogueEntryKind.Product, "100", localRowProviderId: "77", anotherRowCarriesIt: false);

        Assert.NotNull(refusal);
        Assert.Contains("77", refusal, StringComparison.Ordinal);
        Assert.Contains("100", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void A_provider_id_another_local_row_already_carries_is_refused()
    {
        var refusal = CatalogueReviewPolicy.LinkRefusal(
            ProviderCatalogueEntryKind.Category, "10", localRowProviderId: null, anotherRowCarriesIt: true);

        Assert.NotNull(refusal);
        Assert.Contains("category", refusal, StringComparison.Ordinal);
    }

    /// <summary>
    /// Recording the id a row already carries is the state the apply was asked to reach, not a
    /// conflict. It is what makes applying the same review a second time a no-op rather than a
    /// refusal.
    /// </summary>
    [Fact]
    public void Re_recording_the_same_provider_id_on_the_same_row_is_not_refused()
    {
        Assert.Null(CatalogueReviewPolicy.LinkRefusal(
            ProviderCatalogueEntryKind.Product, "100", localRowProviderId: "100", anotherRowCarriesIt: false));
    }

    [Theory]
    [InlineData(CatalogueReviewAction.Defer, null, false, false)]
    [InlineData(CatalogueReviewAction.CreateNew, null, false, false)]
    [InlineData(CatalogueReviewAction.LinkExisting, 5L, false, false)]
    [InlineData(CatalogueReviewAction.LinkExisting, null, false, true)]
    [InlineData(CatalogueReviewAction.CreateNew, 5L, false, true)]
    [InlineData(CatalogueReviewAction.LinkExisting, 5L, true, true)]
    [InlineData(CatalogueReviewAction.Defer, null, true, false)]
    public void A_decision_is_usable_only_in_the_shapes_the_review_offers(
        CatalogueReviewAction action, long? localRowId, bool alreadyLinked, bool refused) =>
        Assert.Equal(refused, CatalogueReviewPolicy.DecisionRefusal(action, localRowId, alreadyLinked) is not null);

    [Fact]
    public void A_provider_value_that_differs_from_the_local_one_is_proposed()
    {
        Assert.True(CatalogueReviewPolicy.IsProposed(
            localValue: "3.00", providerValue: "3.50", lastDecidedProviderValue: null));
    }

    [Fact]
    public void A_provider_value_equal_to_the_local_one_is_not_proposed()
    {
        Assert.False(CatalogueReviewPolicy.IsProposed(
            localValue: "3.50", providerValue: "3.50", lastDecidedProviderValue: null));
    }

    [Fact]
    public void An_absent_provider_value_is_never_proposed()
    {
        Assert.False(CatalogueReviewPolicy.IsProposed(
            localValue: "3.50", providerValue: null, lastDecidedProviderValue: null));
    }

    /// <summary>
    /// The skip rule. The operator kept their own $3.00 over the provider's $3.50; the provider still
    /// says $3.50, so there is nothing new to ask.
    /// </summary>
    [Fact]
    public void A_provider_value_already_decided_about_is_not_proposed_again()
    {
        Assert.False(CatalogueReviewPolicy.IsProposed(
            localValue: "3.00", providerValue: "3.50", lastDecidedProviderValue: "3.50"));
    }

    /// <summary>...and the other half of it: once the provider moves on, the question is new.</summary>
    [Fact]
    public void A_provider_value_that_changed_since_the_decision_is_proposed_again()
    {
        Assert.True(CatalogueReviewPolicy.IsProposed(
            localValue: "3.00", providerValue: "3.95", lastDecidedProviderValue: "3.50"));
    }

    [Theory]
    [InlineData("Chips", "Crisps", true)]
    [InlineData("Chips", "Chips", false)]
    [InlineData(null, "Chips", true)]
    [InlineData("Chips", null, true)]
    [InlineData(null, null, false)]
    public void A_provider_owned_field_is_an_automatic_change_whenever_it_differs(
        string? local, string? provider, bool changed) =>
        Assert.Equal(changed, CatalogueReviewPolicy.IsAutomaticChange(local, provider));

    [Theory]
    [InlineData(3.5d, "3.50")]
    [InlineData(0d, "0.00")]
    [InlineData(12.345d, "12.35")]
    [InlineData(null, null)]
    public void A_price_renders_as_invariant_two_decimal_text(double? price, string? expected) =>
        Assert.Equal(expected, ProviderCatalogueFieldValue.FromPrice((decimal?)price));

    /// <summary>
    /// Trailing zeros must not make two readings of the same price two different answers, or the
    /// operator would be asked about $3.50 again every time the provider reported it as 3.5.
    /// </summary>
    [Fact]
    public void The_same_price_written_two_ways_renders_identically() =>
        Assert.Equal(
            ProviderCatalogueFieldValue.FromPrice(3.5m), ProviderCatalogueFieldValue.FromPrice(3.500m));

    [Theory]
    [InlineData("can", "can")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void A_blank_provider_label_is_no_label(string? label, string? expected) =>
        Assert.Equal(expected, ProviderCatalogueFieldValue.FromLabel(label));

    [Theory]
    [InlineData("3.50", true, 3.50)]
    [InlineData("-1.00", true, -1.00)]
    [InlineData("3,50", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData(null, false, 0)]
    public void A_stored_price_parses_back_only_from_text_this_renderer_could_have_written(
        string? stored, bool parsed, double expected)
    {
        Assert.Equal(parsed, ProviderCatalogueFieldValue.TryParsePrice(stored, out var price));
        Assert.Equal((decimal)expected, price);
    }
}
