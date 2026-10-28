using Inventory.Domain.Exceptions;
using Inventory.Domain.PriceCatalogue;
using Xunit;

namespace Inventory.UnitTests.Domain.PriceCatalogue;

/// <summary>
/// The rules the "Track supplier prices" switch is made of (issue #938, parent #817): which unit
/// labels a product may be tracked under, what search text a mapping captures, and what an edit of
/// the product's name or unit label does to a mapping that already exists.
///
/// <para>They are tested apart from issue #923's consistency rules because they are a different kind
/// of rule: those are about combinations a stored row may hold, these are about what a business's
/// switch and a product edit are allowed to do to one.</para>
/// </summary>
public class ProductPriceTrackingSwitchPolicyTests
{
    #region Which unit labels may be tracked

    /// <summary>
    /// A real unit label is what the collector searches with beside the name, so it is required.
    /// Whitespace is no label, and neither is the generic <c>"unit"</c> every catalogue-imported
    /// product carries: searching for "Coca-Cola Classic unit" finds nothing, and a business would be
    /// left waiting on a search that could never match.
    /// </summary>
    [Theory]
    [InlineData("375mL")]
    [InlineData("600 ml")]
    [InlineData("1kg")]
    [InlineData("can")]
    [InlineData("units")]
    public void A_specific_unit_label_may_be_tracked(string unitLabel) =>
        Assert.True(ProductPriceTrackingPolicy.IsTrackableUnitLabel(unitLabel));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unit")]
    [InlineData("Unit")]
    [InlineData("UNIT")]
    [InlineData("  unit  ")]
    public void A_blank_or_generic_unit_label_may_not_be_tracked(string? unitLabel) =>
        Assert.False(ProductPriceTrackingPolicy.IsTrackableUnitLabel(unitLabel));

    /// <summary>
    /// The refusal names the rule rather than the value, and is the one sentence both the API and the
    /// switch show - so the message an operator reads is written exactly once.
    /// </summary>
    [Fact]
    public void A_generic_unit_label_is_refused_with_the_unit_label_message() =>
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage,
            ProductPriceTrackingPolicy.TrackingRefusal("Coca-Cola Classic", "unit"));

    [Fact]
    public void A_blank_unit_label_is_refused_with_the_same_message() =>
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage,
            ProductPriceTrackingPolicy.TrackingRefusal("Coca-Cola Classic", null));

    /// <summary>
    /// A product with no usable name is refused by the name rule, which is #923's own message: the two
    /// refusals stay distinct, because "give it a unit label" is the wrong instruction for a product
    /// that has no name.
    /// </summary>
    [Fact]
    public void A_product_with_no_name_is_refused_by_the_name_rule() =>
        Assert.Equal(
            ProductPriceTrackingPolicy.SearchNameRequiredMessage,
            ProductPriceTrackingPolicy.TrackingRefusal("  ", "375mL"));

    [Fact]
    public void A_name_longer_than_the_limit_is_refused_rather_than_truncated() =>
        Assert.Equal(
            ProductPriceTrackingPolicy.SearchNameTooLongMessage,
            ProductPriceTrackingPolicy.TrackingRefusal(
                new string('x', ProductPriceTrackingPolicy.MaxSearchNameLength + 1),
                "375mL"));

    [Fact]
    public void A_unit_label_longer_than_the_limit_is_refused_rather_than_truncated() =>
        Assert.Equal(
            ProductPriceTrackingPolicy.UnitLabelTooLongMessage,
            ProductPriceTrackingPolicy.TrackingRefusal(
                "Coca-Cola Classic",
                new string('x', ProductPriceTrackingPolicy.MaxUnitLabelLength + 1)));

    [Fact]
    public void A_trackable_product_has_no_refusal() =>
        Assert.Null(ProductPriceTrackingPolicy.TrackingRefusal("Coca-Cola Classic", "375mL"));

    #endregion

    #region What the capture stores

    /// <summary>
    /// The criterion: the search text is the product's own name and unit label, trimmed, and the
    /// waiting key is the fold of exactly what was captured - so the text a collector searches with
    /// and the key its answer is grouped under cannot disagree.
    /// </summary>
    [Fact]
    public void The_capture_is_the_products_own_name_and_unit_label_with_the_key_they_fold_to()
    {
        var captured = ProductPriceTrackingPolicy.Capture("  Coca-Cola Classic ", " 375mL ");

        Assert.Equal("Coca-Cola Classic", captured.SearchName);
        Assert.Equal("375mL", captured.UnitLabel);
        Assert.Equal(PriceCatalogueWaitingKey.For("Coca-Cola Classic", "375mL"), captured.WaitingKey);
    }

    /// <summary>
    /// The capture refuses what the switch refuses, so no path can store a mapping the switch would
    /// not have created: the eligibility answer and the stored row come from one rule.
    /// </summary>
    [Fact]
    public void The_capture_refuses_a_generic_unit_label()
    {
        var refusal = Assert.Throws<DomainValidationException>(
            () => ProductPriceTrackingPolicy.Capture("Coca-Cola Classic", "unit"));

        Assert.Equal(ProductPriceTrackingPolicy.UnitLabelNotSpecificMessage, refusal.Message);
    }

    [Fact]
    public void The_try_capture_reports_a_refused_product_without_throwing()
    {
        Assert.False(ProductPriceTrackingPolicy.TryCapture("Coca-Cola Classic", "unit", out var captured));
        Assert.Null(captured);
    }

    [Fact]
    public void The_try_capture_reports_an_accepted_product()
    {
        Assert.True(ProductPriceTrackingPolicy.TryCapture("Pepsi Max", "375mL", out var captured));
        Assert.Equal("Pepsi Max", captured!.SearchName);
    }

    #endregion

    #region What an edit of the product does to an existing mapping

    /// <summary>
    /// The criterion: a mapping still awaiting a match follows the product's name. Both the stored
    /// text and the waiting key move, because the key is the fold of the text.
    /// </summary>
    [Fact]
    public void A_rename_refreshes_a_mapping_that_is_still_awaiting_a_match()
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.Requested,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic No Sugar",
            productUnit: "375mL");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.Requested, refresh.Status);
        Assert.Equal("Coca-Cola Classic No Sugar", refresh.Text!.SearchName);
        Assert.Equal("375mL", refresh.Text.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic No Sugar", "375mL"),
            refresh.Text.WaitingKey);
    }

    /// <summary>And a change of its unit label, which is the other half of the search text.</summary>
    [Fact]
    public void A_unit_label_change_refreshes_a_mapping_that_is_still_awaiting_a_match()
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.Requested,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit: "600mL");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.Requested, refresh.Status);
        Assert.Equal("600mL", refresh.Text!.UnitLabel);
    }

    /// <summary>
    /// A product saved with the text it already had is not an edit: nothing is written, so a
    /// <c>PUT</c> that sends the whole product back - the Products list's own activation write - does
    /// not touch the mapping. Trailing space is the same text, because the stored value is trimmed.
    /// </summary>
    [Theory]
    [InlineData("Coca-Cola Classic", "375mL")]
    [InlineData("  Coca-Cola Classic  ", " 375mL ")]
    public void Saving_the_same_text_refreshes_nothing(string productName, string productUnit) =>
        Assert.Null(ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.Requested,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName,
            productUnit));

    /// <summary>
    /// The criterion: changing the unit label of a matched product returns its mapping to awaiting a
    /// match with the new text, because the size the match was made on may no longer agree. It holds
    /// for an automatic match and a decided one alike.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public void A_unit_label_change_returns_a_matched_mapping_to_awaiting_a_match(
        ProductPriceTrackingStatus matched)
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            matched,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit: "1.25L");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.Requested, refresh.Status);
        Assert.Equal("1.25L", refresh.Text!.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic", "1.25L"),
            refresh.Text.WaitingKey);
    }

    /// <summary>
    /// And the other half of that criterion: a name-only edit keeps the match. The master product the
    /// mapping names is still the same product, so nothing is written at all - which is also what
    /// preserves the text the match was actually made from.
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public void A_name_only_edit_keeps_the_match(ProductPriceTrackingStatus matched) =>
        Assert.Null(ProductPriceTrackingPolicy.RefreshFor(
            matched,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coke Classic",
            productUnit: "375mL"));

    /// <summary>
    /// "Looked for and not found" is a recorded answer rather than a gap (issue #923), and issue
    /// #938's criteria name only the awaiting and the matched states - so an edit leaves it exactly as
    /// it is rather than quietly re-running a search that already came back empty.
    /// </summary>
    [Fact]
    public void An_edit_leaves_a_not_found_mapping_alone() =>
        Assert.Null(ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.NotFound,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic No Sugar",
            productUnit: "600mL"));

    #endregion

    #region An edit that leaves the product unsearchable (repaired on review of #938)

    /// <summary>
    /// The repair: a <strong>matched</strong> product relabelled to the generic <c>"unit"</c> is
    /// suspended rather than left matched. The mapping carries
    /// <see cref="ProductPriceTrackingStatus.AwaitingProductDetails"/> - which clears the master
    /// product and the match source, so the master product it was matched to can no longer supply
    /// prices for the size the product no longer has - and no text, because there is none this rule
    /// would store. It holds for an automatic match and a decided one alike.
    ///
    /// <para>This test replaced the earlier
    /// <c>A_product_edited_to_the_generic_unit_label_refreshes_nothing</c>, which asserted the
    /// behaviour the review found wrong: the rule intentionally changed, it was not weakened.</para>
    /// </summary>
    [Theory]
    [InlineData(ProductPriceTrackingStatus.AutoMatched)]
    [InlineData(ProductPriceTrackingStatus.Matched)]
    public void Relabelling_a_matched_product_to_the_generic_unit_label_suspends_the_mapping(
        ProductPriceTrackingStatus matched)
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            matched,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit: "unit");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, refresh.Status);
        Assert.Null(refresh.Text);
    }

    /// <summary>
    /// A mapping still awaiting a match is suspended too: its stored text is the size the product no
    /// longer has, and <see cref="ProductPriceTrackingStatus.Requested"/> is exactly what the
    /// collector work list is a list of - so leaving it there would keep searching for a product the
    /// business does not have.
    /// </summary>
    [Theory]
    [InlineData("unit")]
    [InlineData("Unit")]
    [InlineData("  unit  ")]
    public void Relabelling_a_waiting_product_to_the_generic_unit_label_suspends_the_mapping(
        string productUnit)
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.Requested,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit);

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, refresh.Status);
        Assert.Null(refresh.Text);
    }

    /// <summary>
    /// And so is text the capture cannot store for any other reason - a product renamed to something
    /// longer than the search-name limit. One rule, one answer: whatever makes the product
    /// unsearchable suspends the mapping instead of leaving a stale one standing.
    /// </summary>
    [Fact]
    public void A_name_the_capture_cannot_store_suspends_the_mapping()
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.Requested,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: new string('x', ProductPriceTrackingPolicy.MaxSearchNameLength + 1),
            productUnit: "375mL");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.AwaitingProductDetails, refresh.Status);
        Assert.Null(refresh.Text);
    }

    /// <summary>
    /// A suspended mapping whose product is still unsearchable is left exactly as it is: there is
    /// nothing to change, and rewriting the row on every save of the product would record a change
    /// that did not happen.
    /// </summary>
    [Fact]
    public void A_further_edit_leaves_a_suspended_mapping_as_it_is() =>
        Assert.Null(ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.AwaitingProductDetails,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coke Classic",
            productUnit: "unit"));

    /// <summary>
    /// The other half of the repair: correcting the product captures its <em>current</em> name and
    /// unit label and returns the mapping to awaiting a match, so matching may happen again - against
    /// the new size, never against the master product the suspension cleared.
    /// </summary>
    [Fact]
    public void Correcting_the_product_returns_a_suspended_mapping_to_awaiting_a_match()
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.AwaitingProductDetails,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic No Sugar",
            productUnit: "1.25L");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.Requested, refresh.Status);
        Assert.Equal("Coca-Cola Classic No Sugar", refresh.Text!.SearchName);
        Assert.Equal("1.25L", refresh.Text.UnitLabel);
        Assert.Equal(
            PriceCatalogueWaitingKey.For("Coca-Cola Classic No Sugar", "1.25L"),
            refresh.Text.WaitingKey);
    }

    /// <summary>
    /// Even when the corrected text is the text the mapping was suspended with: the status itself is
    /// the change, and the match the mapping once had was invalidated rather than remembered. A
    /// "nothing changed" answer here would leave the mapping paused forever.
    /// </summary>
    [Fact]
    public void Correcting_the_product_back_to_the_stored_text_still_resumes_the_mapping()
    {
        var refresh = ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.AwaitingProductDetails,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit: "375mL");

        Assert.NotNull(refresh);
        Assert.Equal(ProductPriceTrackingStatus.Requested, refresh.Status);
        Assert.Equal("375mL", refresh.Text!.UnitLabel);
    }

    /// <summary>
    /// A <see cref="ProductPriceTrackingStatus.NotFound"/> mapping is still left alone, generic label
    /// or not: it names no master product and the work list does not send it, so there is nothing
    /// stale about it to invalidate - which is why the suspension does not reach it.
    /// </summary>
    [Fact]
    public void An_edit_to_the_generic_unit_label_leaves_a_not_found_mapping_alone() =>
        Assert.Null(ProductPriceTrackingPolicy.RefreshFor(
            ProductPriceTrackingStatus.NotFound,
            storedSearchName: "Coca-Cola Classic",
            storedUnitLabel: "375mL",
            productName: "Coca-Cola Classic",
            productUnit: "unit"));

    #endregion
}
