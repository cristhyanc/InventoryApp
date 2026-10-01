using Inventory.Application.Products;

namespace Inventory.Application.Machines;

/// <summary>
/// One slot in a machine's product listing (issue #240): the catalogue product it dispenses, plus
/// the machine-specific facts that override or extend the catalogue view of it. A machine can list
/// the same catalogue product in more than one slot at a different price, so a listing is a sequence
/// of these rows, never a per-product dictionary.
/// </summary>
public sealed record MachineProductRecord
{
    /// <summary>The catalogue product this slot dispenses.</summary>
    public required ProductRecord Product { get; init; }

    /// <summary>The machine-specific live retail price, from the per-machine Nayax <c>RetailPrice</c>.</summary>
    public required decimal MachinePrice { get; init; }

    /// <summary>Raw Nayax commission metadata. Application commission uses <c>SiteCommissionAgreement</c>.</summary>
    public required decimal CommissionValue { get; init; }

    public int? MdbCode { get; init; }

    /// <summary>Units currently in this machine slot, not the product's storage stock.</summary>
    public required int QuantityInStock { get; init; }

    public int? MaxStockInMachine { get; init; }

    /// <summary>Suggested net proceeds, <c>null</c> when nothing authoritative can be suggested.</summary>
    public decimal? SuggestedNetValue { get; init; }

    /// <summary>Suggested break-even retail price, <c>null</c> on the same terms.</summary>
    public decimal? SuggestedPriceValue { get; init; }
}
