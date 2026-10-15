using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// <c>POST /api/settings/nayax-connection/save</c>, <c>.../retest</c> and
/// <c>GET .../status</c> through the real request pipeline (issue #537): authentication, the required
/// scope, the capability policies, <c>BusinessScopeMiddleware</c>, the controller, the real stores
/// with real AES-GCM encryption and relational SQLite with the real migration history.
///
/// <para>The check runner is the one thing substituted, with a fake that answers a scripted report.
/// No test here makes a Nayax call: the runner's own behaviour against a fake HTTP handler is
/// <c>Infrastructure.Nayax.NayaxConnectionCheckRunnerTests</c>.</para>
///
/// <para>What is under test is the contract at the boundary: that a save accepts a draft id and
/// never a token, that the credentials stored are the tested ones and are ciphertext at rest, the
/// statuses each outcome produces, the <c>409</c> sentences, that a re-test shares the test's
/// allowance, and that the status read tells every member exactly two things and nothing else.
/// </para>
/// </summary>
public sealed class NayaxConnectionSaveRetestApiTests : IClassFixture<NayaxConnectionSaveRetestApiTests.ApiFactory>
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";

    private const string TestRoute = "/api/settings/nayax-connection/test";
    private const string SaveRoute = "/api/settings/nayax-connection/save";
    private const string RetestRoute = "/api/settings/nayax-connection/retest";
    private const string StatusRoute = "/api/settings/nayax-connection/status";

    private readonly ApiFactory _factory;

    public NayaxConnectionSaveRetestApiTests(ApiFactory factory) => _factory = factory;

    /// <summary>
    /// The whole agreed flow: test, then save the draft the test returned. The connection is stored
    /// <c>Ready</c>, the credential revision starts at 1, the token is ciphertext in the database and
    /// never in a response, and the draft is gone.
    /// </summary>
    [Fact]
    public async Task A_tested_draft_is_saved_ready_and_the_draft_is_consumed()
    {
        await _factory.ResetAsync();
        var draftId = await TestAsync();

        var response = await Save(ApiFactory.OwnerObjectId, draftId);

        await AssertOk(response);
        var json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<StatusResponse>(json, CamelCase);
        Assert.Equal("Ready", body!.Status);
        Assert.False(body.MachineChecksPending);
        Assert.DoesNotContain(Token, json, StringComparison.OrdinalIgnoreCase);

        await using var db = _factory.Database();
        var connection = await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
        Assert.Equal(ApiFactory.SeededBusinessId, connection.BusinessId);
        Assert.Equal(OperatorId, connection.OperatorId);
        Assert.Equal(NayaxConnectionStatus.Ready, connection.Status);
        Assert.Equal(1, connection.CredentialRevision);
        Assert.False(connection.MachineChecksPending);
        Assert.NotEqual(Token, connection.AccessTokenCiphertext);
        Assert.DoesNotContain(Token, connection.AccessTokenCiphertext, StringComparison.Ordinal);
        Assert.Equal(Token, _factory.Decrypt(connection));
        Assert.Empty(await db.NayaxConnectionTestDrafts.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The second save of one draft is refused with the agreed sentence, and the revision stays at
    /// one: a test result is accepted exactly once.
    /// </summary>
    [Fact]
    public async Task A_draft_that_was_already_saved_is_refused_as_a_conflict()
    {
        await _factory.ResetAsync();
        var draftId = await TestAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, draftId));

        var response = await Save(ApiFactory.OwnerObjectId, draftId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            SaveNayaxConnection.DraftUnavailableMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var db = _factory.Database();
        Assert.Equal(1, (await db.BusinessNayaxConnections.AsNoTracking().SingleAsync()).CredentialRevision);
    }

    [Fact]
    public async Task A_draft_id_no_test_produced_is_refused_as_a_conflict()
    {
        await _factory.ResetAsync();

        var response = await Save(ApiFactory.OwnerObjectId, Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessNayaxConnections.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task A_save_with_no_draft_id_is_refused_as_a_validation_failure()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsJsonAsync(SaveRoute, new { draftId = (Guid?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessNayaxConnections.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// A token that did not sign in is not stored, and the draft survives the refusal - so an Owner
    /// can read the failing lines and fix the problem without the test result vanishing.
    /// </summary>
    [Fact]
    public async Task A_draft_whose_sign_in_failed_is_refused_and_stores_nothing()
    {
        await _factory.ResetAsync();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.InvalidOrExpiredToken));
        var draftId = await TestAsync();

        var response = await Save(ApiFactory.OwnerObjectId, draftId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessNayaxConnections.AsNoTracking().ToListAsync());
        Assert.Equal(draftId, (await db.NayaxConnectionTestDrafts.AsNoTracking().SingleAsync()).Id);
    }

    /// <summary>
    /// A business with no machines in Nayax yet finishes connecting: the status is <c>Ready</c> and
    /// the connection records that the machine checks are still pending, which is what the status
    /// endpoint then reports to the wizard.
    /// </summary>
    [Fact]
    public async Task A_save_with_no_machines_is_ready_with_the_machine_checks_pending()
    {
        await _factory.ResetAsync();
        _factory.Runner.Report = new NayaxConnectionCheckReport(
            [
                .. NayaxConnectionCheckPolicy.All.Select(check =>
                    NayaxConnectionCheckPolicy.RequiresMachine(check)
                        ? NayaxConnectionCheckLine.NotChecked(check)
                        : NayaxConnectionCheckLine.Passed(check)),
            ],
            MachineCount: 0);

        var saved = await (await Save(ApiFactory.OwnerObjectId, await TestAsync()))
            .Content.ReadFromJsonAsync<StatusResponse>();

        Assert.Equal("Ready", saved!.Status);
        Assert.True(saved.MachineChecksPending);
        Assert.Equal(
            new StatusResponse("Ready", true),
            await (await _factory.Authenticated(ApiFactory.OperatorObjectId).GetAsync(StatusRoute))
                .Content.ReadFromJsonAsync<StatusResponse>());
    }

    [Fact]
    public async Task A_sign_in_that_passed_beside_a_permission_failure_is_saved_pending_permissions()
    {
        await _factory.ResetAsync();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.MachineLastAlerts,
                NayaxConnectionCheckFailure.PermissionNotGranted));

        var saved = await (await Save(ApiFactory.OwnerObjectId, await TestAsync()))
            .Content.ReadFromJsonAsync<StatusResponse>();

        Assert.Equal("PendingPermissions", saved!.Status);
    }

    /// <summary>
    /// A re-test runs the same checks with the saved token and writes what they concluded. It needs
    /// no draft and carries no credential in the request at all.
    /// </summary>
    [Fact]
    public async Task A_re_test_reports_every_line_and_writes_the_concluded_status()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));
        _factory.Runner.Reset();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Passed(NayaxConnectionCheck.SignIn),
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.ProductGroups,
                NayaxConnectionCheckFailure.PermissionNotGranted));

        var response = await Retest(ApiFactory.OwnerObjectId);

        await AssertOk(response);
        var json = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<RetestResponse>(json, CamelCase);
        Assert.Equal("PendingPermissions", body!.Connection.Status);
        Assert.True(body.StatusUpdated);
        Assert.False(body.NothingFailed);
        Assert.Contains(("ProductGroups", "Failed", "PermissionNotGranted"), body.Checks
            .Select(check => (check.Check, check.Status, check.Failure)));

        // The stored token is what was tested, and the response never repeats it.
        var candidate = Assert.Single(_factory.Runner.Candidates);
        Assert.Equal(Token, candidate.AccessToken);
        Assert.Equal(1, candidate.CredentialRevision);
        Assert.DoesNotContain(Token, json, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The recovery path: a connection the status gate refuses for ordinary calls can still be
    /// re-tested, and a passing run brings it back to <c>Ready</c>.
    /// </summary>
    [Fact]
    public async Task A_connection_needing_attention_can_be_re_tested_back_to_ready()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));
        await _factory.SetStatusAsync(NayaxConnectionStatus.NeedsAttention);
        _factory.Runner.Reset();

        var body = await (await Retest(ApiFactory.OwnerObjectId))
            .Content.ReadFromJsonAsync<RetestResponse>();

        Assert.Equal("Ready", body!.Connection.Status);
        Assert.True(body.StatusUpdated);
    }

    [Fact]
    public async Task A_re_test_whose_sign_in_is_refused_moves_the_connection_to_needs_attention()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));
        _factory.Runner.Reset();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.InvalidOrExpiredToken));

        var body = await (await Retest(ApiFactory.OwnerObjectId))
            .Content.ReadFromJsonAsync<RetestResponse>();

        Assert.Equal("NeedsAttention", body!.Connection.Status);
        Assert.True(body.StatusUpdated);

        await using var db = _factory.Database();
        Assert.Equal(
            NayaxConnectionStatus.NeedsAttention,
            (await db.BusinessNayaxConnections.AsNoTracking().SingleAsync()).Status);
    }

    /// <summary>
    /// Nayax was unavailable, so nothing was learned and nothing is written: the stored status and
    /// its last-tested instant are untouched, and the response says the run updated nothing.
    /// </summary>
    [Fact]
    public async Task A_re_test_nayax_never_answered_leaves_the_stored_status_untouched()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));
        var before = await _factory.StoredConnectionAsync();
        _factory.Runner.Reset();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.NayaxUnavailable));

        var response = await Retest(ApiFactory.OwnerObjectId);
        var body = await response.Content.ReadFromJsonAsync<RetestResponse>();

        await AssertOk(response);
        Assert.False(body!.StatusUpdated);
        Assert.Equal("Ready", body.Connection.Status);
        Assert.Equal(before, await _factory.StoredConnectionAsync());
    }

    /// <summary>
    /// The contract correction of #506, end to end: a replacement token is saved while the re-test
    /// is running, so the re-test's result is discarded and the replacement keeps its own status.
    /// </summary>
    [Fact]
    public async Task A_re_test_overtaken_by_a_save_is_discarded_and_the_replacement_keeps_its_status()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));

        // The replacement is prepared first, then saved from inside the running re-test.
        var replacementDraftId = await TestAsync(token: "the-replacement-token");
        _factory.Runner.Reset();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Failed(
                NayaxConnectionCheck.SignIn,
                NayaxConnectionCheckFailure.InvalidOrExpiredToken));
        _factory.Runner.DuringRun = () => Save(ApiFactory.OwnerObjectId, replacementDraftId);

        var response = await Retest(ApiFactory.OwnerObjectId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            RetestNayaxConnection.ConnectionChangedMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        await using var db = _factory.Database();
        var connection = await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxConnectionStatus.Ready, connection.Status);
        Assert.Equal(2, connection.CredentialRevision);
        Assert.Equal("the-replacement-token", _factory.Decrypt(connection));
    }

    /// <summary>
    /// The same correction for a run that concluded nothing (issue #607), through the real pipeline:
    /// a replacement token is saved while an inconclusive re-test is running, so the stale report is
    /// refused with the same <c>409</c> sentence and the replacement's row is left exactly as the
    /// save left it - status, revision, token, last-tested instant and machine-check flag alike.
    ///
    /// A timeout, a <c>5xx</c> and an open circuit all reach this boundary as
    /// <see cref="NayaxConnectionCheckFailure.NayaxUnavailable"/>; the runner's own mapping of each
    /// one is <c>Infrastructure.Nayax.NayaxConnectionCheckRunnerTests</c>'. The two other causes that
    /// conclude nothing are included so the rule is not tied to one of them.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionCheckFailure.NayaxUnavailable)]
    [InlineData(NayaxConnectionCheckFailure.UnexpectedResponse)]
    [InlineData(NayaxConnectionCheckFailure.PermissionNotGranted)]
    public async Task An_inconclusive_re_test_overtaken_by_a_save_is_refused_and_writes_nothing(
        NayaxConnectionCheckFailure failure)
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));

        // The replacement is prepared first, then saved from inside the running re-test.
        var replacementDraftId = await TestAsync(token: "the-replacement-token");
        _factory.Runner.Reset();
        _factory.Runner.Report = Report(
            NayaxConnectionCheckLine.Failed(NayaxConnectionCheck.SignIn, failure));
        string? afterReplacement = null;
        _factory.Runner.DuringRun = async () =>
        {
            await AssertOk(await Save(ApiFactory.OwnerObjectId, replacementDraftId));
            afterReplacement = await _factory.StoredConnectionAsync();
        };

        var response = await Retest(ApiFactory.OwnerObjectId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            RetestNayaxConnection.ConnectionChangedMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        // The replacement's row is byte-for-byte what its own save wrote.
        Assert.Equal(afterReplacement, await _factory.StoredConnectionAsync());

        await using var db = _factory.Database();
        var connection = await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
        Assert.Equal(NayaxConnectionStatus.Ready, connection.Status);
        Assert.Equal(2, connection.CredentialRevision);
        Assert.Equal("the-replacement-token", _factory.Decrypt(connection));
    }

    /// <summary>
    /// There is nothing to re-test, and the answer is the stable "Nayax is not connected" failure
    /// with its own error code rather than an invented one.
    /// </summary>
    [Fact]
    public async Task A_re_test_with_no_stored_connection_reports_that_nayax_is_not_connected()
    {
        await _factory.ResetAsync();

        var response = await Retest(ApiFactory.OwnerObjectId);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(NayaxNotConnectedException.ErrorCode, body, StringComparison.Ordinal);
        Assert.Empty(_factory.Runner.Candidates);
    }

    /// <summary>
    /// Test and re-test spend one per-business allowance between them: five runs in any mix, then
    /// a <c>429</c> with a <c>Retry-After</c> and no Nayax call at all.
    /// </summary>
    [Fact]
    public async Task A_re_test_shares_the_test_s_rate_limit()
    {
        await _factory.ResetAsync();
        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));

        // One run spent by the test above; three more re-tests fill the window of five.
        for (var run = 0; run < 3; run++)
        {
            await AssertOk(await Retest(ApiFactory.OwnerObjectId));
        }

        await AssertOk(await Post(ApiFactory.OwnerObjectId, TestRoute, new { operatorId = OperatorId, token = Token }));

        var runsSoFar = _factory.Runner.Candidates.Count;
        var refusedRetest = await Retest(ApiFactory.OwnerObjectId);
        var refusedTest = await Post(
            ApiFactory.OwnerObjectId, TestRoute, new { operatorId = OperatorId, token = Token });

        Assert.Equal(HttpStatusCode.TooManyRequests, refusedRetest.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, refusedTest.StatusCode);
        Assert.Equal(runsSoFar, _factory.Runner.Candidates.Count);

        var retryAfter = Assert.Single(refusedRetest.Headers.GetValues("Retry-After"));
        Assert.InRange(
            int.Parse(retryAfter, CultureInfo.InvariantCulture),
            1,
            (int)NayaxConnectionTestRateLimiter.Window.TotalSeconds);
    }

    /// <summary>
    /// The status read is for the banners, so every member may make it - and it answers exactly two
    /// things. A business with no connection reads <c>NotConfigured</c>.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.OwnerObjectId)]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    public async Task Every_member_may_read_the_connection_status_and_gets_only_the_two_fields(string objectId)
    {
        await _factory.ResetAsync();

        var notConfigured = await _factory.Authenticated(objectId).GetAsync(StatusRoute);
        await AssertOk(notConfigured);
        var fields = JsonSerializer.Deserialize<JsonElement>(
            await notConfigured.Content.ReadAsStringAsync());

        Assert.Equal(
            new[] { "machineChecksPending", "status" },
            fields.EnumerateObject().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.Equal("NotConfigured", fields.GetProperty("status").GetString());
        Assert.False(fields.GetProperty("machineChecksPending").GetBoolean());

        await AssertOk(await Save(ApiFactory.OwnerObjectId, await TestAsync()));

        Assert.Equal(
            new StatusResponse("Ready", false),
            await (await _factory.Authenticated(objectId).GetAsync(StatusRoute))
                .Content.ReadFromJsonAsync<StatusResponse>());
    }

    /// <summary>
    /// The capability matrix is pinned centrally by <c>CapabilityAuthorizationApiTests</c>; what is
    /// asserted here is the consequence that matters - a caller who may not configure the
    /// integration cannot save a draft or start a re-test, and nothing is stored when they try.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    [InlineData(ApiFactory.StrangerObjectId)]
    public async Task A_caller_without_the_capability_can_neither_save_nor_re_test(string objectId)
    {
        await _factory.ResetAsync();
        var draftId = await TestAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await Save(objectId, draftId)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Retest(objectId)).StatusCode);

        await using var db = _factory.Database();
        Assert.Empty(await db.BusinessNayaxConnections.AsNoTracking().ToListAsync());
        Assert.Equal(draftId, (await db.NayaxConnectionTestDrafts.AsNoTracking().SingleAsync()).Id);
    }

    /// <summary>
    /// A signed-in person with no membership in this business reads no status either: the connection
    /// state is the business's own configuration.
    /// </summary>
    [Fact]
    public async Task A_caller_with_no_membership_cannot_read_the_status()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync(StatusRoute);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_unauthenticated_call_reaches_none_of_the_three_endpoints()
    {
        await _factory.ResetAsync();
        var client = _factory.CreateClient();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsJsonAsync(SaveRoute, new { draftId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await client.PostAsync(RetestRoute, content: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(StatusRoute)).StatusCode);
        Assert.Empty(_factory.Runner.Candidates);
    }

    /// <summary>The API's own property naming, for the two assertions that read the raw body.</summary>
    private static JsonSerializerOptions CamelCase { get; } =
        new(JsonSerializerDefaults.Web);

    private static NayaxConnectionCheckReport Report(params NayaxConnectionCheckLine[] lines) =>
        new(lines, MachineCount: null);

    private static async Task AssertOk(HttpResponseMessage response) =>
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    /// <summary>Runs a test as the Owner and returns the draft id it produced.</summary>
    private async Task<Guid> TestAsync(string token = Token)
    {
        var response = await Post(
            ApiFactory.OwnerObjectId, TestRoute, new { operatorId = OperatorId, token });
        await AssertOk(response);

        return (await response.Content.ReadFromJsonAsync<TestResponse>())!.DraftId;
    }

    private Task<HttpResponseMessage> Save(string objectId, Guid draftId) =>
        Post(objectId, SaveRoute, new { draftId });

    private Task<HttpResponseMessage> Retest(string objectId) =>
        _factory.Authenticated(objectId).PostAsync(RetestRoute, content: null);

    private Task<HttpResponseMessage> Post(string objectId, string route, object body) =>
        _factory.Authenticated(objectId).PostAsJsonAsync(route, body);

    private sealed record TestResponse(Guid DraftId);

    private sealed record StatusResponse(string Status, bool MachineChecksPending);

    private sealed record RetestResponse(
        StatusResponse Connection,
        bool StatusUpdated,
        int? MachineCount,
        bool NothingFailed,
        IReadOnlyList<CheckResponse> Checks);

    private sealed record CheckResponse(string Check, string Status, string? Failure);

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";
        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "22222222-2222-2222-2222-222222222222";
        public const string OperatorObjectId = "33333333-3333-3333-3333-333333333333";
        public const string StrangerObjectId = "55555555-5555-5555-5555-555555555555";

        public const int SeededBusinessId = 1;
        public const string EncryptionKeyId = "2026-10";

        private static readonly DateTime CreatedAt = new(2026, 10, 12, 1, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");

        /// <summary>
        /// The rate limiter's own clock, so one test's spent allowance does not leak into the next.
        /// </summary>
        private readonly FakeClock _limiterClock = new(CreatedAt);

        private readonly NayaxTokenProtectionOptions _protection = Protection();

        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public FakeCheckRunner Runner { get; } = new();

        /// <summary>An unrestricted context for arranging and inspecting state outside a request.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which is what applies the migrations
            // this context then reads and writes through.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public HttpClient Authenticated(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ScopeHeader, "access_as_user");
            return client;
        }

        /// <summary>
        /// Puts every test back to the same starting point: no connection, no draft, no recorded
        /// candidate, and a rate-limit window with nothing in it. The window is drained by moving
        /// the limiter's own clock past it, exactly as a real ten-minute wait does, so the
        /// production registration shape stays under test.
        /// </summary>
        public async Task ResetAsync()
        {
            await SeedAsync();
            Runner.Reset();

            await using var db = Database();
            await db.NayaxConnectionTestDrafts.ExecuteDeleteAsync();
            await db.BusinessNayaxConnections.ExecuteDeleteAsync();
            _limiterClock.UtcNow += NayaxConnectionTestRateLimiter.Window + TimeSpan.FromMinutes(1);
        }

        /// <summary>Forces a stored status, for the recovery path that starts from one.</summary>
        public async Task SetStatusAsync(NayaxConnectionStatus status)
        {
            await using var db = Database();
            await db.BusinessNayaxConnections.ExecuteUpdateAsync(
                setters => setters.SetProperty(connection => connection.Status, status));
        }

        /// <summary>The stored connection's own row, as text, so "untouched" is a byte comparison.</summary>
        public async Task<string> StoredConnectionAsync()
        {
            await using var db = Database();
            var row = await db.BusinessNayaxConnections.AsNoTracking().SingleAsync();
            return string.Join(
                '|',
                row.BusinessId,
                row.OperatorId,
                row.AccessTokenCiphertext,
                row.EncryptionKeyId,
                row.Status,
                row.CredentialRevision,
                row.MachineChecksPending,
                row.LastTestedAtUtc?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
                row.UpdatedAtUtc.ToString("O", CultureInfo.InvariantCulture));
        }

        /// <summary>The stored token, so a test can state that what was saved is what was tested.</summary>
        public string Decrypt(BusinessNayaxConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            return new AesGcmNayaxTokenProtector(_protection).Unprotect(
                new ProtectedNayaxToken(connection.EncryptionKeyId, connection.AccessTokenCiphertext));
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is the
            // migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { });

                // The one behavioural substitution: no test here may reach Nayax. Everything else -
                // the capability policies, the business scope, the use cases, the real encrypting
                // stores and the real transaction - is the production registration.
                services.AddSingleton<INayaxConnectionCheckRunner>(Runner);

                // The real limiter over a clock this fixture advances, so each test starts from an
                // empty window and test and re-test still share it.
                services.AddSingleton(new NayaxConnectionTestRateLimiter(_limiterClock));

                // The real AES-GCM protector with a test key, so the draft's and the connection's
                // tokens are genuinely encrypted at rest here rather than refused by the
                // fail-closed unconfigured one.
                services.AddSingleton<INayaxTokenProtector>(new AesGcmNayaxTokenProtector(_protection));
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        private static NayaxTokenProtectionOptions Protection()
        {
            var protection = new NayaxTokenProtectionOptions { ActiveKeyId = EncryptionKeyId };
            protection.Keys[EncryptionKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return protection;
        }

        private async Task SeedAsync()
        {
            await using var db = Database();
            if (_seeded)
            {
                return;
            }

            db.Businesses.Add(new Business
            {
                Id = SeededBusinessId,
                Name = "Vending Co",
                Status = BusinessStatus.Active,
                TimeZoneId = "Australia/Sydney",
                CreatedAtUtc = CreatedAt,
            });

            db.BusinessMemberships.AddRange(
                Membership(OwnerObjectId, BusinessRole.Owner),
                Membership(ManagerObjectId, BusinessRole.Manager),
                Membership(OperatorObjectId, BusinessRole.Operator));

            await db.SaveChangesAsync();
            _seeded = true;
        }

        private static BusinessMembership Membership(string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = SeededBusinessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
            };
    }

    /// <summary>
    /// A check runner that records the candidates it was handed, answers a scripted report, and can
    /// run one action in the middle of a run - which is how "the credentials were replaced while the
    /// checks were running" is tested through the real pipeline without timing.
    /// </summary>
    public sealed class FakeCheckRunner : INayaxConnectionCheckRunner
    {
        public List<NayaxConnectionCredential> Candidates { get; } = [];

        public NayaxConnectionCheckReport Report { get; set; } = AllPassed();

        /// <summary>An action to perform during the next run, if any. Cleared once used.</summary>
        public Func<Task>? DuringRun { get; set; }

        public void Reset()
        {
            Candidates.Clear();
            Report = AllPassed();
            DuringRun = null;
        }

        public async Task<NayaxConnectionCheckReport> RunAsync(
            NayaxConnectionCredential candidate,
            CancellationToken cancellationToken)
        {
            Candidates.Add(candidate);

            if (DuringRun is { } action)
            {
                DuringRun = null;
                await action();
            }

            return Report;
        }

        private static NayaxConnectionCheckReport AllPassed() =>
            new([.. NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed)], 1);
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the capability policies and the scope requirement
    /// are exercised exactly as they are in production without a real token.
    /// </summary>
    private sealed class TestActorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestActor";
        public const string DirectoryTenantIdHeader = "X-Test-Directory-Tenant-Id";
        public const string ObjectIdHeader = "X-Test-Object-Id";
        public const string ScopeHeader = "X-Test-Scope";

        public TestActorAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ObjectIdHeader, out var objectId))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new("tid", Request.Headers[DirectoryTenantIdHeader].ToString()),
                new("oid", objectId.ToString()),
            };
            if (Request.Headers.TryGetValue(ScopeHeader, out var scope))
            {
                claims.Add(new Claim("scp", scope.ToString()));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName)));
        }
    }
}
