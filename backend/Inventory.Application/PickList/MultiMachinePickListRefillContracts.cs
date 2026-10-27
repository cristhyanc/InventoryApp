using Inventory.Domain.Stock;

namespace Inventory.Application.PickList;

/// <summary>
/// One multi-machine refill batch as the Application layer sees it (issue #897): the caller's
/// idempotency key, an optional note, and the allocations the operator typed - each one a local
/// machine, a product and the units taken out of storage for that pair.
///
/// <para>No owning business: it is resolved from the authenticated actor's membership and stamped
/// centrally (AGENTS.md § Tenant ownership and data isolation). No machine at request level either -
/// a batch attributes every line to its own machine, and a refill for no machine at all stays with
/// <see cref="PickListRefillInput"/>, which is unchanged.</para>
///
/// <para>Every part of it is as the caller sent it, which is why the allocations and the elements of
/// the allocations are both nullable: a JSON body can arrive without allocations and with a missing
/// value among them, and <see cref="MultiMachinePickListRefillPolicy"/> is what refuses either, so
/// the boundary hands this layer what was submitted rather than deciding anything about it.</para>
/// </summary>
public sealed record MultiMachinePickListRefillInput(
    string? IdempotencyKey,
    string? Notes,
    IReadOnlyList<MultiMachineRefillAllocation?>? Allocations);

/// <summary>
/// One recorded allocation of a multi-machine batch: the local machine, the product, the units the
/// operator said they took, and the identity of the <c>MachineRefill</c> stock movement that recorded
/// it. The movement is the authoritative fact - the quantity here is the operator's own submitted
/// number, kept so a replay answers what the original request answered however the catalogue has
/// changed since.
/// </summary>
public sealed record MultiMachinePickListRefillRecordedAllocation(
    long MachineId, long ProductId, int QuantityTaken, int StockAdjustmentId);

/// <summary>
/// One recorded multi-machine batch, which is also exactly what a retry of it is answered with.
/// </summary>
public sealed record MultiMachinePickListRefillRecord(
    string IdempotencyKey,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<MultiMachinePickListRefillRecordedAllocation> Allocations);

/// <summary>
/// The answer to one multi-machine batch request. A rejection is a return value rather than an
/// exception because the refusals are per allocation: the API boundary has to address each one to the
/// line it is about, and an exception carries one sentence (docs/architecture.md § Multi-machine
/// refill batch).
///
/// <para>It reports the same three outcomes as the single-machine batch, through the same
/// <see cref="PickListRefillOutcome"/> vocabulary, because they are the same three things that can
/// happen to an idempotent write. The one refusal that is <em>not</em> here is a key reused for a
/// different request: that is a <c>DomainConflictException</c>, which the API boundary already maps to
/// <c>409 Conflict</c>, because it is a conflict with state rather than something wrong with the
/// request.</para>
/// </summary>
public sealed record MultiMachinePickListRefillAttempt(
    PickListRefillOutcome Outcome,
    MultiMachinePickListRefillRecord? Record,
    MultiMachineRefillRejection? Rejection)
{
    public static MultiMachinePickListRefillAttempt Recorded(MultiMachinePickListRefillRecord record) =>
        new(PickListRefillOutcome.Recorded, record, null);

    public static MultiMachinePickListRefillAttempt Replayed(MultiMachinePickListRefillRecord record) =>
        new(PickListRefillOutcome.Replayed, record, null);

    public static MultiMachinePickListRefillAttempt Rejected(MultiMachineRefillRejection rejection) =>
        new(PickListRefillOutcome.Rejected, null, rejection);
}

/// <summary>
/// A validated multi-machine batch, ready to be written: the already-normalised key and fingerprint,
/// the note it carries, the instant the server decided for it, and the allocations to record. Nothing
/// here was supplied by a caller except the note and the allocations, each of which has already been
/// refused or accepted by <see cref="MultiMachinePickListRefillPolicy"/>.
/// </summary>
public sealed record MultiMachinePickListRefillClaim(
    string IdempotencyKey,
    string RequestFingerprint,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<MultiMachineRefillLine> Allocations);

/// <summary>
/// A refill already recorded under one idempotency key: the fingerprint of the request that recorded
/// it, and the result that request was answered with.
///
/// <para><see cref="Record"/> is <see langword="null"/> when the key's stored refill is not a
/// multi-machine batch at all - a single-machine or no-machine refill recorded through
/// <c>POST /api/pick-list/refills</c>, which shares this per-business key namespace. Such a refill is
/// not reinterpreted and not reshaped: it has no per-allocation machine to answer with, so the
/// adapter reports that it cannot be answered with and the use case refuses the key as reused. Its
/// fingerprint could not have matched in any case, so this is the fail-closed restatement of a rule
/// the rendering already guarantees.</para>
/// </summary>
public sealed record MultiMachinePickListRefillSnapshot(
    string RequestFingerprint, MultiMachinePickListRefillRecord? Record);
