namespace Inventory.Domain.Stock;

/// <summary>
/// One row of a batch refill as the operator submitted it: a product of their own catalogue and the
/// whole number of units they took out of storage for it (issue #743, parent #329 child H1).
///
/// <para>The quantity is <em>taken</em>, always positive. The movement it becomes is negative,
/// because <c>StockAdjustment.QuantityChange</c> is signed from storage's point of view throughout
/// the application; this record is deliberately the operator's own number rather than that signed
/// one, so nothing has to remember which way round the request was.</para>
/// </summary>
public sealed record PickListRefillRequestRow(long ProductId, int QuantityTaken);

/// <summary>
/// The stored facts about one product a batch refill row names, as
/// <see cref="PickListRefillPolicy"/> needs them: whether the catalogue still offers it and the
/// physical storage quantity the pick comes out of. A product the caller's business does not have
/// is simply absent, which is what the tenant query filters already produce.
/// </summary>
public sealed record PickListRefillProductFacts(long ProductId, bool IsActive, int QuantityInStock);

/// <summary>
/// The stored facts about the machine a batch refill names, when it names one. A machine that is
/// not the caller's business's is absent rather than inactive - the policy refuses both the same
/// way, so neither answer reports whether an id exists somewhere else.
/// </summary>
public sealed record PickListRefillMachineFacts(long MachineId, bool IsActive);

/// <summary>
/// The part of a batch refill request a refusal is about. It is a Domain vocabulary rather than a
/// field name, so this layer states <em>what</em> was wrong and the API boundary alone decides how
/// to address it in a response.
/// </summary>
public enum PickListRefillRequestPart
{
    /// <summary>The submitted idempotency key.</summary>
    IdempotencyKey,

    /// <summary>The optional local machine the pick was for.</summary>
    Machine,

    /// <summary>The optional free-text note.</summary>
    Notes,

    /// <summary>The row list as a whole - its presence and its size, never one row's content.</summary>
    Rows,
}

/// <summary>One refusal about the request itself.</summary>
public sealed record PickListRefillRequestRefusal(PickListRefillRequestPart Part, string Message);

/// <summary>
/// One refusal about one submitted row, identified by its position in the request the operator
/// sent. The index is the request's own ordering and not a sorted one, because what it addresses is
/// a line on somebody's screen.
///
/// <para><see cref="ProductId"/> is <see langword="null"/> for a row that is not there at all - a
/// row the caller sent as a missing value - because such a row names no product and reporting one
/// would be this refusal inventing it.</para>
/// </summary>
public sealed record PickListRefillRowRefusal(int RowIndex, long? ProductId, string Message);

/// <summary>
/// Everything wrong with one batch refill request. An empty rejection is the only thing that lets
/// a refill proceed: any refusal at all, on the request or on a single row, refuses the whole batch,
/// because a refill that records some rows is the failure mode this endpoint exists to remove.
/// </summary>
public sealed record PickListRefillRejection(
    IReadOnlyList<PickListRefillRequestRefusal> Request,
    IReadOnlyList<PickListRefillRowRefusal> Rows)
{
    /// <summary>Nothing was refused, which is the only state a refill may proceed from.</summary>
    public bool IsEmpty => Request.Count == 0 && Rows.Count == 0;
}
