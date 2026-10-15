using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Controllers;
using InventoryApi.Tests.Application.SalesSync;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #187: <c>POST api/nayax-sales-sync</c> is a thin binding over the Application use case
/// <see cref="SyncLatestNayaxSales"/> - the controller holds no AppDbContext, no Nayax client and no
/// import logic of its own, and it passes the request's cancellation token through to the use case.
///
/// <para>Issue #681 added the per-business run limit, so the controller now maps two outcomes rather
/// than one: 204 when the synchronization ran, and 429 with <c>Retry-After</c> when the business has
/// run too many too recently.</para>
/// </summary>
public class NayaxSalesSyncControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SyncLatest_runs_the_use_case_with_the_request_token_and_returns_no_content()
    {
        using var cancellation = new CancellationTokenSource();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(cancellation.Token))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, cancellation.Token))
            .ReturnsAsync(new List<NayaxLastSalesReport>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), cancellation.Token))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, store.Object));

        var result = await controller.SyncLatest(cancellation.Token);

        Assert.IsType<NoContentResult>(result);
        nayax.VerifyAll();
        store.VerifyAll();
    }

    /// <summary>
    /// The run limit of issue #681, at the HTTP boundary: once a business has spent its allowance the
    /// endpoint answers <c>429</c> with <c>Retry-After</c> in whole seconds, and - the part that
    /// matters - the refused request makes no Nayax call and persists nothing. Before the limit
    /// existed this loop ran twelve full synchronizations, each one a Nayax request per machine plus
    /// one, and each one writing.
    /// </summary>
    [Fact]
    public async Task SyncLatest_refuses_with_429_and_retry_after_once_the_business_has_spent_its_allowance()
    {
        var clock = new FakeClock(Now);
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, store.Object, clock));

        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            Assert.IsType<NoContentResult>(await controller.SyncLatest(CancellationToken.None));
        }

        var refused = Assert.IsType<ObjectResult>(await controller.SyncLatest(CancellationToken.None));

        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(refused.Value);
        // The fixed sentence the controller declares, asserted as text because the constant itself is
        // internal to InventoryApi: a reader of this test should see what the caller is told.
        Assert.Equal("Too many Nayax sales refreshes", problem.Title);
        Assert.Contains("Wait a minute", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(
            ((int)NayaxSalesSyncRateLimiter.Window.TotalSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            controller.Response.Headers.RetryAfter.ToString());

        // The refused run reached neither Nayax nor the store: the allowance is claimed first.
        nayax.Verify(
            x => x.GetMachinesAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(NayaxSalesSyncRateLimiter.MaxRunsPerWindow));
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(NayaxSalesSyncRateLimiter.MaxRunsPerWindow));
    }

    /// <summary>
    /// The window really slides: once the oldest run has left it the business may synchronize again,
    /// so the limit bounds the rate rather than the total.
    /// </summary>
    [Fact]
    public async Task SyncLatest_allows_another_run_once_the_window_has_passed()
    {
        var clock = new FakeClock(Now);
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, store.Object, clock));

        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            await controller.SyncLatest(CancellationToken.None);
        }

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            Assert.IsType<ObjectResult>(await controller.SyncLatest(CancellationToken.None)).StatusCode);

        clock.UtcNow = Now + NayaxSalesSyncRateLimiter.Window;

        Assert.IsType<NoContentResult>(await controller.SyncLatest(CancellationToken.None));
    }

    /// <summary>
    /// A controller with a real <see cref="HttpContext"/>, because the refusal writes a response
    /// header and a controller with no context cannot.
    /// </summary>
    private static NayaxSalesSyncController Controller(SyncLatestNayaxSales useCase) =>
        new(useCase)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
}
