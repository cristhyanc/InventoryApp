using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The deterministic validation a batch refill must pass before any movement is applied (issue
/// #743). It is split in two on purpose - the shape of the request, then the request against the
/// stored state - and these tests hold both halves to the same rule: every refusal names the part
/// of the request it is about, an invalid row refuses the whole batch, and a valid request produces
/// no refusal at all.
/// </summary>
public class PickListRefillPolicyTests
{
    private const string Key = "7f1b0c6e-7a1e-4b1e-9f5f-2b0f9d8c1a44";

    private static PickListRefillRequestRow Row(long productId, int quantityTaken) =>
        new(productId, quantityTaken);

    private static Dictionary<long, PickListRefillProductFacts> Products(
        params PickListRefillProductFacts[] products) =>
        products.ToDictionary(product => product.ProductId);

    private static PickListRefillProductFacts Product(
        long productId, int quantityInStock, bool isActive = true) =>
        new(productId, isActive, quantityInStock);

    #region The shape of the request

    [Fact]
    public void A_single_positive_row_with_a_key_passes_the_shape_rules() =>
        Assert.True(PickListRefillPolicy.ValidateRequestShape(Key, null, [Row(1, 3)]).IsEmpty);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_idempotency_key_is_refused(string key)
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(key, null, [Row(1, 3)]);

        var refusal = Assert.Single(rejection.Request);
        Assert.Equal(PickListRefillRequestPart.IdempotencyKey, refusal.Part);
        Assert.Equal(PickListRefillPolicy.IdempotencyKeyRequiredMessage, refusal.Message);
    }

    [Fact]
    public void An_idempotency_key_longer_than_the_bound_is_refused()
    {
        var key = new string('k', PickListRefillPolicy.MaximumIdempotencyKeyLength + 1);

        var refusal = Assert.Single(
            PickListRefillPolicy.ValidateRequestShape(key, null, [Row(1, 3)]).Request);

        Assert.Equal(PickListRefillRequestPart.IdempotencyKey, refusal.Part);
        Assert.Equal(PickListRefillPolicy.IdempotencyKeyTooLongMessage, refusal.Message);
    }

    [Fact]
    public void A_key_exactly_at_the_bound_is_accepted() =>
        Assert.True(PickListRefillPolicy
            .ValidateRequestShape(new string('k', PickListRefillPolicy.MaximumIdempotencyKeyLength), null, [Row(1, 3)])
            .IsEmpty);

    [Fact]
    public void A_request_with_no_rows_is_refused()
    {
        var refusal = Assert.Single(PickListRefillPolicy.ValidateRequestShape(Key, null, []).Request);

        Assert.Equal(PickListRefillRequestPart.Rows, refusal.Part);
        Assert.Equal(PickListRefillPolicy.RowsRequiredMessage, refusal.Message);
    }

    [Fact]
    public void More_rows_than_the_bound_are_refused()
    {
        var rows = Enumerable
            .Range(1, PickListRefillPolicy.MaximumRows + 1)
            .Select(index => Row(index, 1))
            .ToArray();

        var refusal = Assert.Single(PickListRefillPolicy.ValidateRequestShape(Key, null, rows).Request);

        Assert.Equal(PickListRefillRequestPart.Rows, refusal.Part);
        Assert.Equal(PickListRefillPolicy.TooManyRowsMessage, refusal.Message);
    }

    [Fact]
    public void Notes_longer_than_the_bound_are_refused()
    {
        var notes = new string('n', PickListRefillPolicy.MaximumNotesLength + 1);

        var refusal = Assert.Single(
            PickListRefillPolicy.ValidateRequestShape(Key, notes, [Row(1, 3)]).Request);

        Assert.Equal(PickListRefillRequestPart.Notes, refusal.Part);
        Assert.Equal(PickListRefillPolicy.NotesTooLongMessage, refusal.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-7)]
    public void A_quantity_that_is_not_greater_than_zero_is_refused_on_its_own_row(int quantityTaken)
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(
            Key, null, [Row(10, 2), Row(11, quantityTaken)]);

        Assert.Empty(rejection.Request);
        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(1, refusal.RowIndex);
        Assert.Equal(11, refusal.ProductId);
        Assert.Equal(PickListRefillPolicy.QuantityMustBePositiveMessage, refusal.Message);
    }

    /// <summary>
    /// A product twice in one request is refused rather than summed: the two rows are two different
    /// statements about one storage balance, and adding them up would be this policy inventing a
    /// quantity nobody typed.
    /// </summary>
    [Fact]
    public void A_product_submitted_more_than_once_is_refused_on_the_repeated_rows()
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(
            Key, null, [Row(10, 2), Row(11, 1), Row(10, 5)]);

        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(2, refusal.RowIndex);
        Assert.Equal(10, refusal.ProductId);
        Assert.Equal(PickListRefillPolicy.DuplicateProductMessage, refusal.Message);
    }

    /// <summary>
    /// Every bad row is reported, not only the first: an operator retyping one row at a time after
    /// a save is exactly the workflow this endpoint exists to replace.
    /// </summary>
    [Fact]
    public void Every_invalid_row_is_reported()
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(
            Key, null, [Row(10, 0), Row(11, -2), Row(12, 4)]);

        Assert.Equal([0, 1], rejection.Rows.Select(row => row.RowIndex));
    }

    /// <summary>
    /// A row a caller sent as a missing value - <c>"rows": [null]</c>, which JSON carries whatever
    /// the element type declares - is refused on its own index and names no product, because there
    /// is none to name. It is refused here rather than being read anywhere: this is the only place
    /// that may see an absent row.
    /// </summary>
    [Fact]
    public void A_missing_row_is_refused_on_its_own_row_and_names_no_product()
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(Key, null, [Row(10, 2), null]);

        Assert.Empty(rejection.Request);
        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(1, refusal.RowIndex);
        Assert.Null(refusal.ProductId);
        Assert.Equal(PickListRefillPolicy.MissingRowMessage, refusal.Message);
    }

    /// <summary>
    /// A missing row is one refusal among the others rather than a short circuit, so an operator
    /// still gets every line they have to fix in one answer.
    /// </summary>
    [Fact]
    public void A_missing_row_is_reported_beside_every_other_invalid_row()
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(
            Key, null, [null, Row(11, 0), Row(12, 4), null]);

        Assert.Equal([0, 1, 3], rejection.Rows.Select(row => row.RowIndex));
        Assert.Equal(
            [PickListRefillPolicy.MissingRowMessage,
             PickListRefillPolicy.QuantityMustBePositiveMessage,
             PickListRefillPolicy.MissingRowMessage],
            rejection.Rows.Select(row => row.Message));
    }

    /// <summary>
    /// Two missing rows are two refusals and not a duplicate: an absent row is refused before
    /// anything reads a product id out of it, so neither one can collide with the other or with a
    /// real product.
    /// </summary>
    [Fact]
    public void Two_missing_rows_are_not_read_as_the_same_product()
    {
        var rejection = PickListRefillPolicy.ValidateRequestShape(Key, null, [null, null]);

        Assert.All(
            rejection.Rows,
            refusal => Assert.Equal(PickListRefillPolicy.MissingRowMessage, refusal.Message));
        Assert.Equal([0, 1], rejection.Rows.Select(row => row.RowIndex));
    }

    #endregion

    #region The request against the stored state

    [Fact]
    public void A_row_within_storage_stock_and_no_machine_is_accepted() =>
        Assert.True(PickListRefillPolicy
            .ValidateAgainstState([Row(10, 4)], Products(Product(10, 4)), machineId: null, machine: null)
            .IsEmpty);

    [Fact]
    public void A_quantity_above_storage_stock_is_refused_with_the_available_quantity()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 5)], Products(Product(10, 4)), machineId: null, machine: null);

        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(0, refusal.RowIndex);
        Assert.Equal(PickListRefillPolicy.NotEnoughStockMessage(4), refusal.Message);
    }

    /// <summary>
    /// The same sentence the single-product movement already refuses with
    /// (<see cref="Inventory.Domain.Exceptions.InsufficientStockException"/>), so a batch and a
    /// single row tell an operator the same thing about the same shelf.
    /// </summary>
    [Fact]
    public void The_stock_refusal_is_the_established_sentence() =>
        Assert.Equal(
            new Inventory.Domain.Exceptions.InsufficientStockException(4).Message,
            PickListRefillPolicy.NotEnoughStockMessage(4));

    [Fact]
    public void A_product_the_business_does_not_have_is_refused()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 1)], Products(), machineId: null, machine: null);

        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(PickListRefillPolicy.UnknownProductMessage, refusal.Message);
    }

    [Fact]
    public void A_retired_product_is_refused()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 1)], Products(Product(10, 9, isActive: false)), machineId: null, machine: null);

        var refusal = Assert.Single(rejection.Rows);
        Assert.Equal(PickListRefillPolicy.RetiredProductMessage, refusal.Message);
    }

    /// <summary>
    /// An unknown product is refused as an unknown product even when the quantity would also have
    /// been too high: the first thing wrong with the row is what an operator is told, and "not
    /// enough stock" of a product that is not theirs would be a statement about somebody else's
    /// shelf.
    /// </summary>
    [Fact]
    public void An_unknown_product_is_refused_once()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 500)], Products(), machineId: null, machine: null);

        Assert.Equal(PickListRefillPolicy.UnknownProductMessage, Assert.Single(rejection.Rows).Message);
    }

    [Fact]
    public void An_active_machine_of_the_business_is_accepted() =>
        Assert.True(PickListRefillPolicy
            .ValidateAgainstState(
                [Row(10, 1)],
                Products(Product(10, 4)),
                machineId: 7,
                machine: new PickListRefillMachineFacts(7, IsActive: true))
            .IsEmpty);

    [Fact]
    public void A_machine_that_is_not_a_machine_of_the_business_is_refused()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 1)], Products(Product(10, 4)), machineId: 7, machine: null);

        var refusal = Assert.Single(rejection.Request);
        Assert.Equal(PickListRefillRequestPart.Machine, refusal.Part);
        Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, refusal.Message);
    }

    /// <summary>
    /// A deactivated machine is refused here, unlike the single-product movement, which accepts one
    /// because it is still a machine the business operated. A pick is work being done now, so the
    /// machine has to be in service now.
    /// </summary>
    [Fact]
    public void A_deactivated_machine_is_refused()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 1)],
            Products(Product(10, 4)),
            machineId: 7,
            machine: new PickListRefillMachineFacts(7, IsActive: false));

        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(rejection.Request).Message);
    }

    /// <summary>
    /// The refusal names no machine and no business, so it cannot report whether an id exists
    /// somewhere else.
    /// </summary>
    [Fact]
    public void The_machine_refusal_names_nothing()
    {
        Assert.DoesNotContain("7", PickListRefillPolicy.MachineMustBeActiveMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("id", PickListRefillPolicy.MachineMustBeActiveMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bad_machine_and_a_bad_row_are_reported_together()
    {
        var rejection = PickListRefillPolicy.ValidateAgainstState(
            [Row(10, 99)], Products(Product(10, 4)), machineId: 7, machine: null);

        Assert.Single(rejection.Request);
        Assert.Single(rejection.Rows);
        Assert.False(rejection.IsEmpty);
    }

    #endregion
}
