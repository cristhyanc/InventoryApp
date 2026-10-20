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
/// The batch refill use case's orchestration (issue #743): what it reads, in what order, what it
/// refuses, and - above all - what it commits. The rules themselves are
/// <c>PickListRefillPolicyTests</c>'s, the relational behaviour is
/// <c>EfPickListRefillStoreTests</c>'s and the write lock is <c>PickListRefillConcurrencyTests</c>'s;
/// what is tested here is that nothing is written unless every rule passed, and that the claim and
/// the stock read both happen inside the transaction the write is committed in.
/// </summary>
public class RecordPickListRefillTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc);

    private const string Key = "d4d1f0f6-2f6e-4a2f-9a0a-2b4e6f8c0a11";
    private const long MachineId = 5;

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

    private static FakeLocalMachineStore Machines(bool isActive = true) =>
        new([new LocalMachineRecord(MachineId, "Machine A", isActive)]);

    private static RecordPickListRefill UseCase(
        IPickListRefillStore refills,
        Mock<IPickListStorageStockStore>? storage = null,
        FakeLocalMachineStore? machines = null) =>
        new(refills, (storage ?? Storage((10, 10, true))).Object, machines ?? Machines(), new FakeClock(RecordedAt));

    private static PickListRefillInput Input(
        string? key = Key,
        long? machineId = null,
        string? notes = null,
        params (long ProductId, int Quantity)[] rows) =>
        new(
            key,
            machineId,
            notes,
            [.. rows.Select(row => new PickListRefillRequestRow(row.ProductId, row.Quantity))]);

    [Fact]
    public async Task A_valid_refill_is_recorded_and_committed()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 10, true), (11, 4, true)))
            .Handle(Input(rows: [(10, 3), (11, 4)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
        Assert.Equal([3, 4], attempt.Record!.Rows.Select(row => row.QuantityTaken));
        Assert.Equal(RecordedAt, attempt.Record.RecordedAtUtc);
    }

    /// <summary>
    /// A row exactly at the shelf's quantity is a valid pick: the check is "more than storage
    /// holds", not "all of it".
    /// </summary>
    [Fact]
    public async Task A_row_that_takes_all_of_the_storage_stock_is_recorded()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 3, true)))
            .Handle(Input(rows: [(10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
    }

    [Fact]
    public async Task A_refill_with_no_machine_is_recorded()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(rows: [(10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        Assert.Null(store.LastClaim!.MachineId);
    }

    [Fact]
    public async Task A_refill_for_an_active_machine_of_the_business_records_that_machine()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(Input(machineId: MachineId, rows: [(10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, attempt.Outcome);
        Assert.Equal(MachineId, attempt.Record!.MachineId);
        Assert.Equal(MachineId, store.LastClaim!.MachineId);
    }

    [Fact]
    public async Task A_machine_that_is_not_the_callers_refuses_the_whole_refill()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store, machines: new FakeLocalMachineStore())
            .Handle(Input(machineId: MachineId, rows: [(10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
        Assert.Equal(0, store.ApplyCalls);
        Assert.Equal(0, store.TransactionsCommitted);
    }

    [Fact]
    public async Task A_deactivated_machine_refuses_the_whole_refill()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store, machines: Machines(isActive: false))
            .Handle(Input(machineId: MachineId, rows: [(10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(0, store.ApplyCalls);
    }

    /// <summary>
    /// The whole point of the endpoint: one bad row records nothing, not "the good rows".
    /// </summary>
    [Fact]
    public async Task One_row_above_storage_stock_refuses_the_whole_refill()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store, Storage((10, 10, true), (11, 1, true)))
            .Handle(Input(rows: [(10, 3), (11, 4)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        var refusal = Assert.Single(attempt.Rejection!.Rows);
        Assert.Equal(1, refusal.RowIndex);
        Assert.Equal(PickListRefillPolicy.NotEnoughStockMessage(1), refusal.Message);
        Assert.Equal(0, store.ApplyCalls);
        Assert.Empty(store.Committed);
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
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(key: key, rows: [(10, 1)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.IdempotencyKeyRequiredMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task A_request_with_no_rows_is_refused_before_any_transaction()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task A_null_row_list_is_refused_rather_than_throwing()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store)
            .Handle(new PickListRefillInput(Key, null, null, null), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.RowsRequiredMessage,
            Assert.Single(attempt.Rejection!.Request).Message);
    }

    [Fact]
    public async Task A_non_positive_quantity_is_refused_before_any_transaction()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(rows: [(10, 0)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.QuantityMustBePositiveMessage,
            Assert.Single(attempt.Rejection!.Rows).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    [Fact]
    public async Task A_product_submitted_twice_is_refused_before_any_transaction()
    {
        var store = new FakePickListRefillStore();

        var attempt = await UseCase(store).Handle(Input(rows: [(10, 1), (10, 2)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Rejected, attempt.Outcome);
        Assert.Equal(
            PickListRefillPolicy.DuplicateProductMessage,
            Assert.Single(attempt.Rejection!.Rows).Message);
        Assert.Equal(0, store.TransactionsOpened);
    }

    #region The idempotency key

    /// <summary>
    /// The claim and the stock read both have to be inside the transaction, because both decide the
    /// write. A read taken outside it is a read two callers pass.
    /// </summary>
    [Fact]
    public async Task The_key_claim_and_the_write_happen_inside_one_transaction()
    {
        var store = new FakePickListRefillStore();

        await UseCase(store).Handle(Input(rows: [(10, 1)]), CancellationToken.None);

        Assert.True(store.KeyLookedUpInsideTransaction);
        Assert.True(store.AppliedInsideTransaction);
        Assert.Equal(1, store.TransactionsOpened);
    }

    [Fact]
    public async Task The_same_key_and_the_same_request_returns_the_original_and_records_nothing()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);
        var input = Input(machineId: MachineId, notes: "Friday run", rows: [(10, 3)]);

        var first = await useCase.Handle(input, CancellationToken.None);
        var retry = await useCase.Handle(input, CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Recorded, first.Outcome);
        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Equal(first.Record, retry.Record);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
    }

    /// <summary>
    /// A retry whose rows arrive in another order is the same submission, because row order is not
    /// part of what is deducted.
    /// </summary>
    [Fact]
    public async Task The_same_rows_in_another_order_is_the_same_submission()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store, Storage((10, 10, true), (11, 10, true)));

        await useCase.Handle(Input(rows: [(10, 3), (11, 2)]), CancellationToken.None);
        var retry = await useCase.Handle(Input(rows: [(11, 2), (10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Equal(1, store.ApplyCalls);
    }

    [Fact]
    public async Task The_same_key_with_a_different_request_is_a_conflict_and_records_nothing()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);

        await useCase.Handle(Input(rows: [(10, 3)]), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(Input(rows: [(10, 4)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        Assert.Equal(1, store.ApplyCalls);
        Assert.Equal(1, store.TransactionsCommitted);
    }

    [Fact]
    public async Task The_same_key_with_a_different_machine_is_a_conflict()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);

        await useCase.Handle(Input(rows: [(10, 3)]), CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(Input(machineId: MachineId, rows: [(10, 3)]), CancellationToken.None));
    }

    [Fact]
    public async Task The_same_key_with_a_different_note_is_a_conflict()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);

        await useCase.Handle(Input(notes: "Friday run", rows: [(10, 3)]), CancellationToken.None);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => useCase.Handle(Input(notes: "Saturday run", rows: [(10, 3)]), CancellationToken.None));
    }

    /// <summary>
    /// A key is stored and matched trimmed, so trailing whitespace from a form field is not a
    /// second key for the same submission.
    /// </summary>
    [Fact]
    public async Task A_key_is_matched_after_trimming()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);

        await useCase.Handle(Input(rows: [(10, 3)]), CancellationToken.None);
        var retry = await useCase.Handle(Input(key: $"  {Key}  ", rows: [(10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Equal(Key, store.LastClaim!.IdempotencyKey);
    }

    /// <summary>
    /// A blank note and no note are the same submission, so a client that sends an empty string on
    /// a retry is not refused as a different request.
    /// </summary>
    [Fact]
    public async Task A_blank_note_is_the_same_submission_as_no_note()
    {
        var store = new FakePickListRefillStore();
        var useCase = UseCase(store);

        await useCase.Handle(Input(rows: [(10, 3)]), CancellationToken.None);
        var retry = await useCase.Handle(Input(notes: "   ", rows: [(10, 3)]), CancellationToken.None);

        Assert.Equal(PickListRefillOutcome.Replayed, retry.Outcome);
        Assert.Null(store.LastClaim!.Notes);
    }

    /// <summary>
    /// The index refusing the write is the last line of defence behind the claim. It has to become
    /// the same stable conflict, because a provider error surfacing here would be a <c>500</c> for a
    /// refusal the contract answers with <c>409</c>.
    /// </summary>
    [Fact]
    public async Task A_duplicate_key_refused_by_the_index_becomes_the_same_conflict()
    {
        var store = new FakePickListRefillStore
        {
            ApplyThrows = new InvalidOperationException("UNIQUE constraint failed"),
            ApplyFailureIsADuplicateKey = true,
        };

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => UseCase(store).Handle(Input(rows: [(10, 1)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
        Assert.Equal(0, store.TransactionsCommitted);
    }

    /// <summary>
    /// Any other database failure keeps propagating as itself: translating one would publish an
    /// internal message as a caller-facing conflict.
    /// </summary>
    [Fact]
    public async Task Another_database_failure_is_not_reported_as_a_key_conflict()
    {
        var store = new FakePickListRefillStore
        {
            ApplyThrows = new InvalidOperationException("disk I/O error"),
            ApplyFailureIsADuplicateKey = false,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => UseCase(store).Handle(Input(rows: [(10, 1)]), CancellationToken.None));

        Assert.Equal(0, store.TransactionsCommitted);
    }

    #endregion
}
