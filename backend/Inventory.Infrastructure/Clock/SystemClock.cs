using Inventory.Application.NayaxFeeSettings;

namespace Inventory.Infrastructure.Clock;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
