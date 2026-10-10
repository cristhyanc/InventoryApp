namespace Inventory.Application.Sites;

/// <summary>
/// One machine's MDB code for a site product row (issue #495), the same
/// <see cref="Inventory.Application.Nayax.NayaxMachineProduct.MDBCode"/> field the Pick List
/// projection (issue #496) already surfaces for one machine-product entry. A product is never
/// assumed to have one globally unique code: the same product can legitimately carry a different
/// code on another machine at the site, or the same code again, and every machine mapping stays
/// visible here rather than being collapsed into one shared value.
/// </summary>
public sealed record SiteProductMachineMdbCode(long MachineId, string MachineLabel, int? MdbCode);

/// <summary>
/// <see cref="MdbCode"/> is the row's representative MDB code (issue #495): the lowest non-null
/// <see cref="SiteProductMachineMdbCode.MdbCode"/> across <see cref="MachineMdbCodes"/>, or
/// <see langword="null"/> when none of them has one. A display/sort convenience only, mirroring
/// <c>PickListProduct.MdbCode</c> (issue #496) - see <see cref="MachineMdbCodes"/> for the
/// per-machine values this page shows in the MDB Code cell.
/// </summary>
public sealed record SiteProductRecord(
    long ProductId,
    string Name,
    decimal? AverageUnitCost,
    decimal SitePrice,
    decimal? EstimatedCardProfit,
    int QuantityInStock,
    int MaxStock,
    int? MdbCode,
    IReadOnlyList<SiteProductMachineMdbCode> MachineMdbCodes);
