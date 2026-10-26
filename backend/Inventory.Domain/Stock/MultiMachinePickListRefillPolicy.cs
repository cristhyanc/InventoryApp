namespace Inventory.Domain.Stock;

/// <summary>
/// The deterministic validation a multi-machine refill batch must pass before any movement is applied
/// (issue #897), beside <see cref="PickListRefillPolicy"/>, which is the same kind of rule for the
/// single-machine batch, and <see cref="ManualStockAdjustmentPolicy"/>, which is it for one movement.
///
/// <para><strong>It is in two halves, and which half a rule is in matters.</strong>
/// <see cref="ValidateRequestShape"/> judges the request on its own - the key, the note, the number of
/// allocations, each allocation's presence, its machine, its quantity, and a machine/product pair
/// named twice - and needs no stored state, so a malformed request is refused before anything is read
/// and before the write lock is taken. <see cref="ValidateAgainstState"/> judges the same request
/// against the machines, the catalogue and the storage quantities, which the caller must have read
/// <em>inside</em> the write-locked transaction the batch is applied in: a stock check taken outside
/// it is a check two callers can both pass.</para>
///
/// <para><strong>The storage check is over a product's total, not over one allocation.</strong> A
/// batch that takes 6 of a product to one machine and 6 to another takes 12 off one shelf, so each
/// allocation fitting on its own proves nothing. The rule is therefore per product, over the sum of
/// its allocations, and it is reported on every allocation that contributed to the sum - because the
/// thing to correct is the combination and an operator cannot see which lines combined from a
/// refusal on one of them.</para>
///
/// <para><strong>Every refusal refuses the whole batch</strong>, and every refusal is reported rather
/// than only the first: one request records every allocation or none of them, and an operator fixing
/// one line per round trip is the workflow this replaces.</para>
///
/// <para>Validation never reads or writes persisted state and never decides a cost: a refill is
/// costing-neutral, and what it costs is <c>StockMovementCostPolicy</c>'s answer, unchanged
/// (AGENTS.md § Inventory and historical costing invariants).</para>
/// </summary>
public static class MultiMachinePickListRefillPolicy
{
    /// <summary>
    /// The most allocations one request may record. A pick is one person walking one van's route, so
    /// this is far above any real batch - it is the same bound as the single-machine batch's row
    /// limit, declared separately because it counts machine/product pairs rather than products. What
    /// it bounds is the work a single request can ask for inside the write lock, which every other
    /// caller waits on.
    /// </summary>
    public const int MaximumAllocations = 500;

    public const string AllocationsRequiredMessage = "At least one allocation is required.";

    public static readonly string TooManyAllocationsMessage =
        $"A refill may record at most {MaximumAllocations} allocations in one request.";

    /// <summary>
    /// What a caller is told about an allocation that is not there at all. A JSON array may carry a
    /// missing value as an element - <c>"allocations": [null]</c> - whatever the element type says, so
    /// a submitted allocation that is absent is a refusal of this request like any other, reported on
    /// its own index rather than reaching anything that would read a machine or a product out of it.
    /// </summary>
    public const string MissingAllocationMessage = "This allocation is missing.";

    /// <summary>
    /// What a caller is told about an allocation that names no machine. A batch exists to attribute
    /// each line to the machine it was walked to; a refill for no machine at all is still recorded by
    /// <c>POST /api/pick-list/refills</c>, which is unchanged.
    /// </summary>
    public const string MachineRequiredMessage = "This allocation must name a machine.";

    public const string DuplicateAllocationMessage =
        "This machine and product appear more than once in the request.";

    /// <summary>
    /// What a caller is told when a product's allocations together take more than storage holds. It
    /// is deliberately <em>not</em> <see cref="PickListRefillPolicy.NotEnoughStockMessage"/>: that
    /// sentence states what one line asked for against the shelf, and repeating it on an allocation
    /// of 6 against an available 10 would read as wrong. This one states the sum, which is the figure
    /// that does not fit. The available quantity is a figure the caller is already entitled to see.
    ///
    /// <para>The total is a <see cref="long"/> because a sum of positive <see cref="int"/>
    /// quantities need not be one: it is the real total that is reported, never a narrowed or
    /// wrapped one, so the sentence and the comparison behind it say the same thing.</para>
    /// </summary>
    public static string NotEnoughStockForTotalMessage(long totalRequested, int availableStock) =>
        $"The allocations for this product take {totalRequested} units, but only {availableStock} are in stock.";

    /// <summary>
    /// Everything that can be decided from the request alone, before any read: the key, the note, the
    /// number of allocations, and each allocation's presence, machine, quantity and uniqueness.
    /// </summary>
    /// <param name="idempotencyKey">The key exactly as the caller is to be held to it - already trimmed.</param>
    /// <param name="notes">The note, already normalised to <see langword="null"/> when blank.</param>
    /// <param name="allocations">
    /// Every submitted allocation, in the order the caller sent them. An element may be
    /// <see langword="null"/>: that is an allocation a caller sent as a missing value, which this is
    /// the one place that refuses, so nothing downstream has to be defensive about it.
    /// </param>
    public static MultiMachineRefillRejection ValidateRequestShape(
        string? idempotencyKey,
        string? notes,
        IReadOnlyList<MultiMachineRefillAllocation?> allocations)
    {
        ArgumentNullException.ThrowIfNull(allocations);

        List<MultiMachineRefillRequestRefusal> request = [];

        // The key and the note are bounded by the same rules and the same sentences as the
        // single-machine batch's: they are the same contract parts, stored in the same columns.
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            request.Add(new(
                MultiMachineRefillRequestPart.IdempotencyKey,
                PickListRefillPolicy.IdempotencyKeyRequiredMessage));
        }
        else if (idempotencyKey.Length > PickListRefillPolicy.MaximumIdempotencyKeyLength)
        {
            request.Add(new(
                MultiMachineRefillRequestPart.IdempotencyKey,
                PickListRefillPolicy.IdempotencyKeyTooLongMessage));
        }

        if (notes is { Length: > PickListRefillPolicy.MaximumNotesLength })
        {
            request.Add(new(MultiMachineRefillRequestPart.Notes, PickListRefillPolicy.NotesTooLongMessage));
        }

        if (allocations.Count == 0)
        {
            request.Add(new(MultiMachineRefillRequestPart.Allocations, AllocationsRequiredMessage));
        }
        else if (allocations.Count > MaximumAllocations)
        {
            request.Add(new(MultiMachineRefillRequestPart.Allocations, TooManyAllocationsMessage));
        }

        List<MultiMachineRefillAllocationRefusal> refusals = [];
        HashSet<(long MachineId, long ProductId)> seen = [];

        for (var index = 0; index < allocations.Count; index++)
        {
            var allocation = allocations[index];

            // An allocation that is not there is refused before anything reads a machine, a product
            // or a quantity out of it, and keeps the index the caller sent so the refusal still
            // addresses its line.
            if (allocation is null)
            {
                refusals.Add(new(index, null, null, MissingAllocationMessage));
                continue;
            }

            if (allocation.MachineId is not { } machineId)
            {
                refusals.Add(new(index, null, allocation.ProductId, MachineRequiredMessage));
                continue;
            }

            if (allocation.QuantityTaken <= 0)
            {
                refusals.Add(new(
                    index, machineId, allocation.ProductId, PickListRefillPolicy.QuantityMustBePositiveMessage));
                continue;
            }

            // Refused rather than summed: two allocations for one machine and one product are two
            // different statements about one slot, and adding them up would be this policy inventing
            // a quantity nobody typed. The same product on *another* machine is a different
            // allocation and is exactly what this endpoint is for.
            if (!seen.Add((machineId, allocation.ProductId)))
            {
                refusals.Add(new(index, machineId, allocation.ProductId, DuplicateAllocationMessage));
            }
        }

        return new(request, refusals);
    }

    /// <summary>
    /// The same request against the stored state the caller read inside the write-locked transaction:
    /// the machines it names, the products it names, and - per product - what their allocations take
    /// in total against the shelf they come off.
    /// </summary>
    /// <param name="lines">Every validated allocation, in the order the caller sent them.</param>
    /// <param name="machines">
    /// The facts for at least the machines this request names, by local machine id - the caller's own
    /// fleet is what it is given. A machine the caller's business does not have is absent - which is
    /// what the tenant query filters produce - and is refused by the same sentence as a deactivated
    /// one, so the refusal cannot report whether an id exists somewhere else.
    /// </param>
    /// <param name="products">
    /// The facts for the products this request names, by product id. A product the caller's business
    /// does not have is absent and is refused as unknown rather than looked for anywhere else.
    /// </param>
    public static MultiMachineRefillRejection ValidateAgainstState(
        IReadOnlyList<MultiMachineRefillLine> lines,
        IReadOnlyDictionary<long, PickListRefillMachineFacts> machines,
        IReadOnlyDictionary<long, PickListRefillProductFacts> products)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(machines);
        ArgumentNullException.ThrowIfNull(products);

        List<MultiMachineRefillAllocationRefusal> refusals = [];
        HashSet<int> refused = [];

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            // One refusal per allocation, in this order. "Not enough stock" for a product that is not
            // the caller's would be a statement about somebody else's shelf, so an unknown product is
            // refused as an unknown product and nothing further is read into it.
            var message = DescribeMachineOrProductRefusal(line, machines, products);

            if (message is not null)
            {
                refusals.Add(new(index, line.MachineId, line.ProductId, message));
                refused.Add(index);
            }
        }

        refusals.AddRange(BuildAggregateStockRefusals(lines, products, refused));

        return new([], [.. refusals.OrderBy(refusal => refusal.AllocationIndex)]);
    }

    /// <summary>
    /// What one allocation's own machine and product say about it, independent of every other
    /// allocation in the batch - everything <see cref="ValidateAgainstState"/> can decide before the
    /// aggregate storage rule, which needs the whole batch at once.
    /// </summary>
    private static string? DescribeMachineOrProductRefusal(
        MultiMachineRefillLine line,
        IReadOnlyDictionary<long, PickListRefillMachineFacts> machines,
        IReadOnlyDictionary<long, PickListRefillProductFacts> products)
    {
        if (!machines.TryGetValue(line.MachineId, out var machine) || !machine.IsActive)
        {
            return PickListRefillPolicy.MachineMustBeActiveMessage;
        }

        if (!products.TryGetValue(line.ProductId, out var product))
        {
            return PickListRefillPolicy.UnknownProductMessage;
        }

        return product.IsActive ? null : PickListRefillPolicy.RetiredProductMessage;
    }

    /// <summary>
    /// The rule this endpoint exists for. A product's allocations come off one shelf, so the
    /// quantity to compare with storage is their sum: 6 to one machine and 6 to another is 12 off
    /// a shelf of 10, however comfortably each one fits on its own. Only a product the business
    /// actually has and still offers has a shelf to compare with - an unknown or retired one is
    /// already refused as what it is, and a stock sentence about it would be a statement about a
    /// product the caller does not have. An allocation already refused for its machine or product
    /// keeps that refusal, so a client ends up with exactly one message per line.
    ///
    /// <para><strong>The sum is 64-bit, and that is not a detail.</strong> Every quantity here is a
    /// positive <see cref="int"/> - the shape half accepts any of them - and their total need not fit
    /// in one: two allocations of <see cref="int.MaxValue"/> add up to -2 in 32 bits, which compares
    /// as comfortably under any shelf, so the one rule this endpoint exists for would be skipped and
    /// the batch written. A <see cref="long"/> holds
    /// <see cref="MaximumAllocations"/> × <see cref="int.MaxValue"/> with more than seven orders of
    /// magnitude to spare, so the accumulator cannot overflow either, and the comparison and the
    /// refusal both use the real total - it is never narrowed back to an <see cref="int"/>
    /// anywhere.</para>
    /// </summary>
    private static List<MultiMachineRefillAllocationRefusal> BuildAggregateStockRefusals(
        IReadOnlyList<MultiMachineRefillLine> lines,
        IReadOnlyDictionary<long, PickListRefillProductFacts> products,
        HashSet<int> refused)
    {
        var totals = new Dictionary<long, long>();
        foreach (var line in lines)
        {
            if (products.TryGetValue(line.ProductId, out var product) && product.IsActive)
            {
                totals[line.ProductId] = totals.GetValueOrDefault(line.ProductId) + line.QuantityTaken;
            }
        }

        List<MultiMachineRefillAllocationRefusal> refusals = [];
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            if (refused.Contains(index)
                || !totals.TryGetValue(line.ProductId, out var total)
                || total <= products[line.ProductId].QuantityInStock)
            {
                continue;
            }

            // Reported on every allocation that contributed to the sum, because the thing to correct
            // is the combination and an operator cannot see which lines combined from a refusal on
            // one of them.
            refusals.Add(new(
                index,
                line.MachineId,
                line.ProductId,
                NotEnoughStockForTotalMessage(total, products[line.ProductId].QuantityInStock)));
        }

        return refusals;
    }
}
