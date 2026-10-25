using Inventory.Application.Machines;
using Inventory.Application.PickList;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using InventoryApi.Tests.Application.Machines;
using InventoryApi.Tests.Application.Time;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.PickList;

/// <summary>
/// The multi-machine batch use case's orchestration (issue #897): what it reads, in what order, what
/// it refuses, and - above all - what it commits. The rules themselves are
/// <c>MultiMachinePickListRefillPolicyTests</c>'s, the relational behaviour is
/// <c>EfMultiMachinePickListRefillStoreTests</c>'s and the write lock is
/// <c>MultiMachinePickListRefillConcurrencyTests</c>'s; what is tested here is that nothing is written
/// unless every rule passed, and that the claim, the machines and the stock read all happen inside the
/// transaction the write is committed in.
/// </summary>
public class RecordMultiMachinePickListRefillTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 25, 2, 30, 0, DateTimeKind.Utc);

    private const string Key = "7c6b5a49-3827-4615-9403-fe12dc34ba56";
    private const long MachineA = 5;
    private const long MachineB = 6;

    private static Mock<IPickListStorageStockStore> Storage(
        params (long ProductId, int QuantityInStock, bool IsActive)[] products)
    {
        var dictionary = products.ToDictionary(
            product => product.ProductId,
            product => new PickListStorageProduct(
                product.ProductId, $"Product {product.ProductId}", product.QuantityInStock, product.IsActive));

        var store = new Mock<IPickListStorageStockStore>();
        store
            .Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)dictionary);
        return store;
    }

    private static FakeLocalMachineStore Machines(bool machineBIsActive = true) =>
        new(
        [
            new LocalMachineRecord(MachineA, "Machine A", IsActive: true),
            new LocalMachineRecord(MachineB, "Machine B", machineBIsActive),
        ]);

    private static RecordMultiMachinePickListRefill UseCase(
        IMultiMachinePickListRefillStore refills,
        Mock<IPickListStorageStockStore>? storage = null,
        FakeLocalMachineStore? machines = null) =>
        new(
            refills,
            (storage ?? Storage((10, 10, true))).Object,
            machines ?? Machines(),
            new FakeClock(RecordedAt));

    private static MultiMachinePickListRefillInput Input(
        string? key = Key,
        string? notes = null,
        params (long? MachineId, long ProductId, int Quantity)[] allocations) =>
        new(
            key,
            notes,
            [.. allocations.Select(allocation => new MultiMachineRefillAllocation(
                allocation.MachineId, allocation.ProductId, allocation.Quantity))]);

    #region Recording the batch

    [Fact]
    public async Task A_batch_across_two_machines_is_recorded_and_committed()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 10, true), (11, 6, true)))
            .Handle(
                Input(allocations: [(MachineA, 10, 3), (MachineB, 10, 4), (MachineB, 11, 2)]),
                CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
        Assert.Equal(
            [(MachineA, 10L, 3), (MachineB, 10L, 4), (MachineB, 11L, 2)],
            attempt.Record!.Allocations.Select(
                allocation => (allocation.MachineId, allocation.ProductId, allocation.QuantityTaken)));
        Assert.Equal(RecordedAt, attempt.Record.RecordedAtUtc);
    }

    /// <summary>
    /// The instant is the server's, from the clock port, and the key and note are what the use case
    /// normalised - nothing a caller sent reaches the claim unexamined.
    /// </summary>
    [Fact]
    public async Task The_claim_carries_the_servers_instant_and_the_normalised_key_and_note()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        await UseCase(store).Handle(
            Input(key: $"  {Key}  ", notes: "  Friday run  ", allocations: [(MachineA, 10, 1)]),
            CancellationToken.None);

        Assert.Equal(Key, store.LastClaim!.IdempotencyKey);
        Assert.Equal("Friday run", store.LastClaim.Notes);
        Assert.Equal(RecordedAt, store.LastClaim.RecordedAtUtc);
    }

    #endregion

    #region Refusals leave nothing behind

    /// <summary>
    /// The acceptance criterion: storage 10, allocations of 6 and 6. Each fits on its own and together
    /// they do not, so nothing is written - no transaction is even committed.
    /// </summary>
    [Fact]
    public async Task Allocations_that_together_exceed_storage_refuse_the_whole_batch()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 10, true)))
            .Handle(Input(allocations: [(MachineA, 10, 6), (MachineB, 10, 6)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal([0, 1], attempt.Rejection!.Allocations.Select(refusal => refusal.AllocationIndex));
        Assert.All(
            attempt.Rejection.Allocations,
            refusal => Assert.Equal(
                MultiMachinePickListRefillPolicy.NotEnoughStockForTotalMessage(12, 10), refusal.Message));
        Assert.Equal(0, store.ApplyCalls);
        Assert.Equal(0, store.TransactionsCommitted);
        Assert.Empty(store.Committed);
    }

    [Fact]
    public async Task A_machine_that_is_not_the_callers_refuses_the_whole_batch()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store, machines: new FakeLocalMachineStore())
            .Handle(Input(allocations: [(MachineA, 10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.ApplyCalls);
        Assert.Equal(0, store.TransactionsCommitted);
    }

    /// <summary>
    /// One deactivated machine refuses the batch, including the allocations for the machines that are
    /// in service: a run records every allocation or none of them.
    /// </summary>
    [Fact]
    public async Task A_deactivated_machine_refuses_the_whole_batch()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store, machines: Machines(machineBIsActive: false))
            .Handle(Input(allocations: [(MachineA, 10, 1), (MachineB, 10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        var refusal = Assert.Single(attempt.Rejection!.Allocations);
        Assert.Equal(1, refusal.AllocationIndex);
        Assert.Equal(PickListRefillPolicy.MachineMustBeActiveMessage, refusal.Message);
        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task An_allocation_with_no_machine_refuses_the_whole_batch_before_any_transaction()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(Input(allocations: [(MachineA, 10, 1), (null, 10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            MultiMachinePickListRefillPolicy.MachineRequiredMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    /// <summary>
    /// A malformed request must not take the write lock every other picker waits on, so the shape
    /// rules are applied before the transaction is opened.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_request_with_no_idempotency_key_is_refused_before_any_transaction(string key)
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(Input(key: key, allocations: [(MachineA, 10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.IdempotencyKeyRequiredMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task An_empty_batch_is_refused_before_any_transaction()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            MultiMachinePickListRefillPolicy.AllocationsRequiredMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task A_null_allocation_list_is_refused_rather_than_throwing()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store).Handle(
            new MultiMachinePickListRefillInput(Key, null, null), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            MultiMachinePickListRefillPolicy.AllocationsRequiredMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
    }

    [Fact]
    public async Task A_missing_allocation_is_refused_rather_than_read()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store).Handle(
            new MultiMachinePickListRefillInput(
                Key, null, [new MultiMachineRefillAllocation(MachineA, 10, 1), null]),
            CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            MultiMachinePickListRefillPolicy.MissingAllocationMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public async Task A_non_positive_quantity_is_refused_before_any_transaction(int quantity)
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(Input(allocations: [(MachineA, 10, quantity)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.QuantityMustBePositiveMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task The_same_machine_and_product_twice_is_refused_before_any_transaction()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(Input(allocations: [(MachineA, 10, 1), (MachineA, 10, 2)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            MultiMachinePickListRefillPolicy.DuplicateAllocationMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task Another_businesss_product_refuses_the_whole_batch()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 10, true)))
            .Handle(Input(allocations: [(MachineA, 10, 1), (MachineB, 99, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.UnknownProductMessage,
            Assert.Single(attempt.Rejection!.Allocations).Message);
        Assert.Equal(0, store.ApplyCalls);
    }

    #endregion

    #region The idempotency key

    /// <summary>
    /// The claim, the machines and the stock read all have to be inside the transaction, because all
    /// three decide the write. A read taken outside it is a read two callers pass.
    /// </summary>
    [Fact]
    public async Task The_key_claim_and_the_write_happen_inside_one_transaction()
    {
        var store = new FakeMultiMachinePickListRefillStore();

        await UseCase(store).Handle(Input(allocations: [(MachineA, 10, 1)]), CancellationToken.None);

        Assert.True(store.KeyLookedUpInsideTransaction);
        Assert.True(store.AppliedInsideTransaction);
        Assert.Equal(1, store.TransactionsOpened);
    }

    [Fact]
    public async Task The_same_key_and_the_same_batch_returns_the_original_and_records_nothing()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));
        var input = Input(notes: "Friday run", allocations: [(MachineA, 10, 3), (MachineB, 10, 2)]);

        var first = await useCase.Handle(input, CancellationToken.None);
        var retry = await useCase.Handle(input, CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, first.Outcome);
        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Equal(first.Record, retry.Record);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
    }

    /// <summary>
    /// The criterion's order-independence: a retry whose allocations arrive in another order is the
    /// same submission, because the order they were typed in is not part of what is deducted.
    /// </summary>
    [Fact]
    public async Task The_same_allocations_in_another_order_is_the_same_submission()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true), (11, 10, true)));

        await useCase.Handle(
            Input(allocations: [(MachineA, 10, 3), (MachineB, 11, 2)]), CancellationToken.None);
        var retry = await useCase.Handle(
            Input(allocations: [(MachineB, 11, 2), (MachineA, 10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task The_same_key_with_different_allocations_is_a_conflict_and_records_nothing()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));

        await useCase.Handle(Input(allocations: [(MachineA, 10, 3)]), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(Input(allocations: [(MachineA, 10, 4)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
    }

    /// <summary>
    /// The same products and quantities moved to another machine is a different pick, not a retry.
    /// </summary>
    [Fact]
    public async Task The_same_key_with_the_quantities_on_another_machine_is_a_conflict()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));

        await useCase.Handle(Input(allocations: [(MachineA, 10, 3)]), CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(Input(allocations: [(MachineB, 10, 3)]), CancellationToken.None));
    }

    [Fact]
    public async Task The_same_key_with_a_different_note_is_a_conflict()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));

        await useCase.Handle(
            Input(notes: "Friday run", allocations: [(MachineA, 10, 3)]), CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(
                Input(notes: "Saturday run", allocations: [(MachineA, 10, 3)]), CancellationToken.None));
    }

    /// <summary>
    /// A key the single-machine endpoint already spent is refused as reused rather than replayed:
    /// nothing it recorded is reinterpreted as a batch. The adapter reports such a refill as one this
    /// contract cannot answer with, and the use case fails closed on it whatever the fingerprint says.
    /// </summary>
    [Fact]
    public async Task A_key_that_recorded_a_single_machine_refill_is_a_conflict()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        store.SeedUnanswerable(Key, PickListRefillFingerprint.Compute(MachineA, null, []));

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => UseCase(store).Handle(Input(allocations: [(MachineA, 10, 1)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        Assert.Equal(0, store.ApplyCalls);
    }

    [Fact]
    public async Task A_key_is_matched_after_trimming()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));

        await useCase.Handle(Input(allocations: [(MachineA, 10, 3)]), CancellationToken.None);
        var retry = await useCase.Handle(
            Input(key: $"  {Key}  ", allocations: [(MachineA, 10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
    }

    [Fact]
    public async Task A_blank_note_is_the_same_submission_as_no_note()
    {
        var store = new FakeMultiMachinePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true)));

        await useCase.Handle(Input(allocations: [(MachineA, 10, 3)]), CancellationToken.None);
        var retry = await useCase.Handle(
            Input(notes: "   ", allocations: [(MachineA, 10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Null(store.LastClaim!.Notes);
    }

    /// <summary>
    /// The index refusing the write is the last line of defence behind the claim. It has to become the
    /// same stable conflict, because a provider error surfacing here would be a <c>500</c> for a
    /// refusal the contract answers with <c>409</c>.
    /// </summary>
    [Fact]
    public async Task A_duplicate_key_refused_by_the_index_becomes_the_same_conflict()
    {
        var store = new FakeMultiMachinePickListRefillStore
        {
            ApplyThrows = new InvalidOperationException("UNIQUE constraint failed"),
            ApplyFailureIsADuplicateKey = true,
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => UseCase(store).Handle(Input(allocations: [(MachineA, 10, 1)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        Assert.Equal(0, store.TransactionsCommitted);
    }

    /// <summary>
    /// Any other database failure keeps propagating as itself, and commits nothing: translating one
    /// would publish an internal message as a caller-facing conflict.
    /// </summary>
    [Fact]
    public async Task Another_database_failure_is_not_reported_as_a_key_conflict()
    {
        var store = new FakeMultiMachinePickListRefillStore
        {
            ApplyThrows = new InvalidOperationException("disk I/O error"),
            ApplyFailureIsADuplicateKey = false,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(store).Handle(Input(allocations: [(MachineA, 10, 1)]), CancellationToken.None));

        Assert.Equal(0, store.TransactionsCommitted);
        Assert.Empty(store.Committed);
    }

    #endregion
}
