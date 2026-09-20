using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.NayaxFeeSettings;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.NayaxFeeSettings;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class SettingsControllerTests
{
    private static SettingsController CreateController(FakeNayaxFeeRateStore store, FakeClock clock) =>
        new(new ListNayaxFeeRates(store), new SaveNayaxFeeRate(store, clock));

    [Fact]
    public async Task Get_returns_ok_with_rates_ordered_by_effective_date_descending()
    {
        var store = new FakeNayaxFeeRateStore();
        var clock = new FakeClock(DateTime.UtcNow);
        var controller = CreateController(store, clock);
        await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 1, 1), 0.10m), CancellationToken.None);
        await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 3, 1), 0.20m), CancellationToken.None);

        var result = await controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var rates = Assert.IsAssignableFrom<IReadOnlyList<NayaxFeeRateResponse>>(ok.Value);
        Assert.Equal(new DateTime(2026, 3, 1), rates[0].EffectiveFrom);
        Assert.Equal(new DateTime(2026, 1, 1), rates[1].EffectiveFrom);
    }

    [Fact]
    public async Task Save_valid_new_effective_date_returns_ok_with_normalized_date_and_created_at()
    {
        var store = new FakeNayaxFeeRateStore();
        var clockNow = new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var controller = CreateController(store, new FakeClock(clockNow));

        var result = await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 3, 1, 9, 0, 0), 0.19m), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var response = Assert.IsType<NayaxFeeRateResponse>(ok.Value);
        Assert.Equal(new DateTime(2026, 3, 1), response.EffectiveFrom);
        Assert.Equal(0.19m, response.FeeExGst);
        Assert.Equal(clockNow, response.CreatedAt);
    }

    [Fact]
    public async Task Save_same_effective_date_updates_existing_row_without_creating_a_duplicate()
    {
        var store = new FakeNayaxFeeRateStore();
        var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var controller = CreateController(store, clock);
        var first = await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 3, 1), 0.17m), CancellationToken.None);
        var firstResponse = Assert.IsType<NayaxFeeRateResponse>(Assert.IsType<OkObjectResult>(first.Result).Value);

        var second = await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 3, 1), 0.21m), CancellationToken.None);

        var secondResponse = Assert.IsType<NayaxFeeRateResponse>(Assert.IsType<OkObjectResult>(second.Result).Value);
        Assert.Equal(firstResponse.Id, secondResponse.Id);
        Assert.Equal(firstResponse.CreatedAt, secondResponse.CreatedAt);
        Assert.Equal(0.21m, secondResponse.FeeExGst);
        Assert.Single(await store.ListOrderedByEffectiveDateDescendingAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(0.12345)]
    public async Task Save_invalid_fee_returns_bad_request_with_exact_message(decimal invalidFee)
    {
        var store = new FakeNayaxFeeRateStore();
        var controller = CreateController(store, new FakeClock(DateTime.UtcNow));

        var result = await controller.Save(new NayaxFeeRateRequest(new DateTime(2026, 3, 1), invalidFee), CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal("Fee must be required, non-negative, and have no more than four decimal places.", badRequest.Value);
        Assert.Empty(await store.ListOrderedByEffectiveDateDescendingAsync(CancellationToken.None));
    }
}
