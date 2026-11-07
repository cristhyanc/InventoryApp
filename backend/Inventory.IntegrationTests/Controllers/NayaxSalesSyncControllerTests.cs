using Inventory.Application.Nayax;
using Inventory.Application.NayaxSqs;
using Inventory.Application.SalesSync;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.NayaxSqs;
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
///
/// <para>Issue #1106 adds the second reason that <em>same</em> 200 body is sent: this business's
/// sales arrive live from the shared Nayax SQS queue, so there was nothing to read. The body's
/// existing fields keep their meaning and three are added - <c>liveViaSqs</c>,
/// <c>lastSqsMessageReceivedAtUtc</c> and <c>sqsNotLiveReason</c> - and 204, 409 and 429 are
/// untouched.</para>
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
        var store = TestSalesSync.EmptyStoreMock();
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
        var store = TestSalesSync.EmptyStoreMock();
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
        var store = TestSalesSync.EmptyStoreMock();
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
        var store = TestSalesSync.EmptyStoreMock();
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

    /// <summary>
    /// The already-running answer at the HTTP boundary (issue #1104): <c>409 Conflict</c>, kept
    /// distinct from <c>204</c>, <c>200</c> and <c>429</c> so each of those keeps exactly the meaning
    /// every existing caller already has.
    ///
    /// <para>It carries <strong>no <c>Retry-After</c></strong>, deliberately: this is not a rationed
    /// request, and the wait is however long the run in progress still needs. And it claims no
    /// allowance - the two assertions at the end are what make the attempt free.</para>
    /// </summary>
    [Fact]
    public async Task SyncLatest_answers_409_when_a_sync_for_the_business_is_already_running()
    {
        var clock = new FakeClock(Now);
        var syncLock = new NayaxSalesSyncLock(TimeSpan.FromMilliseconds(20));
        var nayax = SingleMachineNayax();
        var store = EmptyStore();
        var controller = Controller(
            TestSalesSync.UseCase(nayax.Object, store.Object, clock, syncLock: syncLock));

        using var held = await syncLock.TryAcquireAsync(
            Inventory.Domain.Tenancy.BusinessId.From(TestSalesSync.BusinessId), CancellationToken.None);
        Assert.True(held.Acquired);

        var busy = Assert.IsType<ConflictObjectResult>(
            await controller.SyncLatest(force: true, CancellationToken.None));

        Assert.Equal(StatusCodes.Status409Conflict, busy.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(busy.Value);

        // The fixed sentence the controller declares, asserted as text because the constant itself is
        // internal to InventoryApi: a reader of this test should see what the caller is told.
        Assert.Equal("A Nayax sales sync is already running", problem.Title);
        Assert.Equal(
            "A sync for this business is already running. Try again shortly.", problem.Detail);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);

        // Not a rationed request: no Retry-After, and nothing read or written.
        Assert.Empty(controller.Response.Headers.RetryAfter.ToString());
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(
            x => x.PersistLatestSalesAsync(
                It.IsAny<IReadOnlyList<NayaxLastSalesReport>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>
    /// And once the run in progress is finished the very next request answers the unchanged
    /// <c>204</c>, having spent only one allowance for the whole exchange - so the busy attempt cost
    /// the business nothing at all.
    /// </summary>
    [Fact]
    public async Task A_409_costs_the_business_none_of_its_run_allowance()
    {
        var clock = new FakeClock(Now);
        var syncLock = new NayaxSalesSyncLock(TimeSpan.FromMilliseconds(20));
        var controller = Controller(TestSalesSync.UseCase(
            SingleMachineNayax().Object, EmptyStore().Object, clock, syncLock: syncLock));

        using (var held = await syncLock.TryAcquireAsync(
            Inventory.Domain.Tenancy.BusinessId.From(TestSalesSync.BusinessId), CancellationToken.None))
        {
            for (var attempt = 0; attempt < NayaxSalesSyncRateLimiter.MaxRunsPerWindow + 5; attempt++)
            {
                Assert.IsType<ConflictObjectResult>(
                    await controller.SyncLatest(force: true, CancellationToken.None));
            }
        }

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: true, CancellationToken.None));
    }

    /// <summary>
    /// The live answer at the HTTP boundary (issue #1106): a business whose sales arrive from SQS is
    /// answered <c>200</c> with <c>liveViaSqs</c>, the instant its queue last delivered and no
    /// not-live reason - and the request reaches Nayax not at all.
    ///
    /// <para>It is the existing <c>200</c> body with fields added rather than a new status, so
    /// <c>204</c>, <c>409</c> and <c>429</c> all keep exactly the meaning every existing caller has;
    /// <c>wasRecentlySynced</c> stays <see langword="false"/> here, because this is the other skip.</para>
    /// </summary>
    [Fact]
    public async Task SyncLatest_answers_200_with_live_via_sqs_for_a_business_on_a_healthy_queue()
    {
        var clock = new FakeClock(Now);
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddSeconds(-5));
        var nayax = SingleMachineNayax();
        var controller = Controller(TestSalesSync.UseCase(
            nayax.Object,
            EmptyStore().Object,
            clock,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(
                reconciliationSucceededAtUtc: Now.AddMinutes(-30),
                lastMessageReceivedAtUtc: Now.AddMinutes(-7)),
            readerHealth: health));

        var live = Assert.IsType<OkObjectResult>(
            await controller.SyncLatest(force: false, CancellationToken.None));

        var body = Assert.IsType<NayaxSalesRecentlySyncedResponse>(live.Value);
        Assert.True(body.LiveViaSqs);
        Assert.Equal(Now.AddMinutes(-7), body.LastSqsMessageReceivedAtUtc);
        Assert.Null(body.SqsNotLiveReason);
        Assert.False(body.WasRecentlySynced);
        Assert.Null(body.LastSyncedAtUtc);

        // Nothing was read and nothing was rationed.
        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(controller.Response.Headers.RetryAfter.ToString());
    }

    /// <summary>
    /// A queue that has delivered nothing yet is still live, and the body says so with a null
    /// instant rather than with a reason: that is what the Dashboard renders as "no sales received
    /// yet".
    /// </summary>
    [Fact]
    public async Task SyncLatest_answers_live_with_no_instant_for_a_queue_that_has_delivered_nothing()
    {
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddSeconds(-5));
        var controller = Controller(TestSalesSync.UseCase(
            SingleMachineNayax().Object,
            EmptyStore().Object,
            new FakeClock(Now),
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: health));

        var body = Assert.IsType<NayaxSalesRecentlySyncedResponse>(
            Assert.IsType<OkObjectResult>(
                await controller.SyncLatest(force: false, CancellationToken.None)).Value);

        Assert.True(body.LiveViaSqs);
        Assert.Null(body.LastSqsMessageReceivedAtUtc);
    }

    /// <summary>
    /// A business that is not live behaves exactly as it did before - the first load runs and
    /// answers <c>204</c>, the second is the recent-sync <c>200</c> - and the reason travels with the
    /// body that exists, as one of the published codes rather than as an enum's number.
    /// </summary>
    [Fact]
    public async Task SyncLatest_reports_the_not_live_reason_on_the_recent_sync_body()
    {
        var clock = new FakeClock(Now);
        var controller = Controller(TestSalesSync.UseCase(
            SingleMachineNayax().Object,
            EmptyStore().Object,
            clock,
            sqsStatus: FakeNayaxSqsLiveStatusStore.Disabled(Now.AddMinutes(-30)),
            readerHealth: new NayaxSqsReaderHealth()));

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: false, CancellationToken.None));

        clock.UtcNow = Now.AddMinutes(1);
        var body = Assert.IsType<NayaxSalesRecentlySyncedResponse>(
            Assert.IsType<OkObjectResult>(
                await controller.SyncLatest(force: false, CancellationToken.None)).Value);

        Assert.False(body.LiveViaSqs);
        Assert.True(body.WasRecentlySynced);
        Assert.Equal(Now, body.LastSyncedAtUtc);
        Assert.Equal(NayaxSqsNotLiveReasons.SenderDisabled, body.SqsNotLiveReason);
    }

    /// <summary>
    /// The Refresh control's <c>force=true</c> against a live business: the unchanged <c>204</c>,
    /// having read every machine, with no live claim in the response at all.
    /// </summary>
    [Fact]
    public async Task SyncLatest_with_force_still_reads_lastSales_for_a_live_business()
    {
        var health = new NayaxSqsReaderHealth();
        health.RecordSuccessfulPoll(Now.AddSeconds(-5));
        var nayax = SingleMachineNayax();
        var controller = Controller(TestSalesSync.UseCase(
            nayax.Object,
            EmptyStore().Object,
            new FakeClock(Now),
            sqsStatus: FakeNayaxSqsLiveStatusStore.Enabled(Now.AddMinutes(-30)),
            readerHealth: health));

        Assert.IsType<NoContentResult>(await controller.SyncLatest(force: true, CancellationToken.None));

        nayax.Verify(x => x.GetMachinesFreshAsync(It.IsAny<CancellationToken>()), Times.Once);
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

    private static Mock<ILatestNayaxSalesStore> EmptyStore() => TestSalesSync.EmptyStoreMock();

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
