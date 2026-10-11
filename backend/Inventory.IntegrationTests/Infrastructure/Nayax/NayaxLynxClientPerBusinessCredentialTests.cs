using System.Net;
using System.Text;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// The whole per-business Nayax credential path, end to end and relational (issue #520): the real
/// <see cref="NayaxLynxClient"/> over the real <see cref="NayaxRequestCredentialProvider"/> over the
/// real <see cref="EfNayaxConnectionStore"/> and real AES-GCM encryption, on SQLite, with two
/// businesses.
///
/// Relational rather than in-memory on purpose. The isolation under test is the
/// <see cref="AppDbContext"/> tenant query filter and the conditional
/// <c>UPDATE ... WHERE CredentialRevision = @r</c>, and both are database behaviour: a fake store
/// could be made to pass while the real statements crossed a business boundary or overwrote a
/// newer token's status.
///
/// There is deliberately no global operator id or token anywhere in these tests. The client is
/// constructed with a base address and a credential provider and nothing else, so "no fallback to
/// the global credential" is structural here rather than asserted.
/// </summary>
public class NayaxLynxClientPerBusinessCredentialTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorA = "2002736764";
    private const string OperatorB = "9009009009";
    private const string TokenA = "fake-token-for-business-a";
    private const string TokenB = "fake-token-for-business-b";
    private const string ActiveKeyId = "2026-10";
    private static readonly DateTime Now = new(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
    private static readonly DateTime TestedAt = new(2026, 10, 10, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task Each_business_s_call_carries_its_own_operator_id_and_token()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await fixture.ConnectAsync(fixture.BusinessB, OperatorB, TokenB, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");

        await fixture.ClientFor(fixture.BusinessA, handler).GetProductsAsync(CancellationToken.None);
        await fixture.ClientFor(fixture.BusinessB, handler).GetProductsAsync(CancellationToken.None);

        Assert.Equal(
            new[]
            {
                $"https://nayax.invalid/operational/v1/operators/{OperatorA}/products",
                $"https://nayax.invalid/operational/v1/operators/{OperatorB}/products",
            },
            handler.Requests.Select(request => request.Url));
        Assert.Equal(
            new[] { $"Bearer {TokenA}", $"Bearer {TokenB}" },
            handler.Requests.Select(request => request.Authorization));
    }

    /// <summary>
    /// The one business that is connected does not make the other one connected. There is nothing
    /// global left to fall back to, and the tenant filter is what keeps A's record out of B's read.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_connection_of_its_own_fails_closed_and_calls_nothing()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => fixture.ClientFor(fixture.BusinessB, handler).GetMachinesAsync(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NotConfigured, exception.Status);
        Assert.Empty(handler.Requests);
    }

    /// <summary>
    /// A caller whose business could not be resolved reads nothing and therefore calls nothing:
    /// "no current business" must never mean "no filter".
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_resolved_business_fails_closed()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => fixture.DeniedClient(handler).GetMachinesAsync(CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready, true)]
    [InlineData(NayaxConnectionStatus.PendingPermissions, true)]
    [InlineData(NayaxConnectionStatus.NeedsAttention, false)]
    [InlineData(NayaxConnectionStatus.NotConfigured, false)]
    public async Task The_stored_status_decides_whether_a_call_reaches_nayax(
        NayaxConnectionStatus status, bool expectedToCall)
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, status);
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var client = fixture.ClientFor(fixture.BusinessA, handler);

        var exception = await Record.ExceptionAsync(() => client.GetMachinesAsync(CancellationToken.None));

        if (expectedToCall)
        {
            Assert.Null(exception);
            Assert.Single(handler.Requests);
        }
        else
        {
            Assert.IsType<NayaxNotConnectedException>(exception);
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task A_401_marks_only_the_calling_business_s_connection_as_needing_attention()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        await fixture.ConnectAsync(fixture.BusinessB, OperatorB, TokenB, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{\"message\":\"denied\"}");

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => fixture.ClientFor(fixture.BusinessA, handler).GetMachinesAsync(CancellationToken.None));

        var a = await fixture.ReadRowAsync(fixture.BusinessA);
        var b = await fixture.ReadRowAsync(fixture.BusinessB);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, a.Status);
        Assert.Equal(Now, a.LastTestedAtUtc);
        Assert.Equal(NayaxConnectionStatus.Ready, b.Status);
        Assert.Equal(TestedAt, b.LastTestedAtUtc);
    }

    /// <summary>
    /// The next call fails closed on the status the 401 just wrote, without another request: the
    /// provider caches nothing, so a revoked token is not retried for the rest of the request.
    /// </summary>
    [Fact]
    public async Task The_call_after_a_401_is_refused_by_the_gate()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{\"message\":\"denied\"}");
        var client = fixture.ClientFor(fixture.BusinessA, handler);

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => client.GetMachinesAsync(CancellationToken.None));
        var second = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => client.GetMachinesAsync(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NeedsAttention, second.Status);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    public async Task A_403_leaves_the_stored_connection_exactly_as_it_was(NayaxConnectionStatus status)
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, status);
        var before = await fixture.ReadRowAsync(fixture.BusinessA);
        var handler = new RecordingHandler(
            HttpStatusCode.Forbidden, "{\"message\":\"Insufficient permissions to perform this action.\"}");

        await Assert.ThrowsAnyAsync<Exception>(
            () => fixture.ClientFor(fixture.BusinessA, handler).GetMachinesAsync(CancellationToken.None));

        var after = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(
            (before.Status, before.CredentialRevision, before.LastTestedAtUtc, before.UpdatedAtUtc),
            (after.Status, after.CredentialRevision, after.LastTestedAtUtc, after.UpdatedAtUtc));
    }

    /// <summary>
    /// The interleaving the credential revision exists for: the call reads revision 1, a different
    /// token is saved as revision 2 while the call is in flight, and the 401 then arrives. The
    /// result describes credentials that no longer exist, so the store's conditional update discards
    /// it and revision 2 keeps its own status - otherwise a slow in-flight call could mark a token
    /// an operator has just fixed as broken.
    /// </summary>
    [Fact]
    public async Task A_401_from_a_superseded_revision_leaves_the_newer_revision_s_status()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            "{\"message\":\"denied\"}",
            onRequest: () => fixture.SaveCredentialAsync(fixture.BusinessA, OperatorA, TokenB));

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => fixture.ClientFor(fixture.BusinessA, handler).GetMachinesAsync(CancellationToken.None));

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(2, stored.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, stored.Status);
        Assert.Null(stored.LastTestedAtUtc);
        Assert.Contains(
            fixture.StoreLogger.Entries,
            entry => entry.Message.Contains("Stale Nayax status result discarded", StringComparison.Ordinal));
    }

    /// <summary>
    /// A 403 is classified against the status of the credential the request actually carried, with a
    /// credential replacement landing in the middle of the client resolving them.
    ///
    /// Both outcomes are legitimate - which one happens depends on whether the replacement commits
    /// before or after the connection is read - but they must agree with each other: revision 1 is
    /// <see cref="NayaxConnectionStatus.Ready"/>, tested credentials, so a 403 is an ordinary
    /// upstream failure, while revision 2 is <see cref="NayaxConnectionStatus.PendingPermissions"/>,
    /// so a 403 is the per-feature permission answer. The pair that must never occur is revision 2's
    /// token classified against revision 1's status, which is exactly what resolving the status and
    /// the token in two separate reads produces.
    /// </summary>
    [Fact]
    public async Task A_403_is_classified_against_the_status_of_the_credential_the_request_carried()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(
            HttpStatusCode.Forbidden, "{\"message\":\"Insufficient permissions to perform this action.\"}");
        var client = fixture.ClientReplacingTheCredentialMidResolution(
            fixture.BusinessA, OperatorA, TokenB, handler);

        var exception = await Record.ExceptionAsync(() => client.GetMachinesAsync(CancellationToken.None));

        var sent = Assert.Single(handler.Requests).Authorization;
        if (sent == $"Bearer {TokenA}")
        {
            Assert.IsType<NayaxUpstreamException>(exception);
        }
        else
        {
            Assert.Equal($"Bearer {TokenB}", sent);
            Assert.IsType<NayaxPermissionNotGrantedException>(exception);
        }
    }

    /// <summary>
    /// A consumer written against <c>INayaxLynxClient</c> keeps working unchanged, which is the
    /// point of resolving the credential centrally: this adapter never learns that an operator id, a
    /// token or a connection status exists.
    /// </summary>
    [Fact]
    public async Task An_existing_consumer_keeps_working_through_the_per_business_client()
    {
        await using var fixture = await ClientFixture.CreateAsync();
        await fixture.ConnectAsync(fixture.BusinessA, OperatorA, TokenA, NayaxConnectionStatus.Ready);
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            """[{ "NayaxProductID": 100, "ProductName": "Chips" }]""");
        INayaxCatalogSnapshotProvider snapshots =
            new NayaxCatalogSnapshotProvider(fixture.ClientFor(fixture.BusinessA, handler));

        var products = await snapshots.GetProductsAsync(CancellationToken.None);

        var product = Assert.Single(products);
        Assert.Equal((100L, "Chips"), (product.ExternalId, product.Name));
        Assert.Equal(
            $"https://nayax.invalid/operational/v1/operators/{OperatorA}/products",
            Assert.Single(handler.Requests).Url);
    }

    /// <summary>
    /// One in-memory SQLite database with two synthetic businesses, real AES-GCM token encryption,
    /// and the shared capturing logger the discarded-result assertion reads.
    /// </summary>
    private sealed class ClientFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private ClientFixture(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options,
            int businessA,
            int businessB)
        {
            _connection = connection;
            _options = options;
            BusinessA = businessA;
            BusinessB = businessB;
        }

        public int BusinessA { get; }

        public int BusinessB { get; }

        public CapturingLogger<EfNayaxConnectionStore> StoreLogger { get; } = new();

        public static async Task<ClientFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var setup = TestAppDbContext.Unrestricted(options);
            await setup.Database.EnsureCreatedAsync();
            var a = new Business { Name = "Vending A", CreatedAtUtc = Now };
            var b = new Business { Name = "Vending B", CreatedAtUtc = Now };
            setup.Businesses.AddRange(a, b);
            await setup.SaveChangesAsync();

            return new ClientFixture(connection, options, a.Id, b.Id);
        }

        /// <summary>Stores a business's credential and leaves it in <paramref name="status"/>.</summary>
        public async Task ConnectAsync(
            int businessId, string operatorId, string accessToken, NayaxConnectionStatus status)
        {
            if (status == NayaxConnectionStatus.NotConfigured)
            {
                // Nothing stored at all is exactly what NotConfigured means (issue #518), so this
                // seeds no row rather than a row claiming that status.
                return;
            }

            var store = StoreFor(businessId);
            var saved = await store.SaveCredentialAsync(operatorId, accessToken, CancellationToken.None);
            if (status != NayaxConnectionStatus.PendingPermissions)
            {
                Assert.True(await store.TryApplyStatusResultAsync(
                    new NayaxConnectionStatusResult(saved.CredentialRevision, status, TestedAt),
                    CancellationToken.None));
            }
        }

        /// <summary>Replaces a business's stored credential, as a concurrent save would.</summary>
        public async Task SaveCredentialAsync(int businessId, string operatorId, string accessToken) =>
            await StoreFor(businessId).SaveCredentialAsync(operatorId, accessToken, CancellationToken.None);

        public INayaxLynxClient ClientFor(int businessId, HttpMessageHandler handler) =>
            CreateClient(new NayaxRequestCredentialProvider(StoreFor(businessId), new FakeClock(Now)), handler);

        /// <summary>
        /// A client whose store replaces the stored credential as soon as the connection has been
        /// read once, so a real save commits while the credentials for one call are being resolved.
        /// </summary>
        public INayaxLynxClient ClientReplacingTheCredentialMidResolution(
            int businessId, string operatorId, string accessToken, HttpMessageHandler handler) =>
            CreateClient(
                new NayaxRequestCredentialProvider(
                    new ReplacingAfterFirstReadStore(
                        StoreFor(businessId),
                        () => SaveCredentialAsync(businessId, operatorId, accessToken)),
                    new FakeClock(Now)),
                handler);

        public INayaxLynxClient DeniedClient(HttpMessageHandler handler) =>
            CreateClient(
                new NayaxRequestCredentialProvider(StoreForContext(TestAppDbContext.Denied(_options)), new FakeClock(Now)),
                handler);

        public async Task<BusinessNayaxConnection> ReadRowAsync(int businessId)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections
                .AsNoTracking()
                .SingleAsync(connection => connection.BusinessId == businessId);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private static INayaxLynxClient CreateClient(
            INayaxRequestCredentialProvider credentials, HttpMessageHandler handler)
        {
            // A deliberately unroutable host: these tests must never reach a real Nayax endpoint.
            var http = new HttpClient(handler)
            {
                BaseAddress = new Uri("https://nayax.invalid/operational/v1/"),
            };

            return new NayaxLynxClient(http, credentials, new CapturingLogger<NayaxLynxClient>());
        }

        private EfNayaxConnectionStore StoreFor(int businessId) =>
            StoreForContext(TestAppDbContext.For(_options, businessId));

        private EfNayaxConnectionStore StoreForContext(AppDbContext context)
        {
            _contexts.Add(context);

            var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
            options.Keys[ActiveKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return new EfNayaxConnectionStore(
                context,
                new AesGcmNayaxTokenProtector(options),
                new FakeClock(Now),
                StoreLogger);
        }
    }

    /// <summary>
    /// Replaces the stored credential - a real save, through the real store - the first time the
    /// connection is read, whichever read that is. Under a two-read resolution that save lands
    /// between the status read and the token read, which is the interleaving that could once produce
    /// a request carrying the new token while being classified against the old status.
    /// </summary>
    private sealed class ReplacingAfterFirstReadStore : INayaxConnectionStore
    {
        private readonly INayaxConnectionStore _inner;
        private readonly Func<Task> _replaceCredential;
        private bool _replaced;

        public ReplacingAfterFirstReadStore(INayaxConnectionStore inner, Func<Task> replaceCredential)
        {
            _inner = inner;
            _replaceCredential = replaceCredential;
        }

        public async Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken)
        {
            var connection = await _inner.FindAsync(cancellationToken);
            await ReplaceOnceAsync();

            return connection;
        }

        public async Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken)
        {
            var credential = await _inner.FindCredentialAsync(cancellationToken);
            await ReplaceOnceAsync();

            return credential;
        }

        public async Task<NayaxConnectionSnapshot?> FindForOperationAsync(
            Func<NayaxConnectionStatus, bool> mayDecryptToken, CancellationToken cancellationToken)
        {
            var snapshot = await _inner.FindForOperationAsync(mayDecryptToken, cancellationToken);
            await ReplaceOnceAsync();

            return snapshot;
        }

        public Task<NayaxConnection> SaveCredentialAsync(
            string operatorId, string accessToken, CancellationToken cancellationToken) =>
            _inner.SaveCredentialAsync(operatorId, accessToken, cancellationToken);

        public Task<bool> TryApplyStatusResultAsync(
            NayaxConnectionStatusResult result, CancellationToken cancellationToken) =>
            _inner.TryApplyStatusResultAsync(result, cancellationToken);

        private async Task ReplaceOnceAsync()
        {
            if (_replaced)
            {
                return;
            }

            _replaced = true;
            await _replaceCredential();
        }
    }

    /// <summary>
    /// Records a safe snapshot of each request, and can run a callback at the moment the request is
    /// in flight - which is how the superseded-revision test saves a new credential between the
    /// client's credential read and the 401 it reports.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Func<Task>? _onRequest;

        public RecordingHandler(HttpStatusCode status, string body, Func<Task>? onRequest = null)
        {
            _status = status;
            _body = body;
            _onRequest = onRequest;
        }

        public List<(string Method, string? Url, string? Authorization)> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((
                request.Method.Method,
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString()));

            if (_onRequest is not null)
            {
                await _onRequest();
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
