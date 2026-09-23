using Inventory.Application.NayaxFeeSettings;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxFeeSettings;

public class ListNayaxFeeRatesTests
{
    [Fact]
    public async Task Returns_rates_ordered_by_effective_date_descending()
    {
        var store = new FakeNayaxFeeRateStore();
        var clock = new FakeClock(DateTime.UtcNow);
        await new SaveNayaxFeeRate(store, clock).Handle(0.10m, new DateTime(2026, 1, 1), CancellationToken.None);
        await new SaveNayaxFeeRate(store, clock).Handle(0.20m, new DateTime(2026, 3, 1), CancellationToken.None);
        await new SaveNayaxFeeRate(store, clock).Handle(0.15m, new DateTime(2026, 2, 1), CancellationToken.None);

        var result = await new ListNayaxFeeRates(store).Handle(CancellationToken.None);

        Assert.Equal(
            new[] { new DateTime(2026, 3, 1), new DateTime(2026, 2, 1), new DateTime(2026, 1, 1) },
            result.Select(x => x.EffectiveFrom));
    }
}
