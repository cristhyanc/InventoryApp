using Inventory.Application.Time;

namespace InventoryApi.Tests.Application.Time;

public sealed class FakeBusinessCalendar : IBusinessCalendar
{
    public FakeBusinessCalendar(DateTime today) => Today = today.Date;

    public DateTime Today { get; set; }

    public DateTime ToBusinessDate(DateTime utcInstant) => utcInstant.Date;

    public DateTime StartOfBusinessDayUtc(DateTime businessDate) =>
        DateTime.SpecifyKind(businessDate.Date, DateTimeKind.Utc);
}
