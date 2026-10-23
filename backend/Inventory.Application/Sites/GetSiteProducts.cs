using Inventory.Application.Access;
using Inventory.Application.Nayax;
using Inventory.Application.Reorder;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Sites;

namespace Inventory.Application.Sites;

/// <summary>
/// The site product preview use case: for every machine at a site, aggregates its Nayax product
/// mappings into one row per catalogue product with an estimated card-sale profit. Mirrors the former
/// <c>InventoryApi.Services.SiteService.GetProducts</c> exactly (issue #241). The per-machine
/// requests run at most <see cref="CalculateReorderNeeds.MaxConcurrentMachineRequests"/> at a time
/// (issue #845) rather than one per machine at once.
///
/// <para>Each mapping's <c>NayaxProductID</c> is resolved to a local product through
/// <see cref="INayaxProductLinkStore"/> (issue #733) before any row is built, so the rows, the MDB
/// code lists and the cost-basis lookup all carry local product ids. A mapping this business holds
/// no Nayax link for is dropped before the pricing rule sees it, which is where a mapping with no
/// catalogue cost basis was already dropped.</para>
///
/// <para><b>The estimated profit depends on what the request may see (issue #697).</b> The endpoint
/// requires <c>Inventory.Operate</c> and every role keeps reaching it - the page is how stock and
/// pricing per site are read - but the estimate additionally needs <c>Dashboard.ViewFinancials</c>.
/// For a request that does not hold it neither
/// <see cref="ISiteFactsStore.ResolveCardCommissionAsync"/> nor
/// <see cref="ISiteFactsStore.ResolveEffectiveFeeExGstAsync"/> is called, because those two reads
/// exist only to calculate it, and <see cref="SiteProductRecord.EstimatedCardProfit"/> is
/// <see langword="null"/>. The <b>unit cost and the site price stay visible to every role</b>, which
/// is the recorded decision this slice implements, so the cost-basis read is made either way. The
/// decision is made here rather than at the HTTP boundary because it decides what is <em>read</em>,
/// and it comes from <see cref="CurrentCapabilities"/> - the one place that answers "what may this
/// request do", for a member's role and for a support session's fixed set alike - so it cannot
/// disagree with the capability the endpoint enforces or with what <c>GET /api/me/access</c>
/// reported. No role name appears in it. See <see cref="GetSiteSummaries"/>, which redacts the site
/// revenue on the same rule.</para>
/// </summary>
public sealed class GetSiteProducts
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteFactsStore _facts;
    private readonly INayaxProductLinkStore _productLinks;
    private readonly IBusinessCalendar _businessCalendar;
    private readonly CurrentCapabilities _capabilities;

    public GetSiteProducts(
        INayaxLynxClient nayax,
        ISiteFactsStore facts,
        INayaxProductLinkStore productLinks,
        IBusinessCalendar businessCalendar,
        CurrentCapabilities capabilities)
    {
        _nayax = nayax;
        _facts = facts;
        _productLinks = productLinks;
        _businessCalendar = businessCalendar;
        _capabilities = capabilities;
    }

    /// <exception cref="BusinessAccessDeniedException">
    /// The caller has neither a usable membership nor a support session, so there is nothing to
    /// decide the estimate from. Nothing is read: an unresolved membership is a refusal, never a fall
    /// back to the least-privileged role. A request cannot reach this - the endpoint's
    /// <c>Inventory.Operate</c> policy refuses such a caller first - and it fails closed if one ever
    /// could.
    /// </exception>
    public async Task<IReadOnlyList<SiteProductRecord>> Handle(long siteId, CancellationToken cancellationToken)
    {
        // Resolved before anything else, and awaited, because the membership this reads resolves
        // through the request's own scoped AppDbContext - the context every facts-store read below
        // lands on, and which supports one operation at a time.
        var mayViewFinancials = await _capabilities
            .AllowsAsync(Capability.DashboardViewFinancials, cancellationToken)
            .ConfigureAwait(false);

        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID == siteId)
            .ToList();
        if (machines.Count == 0) return [];

        // A bounded fan-out (issue #845): at most
        // CalculateReorderNeeds.MaxConcurrentMachineRequests machine requests are in flight at once,
        // the same limit CalculateReorderNeeds and GetPickList already fan this exact call out under,
        // rather than one request per machine at once. Parallel.ForEachAsync observes every request,
        // so a failing one propagates and none is left unobserved when another fails first.
        //
        // Each request writes to its own machine's slot, so the mappings stay paired with the machine
        // the request was actually made for - never re-derived from the response's own MachineID
        // field, which GET .../machineProducts documents as nullable, the reason
        // CalculateReorderNeeds/GetPickList do not trust it either - and in machine order, whatever
        // order the bounded requests completed in.
        var productsByMachine = new List<NayaxMachineProduct>[machines.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, machines.Count),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = CalculateReorderNeeds.MaxConcurrentMachineRequests,
                CancellationToken = cancellationToken,
            },
            async (index, machineCancellationToken) =>
            {
                productsByMachine[index] =
                    (await _nayax.GetMachineProductsAsync(machines[index].MachineID, machineCancellationToken))
                    .Where(mp => mp.NayaxProductID.HasValue)
                    .ToList();
            });

        var machineProductsByMachine = machines
            .Select((machine, index) => (Machine: machine, Products: productsByMachine[index]))
            .ToList();

        var machineProducts = machineProductsByMachine.SelectMany(entry => entry.Products).ToList();

        // Read after the fan-out has completed and before the facts reads below, because all of them
        // land on the request's own scoped AppDbContext, which supports one operation at a time.
        var localProductIdByNayaxId = await _productLinks.GetLocalProductIdsAsync(
            machineProducts.Select(mp => mp.NayaxProductID!.Value).Distinct().ToList(), cancellationToken);

        // Each selection paired with the local product it names. A selection whose Nayax product
        // this business holds no link for names no product of ours and is dropped here, in the same
        // order the mappings were read, so the per-product averages and sums are unchanged.
        var linkedSelections = machineProductsByMachine
            .SelectMany(entry => entry.Products.Select(mp => (entry.Machine, Product: mp)))
            .Select(pair => (
                pair.Machine,
                pair.Product,
                LocalProductId: localProductIdByNayaxId.TryGetValue(pair.Product.NayaxProductID!.Value, out var localId)
                    ? localId
                    : (long?)null))
            .Where(pair => pair.LocalProductId.HasValue)
            .ToList();

        // The effective-dated commission and Nayax fee configuration is selected by the business
        // date (issue #310), not the host's local date, exactly as ResolveMachineProductPricing
        // selects it for the machine product listing that shares this port.
        var today = _businessCalendar.Today;
        var distinctPrices = machineProducts
            .Where(mp => mp.RetailPrice.HasValue)
            .Select(mp => mp.RetailPrice!.Value)
            .Distinct()
            .ToList();

        // Awaited one at a time, deliberately not with Task.WhenAll: all three reads land on the same
        // scoped ISiteFactsStore, whose EF adapter shares one AppDbContext, and a DbContext supports
        // only one operation at a time. SQLite's synchronous async implementation would usually hide
        // the overlap locally while failing against a real asynchronous provider. They also start
        // only after the Nayax fan-out above has completed, because since issue #520 each of those
        // remote reads resolves the business's credentials through that same context (issue #667);
        // the fan-out itself stays concurrent, with its credential reads serialized in the store.
        //
        // The cost basis is read for every caller, because the unit cost it carries stays visible to
        // every role; the commission and the fee are read only for a caller who may be shown the
        // estimate they exist to calculate (issue #697).
        var costBasis = await _facts.GetProductCostBasisAsync(cancellationToken);
        var commission = mayViewFinancials
            ? await _facts.ResolveCardCommissionAsync(siteId, today, distinctPrices, cancellationToken)
            : null;
        var feeExGst = mayViewFinancials
            ? await _facts.ResolveEffectiveFeeExGstAsync(today, cancellationToken)
            : null;

        var priceFacts = linkedSelections.Select(pair => new SiteProductPriceFact(
            pair.LocalProductId!.Value,
            pair.Product.RetailPrice,
            pair.Product.RetailPrice.HasValue && commission is not null
                ? commission.CommissionAmountByRetailPrice.GetValueOrDefault(pair.Product.RetailPrice.Value)
                : 0m,
            pair.Product.PAR ?? 0,
            pair.Product.MissingStockByMDB ?? 0));

        var machineMdbCodesByProduct = linkedSelections
            .GroupBy(pair => pair.LocalProductId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SiteProductMachineMdbCode>)group
                    .OrderBy(pair => pair.Machine.MachineID)
                    .Select(pair => new SiteProductMachineMdbCode(
                        pair.Machine.MachineID, MachineLabel(pair.Machine), pair.Product.MDBCode))
                    .ToList());

        // No commission configuration was resolved for a redacted request, which is what the policy
        // is told, so it has nothing to estimate from and every other figure it returns is the one
        // it returns for any caller. The estimate is then withheld explicitly as well: a figure this
        // request may not see must not become publishable by a later change to how the policy treats
        // missing inputs.
        return SiteProductPricingPolicy
            .Calculate(priceFacts, costBasis, feeExGst, commission?.ConfigurationUnavailable ?? true)
            .Select(result =>
            {
                var machineMdbCodes = machineMdbCodesByProduct.GetValueOrDefault(
                    result.ProductId, Array.Empty<SiteProductMachineMdbCode>());
                var mdbCode = machineMdbCodes.Select(code => code.MdbCode).Min();

                return new SiteProductRecord(
                    result.ProductId, result.Name, result.AverageUnitCost, result.SitePrice,
                    mayViewFinancials ? result.EstimatedCardProfit : null, result.QuantityInStock,
                    result.MaxStock, mdbCode, machineMdbCodes);
            })
            .ToList();
    }

    /// <summary>
    /// The display label for one of the site's machines in the MDB Code cell, matching the
    /// name/number/id fallback <c>PickListComponent.machineLabel</c> already applies on the frontend
    /// (issue #222) when no machine name is configured in Nayax.
    /// </summary>
    private static string MachineLabel(NayaxMachine machine)
    {
        if (!string.IsNullOrWhiteSpace(machine.MachineName)) return machine.MachineName;
        if (!string.IsNullOrWhiteSpace(machine.MachineNumber)) return machine.MachineNumber;
        return $"Machine #{machine.MachineID}";
    }
}
