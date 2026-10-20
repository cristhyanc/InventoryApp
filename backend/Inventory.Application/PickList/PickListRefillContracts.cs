using Inventory.Domain.Stock;

namespace Inventory.Application.PickList;

/// <summary>
/// One batch refill request as the Application layer sees it (issue #743, parent #329 child H1):
/// the caller's idempotency key, the local machine the pick was for when there is one, an optional
/// note, and the rows the operator typed.
///
/// <para>No owning business: it is resolved from the authenticated actor's membership and stamped
/// centrally (AGENTS.md § Tenant ownership and data isolation).</para>
///
/// <para>Every part of it is as the caller sent it, which is why the rows and the elements of the
/// rows are both nullable: a JSON body can arrive without rows and with a missing value among them,
/// and <see cref="PickListRefillPolicy"/> is what refuses either, so the boundary hands this layer
/// what was submitted rather than deciding anything about it.</para>
/// </summary>
public sealed record PickListRefillInput(
    string? IdempotencyKey,
    long? MachineId,
    string? Notes,
    IReadOnlyList<PickListRefillRequestRow?>? Rows);

/// <summary>
/// One row of a recorded batch refill: the product, the units taken out of storage, and the
/// identity of the <c>MachineRefill</c> stock movement that recorded it. The movement is the
/// authoritative fact - the quantity here is the operator's own submitted number, kept so a replay
/// answers what the original request answered however the catalogue has changed since.
/// </summary>
public sealed record PickListRefillRecordedRow(long ProductId, int QuantityTaken, int StockAdjustmentId);

/// <summary>One recorded batch refill, which is also exactly what a retry of it is answered with.</summary>
public sealed record PickListRefillRecord(
    string IdempotencyKey,
    long? MachineId,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<PickListRefillRecordedRow> Rows);

/// <summary>What one batch refill request did.</summary>
public enum PickListRefillOutcome
{
    /// <summary>The refill was recorded by this request: one movement per row, committed together.</summary>
    Recorded,

    /// <summary>
    /// This key and this request had already been recorded, so the original result was returned and
    /// nothing was written.
    /// </summary>
    Replayed,

    /// <summary>
    /// The request was refused and nothing was written. <see cref="PickListRefillAttempt.Rejection"/>
    /// says what was wrong, per request part and per row.
    /// </summary>
    Rejected,
}

/// <summary>
/// The answer to one batch refill request. A rejection is a return value rather than an exception
/// because the refusals are per row: the API boundary has to address each one to the line it is
/// about, and an exception carries one sentence (docs/architecture.md § Batch refill from the pick
/// list).
///
/// <para>The one refusal that is <em>not</em> here is a key reused for a different request: that is
/// a <c>DomainConflictException</c>, which the API boundary already maps to <c>409 Conflict</c>
/// (docs/architecture.md § Domain and application error mapping), because it is a conflict with
/// state rather than something wrong with the request.</para>
/// </summary>
public sealed record PickListRefillAttempt(
    PickListRefillOutcome Outcome,
    PickListRefillRecord? Record,
    PickListRefillRejection? Rejection)
{
    public static PickListRefillAttempt Recorded(PickListRefillRecord record) =>
        new(PickListRefillOutcome.Recorded, record, null);

    public static PickListRefillAttempt Replayed(PickListRefillRecord record) =>
        new(PickListRefillOutcome.Replayed, record, null);

    public static PickListRefillAttempt Rejected(PickListRefillRejection rejection) =>
        new(PickListRefillOutcome.Rejected, null, rejection);
}

/// <summary>
/// A validated batch refill, ready to be written: the already-normalised key and fingerprint, the
/// machine and note it carries, the instant the server decided for it, and the rows to record.
/// Nothing here was supplied by a caller except the machine, the note and the rows, each of which
/// has already been refused or accepted by <see cref="PickListRefillPolicy"/>.
/// </summary>
public sealed record PickListRefillClaim(
    string IdempotencyKey,
    string RequestFingerprint,
    long? MachineId,
    string? Notes,
    DateTime RecordedAtUtc,
    IReadOnlyList<PickListRefillRequestRow> Rows);

/// <summary>
/// A batch refill already recorded under one idempotency key: the fingerprint of the request that
/// recorded it, and the result that request was answered with.
/// </summary>
public sealed record PickListRefillSnapshot(string RequestFingerprint, PickListRefillRecord Record);
