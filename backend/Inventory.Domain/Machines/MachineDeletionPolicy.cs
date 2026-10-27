using System.Globalization;

using Inventory.Domain.Providers;

namespace Inventory.Domain.Machines;

/// <summary>
/// The references that decide whether one of the business's own machines may be deleted (issue
/// #896). Every count is of rows that record something that happened at this machine.
///
/// <para>These are exactly the five places that hold <c>Machines.Id</c>, each as a composite
/// <c>(BusinessId, MachineId)</c> foreign key restricted on delete (issues #741, #743, #780 and
/// #897). The
/// provider's own stored data - a sale's <c>MachineID</c>, a Nayax stock event, a sale timestamp
/// repair, an inventory-cost transition's machine stock - still names the <em>provider's</em> machine
/// number and is not a reference to this row at all (AGENTS.md § Database and migrations), which is
/// why a machine carrying a provider id is refused outright instead of counted.</para>
///
/// <para>The five kinds are counted rather than collapsed into one flag so the refusal can say what
/// is in the way: an operator who is told "2 operating expenses" knows where to look, and one who is
/// told "no" does not.</para>
/// </summary>
/// <param name="MachineStockCounts">Machine stock counts an operator recorded here (issue #56).</param>
/// <param name="StockMovements">Stock movements recorded against this machine - refills included.</param>
/// <param name="PickListRefills">Batch pick-list refills of this machine (issue #743).</param>
/// <param name="OperatingExpenses">Operating expenses attributed to this machine (issue #780).</param>
/// <param name="PickListRefillAllocations">
/// Allocations of a multi-machine pick-list refill attributed to this machine, on the batch's lines
/// rather than on the batch itself (issue #897). Counted on its own, because such a batch leaves the
/// batch's own machine empty, so the <paramref name="PickListRefills"/> count never sees it.
/// </param>
public sealed record LocalMachineDeletionReferences(
    int MachineStockCounts,
    int StockMovements,
    int PickListRefills,
    int OperatingExpenses,
    int PickListRefillAllocations)
{
    /// <summary>A machine nothing refers to yet.</summary>
    public static LocalMachineDeletionReferences None { get; } = new(0, 0, 0, 0, 0);

    /// <summary>Whether anything at all refers to the machine.</summary>
    public bool Any => Total > 0;

    private int Total =>
        MachineStockCounts + StockMovements + PickListRefills + OperatingExpenses
            + PickListRefillAllocations;
}

/// <summary>
/// Whether a machine may be deleted <em>as a read answers it</em>, and the refusal that says why not
/// (issue #896), the shape <see cref="Inventory.Domain.Products.ProductDeletionEligibility"/>
/// established for a product (issues #737 and #825).
///
/// <para>It is the same answer <see cref="MachineDeletionPolicy.Refusal"/> gives the delete itself,
/// decided by the same call over the same counts, so the machine list can offer <strong>Delete</strong>
/// only where the API allows it instead of attempting one that must fail. It is deliberately not a
/// second rule: <see cref="Decide"/> is a projection of the policy, which is what stops what a client
/// offers from drifting from what the server permits.</para>
///
/// <para><strong>It is advisory, and it never replaces the delete's own decision.</strong> It
/// describes rows as they were when they were read; a machine can gain an expense, a count, a
/// movement, a refill or a provider link immediately afterwards. The delete therefore counts again
/// inside its own transaction
/// (<c>Inventory.Infrastructure.Persistence.EfLocalMachineStore.DeleteAsync</c>) and refuses there,
/// with the restricted foreign keys behind it as the database's own backstop. Offering one fewer
/// impossible action is the point; being the authority is not.</para>
/// </summary>
/// <param name="Refusal">Why the machine may not be deleted, or <c>null</c> when it may be.</param>
public sealed record LocalMachineDeletionEligibility(string? Refusal)
{
    /// <summary>Whether the delete would be allowed, as far as the read that produced this knows.</summary>
    public bool Allowed => Refusal is null;

    /// <summary>The eligibility of a machine with these references, from the one deletion rule.</summary>
    /// <param name="isProviderNamed">Whether the machine carries the provider's own machine id.</param>
    /// <param name="provider">The business's sales provider, named in a provider refusal.</param>
    /// <param name="references">What already refers to the machine.</param>
    public static LocalMachineDeletionEligibility Decide(
        bool isProviderNamed, ExternalProvider? provider, LocalMachineDeletionReferences references) =>
        new(MachineDeletionPolicy.Refusal(isProviderNamed, provider, references));
}

/// <summary>
/// Whether one of the business's own machines may be deleted, and why not when it may not (issue
/// #896).
///
/// <para><strong>A machine with history is deactivated, not deleted.</strong> Its machine stock
/// counts, stock movements, pick-list refills (and their per-machine allocations) and operating
/// expenses are inventory and financial history, and all five foreign keys are restricted, so a delete would fail inside the database with
/// a message written for a developer. Refusing first, with the count of what is in the way, is the
/// only outcome an operator can act on: deleting a machine must never be a way to lose inventory or
/// financial history (AGENTS.md § Inventory and historical costing invariants).
/// <c>LocalMachine.IsActive</c> is how a machine that stops trading is retired - it keeps every row
/// above, stays listed, and can be put back in service.</para>
///
/// <para><strong>A machine the provider names is never deleted here either</strong>, and not for the
/// same reason: it is managed at the provider, exactly as its rename and its deactivation are
/// (<see cref="MachineCataloguePolicy"/>). Its refusal therefore does <em>not</em> offer deactivation,
/// because that is refused here too - it names the provider instead, which is where the machine is
/// managed. Deleting the local row would also throw away the provider id every later lookup resolves
/// that machine's sales through.</para>
///
/// <para>Deterministic: the answer is a function of its arguments, so the use case that refuses, the
/// adapter that counts and a test all decide identically.</para>
/// </summary>
public static class MachineDeletionPolicy
{
    private const string Deactivate =
        "Deactivate it instead - a deactivated machine keeps its history, stays listed and can be "
            + "put back in service. Nothing was deleted.";

    /// <summary>
    /// Why a delete the restricted foreign keys refused may not go ahead: something was recorded
    /// against the machine while the delete was being carried out, so the count this delete made
    /// inside its own transaction is no longer the whole truth. It is a refusal rather than a server
    /// error, because what happened is exactly what the counted refusal above describes.
    /// </summary>
    public const string ReferencedWhileDeletingRefusal =
        "This machine cannot be deleted because its history refers to it: something was recorded "
            + "against it while the delete was being carried out. " + Deactivate;

    /// <summary>The refusal message, or <c>null</c> when the machine may be deleted.</summary>
    /// <param name="isProviderNamed">Whether the machine carries the provider's own machine id.</param>
    /// <param name="provider">The business's sales provider, named in a provider refusal.</param>
    /// <param name="references">What already refers to the machine.</param>
    public static string? Refusal(
        bool isProviderNamed, ExternalProvider? provider, LocalMachineDeletionReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (isProviderNamed) return MachineCataloguePolicy.ProviderMachineDeletionRefusal(provider);

        if (!references.Any) return null;

        return $"This machine cannot be deleted because its history refers to it ({Describe(references)}). "
            + Deactivate;
    }

    private static string Describe(LocalMachineDeletionReferences references) =>
        string.Join(", ", Counted(references));

    private static IEnumerable<string> Counted(LocalMachineDeletionReferences references)
    {
        if (references.MachineStockCounts > 0)
            yield return Count(references.MachineStockCounts, "machine stock count");
        if (references.StockMovements > 0)
            yield return Count(references.StockMovements, "stock movement");
        if (references.PickListRefills > 0)
            yield return Count(references.PickListRefills, "pick list refill");
        if (references.OperatingExpenses > 0)
            yield return Count(references.OperatingExpenses, "operating expense");
        if (references.PickListRefillAllocations > 0)
            yield return Count(references.PickListRefillAllocations, "pick list refill allocation");
    }

    private static string Count(int count, string singular) =>
        count == 1
            ? $"1 {singular}"
            : $"{count.ToString(CultureInfo.InvariantCulture)} {singular}s";
}
