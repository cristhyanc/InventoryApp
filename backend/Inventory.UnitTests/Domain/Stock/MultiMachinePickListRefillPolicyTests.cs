using Inventory.Domain.Stock;
using Xunit;

namespace InventoryApi.Tests.Domain.Stock;

/// <summary>
/// The multi-machine refill batch's rules (issue #897): what the request alone decides, what the
/// stored state decides, and - the one this endpoint exists for - that storage is checked against a
/// product's <em>total</em> across the machines it was allocated to.
/// </summary>
public class MultiMachinePickListRefillPolicyTests
{
    private const string Key = "e7b1c0d2-5a6f-4c3b-9d8e-1f2a3b4c5d6e";
    private const long MachineA = 7;
    private const long MachineB = 8;
    private const long MachineC = 9;

    private static MultiMachineRefillAllocation Allocation(
        long? machineId, long productId, int quantity) => new(machineId, productId, quantity);

    private static MultiMachineRefillRejection Shape(
        params MultiMachineRefillAllocation?[] allocations) =>
        MultiMachinePickListRefillPolicy.ValidateRequestShape(Key, null, allocations);

    private static MultiMachineRefillRejection State(
        IReadOnlyList<MultiMachineRefillLine> lines,
        (long MachineId, bool IsActive)[]? machines = null,
        params (long ProductId, int QuantityInStock, bool IsActive)[] products) =>
        MultiMachinePickListRefillPolicy.ValidateAgainstState(
            lines,
            (machines ?? [(MachineA, true), (MachineB, true)]).ToDictionary(
                machine => machine.MachineId,
                machine => new PickListRefillMachineFacts(machine.MachineId, machine.IsActive)),
            products.ToDictionary(
                product => product.ProductId,
                product => new PickListRefillProductFacts(
                    product.ProductId, product.IsActive, product.QuantityInStock)));

    #region The request on its own

    [Fact]
    public void A_batch_across_two_machines_is_accepted()
    {
        var rejection = Shape(
            Allocation(MachineA, 100, 6),
            Allocation(MachineB, 100, 4),
            Allocation(MachineA, 101, 2));

        Assert.True(rejection.IsEmpty);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_missing_idempotency_key_is_refused(string? key)
    {
        var rejection = MultiMachinePickListRefillPolicy.ValidateRequestShape(
            key, null, [Allocation(MachineA, 100, 1)]);

        var refusal = Assert.Single(rejection.Request);
        Assert.Equal(MultiMachineRefillRequestPart.IdempotencyKey, refusal.Part);
        Assert.Equal(PickListRefillPolicy.IdempotencyKeyRequiredMessage, refusal.Message);
    }

    [Fact]
    public void An_idempotency_key_longer_than_the_column_is_refused()
    {
        var rejection = MultiMachinePickListRefillPolicy.ValidateRequestShape(
            new string('k', PickListRefillPolicy.MaximumIdempotencyKeyLength + 1),
            null,
            [Allocation(MachineA, 100, 1)]);

        Assert.Equal(
            PickListRefillPolicy.IdempotencyKeyTooLongMessage,
            Assert.Single(rejection.Request).Message);
    }

    [Fact]
    public void A_note_longer_than_the_column_is_refused()
    {
        var rejection = MultiMachinePickListRefillPolicy.ValidateRequestShape(
            Key,
            new string('n', PickListRefillPolicy.MaximumNotesLength + 1),
            [Allocation(MachineA, 100, 1)]);

        var refusal = Assert.Single(rejection.Request);
        Assert.Equal(MultiMachineRefillRequestPart.Notes, refusal.Part);
        Assert.Equal(PickListRefillPolicy.NotesTooLongMessage, refusal.Message);
    }

    [Fact]
    public void An_empty_batch_is_refused()
    {
        var rejection = Shape();

        var refusal = Assert.Single(rejection.Request);
        Assert.Equal(MultiMachineRefillRequestPart.Allocations, refusal.Part);
        Assert.Equal(MultiMachinePickListRefillPolicy.AllocationsRequiredMessage, refusal.Message);
    }

    [Fact]
    public void More_allocations_than_the_bound_are_refused()
    {
        var allocations = Enumerable
            .Range(0, MultiMachinePickListRefillPolicy.MaximumAllocations + 1)
            .Select(index => Allocation(MachineA, index, 1))
            .ToArray<MultiMachineRefillAllocation?>();

        var rejection = Shape(allocations);

        Assert.Equal(
            MultiMachinePickListRefillPolicy.TooManyAllocationsMessage,
            Assert.Single(rejection.Request).Message);
    }

    /// <summary>
    /// A JSON array can carry a missing value whatever the element type declares, so it is refused
    /// here - on its own index, naming neither a machine nor a product, because it has none.
    /// </summary>
    [Fact]
    public void An_allocation_sent_as_a_missing_value_is_refused_on_its_own_index()
    {
        var rejection = Shape(Allocation(MachineA, 100, 2), null);

        var refusal = Assert.Single(rejection.Allocations);
        Assert.Equal(1, refusal.AllocationIndex);
        Assert.Null(refusal.MachineId);
        Assert.Null(refusal.ProductId);
        Assert.Equal(MultiMachinePickListRefillPolicy.MissingAllocationMessage, refusal.Message);
    }

    /// <summary>
    /// A no-machine refill is still recorded by the single-machine endpoint, which is unchanged; an
    /// allocation of a multi-machine batch has to say which machine it was walked to.
    /// </summary>
    [Fact]
    public void An_allocation_with_no_machine_is_refused()
    {
        var rejection = Shape(Allocation(null, 100, 2));

        var refusal = Assert.Single(rejection.Allocations);
        Assert.Equal(0, refusal.AllocationIndex);
        Assert.Null(refusal.MachineId);
        Assert.Equal(100, refusal.ProductId);
        Assert.Equal(MultiMachinePickListRefillPolicy.MachineRequiredMessage, refusal.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_quantity_that_is_not_greater_than_zero_is_refused(int quantity)
    {
        var rejection = Shape(Allocation(MachineA, 100, quantity));

        var refusal = Assert.Single(rejection.Allocations);
        Assert.Equal(PickListRefillPolicy.QuantityMustBePositiveMessage, refusal.Message);
        Assert.Equal(MachineA, refusal.MachineId);
        Assert.Equal(100, refusal.ProductId);
    }

    [Fact]
    public void The_same_machine_and_product_twice_is_refused_rather_than_summed()
    {
        var rejection = Shape(Allocation(MachineA, 100, 2), Allocation(MachineA, 100, 3));

        var refusal = Assert.Single(rejection.Allocations);
        Assert.Equal(1, refusal.AllocationIndex);
        Assert.Equal(MultiMachinePickListRefillPolicy.DuplicateAllocationMessage, refusal.Message);
    }

    /// <summary>One product on two machines is the whole point of the endpoint, not a duplicate.</summary>
    [Fact]
    public void The_same_product_on_two_machines_is_accepted()
    {
        Assert.True(Shape(Allocation(MachineA, 100, 2), Allocation(MachineB, 100, 3)).IsEmpty);
    }

    [Fact]
    public void Two_products_on_one_machine_are_accepted()
    {
        Assert.True(Shape(Allocation(MachineA, 100, 2), Allocation(MachineA, 101, 3)).IsEmpty);
    }

    /// <summary>
    /// Every refusal is reported rather than only the first: an operator correcting one line per
    /// round trip is the workflow this replaces.
    /// </summary>
    [Fact]
    public void Every_refusal_is_reported()
    {
        var rejection = MultiMachinePickListRefillPolicy.ValidateRequestShape(
            null,
            null,
            [Allocation(MachineA, 100, 0), null, Allocation(null, 101, 1)]);

        Assert.Single(rejection.Request);
        Assert.Equal([0, 1, 2], rejection.Allocations.Select(refusal => refusal.AllocationIndex));
    }

    #endregion

    #region The request against the stored state

    [Fact]
    public void A_batch_within_the_stored_state_is_accepted()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 100, 4), new(MachineA, 101, 1)],
            products: [(100, 10, true), (101, 5, true)]);

        Assert.True(rejection.IsEmpty);
    }

    [Fact]
    public void A_machine_that_is_not_the_businesss_refuses_its_allocation()
    {
        var rejection = State(
            [new(MachineA, 100, 1)],
            machines: [],
            products: [(100, 10, true)]);

        var refusal = Assert.Single(rejection.Allocations);
        Assert.Equal(0, refusal.AllocationIndex);
        Assert.Equal(MachineA, refusal.MachineId);
        Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, refusal.Message);
    }

    /// <summary>
    /// A pick is work being done now, so the machine has to be in service now - the same rule the
    /// single-machine batch applies, and deliberately stricter than
    /// <c>ManualStockAdjustmentPolicy</c>'s, which accepts a machine the business used to operate.
    /// </summary>
    [Fact]
    public void A_deactivated_machine_refuses_its_allocation()
    {
        var rejection = State(
            [new(MachineA, 100, 1)],
            machines: [(MachineA, false)],
            products: [(100, 10, true)]);

        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(rejection.Allocations).Message);
    }

    [Fact]
    public void A_product_that_is_not_the_businesss_refuses_its_allocation()
    {
        var rejection = State([new(MachineA, 100, 1)], products: []);

        Assert.Equal(
            PickListRefillPolicy.UnknownProductMessage,
            Assert.Single(rejection.Allocations).Message);
    }

    [Fact]
    public void A_retired_product_refuses_its_allocation()
    {
        var rejection = State([new(MachineA, 100, 1)], products: [(100, 10, false)]);

        Assert.Equal(
            PickListRefillPolicy.RetiredProductMessage,
            Assert.Single(rejection.Allocations).Message);
    }

    /// <summary>
    /// The acceptance criterion: storage 10 with allocations of 6 and 6 is refused, even though each
    /// allocation fits on its own - they come off one shelf. Both contributing allocations carry the
    /// refusal, because the thing to correct is the combination.
    /// </summary>
    [Fact]
    public void Allocations_of_one_product_that_together_exceed_storage_refuse_both_of_them()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 100, 6)],
            products: [(100, 10, true)]);

        Assert.Equal([0, 1], rejection.Allocations.Select(refusal => refusal.AllocationIndex));
        Assert.All(
            rejection.Allocations,
            refusal => Assert.Equal(
                MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(12, 10), refusal.Message));
    }

    /// <summary>
    /// A product's total exactly at the shelf's quantity is a valid pick: the check is "more than
    /// storage holds", not "all of it".
    /// </summary>
    [Fact]
    public void Allocations_that_together_take_all_of_the_storage_stock_are_accepted()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 100, 4)],
            products: [(100, 10, true)]);

        Assert.True(rejection.IsEmpty);
    }

    /// <summary>
    /// The sum is per product, not per request: two products each within their own stock are
    /// accepted however they add up together.
    /// </summary>
    [Fact]
    public void Each_product_is_summed_against_its_own_stock()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 101, 6)],
            products: [(100, 6, true), (101, 6, true)]);

        Assert.True(rejection.IsEmpty);
    }

    [Fact]
    public void One_allocation_above_the_storage_stock_is_refused_with_its_own_total()
    {
        var rejection = State([new(MachineA, 100, 11)], products: [(100, 10, true)]);

        Assert.Equal(
            MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(11, 10),
            Assert.Single(rejection.Allocations).Message);
    }

    /// <summary>
    /// An allocation already refused for its machine keeps that refusal rather than collecting a
    /// second one: one refusal per allocation, so a client can put one message on each line.
    /// </summary>
    [Fact]
    public void An_allocation_refused_for_its_machine_is_not_refused_twice()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 100, 6)],
            machines: [(MachineA, true), (MachineB, false)],
            products: [(100, 10, true)]);

        Assert.Equal([0, 1], rejection.Allocations.Select(refusal => refusal.AllocationIndex));
        Assert.Equal(
            MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(12, 10),
            rejection.Allocations[0].Message);
        Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, rejection.Allocations[1].Message);
    }

    /// <summary>
    /// A total too large for a 32-bit sum is still a total that does not fit on the shelf. Two
    /// allocations of <see cref="int.MaxValue"/> are each a positive whole number the shape half
    /// accepts, and adding them up in an <see cref="int"/> wraps to -2 - which compares as
    /// comfortably below a storage quantity of 10, so the one rule this endpoint exists for would be
    /// skipped and the batch recorded. The sum is therefore accumulated in 64 bits and compared
    /// before it is narrowed anywhere.
    /// </summary>
    [Fact]
    public void Allocations_whose_total_exceeds_a_32_bit_sum_are_still_refused()
    {
        var rejection = State(
            [new(MachineA, 100, int.MaxValue), new(MachineB, 100, int.MaxValue)],
            products: [(100, 10, true)]);

        Assert.Equal([0, 1], rejection.Allocations.Select(refusal => refusal.AllocationIndex));
        Assert.All(
            rejection.Allocations,
            refusal => Assert.Equal(
                MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(2L * int.MaxValue, 10),
                refusal.Message));
    }

    /// <summary>
    /// The same defect with quantities nobody would call extreme: three allocations of 800,000,000
    /// are each far inside an <see cref="int"/>, and their real total - 2,400,000,000 - is not. The
    /// refusal states that real total rather than the wrapped one.
    /// </summary>
    [Fact]
    public void Ordinary_quantities_whose_total_exceeds_a_32_bit_sum_are_refused_with_the_real_total()
    {
        const int quantity = 800_000_000;

        var rejection = State(
            [new(MachineA, 100, quantity), new(MachineB, 100, quantity), new(MachineC, 100, quantity)],
            machines: [(MachineA, true), (MachineB, true), (MachineC, true)],
            products: [(100, 10, true)]);

        Assert.Equal([0, 1, 2], rejection.Allocations.Select(refusal => refusal.AllocationIndex));
        Assert.All(
            rejection.Allocations,
            refusal => Assert.Equal(
                MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(3L * quantity, 10),
                refusal.Message));
    }

    /// <summary>
    /// The widest total the contract can be asked for - <see cref="MultiMachinePickListRefillPolicy.MaximumAllocations"/>
    /// allocations of <see cref="int.MaxValue"/> - is summed exactly and refused, which is what makes
    /// 64 bits sufficient rather than merely wider: the accumulator never overflows, so the refusal
    /// is a refusal and never an arithmetic failure reported as a server error.
    /// </summary>
    [Fact]
    public void The_largest_batch_of_the_largest_quantities_is_summed_exactly_and_refused()
    {
        const int allocations = MultiMachinePickListRefillPolicy.MaximumAllocations;

        var rejection = State(
            [.. Enumerable.Range(0, allocations).Select(index => new MultiMachineRefillLine(
                MachineA + index, 100, int.MaxValue))],
            machines: [.. Enumerable.Range(0, allocations).Select(index => (MachineA + index, true))],
            products: [(100, 10, true)]);

        Assert.Equal(allocations, rejection.Allocations.Count);
        Assert.All(
            rejection.Allocations,
            refusal => Assert.Equal(
                MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(
                    allocations * (long)int.MaxValue, 10),
                refusal.Message));
    }

    /// <summary>
    /// An unknown or retired product has no shelf to compare a total with, so it is refused as what
    /// it is and no stock statement is made about somebody else's product.
    /// </summary>
    [Fact]
    public void An_unknown_product_is_not_also_refused_for_stock()
    {
        var rejection = State(
            [new(MachineA, 100, 6), new(MachineB, 100, 6)],
            products: []);

        Assert.All(
            rejection.Allocations,
            refusal => Assert.Equal(PickListRefillPolicy.UnknownProductMessage, refusal.Message));
    }

    #endregion
}
