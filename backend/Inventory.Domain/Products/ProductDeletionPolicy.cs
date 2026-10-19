using System.Globalization;

using Inventory.Domain.Providers;

namespace Inventory.Domain.Products;

/// <summary>
/// The references that decide whether a product may be deleted (issue #736, parent #329 decision
/// P2). Every count is of rows that record something that happened to this product.
///
/// <para>The six kinds are counted rather than collapsed into one flag so the refusal can say what
/// is in the way: an operator who is told "2 purchase lines" knows where to look, and one who is
/// told "no" does not.</para>
/// </summary>
/// <param name="StockAdjustments">Stock movements - restocks, corrections, write-offs, refills.</param>
/// <param name="PurchaseLines">Purchase lines that bought this product.</param>
/// <param name="SupplierOrderLines">Supplier order lines that ordered it.</param>
/// <param name="Sales">Stored sales whose own <c>ProductId</c> names it (issue #758).</param>
/// <param name="MachineCounts">Machine stock counts an operator recorded for it (issue #56).</param>
/// <param name="CostingRecords">
/// Costing history that is neither a movement nor a sale: an inventory-cost transition baseline
/// (issue #57) and a costing repair (issue #359). Both are restricted foreign keys, so a delete
/// would be refused by the database with no message an operator could act on.
/// </param>
public sealed record ProductDeletionReferences(
    int StockAdjustments,
    int PurchaseLines,
    int SupplierOrderLines,
    int Sales,
    int MachineCounts,
    int CostingRecords)
{
    /// <summary>A product nothing refers to yet.</summary>
    public static ProductDeletionReferences None { get; } = new(0, 0, 0, 0, 0, 0);

    /// <summary>Whether anything at all refers to the product.</summary>
    public bool Any => Total > 0;

    private int Total =>
        StockAdjustments + PurchaseLines + SupplierOrderLines + Sales + MachineCounts + CostingRecords;
}

/// <summary>
/// Whether a product may be deleted <em>as a read answers it</em>, and the refusal that says why not
/// (issue #737, B2 of parent #329).
///
/// <para>It is the same answer <see cref="ProductDeletionPolicy.Refusal"/> gives the delete itself,
/// decided by the same call over the same counts, so a screen can offer <strong>Delete</strong> only
/// where the API allows it instead of attempting one that must fail. It is deliberately not a second
/// rule: <see cref="Decide"/> is a projection of the policy, which is what stops what a client offers
/// from drifting from what the server permits.</para>
///
/// <para><strong>It is advisory, and it never replaces the delete's own decision.</strong> It
/// describes rows as they were when they were read; a product can gain a stock movement or a provider
/// link immediately afterwards. The delete therefore counts again inside its own transaction
/// (<c>Inventory.Infrastructure.Persistence.EfProductStore.DeleteAsync</c>) and refuses there, and a
/// client still has to show that refusal. Offering one fewer impossible action is the point; being
/// the authority is not.</para>
/// </summary>
/// <param name="Refusal">Why the product may not be deleted, or <c>null</c> when it may be.</param>
public sealed record ProductDeletionEligibility(string? Refusal)
{
    /// <summary>Whether the delete would be allowed, as far as the read that produced this knows.</summary>
    public bool Allowed => Refusal is null;

    /// <summary>
    /// The eligibility of a product with these references, from the one deletion rule.
    /// </summary>
    /// <param name="isProviderManaged">Whether the product carries the provider's own product id.</param>
    /// <param name="provider">The business's sales provider, named in a provider refusal.</param>
    /// <param name="references">What already refers to the product.</param>
    public static ProductDeletionEligibility Decide(
        bool isProviderManaged, ExternalProvider? provider, ProductDeletionReferences references) =>
        new(ProductDeletionPolicy.Refusal(isProviderManaged, provider, references));
}

/// <summary>
/// Whether a product may be deleted, and why not when it may not (issue #736, parent #329 decision
/// P2).
///
/// <para><strong>A product with history is retired, not deleted.</strong> Its stock movements and
/// machine counts would be erased with it - those two foreign keys cascade - and its purchase lines,
/// supplier order lines, sales and costing records are restricted, so the delete would fail inside
/// the database with a message written for a developer. Both outcomes are worse than refusing:
/// deleting a product must never be a way to lose inventory or financial history (AGENTS.md
/// § Inventory and historical costing invariants). <c>Product.IsActive</c> is the way to retire one -
/// a deactivated product keeps every row above, stays out of the reorder alerts, and can be
/// reactivated.</para>
///
/// <para><strong>A product the provider names is never deleted locally either.</strong> The next
/// catalogue import would simply create it again, so the refusal names the provider rather than
/// letting the row come back unexplained.</para>
///
/// <para>Deterministic: the answer is a function of its arguments, so the use case that refuses, the
/// adapter that counts and a test all decide identically.</para>
/// </summary>
public static class ProductDeletionPolicy
{
    private const string Retire =
        "Deactivate it instead - a deactivated product keeps its history, stays out of the reorder "
            + "alerts and can be reactivated. Nothing was deleted.";

    /// <summary>
    /// The refusal message, or <c>null</c> when the product may be deleted.
    /// </summary>
    /// <param name="isProviderManaged">Whether the product carries the provider's own product id.</param>
    /// <param name="provider">The business's sales provider, named in a provider refusal.</param>
    /// <param name="references">What already refers to the product.</param>
    public static string? Refusal(
        bool isProviderManaged, ExternalProvider? provider, ProductDeletionReferences references)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (isProviderManaged)
        {
            return $"This product is managed by {ProviderLabel(provider)} and cannot be deleted: the "
                + $"next catalogue import would create it again. {Retire}";
        }

        if (!references.Any) return null;

        return $"This product cannot be deleted because its history refers to it ({Describe(references)}). "
            + Retire;
    }

    private static string ProviderLabel(ExternalProvider? provider) =>
        provider?.ToString() ?? "this business's sales provider";

    private static string Describe(ProductDeletionReferences references) =>
        string.Join(", ", Counted(references));

    private static IEnumerable<string> Counted(ProductDeletionReferences references)
    {
        if (references.StockAdjustments > 0)
            yield return Count(references.StockAdjustments, "stock movement");
        if (references.PurchaseLines > 0)
            yield return Count(references.PurchaseLines, "purchase line");
        if (references.SupplierOrderLines > 0)
            yield return Count(references.SupplierOrderLines, "supplier order line");
        if (references.Sales > 0)
            yield return Count(references.Sales, "sale");
        if (references.MachineCounts > 0)
            yield return Count(references.MachineCounts, "machine stock count");
        if (references.CostingRecords > 0)
            yield return Count(references.CostingRecords, "costing record");
    }

    private static string Count(int count, string singular) =>
        count == 1
            ? $"1 {singular}"
            : $"{count.ToString(CultureInfo.InvariantCulture)} {singular}s";
}
