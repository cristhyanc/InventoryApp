using Inventory.Application.Machines;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;

namespace Inventory.Application.Stock;

/// <summary>
/// The manual stock-adjustment use case (issue #282), mirroring the former
/// <c>InventoryApi.Services.StockService.Adjust</c>: an unknown product returns <c>null</c> before
/// any validation, then <see cref="ManualStockAdjustmentPolicy"/> validates the request before the
/// movement is applied.
///
/// <para>Since issue #741 the adjustment's machine is a local machine row (<c>Machines.Id</c>), so
/// "is this a machine of mine" has an answer and is checked here, the way the operating expense
/// checks its own (issue #780). The machine store reads through the ordinary tenant query filters,
/// so another business's machine - and a provider machine number that names no local machine - is
/// simply not found. A deactivated machine is accepted: it is still a machine the business
/// operated.</para>
/// </summary>
public sealed class AdjustStock
{
    private readonly IStockAdjustmentStore _store;
    private readonly ILocalMachineStore _machines;

    public AdjustStock(IStockAdjustmentStore store, ILocalMachineStore machines)
    {
        _store = store;
        _machines = machines;
    }

    public async Task<StockAdjustmentRecord?> Handle(
        long productId, ManualStockAdjustmentInput input, CancellationToken cancellationToken)
    {
        if (!await _store.ProductExistsAsync(productId, cancellationToken)) return null;

        ManualStockAdjustmentPolicy.Validate(input.Reason, input.QuantityChange, input.UnitCost);

        // The foreign key refuses the same thing at the write, but a caller deserves the reason
        // rather than a constraint violation.
        if (input.MachineId is { } machineId
            && await _machines.FindByIdAsync(machineId, cancellationToken) is null)
        {
            throw new DomainValidationException(ManualStockAdjustmentPolicy.UnknownMachineMessage);
        }

        return await _store.ApplyAsync(productId, input, cancellationToken);
    }
}
