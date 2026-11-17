namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The sentences every Sync Restock resolution answers with, in one place (issue #1248), because
/// the single resolution of issue #196 and the bulk one of issue #242 now decide the same settled
/// states and must say the same things about them.
/// </summary>
internal static class MachineStockResolutionMessages
{
    internal const string NotFound = "Event not found for this machine.";

    internal const string Reconciled =
        "Reconciled as already recorded manually; no Nayax movement was applied.";

    internal const string AlreadyReconciled =
        "Already reconciled as recorded manually; no Nayax movement was applied.";

    internal const string AlreadyApplied = "Already applied.";

    /// <summary>
    /// The refusal for "Already recorded manually" on an event that already has a Nayax movement.
    /// An applied event can never be retroactively marked as one that was recorded by hand instead.
    /// </summary>
    internal const string AppliedCannotBeReconciled =
        "This event has already been applied as a Nayax inventory movement and cannot be marked as "
            + "already recorded manually.";

    /// <summary>
    /// The refusal for "Apply as separate restock" on an event already reconciled as recorded
    /// manually. The operator decided this event records no separate physical restock; applying it
    /// now would deduct storage for a refill that was already accounted for.
    /// </summary>
    internal const string ReconciledCannotBeApplied =
        "This event has already been reconciled as recorded manually and cannot be applied as a "
            + "separate restock.";
}
