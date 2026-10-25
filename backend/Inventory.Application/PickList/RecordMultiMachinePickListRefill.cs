using Inventory.Application.Machines;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;

namespace Inventory.Application.PickList;

/// <summary>
/// Records a whole pick run - several machines, in one request, all or nothing (issue #897). It is the
/// backend half of the multi-machine manual pick; the entry screen that calls it is its own issue.
///
/// <para><strong>Why it exists beside <see cref="RecordPickListRefill"/>.</strong> That use case
/// records one refill with one machine on the batch, so a run that visited three machines had to be
/// sent as three requests, each with its own idempotency key and its own storage check. Three requests
/// are three chances to half-save a run, and - worse - each one's storage check passes on its own: a
/// shelf of 10 can satisfy "6 for machine A" and then "6 for machine B" only because the two were
/// never weighed together. This use case weighs them together. The single-machine endpoint is
/// untouched, and a refill for no machine at all still goes there.</para>
///
/// <para><strong>One request records every allocation or none of them.</strong> The checks that decide
/// the write and the write itself are one operation: the whole use case runs inside the store's
/// write-locked transaction, which commits only after the last movement is saved. A refusal - a single
/// bad allocation included - returns without committing, so the transaction's disposal discards every
/// movement the batch had staged.</para>
///
/// <para><strong>The storage check is inside the write lock, over each product's total, and that is
/// the point.</strong> Two requests that each have enough stock on their own but together take more
/// than the shelf holds is not a theoretical race: it is two people picking from one storeroom. A
/// stock read taken before the lock is a read both of them pass. So the transaction is
/// <c>BEGIN IMMEDIATE</c> on SQLite - the write lock is held before <see cref="IPickListStorageStockStore"/>
/// and <see cref="ILocalMachineStore"/> are asked anything - and the loser waits, re-reads the
/// winner's committed quantities, and is refused having written nothing. The machines are read in the
/// same place for the same reason: a machine deactivated between a caller's read and this write is
/// refused, because the deactivation either commits before this transaction opens and is seen, or
/// after it and comes second.</para>
///
/// <para><strong>The idempotency key is claimed in the same lock.</strong> The key is read inside the
/// transaction, so a retry that arrives while the original is still committing waits and then finds
/// it: the same key with the same allocations - in any order, because
/// <see cref="MultiMachinePickListRefillFingerprint"/> is canonical - is answered with the original
/// result and records nothing new, and the same key with a <em>different</em> batch is refused with
/// <see cref="PickListRefillPolicy.IdempotencyKeyReusedMessage"/> rather than recording a second pick.
/// A key that already recorded a single-machine refill is refused the same way and is never
/// reinterpreted as a batch, because the two renderings cannot collide. The per-business unique index
/// on the key backs that up at the write, and the store translates it into the same refusal.</para>
///
/// <para><strong>It decides no cost.</strong> Each allocation becomes exactly the <c>MachineRefill</c>
/// movement the single-product action already records - negative by the units taken, signed from
/// storage's point of view, attributed to that allocation's own machine - through the established
/// recording and rebuild use cases. A refill reduces storage quantity and is costing-neutral: it
/// changes no costing quantity, no inventory value and creates no COGS, and nothing here is in a
/// position to change that (AGENTS.md § Inventory and historical costing invariants).</para>
/// </summary>
public sealed class RecordMultiMachinePickListRefill
{
    private readonly IMultiMachinePickListRefillStore _refills;
    private readonly IPickListStorageStockStore _products;
    private readonly ILocalMachineStore _machines;
    private readonly IClock _clock;

    public RecordMultiMachinePickListRefill(
        IMultiMachinePickListRefillStore refills,
        IPickListStorageStockStore products,
        ILocalMachineStore machines,
        IClock clock)
    {
        _refills = refills;
        _products = products;
        _machines = machines;
        _clock = clock;
    }

    /// <exception cref="DomainConflictException">
    /// The idempotency key has already recorded a different refill. Nothing is written.
    /// </exception>
    public async Task<MultiMachinePickListRefillAttempt> Handle(
        MultiMachinePickListRefillInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Normalised once, here, so the key that is stored, the key that is matched and the key the
        // fingerprint is paired with are the same string, and a note is either text or nothing.
        var idempotencyKey = input.IdempotencyKey?.Trim() ?? string.Empty;
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        var submitted = input.Allocations ?? [];

        // Before anything is read and before the write lock is taken: a malformed request must not
        // make every other picker wait on it.
        var shape = MultiMachinePickListRefillPolicy.ValidateRequestShape(idempotencyKey, notes, submitted);
        if (!shape.IsEmpty)
        {
            return MultiMachinePickListRefillAttempt.Rejected(shape);
        }

        // An allocation a caller sent as a missing value, and one that named no machine, are both
        // refusals above, so an accepted shape is also the proof that every element is there and
        // every machine is named: from here on the allocations are lines.
        List<MultiMachineRefillLine> lines =
        [
            .. submitted.Select(allocation =>
                new MultiMachineRefillLine(
                    allocation!.MachineId!.Value, allocation.ProductId, allocation.QuantityTaken)),
        ];

        var fingerprint = MultiMachinePickListRefillFingerprint.Compute(notes, lines);

        await using var transaction = await _refills
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // First inside the lock: has this key already recorded something? A retry is answered with
        // the original result, and a different request under a spent key is refused - neither of them
        // reads a storage quantity or writes a movement.
        if (await _refills.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            is { } recorded)
        {
            // A key that recorded a single-machine refill has no batch to answer with, and its
            // fingerprint cannot match this contract's rendering either: both are refused as a reused
            // key, which is the fail-closed direction and leaves the recorded refill exactly as it is.
            if (recorded.Record is not { } replayed
                || !string.Equals(recorded.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException(PickListRefillPolicy.IdempotencyKeyReusedMessage);
            }

            return MultiMachinePickListRefillAttempt.Replayed(replayed);
        }

        // Read inside the lock, so the fleet and the quantities these rules are decided on are the
        // ones the movements below are applied to. The whole fleet is read in one statement rather
        // than one machine at a time: a business's machines are few, and a per-allocation lookup
        // would be several round trips inside the lock every other picker waits on.
        var machines = await _machines.ListOrderedByNameAsync(cancellationToken).ConfigureAwait(false);

        var products = await _products
            .GetStorageProductsAsync(
                [.. lines.Select(line => line.ProductId).Distinct()], cancellationToken)
            .ConfigureAwait(false);

        var state = MultiMachinePickListRefillPolicy.ValidateAgainstState(
            lines,
            machines.ToDictionary(
                machine => machine.Id,
                machine => new PickListRefillMachineFacts(machine.Id, machine.IsActive)),
            products.ToDictionary(
                product => product.Key,
                product => new PickListRefillProductFacts(
                    product.Value.ProductId, product.Value.IsActive, product.Value.QuantityInStock)));

        if (!state.IsEmpty)
        {
            // Returned without committing: the transaction's disposal is what discards the batch.
            return MultiMachinePickListRefillAttempt.Rejected(state);
        }

        MultiMachinePickListRefillRecord record;
        try
        {
            record = await _refills
                .ApplyAsync(
                    new MultiMachinePickListRefillClaim(
                        idempotencyKey, fingerprint, notes, _clock.UtcNow, lines),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (_refills.IsDuplicateIdempotencyKey(exception))
        {
            // The index refusing the write, mapped to the same refusal the claim above produces. It
            // is reachable even with that claim in place - a database this process did not serialise
            // with - and when it is reached it must not surface as an unexplained 500.
            throw new DomainConflictException(PickListRefillPolicy.IdempotencyKeyReusedMessage);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return MultiMachinePickListRefillAttempt.Recorded(record);
    }
}
