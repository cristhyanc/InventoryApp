using Inventory.Domain.Machines;

namespace Inventory.Domain.Sites;

/// <summary>
/// One machine-product mapping's stock facts, aggregated across every machine at a site. Carries no
/// query or persistence behavior; a caller resolves <see cref="ProductId"/>/<see cref="ProductIsActive"/>
/// from the catalogue before building this fact.
/// </summary>
public sealed record SiteMachineProductStockFact(
    long? ProductId,
    bool ProductIsActive,
    int Par,
    int MissingStockByMdb,
    int VendOutAlertThreshold);

/// <summary>
/// Site dashboard stock rules: the overall stock percentage across every machine at a site, and the
/// low/empty product alert counts. Mirrors the former <c>InventoryApi.Services.SiteService</c>
/// <c>GetStockTotals</c>/<c>GetStockCounts</c> private helpers exactly (issue #241).
/// </summary>
public static class SiteStockPolicy
{
    /// <summary>
    /// The site's overall stock percentage, across every machine-product mapping regardless of
    /// whether it resolves to a known/active catalogue product - unmapped stock still counts toward
    /// physical capacity. 100% when the site reports no capacity at all.
    /// </summary>
    public static decimal CalculateStockPercentage(IEnumerable<SiteMachineProductStockFact> facts)
    {
        var totals = Totals(facts);

        return totals.MaxStock == 0 ? 100 : Percentage(totals);
    }

    /// <summary>
    /// One machine's own stock percentage, over that machine's selections alone (issue #1013). It is
    /// the same arithmetic as <see cref="CalculateStockPercentage"/> over a narrower group - the same
    /// sum of (PAR - MissingStockByMDB) over the same sum of PAR, counting unmapped and inactive
    /// selections toward physical capacity exactly as the site figure does - so the machine figures
    /// and the site figure above them cannot come to disagree about what "stocked" means.
    ///
    /// <para>The one deliberate difference is the no-capacity answer. A <em>site</em> with no
    /// capacity reports 100%, which is long-standing published behaviour and is unchanged. A
    /// <em>machine</em> with no capacity reports <see langword="null"/>: nothing is known about how
    /// full it is, and reporting either 0% or 100% for it would be a statement about a machine the
    /// provider gave no capacity for.</para>
    /// </summary>
    public static decimal? CalculateMachineStockPercentage(IEnumerable<SiteMachineProductStockFact> facts)
    {
        var totals = Totals(facts);

        return totals.MaxStock == 0 ? null : Percentage(totals);
    }

    private static (int QuantityInStock, int MaxStock) Totals(IEnumerable<SiteMachineProductStockFact> facts) =>
        facts.Aggregate(
            (QuantityInStock: 0, MaxStock: 0),
            (totals, fact) => (
                totals.QuantityInStock + fact.Par - fact.MissingStockByMdb,
                totals.MaxStock + fact.Par));

    /// <summary>
    /// The percentage itself, for a group that has capacity. More missing than was ever stocked for
    /// gives a negative figure, which is reported rather than clamped: it is a contradiction in the
    /// provider's own data, and correcting it here would hide it from the only place that can
    /// notice.
    /// </summary>
    private static decimal Percentage((int QuantityInStock, int MaxStock) totals) =>
        (decimal)totals.QuantityInStock / totals.MaxStock * 100;

    /// <summary>
    /// Counts how many distinct active catalogue products are low (in stock but at/under their
    /// combined vend-out alert threshold) or empty (at or under zero), summed across every machine at
    /// the site that carries the product.
    ///
    /// The low/empty test itself is <see cref="MachineRefillAlertPolicy.Classify"/> (issue #459), so
    /// this site count and the home Dashboard's fleet refill count cannot come to disagree about what
    /// "low" and "empty" mean. Only the grouping differs: a site counts distinct products across its
    /// machines, the fleet counts selections per machine.
    ///
    /// <para>Since issue #1019 the same method is also called with <b>one machine's</b> selections,
    /// to give that machine's row its own low and empty counts. That is this rule over a narrower
    /// group and not a second rule: the grouping it then applies - distinct active products on that
    /// one machine, several slots of one product summed first - is exactly the grouping
    /// <see cref="MachineRefillAlertPolicy.Summarize"/> applies per machine. It is also why a site's
    /// counts need not equal the sum of its machines': a product low on two machines is two machine
    /// alerts and one site alert, and a product this method reports stocked over a site's combined
    /// stock can still be low on one of the machines holding it.</para>
    /// </summary>
    public static (int LowProductCount, int EmptyProductCount) CalculateAlertCounts(
        IEnumerable<SiteMachineProductStockFact> facts)
    {
        var levels = facts
            .Where(fact => fact.ProductId.HasValue && fact.ProductIsActive)
            .GroupBy(fact => fact.ProductId!.Value)
            .Select(group => MachineRefillAlertPolicy.Classify(
                group.Sum(fact => fact.Par - fact.MissingStockByMdb),
                group.Sum(fact => fact.VendOutAlertThreshold)))
            .ToList();

        return (
            levels.Count(level => level == MachineSelectionStockLevel.Low),
            levels.Count(level => level == MachineSelectionStockLevel.Empty));
    }
}
