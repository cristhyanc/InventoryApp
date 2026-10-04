using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Costing;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Costing;

/// <summary>
/// Appends a previewed costing repair and recosts the product from it (issue #359).
///
/// Everything happens in one transaction, and in this order deliberately: the authoritative read of
/// the ledger, the comparison against the fingerprint the preview reported, and the write are one
/// operation, so a purchase, count, refill, sale or earlier repair landing between the preview and
/// the apply is rejected rather than silently repaired against a history nobody approved - the same
/// stale-preview model as <see cref="ApplyInventoryCostTransition"/>, minus its stored draft,
/// because a repair preview persists nothing.
///
/// Two more checks stand between a proposal and historical COGS changing. The repair must replay
/// before the first completed sale the ledger cannot cost, verified through the replay's own
/// ordering rather than a timestamp comparison (see <see cref="CostingRepairPolicy"/>). And the
/// rebuild has to come out clean: <see cref="IRebuildProductCost"/> recosts the completed sales
/// from the repair's effective time, but refuses a history that still has a fatal data-quality
/// issue, and stages nothing when it does, so a repair that only partly explains the missing
/// history fails and persists nothing rather than half-costing the product.
///
/// What it never touches: physical home stock, machine quantities, stock adjustments, machine
/// refills, and the transition baseline.
/// </summary>
public sealed class ApplyInventoryCostRepair
{
    private readonly IInventoryCostRepairStore _store;
    private readonly IRebuildProductCost _rebuild;
    private readonly IAuthenticatedActorAccessor _actors;
    private readonly IClock _clock;
    private readonly InventoryCostRepairProjection _projection;

    public ApplyInventoryCostRepair(
        IInventoryCostRepairStore store,
        IInventoryCostLedgerStore ledgerStore,
        IRebuildProductCost rebuild,
        IAuthenticatedActorAccessor actors,
        IClock clock)
    {
        _store = store;
        _rebuild = rebuild;
        _actors = actors;
        _clock = clock;
        _projection = new InventoryCostRepairProjection(store, ledgerStore);
    }

    public async Task<InventoryCostRepairApplied> Handle(
        ApplyInventoryCostRepairRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Resolved before anything is read or written: a repair record that cannot name who applied
        // it is not auditable, so an unidentifiable caller is refused rather than recorded as blank.
        var actor = _actors.GetCurrentActor();
        var identity = actor.Actor
            ?? throw new BusinessAccessDeniedException(actor.DenialReason ?? BusinessAccessDenialReason.UnidentifiableActor);

        await using var transaction = await _store.BeginTransactionAsync(cancellationToken);
        var projection = await _projection.BuildAsync(
            new(request.ProductId, request.EffectiveAt, request.Quantity, request.UnitCost, request.Reason),
            cancellationToken);
        var preview = projection.Preview;

        if (!string.Equals(preview.LedgerFingerprint, request.LedgerFingerprint, StringComparison.Ordinal))
            throw new DomainValidationException(
                "This product's cost history changed after the preview. Run the preview again before applying the repair.");
        if (projection.FirstUncostableSale is { } firstUncostableSale)
            CostingRepairPolicy.EnsureReplaysBeforeSale(projection.PendingRepair, firstUncostableSale);

        var record = await _store.AppendAsync(
            new(
                preview.ProductId,
                preview.EffectiveAt,
                preview.Quantity,
                preview.UnitCost,
                preview.TotalValue,
                preview.Reason,
                _clock.UtcNow,
                identity.DirectoryTenantId,
                identity.ObjectId),
            cancellationToken);

        InventoryCostRebuildResult rebuilt;
        try
        {
            rebuilt = await _rebuild.RebuildAsync(
                preview.ProductId, preview.EffectiveAt, cancellationToken: cancellationToken);
        }
        catch (InventoryCostDataQualityException exception)
        {
            // The repair was not enough. Rolling the transaction back is what leaves nothing behind;
            // the caller-safe message is what tells the operator the proposal, not the system, was
            // incomplete.
            throw new DomainValidationException(
                "This repair does not complete the product's cost history, so nothing was saved: "
                    + exception.Message);
        }

        await _store.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            record,
            rebuilt.CostingQuantity,
            rebuilt.InventoryValue,
            rebuilt.AverageUnitCost,
            rebuilt.RecostedSaleCount);
    }
}
