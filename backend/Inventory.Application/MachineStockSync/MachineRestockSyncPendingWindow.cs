using Inventory.Application.Time;
using Inventory.Domain.Nayax;

namespace Inventory.Application.MachineStockSync;

/// <summary>
/// The instant the restock reminder's pending window starts (issues #948 and #1018): the start, in
/// the business's own timezone, of the business date
/// <see cref="MachineRestockSyncReminderPolicy.PendingFromBusinessDate"/> names for
/// the business day of the given instant. The Dashboard reminder counts pending events from it,
/// and Sync Restock records a machine as synced only when none is left from it, so both take it from
/// here.
/// </summary>
public static class MachineRestockSyncPendingWindow
{
    public static DateTime StartUtc(IBusinessCalendar businessCalendar, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(businessCalendar);

        var businessToday = businessCalendar.ToBusinessDate(nowUtc);
        return businessCalendar.StartOfBusinessDayUtc(
            MachineRestockSyncReminderPolicy.PendingFromBusinessDate(businessToday));
    }
}
