using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Domain.Nayax;

/// <summary>
/// The rule deciding what one confirmed product replacement may write (issue #1326): the shape of
/// the operator's choice, whether both products are usable, and whether storage holds the units the
/// incoming side claims were loaded.
/// </summary>
public class NayaxProductReplacementWritePolicyTests
{
    private static readonly NayaxStockSyncProduct Incoming = new(300, "Red Bull 250mL", 40);
    private static readonly NayaxStockSyncProduct Outgoing = new(301, "V Energy 250mL", 12);

    private static NayaxProductReplacementChoice Choice(
        int incomingQuantity = 6,
        NayaxReplacementOutgoingDisposition disposition = NayaxReplacementOutgoingDisposition.None,
        long? outgoingProductId = null,
        int? outgoingQuantity = null) =>
        new(Incoming.Id, incomingQuantity, disposition, outgoingProductId, outgoingQuantity);

    #region The shape of the choice

    [Fact]
    public void A_loaded_quantity_with_no_outgoing_choice_is_accepted()
    {
        Assert.Null(NayaxProductReplacementWritePolicy.Refusal(Choice()));
    }

    [Fact]
    public void A_loaded_quantity_of_zero_is_accepted_because_nothing_was_loaded()
    {
        Assert.Null(NayaxProductReplacementWritePolicy.Refusal(Choice(incomingQuantity: 0)));
    }

    [Fact]
    public void A_negative_loaded_quantity_is_refused()
    {
        Assert.Equal(
            NayaxProductReplacementWritePolicy.IncomingQuantityMessage,
            NayaxProductReplacementWritePolicy.Refusal(Choice(incomingQuantity: -1)));
    }

    [Fact]
    public void An_incoming_product_is_always_required()
    {
        Assert.Equal(
            NayaxProductReplacementWritePolicy.IncomingProductRequiredMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                new NayaxProductReplacementChoice(0, 0, NayaxReplacementOutgoingDisposition.None, null, null)));
    }

    [Theory]
    [InlineData(NayaxReplacementOutgoingDisposition.ReturnedToStorage)]
    [InlineData(NayaxReplacementOutgoingDisposition.Damaged)]
    [InlineData(NayaxReplacementOutgoingDisposition.Expired)]
    public void Every_outgoing_choice_but_none_needs_a_product_and_a_positive_quantity(
        NayaxReplacementOutgoingDisposition disposition)
    {
        Assert.Equal(
            NayaxProductReplacementWritePolicy.OutgoingProductRequiredMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                Choice(disposition: disposition, outgoingProductId: null, outgoingQuantity: 3)));

        Assert.Equal(
            NayaxProductReplacementWritePolicy.OutgoingQuantityMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                Choice(disposition: disposition, outgoingProductId: Outgoing.Id, outgoingQuantity: null)));

        Assert.Equal(
            NayaxProductReplacementWritePolicy.OutgoingQuantityMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                Choice(disposition: disposition, outgoingProductId: Outgoing.Id, outgoingQuantity: 0)));

        Assert.Equal(
            NayaxProductReplacementWritePolicy.OutgoingQuantityMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                Choice(disposition: disposition, outgoingProductId: Outgoing.Id, outgoingQuantity: -2)));

        Assert.Null(
            NayaxProductReplacementWritePolicy.Refusal(
                Choice(disposition: disposition, outgoingProductId: Outgoing.Id, outgoingQuantity: 3)));
    }

    [Fact]
    public void None_carries_no_outgoing_product_or_quantity_because_nothing_records_them()
    {
        Assert.Equal(
            NayaxProductReplacementWritePolicy.NoOutgoingDetailsMessage,
            NayaxProductReplacementWritePolicy.Refusal(Choice(outgoingProductId: Outgoing.Id)));

        Assert.Equal(
            NayaxProductReplacementWritePolicy.NoOutgoingDetailsMessage,
            NayaxProductReplacementWritePolicy.Refusal(Choice(outgoingQuantity: 3)));
    }

    [Fact]
    public void An_undeclared_outgoing_choice_is_refused_rather_than_read_as_none()
    {
        Assert.Equal(
            NayaxProductReplacementWritePolicy.UnknownDispositionMessage,
            NayaxProductReplacementWritePolicy.Refusal(
                new NayaxProductReplacementChoice(Incoming.Id, 1, (NayaxReplacementOutgoingDisposition)99, 301, 2)));
    }

    #endregion

    #region The products and the storage behind them

    [Fact]
    public void Both_products_present_and_sufficient_storage_decides_apply()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(disposition: NayaxReplacementOutgoingDisposition.Damaged, outgoingProductId: Outgoing.Id, outgoingQuantity: 3),
            Incoming,
            Outgoing);

        Assert.Equal(NayaxReplacementWriteDecision.Apply, ruling.Decision);
    }

    [Fact]
    public void A_retired_foreign_or_missing_incoming_product_is_refused()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(Choice(), incomingProduct: null, outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.ProductUnavailable, ruling.Decision);
        Assert.Equal(NayaxProductReplacementWritePolicy.IncomingProductUnavailableMessage, ruling.Message);
    }

    [Fact]
    public void A_retired_foreign_or_missing_outgoing_product_is_refused()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(disposition: NayaxReplacementOutgoingDisposition.Expired, outgoingProductId: 999, outgoingQuantity: 2),
            Incoming,
            outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.ProductUnavailable, ruling.Decision);
        Assert.Equal(NayaxProductReplacementWritePolicy.OutgoingProductUnavailableMessage, ruling.Message);
    }

    [Fact]
    public void The_incoming_product_is_still_required_when_nothing_was_loaded()
    {
        // Quantity 0 writes no movement, but the replacement still records which product went in,
        // so a retired or foreign product is refused rather than recorded.
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(incomingQuantity: 0), incomingProduct: null, outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.ProductUnavailable, ruling.Decision);
    }

    [Fact]
    public void More_units_than_storage_holds_is_refused_and_names_both_figures()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(incomingQuantity: 41), Incoming, outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.InsufficientStorage, ruling.Decision);
        Assert.Equal(
            NayaxProductReplacementWritePolicy.InsufficientStorageMessage(41, 40),
            ruling.Message);
    }

    [Fact]
    public void Exactly_what_storage_holds_is_accepted()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(incomingQuantity: 40), Incoming, outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.Apply, ruling.Decision);
    }

    [Fact]
    public void The_outgoing_quantity_is_never_checked_against_storage_because_it_comes_out_of_the_machine()
    {
        // A return puts units back into storage and a write-off then removes exactly those units,
        // so storage never has to hold them first - however little of the product is in storage.
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(
                incomingQuantity: 0,
                disposition: NayaxReplacementOutgoingDisposition.Damaged,
                outgoingProductId: Outgoing.Id,
                outgoingQuantity: 500),
            Incoming,
            Outgoing);

        Assert.Equal(NayaxReplacementWriteDecision.Apply, ruling.Decision);
    }

    [Fact]
    public void The_shape_of_the_choice_is_decided_before_the_products_are_looked_at()
    {
        var ruling = NayaxProductReplacementWritePolicy.Decide(
            Choice(incomingQuantity: -1), incomingProduct: null, outgoingProduct: null);

        Assert.Equal(NayaxReplacementWriteDecision.Invalid, ruling.Decision);
        Assert.Equal(NayaxProductReplacementWritePolicy.IncomingQuantityMessage, ruling.Message);
    }

    #endregion

    #region What one accepted choice writes

    [Fact]
    public void Nothing_loaded_and_no_outgoing_choice_writes_no_movement_at_all()
    {
        var writes = NayaxProductReplacementWritePolicy.Movements(Choice(incomingQuantity: 0));

        Assert.False(writes.WritesIncomingRefill);
        Assert.False(writes.WritesOutgoingReturn);
        Assert.False(writes.WritesOutgoingWriteOff);
    }

    [Fact]
    public void A_loaded_quantity_writes_the_incoming_refill_alone()
    {
        var writes = NayaxProductReplacementWritePolicy.Movements(Choice(incomingQuantity: 6));

        Assert.True(writes.WritesIncomingRefill);
        Assert.False(writes.WritesOutgoingReturn);
        Assert.False(writes.WritesOutgoingWriteOff);
    }

    [Fact]
    public void Returned_to_storage_writes_the_return_and_no_write_off()
    {
        var writes = NayaxProductReplacementWritePolicy.Movements(
            Choice(
                disposition: NayaxReplacementOutgoingDisposition.ReturnedToStorage,
                outgoingProductId: Outgoing.Id,
                outgoingQuantity: 3));

        Assert.True(writes.WritesOutgoingReturn);
        Assert.False(writes.WritesOutgoingWriteOff);
        Assert.Null(writes.WriteOffReason);
    }

    [Theory]
    [InlineData(NayaxReplacementOutgoingDisposition.Damaged, Inventory.Domain.Stock.StockAdjustmentReason.Damaged)]
    [InlineData(NayaxReplacementOutgoingDisposition.Expired, Inventory.Domain.Stock.StockAdjustmentReason.Expired)]
    public void A_disposal_writes_the_same_return_and_then_its_own_write_off(
        NayaxReplacementOutgoingDisposition disposition,
        Inventory.Domain.Stock.StockAdjustmentReason expected)
    {
        var writes = NayaxProductReplacementWritePolicy.Movements(
            Choice(disposition: disposition, outgoingProductId: Outgoing.Id, outgoingQuantity: 3));

        Assert.True(writes.WritesOutgoingReturn);
        Assert.True(writes.WritesOutgoingWriteOff);
        Assert.Equal(expected, writes.WriteOffReason);
    }

    #endregion
}
