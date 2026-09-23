using Inventory.Application.NayaxFeeSettings;

namespace InventoryApi.Tests.Application.NayaxFeeSettings;

public sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow) => UtcNow = utcNow;

    public DateTime UtcNow { get; set; }
}
