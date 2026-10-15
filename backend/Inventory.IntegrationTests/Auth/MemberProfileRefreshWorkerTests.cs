using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Auth;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Auth;

/// <summary>
/// The drain (issue #524): the host-side half of the dedicated write path.
///
/// What it has to prove is where the write happens and what it survives. Each refresh runs in a
/// <em>new</em> service scope with its own <c>AppDbContext</c> and its own, denied,
/// <c>BusinessScope</c> - the request's scope is gone by then - and one failed refresh neither stops
/// the drain nor reaches a caller. The display name and the email address never appear in a log
/// line, on the success path or the failure path.
/// </summary>
public class MemberProfileRefreshWorkerTests
{
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";
    private const string OtherOid = "33333333-3333-3333-3333-333333333333";

    private const string DisplayName = "Ada Lovelace";
    private const string Email = "ada@vending.example";

    private static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

    private static ActorIdentity Actor(string oid = Oid)
    {
        Assert.True(ActorIdentity.TryCreate(Tid, oid, out var actor));
        return actor!;
    }

    private static MemberProfile Profile() => MemberProfile.Create(DisplayName, Email);

    /// <summary>
    /// A queued refresh is written, through the real EF adapter, in the drain's own scope.
    /// </summary>
    [Fact]
    public async Task A_queued_refresh_is_written_in_its_own_scope()
    {
        await using var host = await RelationalHost.CreateAsync();
        await host.SeedMembershipAsync(Oid);

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile()));

        await host.DrainAsync();

        var membership = await host.ReadMembershipAsync(Oid);
        Assert.Equal(DisplayName, membership.DisplayName);
        Assert.Equal(Email, membership.Email);
    }

    /// <summary>
    /// The scope the write happens in has no business resolved, which is what "outside the request's
    /// business scope" means: the tenant query filters and <c>BusinessOwnershipEnforcer</c> are in
    /// force over an <c>AppDbContext</c> that can read no business's data at all, and the refresh
    /// still works because the membership table is not tenant-owned.
    /// </summary>
    [Fact]
    public async Task The_scope_the_write_runs_in_has_no_business_resolved()
    {
        await using var host = await RelationalHost.CreateAsync();
        await host.SeedMembershipAsync(Oid);

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile()));
        await host.DrainAsync();

        var observed = Assert.Single(host.ObservedScopes);
        Assert.Null(observed.BusinessId);
        Assert.NotEqual(BusinessScopeState.Unscoped, observed.State);

        // And it was a different scope from any request's: a fresh one per refresh.
        Assert.Equal(DisplayName, (await host.ReadMembershipAsync(Oid)).DisplayName);
    }

    /// <summary>
    /// One refresh failing is logged and abandoned; the queue keeps draining. A display refresh is
    /// never retried here - the member's own next request an hour later schedules a fresh one from
    /// the claims they present then.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_is_abandoned_and_the_drain_continues()
    {
        await using var host = await RelationalHost.CreateAsync(
            failingWriteStore: identity => identity.ObjectId == Actor().ObjectId);
        await host.SeedMembershipAsync(Oid);
        await host.SeedMembershipAsync(OtherOid, businessId: 2);

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile()));
        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(
            Actor(OtherOid),
            MemberProfile.Create("Grace Hopper", "grace@vending.example")));

        await host.DrainAsync();

        Assert.Contains(host.Logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal("Grace Hopper", (await host.ReadMembershipAsync(OtherOid)).DisplayName);
    }

    /// <summary>
    /// The logging rule, over the whole drain: neither value reaches a log line, a log property or
    /// an exception message - on the success path, and on the path where the write failed.
    ///
    /// This is the test that would fail first if a diagnostic were ever added "just while we debug
    /// this": the two values are personal data the row stores for display, and a log is a different
    /// retention, a different destination and a different audience.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Neither_the_display_name_nor_the_email_is_ever_logged(bool theWriteFails)
    {
        await using var host = await RelationalHost.CreateAsync(
            failingWriteStore: theWriteFails ? _ => true : null);
        await host.SeedMembershipAsync(Oid);

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile()));
        await host.DrainAsync();

        Assert.NotEmpty(host.Logger.Entries);
        foreach (var entry in host.Logger.Entries)
        {
            AssertNoDisplayValues(entry.Message);
            AssertNoDisplayValues(entry.Exception?.ToString());
            foreach (var property in entry.Properties)
            {
                AssertNoDisplayValues(property.Value);
            }
        }
    }

    /// <summary>
    /// Issue #681: a refresh that failed gives its hourly slot back, so the member's very next
    /// request may schedule another attempt instead of their stored details staying stale for an
    /// hour because of a transient write failure.
    ///
    /// <para>The claim is made through the real throttle, exactly as the scheduler makes it, so what
    /// this asserts is the throttle's own answer afterwards and not a flag the test set.</para>
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_gives_its_hourly_slot_back()
    {
        await using var host = await RelationalHost.CreateAsync(failingWriteStore: _ => true);
        await host.SeedMembershipAsync(Oid);

        Assert.True(host.Throttle.TryClaim(Actor(), out var claimedAtUtc));
        Assert.False(host.Throttle.TryClaim(Actor()));

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile(), claimedAtUtc));
        await host.DrainAsync();

        // The slot is free again, inside the hour, because nothing was written.
        Assert.True(host.Throttle.TryClaim(Actor()));
    }

    /// <summary>
    /// The contrast: a refresh that <em>succeeded</em> keeps its slot, so the hourly rule still holds
    /// for the case it exists for - a member making dozens of requests a minute with the same two
    /// claims in every one of them.
    /// </summary>
    [Fact]
    public async Task A_successful_refresh_keeps_its_hourly_slot()
    {
        await using var host = await RelationalHost.CreateAsync();
        await host.SeedMembershipAsync(Oid);

        Assert.True(host.Throttle.TryClaim(Actor(), out var claimedAtUtc));

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile(), claimedAtUtc));
        await host.DrainAsync();

        Assert.Equal(DisplayName, (await host.ReadMembershipAsync(Oid)).DisplayName);
        Assert.False(host.Throttle.TryClaim(Actor()));
    }

    /// <summary>
    /// A release only gives back the claim it was told about. A failure arriving after a later
    /// request has already claimed the slot must not reopen it, or one member could hold the drain
    /// open indefinitely by failing repeatedly.
    /// </summary>
    [Fact]
    public async Task A_failed_refresh_does_not_give_back_a_slot_a_later_request_has_claimed()
    {
        await using var host = await RelationalHost.CreateAsync(failingWriteStore: _ => true);
        await host.SeedMembershipAsync(Oid);

        Assert.True(host.Throttle.TryClaim(Actor(), out var firstClaim));
        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile(), firstClaim));
        await host.DrainAsync();

        // The release above freed the slot; a second request, a minute later, claims it - and the
        // *first* claim's release, replayed here, must not take that newer claim away.
        host.ThrottleClock.UtcNow = CreatedAt.AddMinutes(1);
        Assert.True(host.Throttle.TryClaim(Actor(), out var secondClaim));
        Assert.NotEqual(firstClaim, secondClaim);

        Assert.False(host.Throttle.Release(Actor(), firstClaim));
        Assert.False(host.Throttle.TryClaim(Actor()));
    }

    private static void AssertNoDisplayValues(string? text)
    {
        if (text is null)
        {
            return;
        }

        Assert.DoesNotContain(DisplayName, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Email, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Ada", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Shutdown stops accepting refreshes and finishes the ones already queued rather than losing
    /// them silently halfway through the batch.
    /// </summary>
    [Fact]
    public async Task Shutdown_finishes_the_refreshes_already_queued()
    {
        await using var host = await RelationalHost.CreateAsync();
        await host.SeedMembershipAsync(Oid);
        await host.SeedMembershipAsync(OtherOid, businessId: 2);

        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile()));
        host.Queue.TryEnqueue(new MemberProfileRefreshRequest(
            Actor(OtherOid),
            MemberProfile.Create("Grace Hopper", "grace@vending.example")));

        await host.StopAsync();

        Assert.Equal(DisplayName, (await host.ReadMembershipAsync(Oid)).DisplayName);
        Assert.Equal("Grace Hopper", (await host.ReadMembershipAsync(OtherOid)).DisplayName);
        Assert.Equal(0, host.Queue.Pending);
    }

    /// <summary>
    /// The host's own path: the worker started as the hosted service it is registered as drains
    /// what is queued while it runs, in a real service scope.
    ///
    /// It waits for the write rather than for a lifecycle call to return, because what matters here
    /// is that a refresh queued by a request reaches the write path - not how quickly the host's
    /// start and stop calls complete.
    /// </summary>
    [Fact]
    public async Task The_hosted_service_drains_what_is_queued_while_it_runs()
    {
        var written = new TaskCompletionSource<(ActorIdentity Identity, MemberProfile Profile)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var services = new ServiceCollection();
        services.AddScoped<IMemberProfileWriteStore>(_ => new SignallingWriteStore(written));
        services.AddScoped<RefreshMemberProfile>();

        await using var provider = services.BuildServiceProvider();

        var queue = new MemberProfileRefreshQueue();
        IHostedService worker = new MemberProfileRefreshWorker(
            queue,
            new MemberProfileRefreshThrottle(new FakeClock(CreatedAt)),
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CapturingLogger<MemberProfileRefreshWorker>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(new MemberProfileRefreshRequest(Actor(), Profile())));

            var observed = await written.Task.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(Actor(), observed.Identity);
            Assert.Equal(Profile(), observed.Profile);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// The real write store with one identity's write made to fail, so a test can prove what the
    /// drain does about a failure without changing what every other write does.
    /// </summary>
    private sealed class FailingWriteStore(IMemberProfileWriteStore inner, Func<ActorIdentity, bool> fails)
        : IMemberProfileWriteStore
    {
        public Task<int> RefreshActiveMembershipProfileAsync(
            ActorIdentity identity,
            MemberProfile profile,
            CancellationToken cancellationToken) =>
            fails(identity)
                ? throw new InvalidOperationException("The membership profile write failed.")
                : inner.RefreshActiveMembershipProfileAsync(identity, profile, cancellationToken);
    }

    /// <summary>Reports the one write it is asked to make, so a test can wait for it.</summary>
    private sealed class SignallingWriteStore(
        TaskCompletionSource<(ActorIdentity Identity, MemberProfile Profile)> written)
        : IMemberProfileWriteStore
    {
        public Task<int> RefreshActiveMembershipProfileAsync(
            ActorIdentity identity,
            MemberProfile profile,
            CancellationToken cancellationToken)
        {
            written.TrySetResult((identity, profile));
            return Task.FromResult(1);
        }
    }

    /// <summary>
    /// A relational host for the worker: a real service provider with the real EF write store over
    /// a real SQLite database, so the scope the drain opens is a real scope and the write it makes
    /// is real SQL.
    /// </summary>
    private sealed class RelationalHost : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly MemberProfileRefreshWorker _worker;

        private RelationalHost(
            SqliteConnection connection,
            ServiceProvider provider,
            MemberProfileRefreshWorker worker,
            MemberProfileRefreshQueue queue,
            CapturingLogger<MemberProfileRefreshWorker> logger,
            List<IBusinessScope> observedScopes)
        {
            _connection = connection;
            _provider = provider;
            _worker = worker;
            Queue = queue;
            Logger = logger;
            ObservedScopes = observedScopes;
        }

        public MemberProfileRefreshQueue Queue { get; }

        public CapturingLogger<MemberProfileRefreshWorker> Logger { get; }

        /// <summary>The business scope each refresh's own service scope carried.</summary>
        public List<IBusinessScope> ObservedScopes { get; }

        public DbContextOptions<AppDbContext> Options { get; private init; } = null!;

        /// <summary>The throttle the drain gives a failed refresh's hourly slot back to (issue #681).</summary>
        public MemberProfileRefreshThrottle Throttle { get; private init; } = null!;

        /// <summary>The clock that throttle reads, so a test can tell two claims apart by instant.</summary>
        public FakeClock ThrottleClock { get; private init; } = null!;

        public static async Task<RelationalHost> CreateAsync(Func<ActorIdentity, bool>? failingWriteStore = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
            await using (var setup = TestAppDbContext.Unrestricted(options))
            {
                await setup.Database.EnsureCreatedAsync();
            }

            var observedScopes = new List<IBusinessScope>();
            var services = new ServiceCollection();

            // Registered exactly as the composition root registers them: the business scope per
            // scope, so each refresh gets its own, fail-closed one.
            services.AddScoped<BusinessScope>();
            services.AddScoped<IBusinessScope>(sp =>
            {
                var scope = sp.GetRequiredService<BusinessScope>();
                lock (observedScopes)
                {
                    observedScopes.Add(scope);
                }

                return scope;
            });
            services.AddDbContext<AppDbContext>(builder => builder.UseSqlite(connection));

            if (failingWriteStore is null)
            {
                services.AddScoped<IMemberProfileWriteStore, EfMemberProfileWriteStore>();
            }
            else
            {
                // The real write store, with one identity's write made to fail: everything else -
                // including the next refresh in the same drain - still goes to the real database.
                services.AddScoped<IMemberProfileWriteStore>(sp => new FailingWriteStore(
                    new EfMemberProfileWriteStore(sp.GetRequiredService<AppDbContext>()),
                    failingWriteStore));
            }

            services.AddScoped<RefreshMemberProfile>();

            var provider = services.BuildServiceProvider();
            var queue = new MemberProfileRefreshQueue();
            var logger = new CapturingLogger<MemberProfileRefreshWorker>();
            // The real throttle, with a clock a test can move, so the slot a failed refresh gives
            // back (issue #681) is observable through the throttle's own answer rather than a stub's.
            var throttleClock = new FakeClock(CreatedAt);
            var throttle = new MemberProfileRefreshThrottle(throttleClock);

            var worker = new MemberProfileRefreshWorker(
                queue,
                throttle,
                provider.GetRequiredService<IServiceScopeFactory>(),
                logger);

            return new RelationalHost(connection, provider, worker, queue, logger, observedScopes)
            {
                Options = options,
                Throttle = throttle,
                ThrottleClock = throttleClock,
            };
        }

        public async Task SeedMembershipAsync(string objectId, int businessId = 1)
        {
            await using var db = TestAppDbContext.Unrestricted(Options);

            if (!await db.Businesses.AnyAsync(business => business.Id == businessId))
            {
                db.Businesses.Add(new Business
                {
                    Id = businessId,
                    Name = $"Vending Co {businessId}",
                    TimeZoneId = "Australia/Sydney",
                    Status = BusinessStatus.Active,
                    CreatedAtUtc = CreatedAt,
                });
            }

            db.BusinessMemberships.Add(new BusinessMembership
            {
                BusinessId = businessId,
                DirectoryTenantId = Tid,
                ObjectId = objectId,
                Role = BusinessRole.Owner,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            });

            await db.SaveChangesAsync();
        }

        public async Task<BusinessMembership> ReadMembershipAsync(string objectId)
        {
            await using var db = TestAppDbContext.Unrestricted(Options);
            return await db.BusinessMemberships.AsNoTracking().SingleAsync(m => m.ObjectId == objectId);
        }

        /// <summary>
        /// Runs the drain over everything queued and waits for it to finish. Completing the queue
        /// first is what makes it deterministic: the drain reads what is there, and the sequence
        /// then ends on its own.
        /// </summary>
        public async Task DrainAsync()
        {
            Queue.Complete();
            await _worker.DrainAsync(CancellationToken.None);

            Assert.Equal(0, Queue.Pending);
        }

        /// <summary>
        /// Cancels the drain the way host shutdown does, and waits for it to finish what was
        /// already queued.
        /// </summary>
        public async Task StopAsync()
        {
            using var shutdown = new CancellationTokenSource();
            var drain = _worker.DrainAsync(shutdown.Token);

            await shutdown.CancelAsync();
            await drain;
        }


        public async ValueTask DisposeAsync()
        {
            _worker.Dispose();
            await _provider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
