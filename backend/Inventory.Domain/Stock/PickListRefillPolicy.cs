using Inventory.Domain.Exceptions;

namespace Inventory.Domain.Stock;

/// <summary>
/// The deterministic validation a batch refill must pass before any movement is applied (issue
/// #743, parent #329 child H1), beside <see cref="ManualStockAdjustmentPolicy"/>, which is the same
/// kind of rule for the single-product movement.
///
/// <para><strong>It is in two halves, and which half a rule is in matters.</strong>
/// <see cref="ValidateRequestShape"/> judges the request on its own - the key, the note, the row
/// count, each quantity, and a product named twice - and needs no stored state, so a malformed
/// request is refused before anything is read and before the write lock is taken.
/// <see cref="ValidateAgainstState"/> judges the same request against the catalogue, the machines
/// and the storage quantities, which the caller must have read <em>inside</em> the write-locked
/// transaction the refill is applied in: a stock check taken outside it is a check two callers can
/// both pass (docs/architecture.md § Batch refill from the pick list).</para>
///
/// <para><strong>Every refusal refuses the whole batch.</strong> One request records every row or
/// none of them, so a single bad row is reported on its own line and nothing is written - which is
/// the point of the endpoint. Every bad row is reported rather than only the first, because an
/// operator fixing one row per round trip is the workflow this replaces.</para>
///
/// <para>Validation never reads or writes persisted state and never decides a cost: a refill is
/// costing-neutral, and what it costs is <c>StockMovementCostPolicy</c>'s answer, unchanged
/// (AGENTS.md § Inventory and historical costing invariants).</para>
/// </summary>
public static class PickListRefillPolicy
{
    /// <summary>
    /// The longest idempotency key the contract accepts. A key is a client-chosen opaque string -
    /// a GUID in the application's own client - and bounding it keeps an unbounded value out of an
    /// indexed column.
    /// </summary>
    public const int MaximumIdempotencyKeyLength = 128;

    /// <summary>
    /// The most products one request may record. A pick is one person walking to one van, so this
    /// is far above any real batch; what it bounds is the work a single request can ask for inside
    /// the write lock, which every other caller waits on.
    /// </summary>
    public const int MaximumRows = 500;

    /// <summary>The longest note the contract accepts, matching the stored column.</summary>
    public const int MaximumNotesLength = 500;

    public const string IdempotencyKeyRequiredMessage = "An idempotency key is required.";

    public static readonly string IdempotencyKeyTooLongMessage =
        $"An idempotency key cannot be longer than {MaximumIdempotencyKeyLength} characters.";

    /// <summary>
    /// What a caller is told when the key they sent already recorded a <em>different</em> refill.
    /// Retrying is how a refill survives a lost response, so the same key must never be allowed to
    /// record a second, different pick: that is the double deduction the key exists to prevent.
    /// </summary>
    public const string IdempotencyKeyReusedMessage =
        "This idempotency key already recorded a different refill. Use a new key for a new refill.";

    public const string RowsRequiredMessage = "At least one product row is required.";

    public static readonly string TooManyRowsMessage =
        $"A refill may record at most {MaximumRows} products in one request.";

    public static readonly string NotesTooLongMessage =
        $"Notes cannot be longer than {MaximumNotesLength} characters.";

    public const string QuantityMustBePositiveMessage =
        "Quantity taken must be a whole number greater than 0.";

    /// <summary>
    /// What a caller is told about a row that is not there at all. A JSON array may carry a missing
    /// value as an element - <c>"rows": [null]</c> - whatever the element type says, so a submitted
    /// row that is absent is a refusal of this request like any other, reported on its own index
    /// rather than reaching anything that would read a product id out of it.
    /// </summary>
    public const string MissingRowMessage = "This row is missing.";

    public const string DuplicateProductMessage = "This product appears more than once in the request.";

    public const string UnknownProductMessage = "This product is not a product of this business.";

    public const string RetiredProductMessage = "This product is retired and cannot be refilled.";

    /// <summary>
    /// What a caller is told when the refill names a machine that is not an active machine of this
    /// business. A machine that is not theirs and one they have taken out of service are refused by
    /// the same sentence, so the refusal cannot report whether an id exists somewhere else, and it
    /// names no machine and no business.
    ///
    /// <para>It is deliberately not <see cref="ManualStockAdjustmentPolicy.UnknownMachineMessage"/>:
    /// that rule accepts a deactivated machine, because a single movement may be recorded against a
    /// machine the business used to operate. A pick is work being done now, so the machine has to be
    /// in service now.</para>
    /// </summary>
    public const string MachineMustBeActiveMessage =
        "The selected machine is not an active machine of this business.";

    /// <summary>
    /// The established not-enough-stock sentence, reused verbatim from
    /// <see cref="InsufficientStockException"/>, so a batch and a single-product movement tell an
    /// operator the same thing about the same shelf. The available quantity is a figure the caller
    /// is already entitled to see.
    /// </summary>
    public static string NotEnoughStockMessage(int availableStock) =>
        new InsufficientStockException(availableStock).Message;

    /// <summary>
    /// Everything that can be decided from the request alone, before any read: the key, the note,
    /// the number of rows, each row's presence and quantity, and a product named more than once.
    /// </summary>
    /// <param name="idempotencyKey">The key exactly as the caller is to be held to it - already trimmed.</param>
    /// <param name="notes">The note, already normalised to <see langword="null"/> when blank.</param>
    /// <param name="rows">
    /// Every submitted row, in the order the caller sent them. An element may be
    /// <see langword="null"/>: that is a row a caller sent as a missing value, which this is the
    /// one place that refuses, so nothing downstream has to be defensive about it.
    /// </param>
    public static PickListRefillRejection ValidateRequestShape(
        string? idempotencyKey,
        string? notes,
        IReadOnlyList<PickListRefillRequestRow?> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        List<PickListRefillRequestRefusal> request = [];

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Add(new(PickListRefillRequestPart.IdempotencyKey, IdempotencyKeyRequiredMessage));
        }
        else if (idempotencyKey.Length > MaximumIdempotencyKeyLength)
        {
            request.Add(new(PickListRefillRequestPart.IdempotencyKey, IdempotencyKeyTooLongMessage));
        }

        if (notes is { Length: > MaximumNotesLength })
        {
            request.Add(new(PickListRefillRequestPart.Notes, NotesTooLongMessage));
        }

        if (rows.Count == 0)
        {
            request.Add(new(PickListRefillRequestPart.Rows, RowsRequiredMessage));
        }
        else if (rows.Count > MaximumRows)
        {
            request.Add(new(PickListRefillRequestPart.Rows, TooManyRowsMessage));
        }

        List<PickListRefillRowRefusal> rowRefusals = [];
        HashSet<long> seen = [];

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];

            // A row that is not there is refused before anything reads a product or a quantity out
            // of it, and keeps the index the caller sent so the refusal still addresses its line.
            if (row is null)
            {
                rowRefusals.Add(new(index, null, MissingRowMessage));
                continue;
            }

            if (row.QuantityTaken <= 0)
            {
                rowRefusals.Add(new(index, row.ProductId, QuantityMustBePositiveMessage));
                continue;
            }

            // Refused rather than summed: two rows for one product are two different statements
            // about one storage balance, and adding them up would be this policy inventing a
            // quantity nobody typed.
            if (!seen.Add(row.ProductId))
            {
                rowRefusals.Add(new(index, row.ProductId, DuplicateProductMessage));
            }
        }

        return new(request, rowRefusals);
    }

    /// <summary>
    /// The same request against the stored state the caller read inside the write-locked
    /// transaction: the products it names, and the machine it names when it names one.
    /// </summary>
    /// <param name="rows">Every submitted row, in the order the caller sent them.</param>
    /// <param name="products">
    /// The facts for the products this request names, by product id. A product the caller's
    /// business does not have is absent - which is what the tenant query filters produce - and is
    /// refused as unknown rather than looked for anywhere else.
    /// </param>
    /// <param name="machineId">The machine the request named, or <see langword="null"/> when it named none.</param>
    /// <param name="machine">
    /// The facts for that machine, or <see langword="null"/> when it is not a machine of this
    /// business. Ignored when <paramref name="machineId"/> is <see langword="null"/>: no machine is
    /// a valid refill, because a business may pick for a van before its machines are recorded.
    /// </param>
    public static PickListRefillRejection ValidateAgainstState(
        IReadOnlyList<PickListRefillRequestRow> rows,
        IReadOnlyDictionary<long, PickListRefillProductFacts> products,
        long? machineId,
        PickListRefillMachineFacts? machine)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(products);

        List<PickListRefillRequestRefusal> request = [];

        if (machineId is not null && machine is not { IsActive: true })
        {
            request.Add(new(PickListRefillRequestPart.Machine, MachineMustBeActiveMessage));
        }

        List<PickListRefillRowRefusal> rowRefusals = [];

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];

            // One refusal per row, in this order. "Not enough stock" for a product that is not the
            // caller's would be a statement about somebody else's shelf, so an unknown product is
            // refused as an unknown product and nothing further is read into it.
            if (!products.TryGetValue(row.ProductId, out var product))
            {
                rowRefusals.Add(new(index, row.ProductId, UnknownProductMessage));
            }
            else if (!product.IsActive)
            {
                rowRefusals.Add(new(index, row.ProductId, RetiredProductMessage));
            }
            else if (row.QuantityTaken > product.QuantityInStock)
            {
                rowRefusals.Add(new(index, row.ProductId, NotEnoughStockMessage(product.QuantityInStock)));
            }
        }

        return new(request, rowRefusals);
    }
}
