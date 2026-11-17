using Inventory.Domain.Nayax;
using Inventory.Domain.Reporting.ProductMatching;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The advice shown beside a Nayax product replacement (issue #1325): which local products the two
/// reported names most likely are, how many units the slot now shows, and when a 501 near it may
/// record the same refill. Every answer is advice only; an answer the inputs cannot support is empty
/// with its reason.
/// </summary>
public class NayaxProductReplacementSuggestionPolicyTests
{
    private static readonly ParsedNayaxProductReplacement Replacement =
        new(58, "V Energy Drink Sugarfree Original 250ML", "Redbull Red Edition");

    private static readonly ProductMatchCandidate Redbull = new(11, "Redbull Red Edition");
    private static readonly ProductMatchCandidate VEnergy = new(12, "V Energy Drink Sugarfree Original 250ML");
    private static readonly ProductMatchCandidate Linked = new(20, "Red Bull Red (linked)");

    private static NayaxReplacementSlotReading Slot(
        string? name = "Redbull Red Edition",
        ProductMatchCandidate? linked = null,
        int? par = 10,
        int? missing = 2) => new(name, linked ?? Linked, par, missing);

    #region Incoming product

    [Fact]
    public void Incoming_comes_from_the_live_slot_when_it_still_reports_the_incoming_product()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, Slot(), [Redbull, VEnergy]);

        // The linked product wins over an exact-name product, because the slot says what is there.
        Assert.Equal(Linked.Id, suggestion.ProductId);
        Assert.Equal(Linked.Name, suggestion.ProductName);
        Assert.Equal(NayaxReplacementSuggestionSource.LiveSlot, suggestion.Source);
        Assert.Null(suggestion.Reason);
    }

    [Fact]
    public void The_slot_name_is_compared_normalised()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, Slot(name: "  redbull red edition (3.50) "), []);

        Assert.Equal(NayaxReplacementSuggestionSource.LiveSlot, suggestion.Source);
        Assert.Equal(Linked.Id, suggestion.ProductId);
    }

    [Fact]
    public void A_slot_that_changed_again_falls_back_to_an_exact_name_match()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, Slot(name: "Mother 500mL"), [Redbull, VEnergy]);

        Assert.Equal(Redbull.Id, suggestion.ProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, suggestion.Source);
    }

    [Fact]
    public void A_slot_with_no_local_link_falls_back_to_an_exact_name_match()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, new NayaxReplacementSlotReading("Redbull Red Edition", null, 10, 2), [Redbull]);

        Assert.Equal(Redbull.Id, suggestion.ProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, suggestion.Source);
    }

    [Fact]
    public void A_slot_that_changed_and_no_name_match_is_empty_and_says_both()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, Slot(name: "Mother 500mL"), [VEnergy]);

        Assert.Null(suggestion.ProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.None, suggestion.Source);
        Assert.Contains("MDB 58 now reports 'Mother 500mL'", suggestion.Reason, StringComparison.Ordinal);
        Assert.Contains("No active product is named 'Redbull Red Edition'", suggestion.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_slot_and_a_name_tie_is_empty_with_both_reasons()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncoming(
            Replacement, null, [Redbull, new ProductMatchCandidate(30, "redbull red edition")]);

        Assert.Null(suggestion.ProductId);
        Assert.Contains("does not currently report MDB 58", suggestion.Reason, StringComparison.Ordinal);
        Assert.Contains("More than one active product", suggestion.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_live_read_incoming_is_an_exact_name_match()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncomingWithoutSlotRead(
            Replacement, [Redbull, VEnergy]);

        Assert.Equal(Redbull.Id, suggestion.ProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, suggestion.Source);
    }

    [Fact]
    public void Without_a_live_read_and_no_name_match_incoming_is_empty_and_says_both()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestIncomingWithoutSlotRead(Replacement, [VEnergy]);

        Assert.Null(suggestion.ProductId);
        Assert.Contains(NayaxProductReplacementSuggestionPolicy.SlotReadUnavailableReason, suggestion.Reason, StringComparison.Ordinal);
        Assert.Contains("No active product is named 'Redbull Red Edition'", suggestion.Reason, StringComparison.Ordinal);
    }

    #endregion

    #region Outgoing product

    [Fact]
    public void Outgoing_is_a_unique_exact_name_match()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestOutgoing(Replacement, [Redbull, VEnergy]);

        Assert.Equal(VEnergy.Id, suggestion.ProductId);
        Assert.Equal(NayaxReplacementSuggestionSource.ExactName, suggestion.Source);
    }

    [Fact]
    public void Outgoing_with_no_match_is_empty_with_a_reason()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestOutgoing(Replacement, [Redbull]);

        Assert.Null(suggestion.ProductId);
        Assert.Equal("No active product is named 'V Energy Drink Sugarfree Original 250ML'.", suggestion.Reason);
    }

    [Fact]
    public void Outgoing_with_a_tie_is_empty_and_never_picks_one()
    {
        var suggestion = NayaxProductReplacementSuggestionPolicy.SuggestOutgoing(
            Replacement, [VEnergy, new ProductMatchCandidate(40, "V Energy Drink Sugarfree Original 250ML (4.00)")]);

        Assert.Null(suggestion.ProductId);
        Assert.Contains("More than one active product", suggestion.Reason, StringComparison.Ordinal);
    }

    #endregion

    #region Incoming quantity

    [Fact]
    public void The_quantity_is_par_minus_missing_stock_when_the_slot_still_reports_the_incoming_product()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(
            Replacement, Slot(par: 10, missing: 3), newerPendingEventExists: false);

        Assert.Equal(7, quantity.Quantity);
        Assert.Null(quantity.Reason);
    }

    [Fact]
    public void A_full_slot_suggests_par_and_an_empty_one_suggests_zero()
    {
        Assert.Equal(10, NayaxProductReplacementSuggestionPolicy
            .SuggestIncomingQuantity(Replacement, Slot(par: 10, missing: 0), false).Quantity);
        Assert.Equal(0, NayaxProductReplacementSuggestionPolicy
            .SuggestIncomingQuantity(Replacement, Slot(par: 10, missing: 10), false).Quantity);
    }

    [Fact]
    public void No_quantity_when_the_machine_no_longer_reports_the_mdb()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(Replacement, null, false);

        Assert.Null(quantity.Quantity);
        Assert.Contains("does not currently report MDB 58", quantity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_quantity_when_the_slot_changed_again()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(
            Replacement, Slot(name: "Mother 500mL"), false);

        Assert.Null(quantity.Quantity);
        Assert.Contains("now reports 'Mother 500mL'", quantity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_quantity_when_a_newer_pending_event_exists_for_the_mdb()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(Replacement, Slot(), true);

        Assert.Null(quantity.Quantity);
        Assert.Contains("newer pending event", quantity.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(10, null)]
    [InlineData(null, null)]
    public void No_quantity_when_par_or_missing_stock_is_absent(int? par, int? missing)
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(
            Replacement, Slot(par: par, missing: missing), false);

        Assert.Null(quantity.Quantity);
        Assert.Contains("both PAR and missing stock", quantity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_quantity_when_missing_stock_exceeds_par()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.SuggestIncomingQuantity(
            Replacement, Slot(par: 5, missing: 6), false);

        Assert.Null(quantity.Quantity);
        Assert.Contains("more missing units (6) than PAR (5)", quantity.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void No_quantity_without_a_live_read()
    {
        var quantity = NayaxProductReplacementSuggestionPolicy.IncomingQuantityWithoutSlotRead();

        Assert.Null(quantity.Quantity);
        Assert.Contains(NayaxProductReplacementSuggestionPolicy.SlotReadUnavailableReason, quantity.Reason, StringComparison.Ordinal);
    }

    #endregion

    #region Double count

    [Theory]
    [InlineData(0, true)]
    [InlineData(24, true)]
    [InlineData(-24, true)]
    [InlineData(25, false)]
    [InlineData(-25, false)]
    public void A_501_within_24_hours_either_side_is_a_possible_double_count(int hours, bool expected)
    {
        var replacementAt = new DateTime(2026, 11, 15, 4, 33, 46, DateTimeKind.Utc);

        Assert.Equal(
            expected,
            NayaxProductReplacementSuggestionPolicy.IsPossibleDoubleCount(replacementAt, replacementAt.AddHours(hours)));
    }

    #endregion
}
