using Inventory.Application.Time;
using Inventory.Domain.MachineStock;

namespace Inventory.Application.MachineStock;

/// <summary>
/// Records one operator's physical count of a product in a machine (issue #56).
///
/// It appends an observation and nothing else: no storage quantity, no costing quantity, no
/// inventory value, no stock movement and no sale cost changes, so a count can never create or
/// retire COGS. The difference the count absorbs is not written anywhere either - the ledger derives
/// it, so there is one authoritative number instead of a stored copy that could go stale, and
/// shrinkage stays visible instead of being silently corrected away.
///
/// The instant is the server's, taken from <see cref="IClock"/>: a count is recorded when it is
/// submitted. A back-dated count is deliberately not supported here, because it would reorder a
/// ledger the operator cannot see the consequences of.
/// </summary>
public sealed class RecordMachineStockCount
{
    private readonly IMachineStockLedgerStore _store;
    private readonly IClock _clock;

    public RecordMachineStockCount(IMachineStockLedgerStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <summary>Null when the product does not exist (or is not owned by the caller's business).</summary>
    /// <exception cref="Inventory.Domain.Exceptions.DomainValidationException">
    /// The counted quantity is negative, or the note is too long.
    /// </exception>
    public async Task<MachineStockCountResultDto?> Handle(
        long machineId, MachineStockCountRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        MachineStockCountPolicy.Validate(request.CountedQuantity, request.Notes);

        var count = await _store.RecordCountAsync(
            machineId, request.ProductId, request.CountedQuantity, _clock.UtcNow, request.Notes, cancellationToken);

        return count is null
            ? null
            : new MachineStockCountResultDto(
                count.Id,
                count.MachineId,
                count.ProductId,
                count.ProductName,
                count.CountedQuantity,
                count.CountedAtUtc,
                count.Notes);
    }
}
