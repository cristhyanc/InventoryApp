using Inventory.Application.Nayax;
using Inventory.Application.SalesSync;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
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
/// <para>Issue #681 added the per-business run limit and issue #843 the recent-sync skip, so the
/// controller now maps three outcomes rather than one: 204 when the synchronization ran, 200 with
/// <see cref="NayaxSalesRecentlySyncedResponse"/> when it was skipped because this business
/// synchronized moments ago, and 429 with <c>Retry-After</c> when it has run too many too recently.
/// The run-limit cases force every call, because an unforced second call inside three minutes is now
/// skipped before the allowance is considered at all - which is itself asserted below.</para>
/// </summary>
public class NayaxSalesSyncControllerTests
{
    private static readonly DateTime Now = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task SyncLatest_runs_the_use_case_with_the_request_token_and_returns_no_content()
    {
        using var cancellation = new CancellationTokenSource();
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(cancellation.Token))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, cancellation.Token))
            .ReturnsAsync(new List<NayaxLastSalesReport>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), cancellation.Token))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, store.Object));

        var result = await controller.SyncLatest(force: false, cancellation.Token);

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
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
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
            Assert.IsType<NoContentResult>(await controller.SyncLatest(force: true, CancellationToken.None));
        }

        var refused = Assert.IsType<ObjectResult>(
            await controller.SyncLatest(force: true, CancellationToken.None));

        Assert.Equal(StatusCodes.Status429TooManyRequests, refused.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(refused.Value);
        // The fixed sentence the controller declares, asserted as text because the constant itself is
        // internal to InventoryApi: a reader of this test should see what the caller is told.
        Assert.Equal("Too many Nayax sales refreshes", problem.Title);
        // It points at Retry-After rather than naming a wait, which can be up to the whole window.
        Assert.Contains("Retry-After", problem.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("minute", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            ((int)NayaxSalesSyncRateLimiter.Window.TotalSeconds).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            controller.Response.Headers.RetryAfter.ToString());

        // The refused run reached neither Nayax nor the store: the allowance is claimed first.
        nayax.Verify(
            x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(NayaxSalesSyncRateLimiter.MaxRunsPerWindow));
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Exactly(NayaxSalesSyncRateLimiter.MaxRunsPerWindow));
    }

    /// <summary>
    /// Several members of one business opening the Dashboard at the same moment (the review of PR
    /// #696): the allowance is the business's, so their loads share it, and up to
    /// <see cref="NayaxSalesSyncRateLimiter.MaxRunsPerWindow"/> simultaneous loads all synchronize.
    /// Only a load past that is refused, and the Dashboard shows that refusal as "refreshed
    /// recently", not as a failure.
    /// </summary>
    [Fact]
    public async Task SyncLatest_lets_simultaneous_dashboard_loads_of_one_business_share_its_allowance()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var useCase = TestSalesSync.UseCase(nayax.Object, store.Object, new FakeClock(Now));

        var loads = await Task.WhenAll(Enumerable.Range(0, NayaxSalesSyncRateLimiter.MaxRunsPerWindow)
            .Select(_ => Task.Run(() => Controller(useCase).SyncLatest(force: true, CancellationToken.None))));

        Assert.All(loads, load => Assert.IsType<NoContentResult>(load));
        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            Assert.IsType<ObjectResult>(
                await Controller(useCase).SyncLatest(force: true, CancellationToken.None)).StatusCode);
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
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine>());
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, store.Object, clock));

        for (var run = 0; run < NayaxSalesSyncRateLimiter.MaxRunsPerWindow; run++)
        {
            await controller.SyncLatest(force: true, CancellationToken.None);
        }

        Assert.Equal(
            StatusCodes.Status429TooManyRequests,
            Assert.IsType<ObjectResult>(
                await controller.SyncLatest(force: true, CancellationToken.None)).StatusCode);

        clock.UtcNow = Now + NayaxSalesSyncRateLimiter.Window;

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: true, CancellationToken.None));
    }

    /// <summary>
    /// The recent-sync skip at the HTTP boundary (issue #843): the second load of the Dashboard
    /// inside three minutes is answered <c>200</c> with the instant the sales were synchronized, and
    /// the endpoint's other two statuses keep their existing meanings - this is neither a 204 "it
    /// ran" nor a 429 "too many". The body says so in the payload as well as in the status, so a
    /// client branches on data rather than on a status code alone.
    /// </summary>
    [Fact]
    public async Task SyncLatest_answers_200_with_the_last_synced_instant_when_the_sales_are_recent()
    {
        var clock = new FakeClock(Now);
        var controller = Controller(TestSalesSync.UseCase(
            SingleMachineNayax().Object, EmptyStore().Object, clock));

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: false, CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(1);
        var recent = Assert.IsType<OkObjectResult>(
            await controller.SyncLatest(force: false, CancellationToken.None));

        var body = Assert.IsType<NayaxSalesRecentlySyncedResponse>(recent.Value);
        Assert.True(body.WasRecentlySynced);
        Assert.Equal(Now, body.LastSyncedAtUtc);
        // Not a refusal: nothing was rationed, so no Retry-After was written.
        Assert.Empty(controller.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>
    /// The Refresh control's <c>force=true</c>: it synchronizes inside the window and answers the
    /// unchanged <c>204</c>, which is what makes the skip above safe to have at all.
    /// </summary>
    [Fact]
    public async Task SyncLatest_with_force_synchronizes_inside_the_window()
    {
        var clock = new FakeClock(Now);
        var nayax = SingleMachineNayax();
        var controller = Controller(TestSalesSync.UseCase(nayax.Object, EmptyStore().Object, clock));

        await controller.SyncLatest(force: false, CancellationToken.None);
        clock.UtcNow = Now.AddMinutes(1);

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: true, CancellationToken.None));
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private static Mock<INayaxLynxClient> SingleMachineNayax()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachine> { new() { MachineID = 1, MachineName = "Machine A" } });
        nayax.Setup(x => x.GetMachineLastSalesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxLastSalesReport>());
        return nayax;
    }

    private static Mock<ILatestNayaxSalesStore> EmptyStore()
    {
        var store = new Mock<ILatestNayaxSalesStore>();
        store.Setup(x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LatestNayaxSalesPersistResult(new Dictionary<long, DateTime>()));
        return store;
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
