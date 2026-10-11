using Inventory.Application.Tenancy;

namespace InventoryApi.Auth;

/// <summary>
/// Drains the member-profile refresh queue (issue #524): the host-side half of the dedicated write
/// path, and the reason a refresh runs outside the request that scheduled it.
///
/// It is hosting glue and holds no rule. Per refresh it opens a <em>new</em> service scope and
/// invokes the <c>RefreshMemberProfile</c> use case in it. The new scope is what "outside the
/// request's business scope" means concretely: that scope's <c>BusinessScope</c> is a fresh, denied
/// one, so the write happens with no current business at all and the tenant query filters and
/// <c>BusinessOwnershipEnforcer</c> stay fully in force over it. The request's own scope is gone by
/// then, along with its <c>AppDbContext</c>, so a refresh cannot join, delay or fail anything the
/// request did.
///
/// One refresh at a time, in queue order, because the production store is a single SQLite file with
/// one writer (docs/architecture.md § SQLite operating assumptions): a task per sign-in would put
/// cosmetic writes in contention with interactive ones.
///
/// <para><b>Failure recovery is self-healing.</b> Any failure of one refresh is logged and
/// abandoned - never retried here, never surfaced to a caller, and never allowed to stop the drain.
/// The cost is staleness of two display values until the member's next request an hour later
/// schedules the refresh again, and that request re-reads the claims rather than replaying this
/// one.</para>
/// </summary>
public sealed class MemberProfileRefreshWorker : BackgroundService
{
    private readonly MemberProfileRefreshQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MemberProfileRefreshWorker> _logger;

    public MemberProfileRefreshWorker(
        MemberProfileRefreshQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<MemberProfileRefreshWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// The host starts the drain and nothing else; everything it does is in
    /// <see cref="DrainAsync"/>.
    /// </summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => DrainAsync(stoppingToken);

    /// <summary>
    /// Drains queued refreshes until <paramref name="stoppingToken"/> is cancelled and the queue has
    /// been emptied.
    ///
    /// Public so it can be exercised as what it is - a loop with a beginning and an end - rather
    /// than through a hosted service's start/stop timing, which is the host's business and not this
    /// loop's.
    /// </summary>
    public async Task DrainAsync(CancellationToken stoppingToken)
    {
        // Shutdown stops the queue accepting refreshes rather than cancelling the drain: the reader
        // then finishes the ones already queued and the loop ends on its own. That is why the
        // refreshes themselves are not given the stopping token - abandoning a batch halfway
        // through would leave some members' details stale for no gain, and the batch is bounded
        // work (at most MemberProfileRefreshQueue.Capacity single-row updates). The host's own
        // shutdown timeout remains the outer bound, so a stuck write delays nothing indefinitely.
        using var shutdown = stoppingToken.Register(_queue.Complete);

        await foreach (var request in _queue.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                await RefreshAsync(request).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Deliberately identity-free and value-free: the display name and the email address
                // must not reach a log, and the member's own next request is the retry, so an
                // operator needs the failure itself rather than whose row it was.
                _logger.LogWarning(
                    exception,
                    "A member display-name and email refresh failed and was abandoned. The member's "
                        + "stored display details stay as they are until their next request "
                        + "schedules another refresh.");
            }
        }
    }

    private async Task RefreshAsync(MemberProfileRefreshRequest request)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();

        var refresh = scope.ServiceProvider.GetRequiredService<RefreshMemberProfile>();

        var outcome = await refresh
            .Handle(request.Identity, request.Profile, CancellationToken.None)
            .ConfigureAwait(false);

        // The outcome is a count-free, value-free fact about what the write did; which member it
        // was and what was written stay out of it.
        _logger.LogDebug("Member display-details refresh completed with outcome {Outcome}.", outcome);
    }
}
