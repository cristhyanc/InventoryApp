using Inventory.Application.Machines;
using Inventory.Application.Time;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;

namespace Inventory.Application.PickList;

/// <summary>
/// Records everything a worker took out of storage as audited machine refills, in one request
/// (issue #743, parent #329 child H1). It replaces a refill entered one product per request, where
/// a failure part-way left part of a refill recorded and nothing to say which part.
///
/// <para><strong>One request records every row or none of them.</strong> The check that decides the
/// write and the write itself are one operation: the whole use case runs inside the store's
/// write-locked transaction, which commits only after the last movement is saved. A refusal - a
/// single bad row included - returns without committing, so the transaction's disposal discards
/// every movement the batch had staged.</para>
///
/// <para><strong>The storage check is inside the write lock, and that is the point.</strong> Two
/// requests that each have enough stock on their own but together take more than the shelf holds is
/// not a theoretical race: it is two people picking from one storeroom. A stock read taken before
/// the lock is a read both of them pass. So the transaction is <c>BEGIN IMMEDIATE</c> on SQLite -
/// the write lock is held before <see cref="IPickListStorageStockStore"/> is asked anything - and
/// the loser waits, re-reads the winner's committed quantities, and is refused with the established
/// not-enough-stock sentence having written nothing.</para>
///
/// <para><strong>The idempotency key is claimed in the same lock.</strong> The key is read inside
/// the transaction, so a retry that arrives while the original is still committing waits and then
/// finds it: the same key with the same request is answered with the original result and records
/// nothing new, and the same key with a <em>different</em> request is refused with
/// <see cref="PickListRefillPolicy.IdempotencyKeyReusedMessage"/> rather than recording a second
/// pick. The per-business unique index on the key backs that up at the write, and the store
/// translates it into the same refusal.</para>
///
/// <para><strong>It decides no cost.</strong> Each row becomes exactly the <c>MachineRefill</c>
/// movement the single-product action already records - negative by the units taken, signed from
/// storage's point of view - through the established recording and rebuild use cases. A refill
/// reduces storage quantity and is costing-neutral: it changes no costing quantity, no inventory
/// value and creates no COGS, and nothing here is in a position to change that (AGENTS.md §
/// Inventory and historical costing invariants).</para>
/// </summary>
public sealed class RecordPickListRefill
{
    private readonly IPickListRefillStore _refills;
    private readonly IPickListStorageStockStore _products;
    private readonly ILocalMachineStore _machines;
    private readonly IClock _clock;

    public RecordPickListRefill(
        IPickListRefillStore refills,
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
    public async Task<PickListRefillAttempt> Handle(
        PickListRefillInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Normalised once, here, so the key that is stored, the key that is matched and the key the
        // fingerprint is paired with are the same string, and a note is either text or nothing.
        var idempotencyKey = input.IdempotencyKey?.Trim() ?? string.Empty;
        var notes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim();
        var submitted = input.Rows ?? [];

        // Before anything is read and before the write lock is taken: a malformed request must not
        // make every other picker wait on it.
        var shape = PickListRefillPolicy.ValidateRequestShape(idempotencyKey, notes, submitted);
        if (!shape.IsEmpty)
        {
            return PickListRefillAttempt.Rejected(shape);
        }

        // A row a caller sent as a missing value is one of the refusals above, so an accepted shape
        // is also the proof that every element is there: from here on the rows are rows.
        List<PickListRefillRequestRow> rows = [.. submitted.Select(row => row!)];

        var fingerprint = PickListRefillFingerprint.Compute(input.MachineId, notes, rows);

        await using var transaction = await _refills
            .BeginWriteTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // First inside the lock: has this key already recorded something? A retry is answered with
        // the original result, and a different request under a spent key is refused - neither of
        // them reads a storage quantity or writes a movement.
        if (await _refills.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken).ConfigureAwait(false)
            is { } recorded)
        {
            if (!string.Equals(recorded.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new DomainConflictException(PickListRefillPolicy.IdempotencyKeyReusedMessage);
            }

            return PickListRefillAttempt.Replayed(recorded.Record);
        }

        // Read inside the lock, so the quantities these rules are decided on are the quantities the
        // movements below are applied to.
        var machine = input.MachineId is { } machineId
            ? await _machines.FindByIdAsync(machineId, cancellationToken).ConfigureAwait(false)
            : null;

        var products = await _products
            .GetStorageProductsAsync(
                [.. rows.Select(row => row.ProductId)], cancellationToken)
            .ConfigureAwait(false);

        var state = PickListRefillPolicy.ValidateAgainstState(
            rows,
            products.ToDictionary(
                product => product.Key,
                product => new PickListRefillProductFacts(
                    product.Value.ProductId, product.Value.IsActive, product.Value.QuantityInStock)),
            input.MachineId,
            machine is null ? null : new PickListRefillMachineFacts(machine.Id, machine.IsActive));

        if (!state.IsEmpty)
        {
            // Returned without committing: the transaction's disposal is what discards the batch.
            return PickListRefillAttempt.Rejected(state);
        }

        PickListRefillRecord record;
        try
        {
            record = await _refills
                .ApplyAsync(
                    new PickListRefillClaim(
                        idempotencyKey, fingerprint, input.MachineId, notes, _clock.UtcNow, rows),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (_refills.IsDuplicateIdempotencyKey(exception))
        {
            // The index refusing the write, mapped to the same refusal the claim above produces. It
            // is reachable even with that claim in place - a database this process did not
            // serialise with - and when it is reached it must not surface as an unexplained 500.
            throw new DomainConflictException(PickListRefillPolicy.IdempotencyKeyReusedMessage);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return PickListRefillAttempt.Recorded(record);
    }
}
