using Inventory.Application.NayaxFeeSettings;
using Xunit;

namespace InventoryApi.Tests.Application.NayaxFeeSettings;

public class SaveNayaxFeeRateTests
{
    [Fact]
    public async Task Negative_fee_returns_validation_error_without_persisting()
    {
        var store = new FakeNayaxFeeRateStore();
        var useCase = new SaveNayaxFeeRate(store, new FakeClock(DateTime.UtcNow));

        var result = await useCase.Handle(-0.01m, new DateTime(2026, 1, 1), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Equal("Fee must be required, non-negative, and have no more than four decimal places.", result.ValidationError);
        Assert.False(store.AddWasCalled);
        Assert.False(store.UpdateWasCalled);
    }

    [Fact]
    public async Task Fee_with_more_than_four_decimal_places_returns_validation_error_without_persisting()
    {
        var store = new FakeNayaxFeeRateStore();
        var useCase = new SaveNayaxFeeRate(store, new FakeClock(DateTime.UtcNow));

        var result = await useCase.Handle(0.12345m, new DateTime(2026, 1, 1), CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.False(store.AddWasCalled);
        Assert.False(store.UpdateWasCalled);
    }

    [Fact]
    public async Task New_effective_date_creates_record_with_normalized_date_and_clock_timestamp()
    {
        var store = new FakeNayaxFeeRateStore();
        var clockNow = new DateTime(2026, 3, 4, 10, 30, 0, DateTimeKind.Utc);
        var useCase = new SaveNayaxFeeRate(store, new FakeClock(clockNow));

        var result = await useCase.Handle(0.19m, new DateTime(2026, 3, 1, 15, 45, 0), CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.True(store.AddWasCalled);
        Assert.False(store.UpdateWasCalled);
        Assert.Equal(new DateTime(2026, 3, 1), result.Record!.EffectiveFrom);
        Assert.Equal(clockNow, result.Record.CreatedAt);
        Assert.Equal(0.19m, result.Record.FeeExGst);
    }

    [Fact]
    public async Task Same_effective_calendar_date_updates_existing_record_without_creating_a_duplicate()
    {
        var store = new FakeNayaxFeeRateStore();
        var originalCreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var useCase = new SaveNayaxFeeRate(store, new FakeClock(originalCreatedAt));
        var first = await useCase.Handle(0.17m, new DateTime(2026, 3, 1), CancellationToken.None);

        var laterClock = new FakeClock(new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var laterUseCase = new SaveNayaxFeeRate(store, laterClock);
        var second = await laterUseCase.Handle(0.21m, new DateTime(2026, 3, 1, 23, 0, 0), CancellationToken.None);

        var all = await store.ListOrderedByEffectiveDateDescendingAsync(CancellationToken.None);
        Assert.Single(all);
        Assert.Equal(first.Record!.Id, second.Record!.Id);
        Assert.Equal(0.21m, second.Record.FeeExGst);
        Assert.Equal(originalCreatedAt, second.Record.CreatedAt);
    }
}
