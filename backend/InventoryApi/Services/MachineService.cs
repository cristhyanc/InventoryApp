using Inventory.Application.Machines;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;

namespace InventoryApi.Services;

/// <summary>
/// A transitional delegator only: every method maps the <see cref="IMachineService"/> request onto the
/// migrated <see cref="Inventory.Application.Machines"/> use case that owns it -
/// <see cref="GetMachineDashboard"/>/<see cref="ListMachineDashboard"/> (issue #241) and
/// <see cref="ListMachineProducts"/> (issue #240) - and maps the result back to the unchanged
/// <see cref="Machine"/>/<see cref="Product"/> API responses. It holds no <c>AppDbContext</c>, no
/// Nayax client, and no rule of its own. Deleting it is tracked by issue #153.
/// </summary>
public class MachineService : IMachineService
{
    private readonly GetMachineDashboard _getMachineDashboard;
    private readonly ListMachineDashboard _listMachineDashboard;
    private readonly ListMachineProducts _listMachineProducts;

    public MachineService(
        GetMachineDashboard getMachineDashboard,
        ListMachineDashboard listMachineDashboard,
        ListMachineProducts listMachineProducts)
    {
        _getMachineDashboard = getMachineDashboard;
        _listMachineDashboard = listMachineDashboard;
        _listMachineProducts = listMachineProducts;
    }

    public async Task<Machine?> GetById(long id)
    {
        var summary = await _getMachineDashboard.Handle(id, CancellationToken.None);
        return summary is null ? null : ToMachine(summary);
    }

    public async Task<List<Machine>> GetAll()
    {
        var summaries = await _listMachineDashboard.Handle(CancellationToken.None);
        return summaries.Select(ToMachine).ToList();
    }

    private static Machine ToMachine(MachineSummary summary) => new()
    {
        ActorID = summary.ActorId,
        MachineID = summary.MachineId,
        MachineName = summary.MachineName,
        MachineNumber = summary.MachineNumber,
        TodayGrossRevenue = summary.TodayGrossRevenue,
        CurrentWeekGrossRevenue = summary.CurrentWeekGrossRevenue,
        PreviousComparableWeekGrossRevenue = summary.PreviousComparableWeekGrossRevenue,
        LastWeekGrossRevenue = summary.LastWeekGrossRevenue,
        MonthToDateGrossRevenue = summary.MonthToDateGrossRevenue,
        TwoWeeksAgoGrossRevenue = summary.TwoWeeksAgoGrossRevenue,
        TodayDirectProfit = summary.TodayDirectProfit,
        CurrentWeekDirectProfit = summary.CurrentWeekDirectProfit,
        PreviousComparableWeekDirectProfit = summary.PreviousComparableWeekDirectProfit,
        LastWeekDirectProfit = summary.LastWeekDirectProfit,
        MonthToDateDirectProfit = summary.MonthToDateDirectProfit,
        TwoWeeksAgoDirectProfit = summary.TwoWeeksAgoDirectProfit,
        ProfitabilityStatus = summary.ProfitabilityStatus,
    };

    public async Task<List<Product>> GetMachineProducts(long id)
    {
        var rows = await _listMachineProducts.Handle(id, CancellationToken.None);
        return rows.Select(ProductResponseMapper.ToProduct).ToList();
    }
}
