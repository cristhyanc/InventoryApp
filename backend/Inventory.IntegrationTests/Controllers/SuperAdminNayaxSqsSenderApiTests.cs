using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.NayaxSqs;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Models;
using InventoryApi.Auth;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin's Nayax SQS sender identity over real HTTP (issue #1101): who may reach it, what
/// each decision writes, and what it refuses.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>RolePreviewMiddleware</c>,
/// <c>BusinessScopeMiddleware</c>'s independent re-check of the same policy, the tenant query
/// filters, <c>BusinessOwnershipEnforcer</c> and relational SQLite with the real migration history -
/// so the authorization and the tenancy boundary under test are the real ones. Only the
/// authentication is synthetic.</para>
///
/// <para><strong>Two active businesses throughout</strong>, because the claim that matters most here
/// is that a sender id belongs to exactly one of them.</para>
/// </summary>
public class SuperAdminNayaxSqsSenderApiTests
    : IClassFixture<SuperAdminNayaxSqsSenderApiTests.ApiFactory>, IAsyncLifetime
{
    private const string SenderId = "AIDAEXAMPLEUSERID123";
    private const string OtherSenderId = "AIDAEXAMPLEUSERID456";

    private readonly ApiFactory _factory;

    public SuperAdminNayaxSqsSenderApiTests(ApiFactory factory) => _factory = factory;

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Who may reach it

    /// <summary>
    /// The acceptance criterion "members of any business, including an Owner, are refused". An Owner
    /// holds the strongest business role there is and reaches neither endpoint: a business role is
    /// not platform authority, and this controller declares no capability any role could satisfy.
    /// </summary>
    [Theory]
    [InlineData(SuperAdminBusinessesApiFactory.TradingOwnerObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.TradingManagerObjectId)]
    [InlineData(ApiFactory.TradingOperatorObjectId)]
    [InlineData(SuperAdminBusinessesApiFactory.StrangerObjectId)]
    public async Task Both_endpoints_refuse_a_caller_who_is_not_the_configured_administrator(string objectId)
    {
        var client = _factory.Authenticated(objectId);

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasRecordedAsync();
    }

    [Fact]
    public async Task An_unauthenticated_request_is_unauthorized_and_records_nothing()
    {
        foreach (var response in await BothEndpointsAsync(_factory.CreateClient()))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        await AssertNothingWasRecordedAsync();
    }

    /// <summary>
    /// The delegated scope is required of the administrator too: platform authority is an additional
    /// requirement on a delegated API call, never a replacement for one.
    /// </summary>
    [Fact]
    public async Task The_administrator_without_the_delegated_scope_is_refused()
    {
        var client = _factory.WithoutScope(SuperAdminBusinessesApiFactory.AdministratorObjectId);

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasRecordedAsync();
    }

    /// <summary>
    /// The criterion "a read-only support session is refused, by its method rule". A member who
    /// presents a session id is still an ordinary member and is refused by the platform-admin policy,
    /// and the administrator inside their own session reaches the control surface as themselves - a
    /// session grants read-only access to somebody else's business and never platform authority over
    /// it.
    /// </summary>
    [Fact]
    public async Task A_member_presenting_a_support_session_id_is_still_refused()
    {
        var client = await _factory.MemberPresentingASupportSession();

        foreach (var response in await BothEndpointsAsync(client))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        await AssertNothingWasRecordedAsync();
    }

    /// <summary>
    /// A role preview cannot write: the administrator previewing a role is refused the <c>PUT</c>
    /// with <c>read_only_role_preview</c>, by the same middleware that refuses every other write in
    /// the application.
    /// </summary>
    [Fact]
    public async Task The_administrator_in_a_role_preview_cannot_record_a_sender()
    {
        var response = await PutAsync(
            _factory.Previewing("Owner"), _factory.OtherBusinessId, SenderId, isEnabled: true,
            expectedRevision: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("read_only_role_preview", await ErrorCodeAsync(response));

        await AssertNothingWasRecordedAsync();
    }

    #endregion

    #region Recording, changing and removing

    /// <summary>
    /// The first recording, end to end: <c>expectedRevision: null</c> creates the row at revision 1,
    /// the id is stored trimmed and exactly as entered, and no message has been received yet.
    /// </summary>
    [Fact]
    public async Task The_administrator_can_record_a_first_sender_with_a_null_expected_revision()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, $"  {SenderId} ", isEnabled: true,
            expectedRevision: null);

        var body = await ReadAsync(response);
        Assert.Equal(_factory.OtherBusinessId, body.GetProperty("businessId").GetInt32());
        Assert.Equal(SenderId, body.GetProperty("awsSenderId").GetString());
        Assert.True(body.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(1, body.GetProperty("revision").GetInt32());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("lastMessageReceivedAtUtc").ValueKind);

        var sender = Assert.Single(await _factory.SendersOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SenderId, sender.AwsSenderId);
        Assert.True(sender.IsEnabled);
        Assert.Equal(1, sender.Revision);
    }

    /// <summary>
    /// The GET is 404 until a row exists, and reads it back afterwards - which is what makes
    /// <c>expectedRevision: null</c> the only way to create the first one.
    /// </summary>
    [Fact]
    public async Task The_get_is_not_found_until_a_sender_has_been_recorded()
    {
        var administrator = _factory.AsAdministrator();

        var before = await administrator.GetAsync(SenderUrl(_factory.OtherBusinessId));
        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode);

        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);

        var after = await ReadAsync(await administrator.GetAsync(SenderUrl(_factory.OtherBusinessId)));
        Assert.Equal(SenderId, after.GetProperty("awsSenderId").GetString());
        Assert.Equal(1, after.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task An_unknown_business_is_not_found_on_both_endpoints()
    {
        foreach (var response in await BothEndpointsAsync(_factory.AsAdministrator(), businessId: 987654))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    /// <summary>
    /// The audit criterion: every write appends a row recording who, when, both sides of the id and
    /// both sides of the flag, and the revision - and the log event beside it carries no sender id at
    /// all.
    /// </summary>
    [Fact]
    public async Task Every_write_appends_an_audit_row_and_the_log_omits_the_sender_id()
    {
        var administrator = _factory.AsAdministrator();
        _factory.ClearLog();

        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);
        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: false, expectedRevision: 1);
        await PutAsync(administrator, _factory.OtherBusinessId, awsSenderId: null, isEnabled: false, expectedRevision: 2);

        var trail = await _factory.AuditsOfAsync(_factory.OtherBusinessId);
        Assert.Equal(3, trail.Count);

        Assert.Null(trail[0].FromAwsSenderId);
        Assert.Equal(SenderId, trail[0].ToAwsSenderId);
        Assert.False(trail[0].FromIsEnabled);
        Assert.True(trail[0].ToIsEnabled);
        Assert.Equal(1, trail[0].Revision);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            trail[0].ActorObjectId,
            ignoreCase: true);

        // The switch off: the id survives, the flag does not.
        Assert.Equal(SenderId, trail[1].FromAwsSenderId);
        Assert.Equal(SenderId, trail[1].ToAwsSenderId);
        Assert.True(trail[1].FromIsEnabled);
        Assert.False(trail[1].ToIsEnabled);

        // The removal: the id is cleared, and both sides of it are still on the row.
        Assert.Equal(SenderId, trail[2].FromAwsSenderId);
        Assert.Null(trail[2].ToAwsSenderId);
        Assert.Equal(3, trail[2].Revision);

        // Three log events, and not one of them carries the AWS identifier.
        var logged = _factory.LoggedEntries()
            .Where(entry => entry.Contains("Nayax SQS sender audit", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, logged.Length);
        Assert.All(logged, entry => Assert.DoesNotContain(SenderId, entry, StringComparison.Ordinal));
    }

    /// <summary>
    /// Remove, as the issue defines it: the id and the flag are cleared, the row and its audit
    /// history are kept, and the id is immediately free for another business.
    /// </summary>
    [Fact]
    public async Task Remove_clears_the_sender_keeps_the_row_and_frees_the_id()
    {
        var administrator = _factory.AsAdministrator();

        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);
        var removed = await PutAsync(
            administrator, _factory.OtherBusinessId, awsSenderId: null, isEnabled: false, expectedRevision: 1);

        var body = await ReadAsync(removed);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("awsSenderId").ValueKind);
        Assert.False(body.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(2, body.GetProperty("revision").GetInt32());

        // The row is kept, with its trail.
        var sender = Assert.Single(await _factory.SendersOfAsync(_factory.OtherBusinessId));
        Assert.Null(sender.AwsSenderId);
        Assert.Equal(2, sender.Revision);
        Assert.Equal(2, (await _factory.AuditsOfAsync(_factory.OtherBusinessId)).Count);

        // And the other business may now record the very id that was removed.
        var reused = await PutAsync(
            administrator, _factory.TradingBusinessId, SenderId, isEnabled: true, expectedRevision: null);
        Assert.Equal(SenderId, (await ReadAsync(reused)).GetProperty("awsSenderId").GetString());
    }

    /// <summary>And setting again after a removal reuses the row rather than restarting its series.</summary>
    [Fact]
    public async Task Setting_again_after_a_removal_continues_the_revision_series()
    {
        var administrator = _factory.AsAdministrator();

        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);
        await PutAsync(administrator, _factory.OtherBusinessId, awsSenderId: null, isEnabled: false, expectedRevision: 1);

        var body = await ReadAsync(await PutAsync(
            administrator, _factory.OtherBusinessId, OtherSenderId, isEnabled: true, expectedRevision: 2));

        Assert.Equal(OtherSenderId, body.GetProperty("awsSenderId").GetString());
        Assert.Equal(3, body.GetProperty("revision").GetInt32());
        Assert.Single(await _factory.SendersOfAsync(_factory.OtherBusinessId));
    }

    #endregion

    #region The expected-revision guard

    /// <summary>
    /// The three ways a presented revision can be wrong, each <c>409</c> with nothing written:
    /// <c>null</c> when a row exists, a number when none does, and a number that is not the stored
    /// one.
    /// </summary>
    [Fact]
    public async Task A_revision_that_is_not_the_stored_one_is_a_conflict_that_writes_nothing()
    {
        var administrator = _factory.AsAdministrator();

        // A number when no row exists.
        var early = await PutAsync(
            administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: 1);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Empty(await _factory.SendersOfAsync(_factory.OtherBusinessId));

        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);

        // null when a row already exists.
        var duplicateCreate = await PutAsync(
            administrator, _factory.OtherBusinessId, OtherSenderId, isEnabled: true, expectedRevision: null);
        Assert.Equal(HttpStatusCode.Conflict, duplicateCreate.StatusCode);
        Assert.Equal(
            NayaxSqsSenderPolicy.StaleRevisionMessage,
            await MessageAsync(duplicateCreate));

        // A stale number.
        var stale = await PutAsync(
            administrator, _factory.OtherBusinessId, OtherSenderId, isEnabled: true, expectedRevision: 99);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        // The row is exactly as the one accepted write left it.
        var sender = Assert.Single(await _factory.SendersOfAsync(_factory.OtherBusinessId));
        Assert.Equal(SenderId, sender.AwsSenderId);
        Assert.Equal(1, sender.Revision);
        Assert.Single(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
    }

    /// <summary>A body that omits the revision is refused rather than treated as "create".</summary>
    [Fact]
    public async Task A_body_without_an_expected_revision_is_refused()
    {
        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            SenderUrl(_factory.OtherBusinessId),
            new { awsSenderId = SenderId, isEnabled = true });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.SendersOfAsync(_factory.OtherBusinessId));
    }

    /// <summary>
    /// A body that omits <c>isEnabled</c> is refused rather than silently bound to <c>false</c>,
    /// which would turn an under-posted "change the id" request into a silent disable.
    /// </summary>
    [Fact]
    public async Task A_body_without_is_enabled_is_refused()
    {
        var response = await _factory.AsAdministrator().PutAsJsonAsync(
            SenderUrl(_factory.OtherBusinessId),
            new { awsSenderId = SenderId, expectedRevision = (int?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.SendersOfAsync(_factory.OtherBusinessId));
    }

    #endregion

    #region Refusals

    [Theory]
    [InlineData("AIDASHORT")]
    [InlineData("AIDA-EXAMPLE-USER-123")]
    [InlineData("arn:aws:iam::123456789012:user/restockroo")]
    public async Task An_invalid_sender_id_is_a_bad_request(string submitted)
    {
        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, submitted, isEnabled: true,
            expectedRevision: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(NayaxSqsSenderPolicy.InvalidSenderIdMessage, await MessageAsync(response));
        await AssertNothingWasRecordedAsync();
    }

    [Fact]
    public async Task Turning_the_mapping_on_with_no_sender_id_is_a_bad_request()
    {
        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, awsSenderId: null, isEnabled: true,
            expectedRevision: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(NayaxSqsSenderPolicy.EnabledNeedsSenderIdMessage, await MessageAsync(response));
        await AssertNothingWasRecordedAsync();
    }

    /// <summary>
    /// The duplicate criterion, across two real businesses: the second is refused with <c>409</c> and
    /// the message names and hints at no other business - not its name, not its id, and not the fact
    /// that it is the trading one.
    /// </summary>
    [Fact]
    public async Task A_sender_id_recorded_for_another_business_is_a_conflict_that_leaks_nothing()
    {
        var administrator = _factory.AsAdministrator();

        await PutAsync(administrator, _factory.TradingBusinessId, SenderId, isEnabled: true, expectedRevision: null);

        var response = await PutAsync(
            administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var message = await MessageAsync(response);
        Assert.Equal(NayaxSqsSenderPolicy.DuplicateSenderIdMessage, message);
        Assert.DoesNotContain(
            SuperAdminBusinessesApiFactory.TradingBusinessName, message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            _factory.TradingBusinessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            message,
            StringComparison.Ordinal);

        // And nothing was written for the business that asked - not even an audit row.
        Assert.Empty(await _factory.SendersOfAsync(_factory.OtherBusinessId));
        Assert.Empty(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
    }

    /// <summary>
    /// The lifecycle gate: a business that is not approved and trading cannot have the answer to
    /// "whose sales are these?" changed, which is <c>409</c> and writes nothing.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public async Task A_business_that_is_not_active_is_a_conflict(BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.OtherBusinessId, status);

        var response = await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, SenderId, isEnabled: true,
            expectedRevision: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(NayaxSqsSenderPolicy.BusinessNotActiveMessage, await MessageAsync(response));
        await AssertNothingWasRecordedAsync();
    }

    #endregion

    #region Tenant isolation

    /// <summary>
    /// The isolation criterion over HTTP: a decision addressed to one business neither reads nor
    /// writes the other's row, even when the other holds the id being submitted.
    /// </summary>
    [Fact]
    public async Task A_decision_for_one_business_leaves_the_other_untouched()
    {
        var administrator = _factory.AsAdministrator();

        await PutAsync(administrator, _factory.TradingBusinessId, SenderId, isEnabled: true, expectedRevision: null);
        await PutAsync(administrator, _factory.OtherBusinessId, OtherSenderId, isEnabled: false, expectedRevision: null);

        // Each GET reports that business's own row and nothing of the other's.
        var trading = await ReadAsync(await administrator.GetAsync(SenderUrl(_factory.TradingBusinessId)));
        var other = await ReadAsync(await administrator.GetAsync(SenderUrl(_factory.OtherBusinessId)));

        Assert.Equal(SenderId, trading.GetProperty("awsSenderId").GetString());
        Assert.True(trading.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(OtherSenderId, other.GetProperty("awsSenderId").GetString());
        Assert.False(other.GetProperty("isEnabled").GetBoolean());

        // And a refused duplicate against the trading business's id changed nothing of its row.
        await PutAsync(administrator, _factory.OtherBusinessId, SenderId, isEnabled: true, expectedRevision: 1);

        var unchanged = Assert.Single(await _factory.SendersOfAsync(_factory.TradingBusinessId));
        Assert.Equal(SenderId, unchanged.AwsSenderId);
        Assert.Equal(1, unchanged.Revision);
    }

    /// <summary>
    /// The response carries nothing about any business but its own key: no name, no contact detail,
    /// no plan and no Nayax Lynx credential value - the nine declared properties and no tenth.
    ///
    /// <para>Issue #1106 added two of them and they are the whole of that change:
    /// <c>lastReconciliationSucceededAtUtc</c>, the one column that issue widened this surface's read
    /// by, and <c>readerHealth</c>, which is about the queue reader's process and about no business
    /// at all.</para>
    /// </summary>
    [Fact]
    public async Task The_response_carries_exactly_the_declared_properties()
    {
        await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, SenderId, isEnabled: true,
            expectedRevision: null);

        var body = await ReadAsync(
            await _factory.AsAdministrator().GetAsync(SenderUrl(_factory.OtherBusinessId)));

        Assert.Equal(
            [
                "awsSenderId",
                "businessId",
                "createdAtUtc",
                "isEnabled",
                "lastMessageReceivedAtUtc",
                "lastReconciliationSucceededAtUtc",
                "readerHealth",
                "revision",
                "updatedAtUtc",
            ],
            body.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());

        // And the reader's health is three values about the reader: no business id, no message id,
        // no receipt handle, no queue URL and no count of anybody's sales.
        Assert.Equal(
            [
                "consecutivePollFailures",
                "deadLetterQueueDepth",
                "lastSuccessfulPollUtc",
            ],
            body.GetProperty("readerHealth")
                .EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// What the card reports for a test host, which is also the shipped state and every deployment
    /// with no queue configured: nothing has polled, nothing has failed, no dead-letter depth has
    /// been read, and this business's reconciliation has never succeeded. Each is reported as "not
    /// known" rather than as a reassuring zero instant (issue #1106).
    /// </summary>
    [Fact]
    public async Task The_response_reports_an_unstarted_reader_and_an_unverified_business()
    {
        await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, SenderId, isEnabled: true,
            expectedRevision: null);

        var body = await ReadAsync(
            await _factory.AsAdministrator().GetAsync(SenderUrl(_factory.OtherBusinessId)));

        Assert.Equal(
            JsonValueKind.Null, body.GetProperty("lastReconciliationSucceededAtUtc").ValueKind);

        var health = body.GetProperty("readerHealth");
        Assert.Equal(JsonValueKind.Null, health.GetProperty("lastSuccessfulPollUtc").ValueKind);
        Assert.Equal(0, health.GetProperty("consecutivePollFailures").GetInt32());
        Assert.Equal(JsonValueKind.Null, health.GetProperty("deadLetterQueueDepth").ValueKind);
    }

    /// <summary>
    /// The instant the hourly run of issue #1104 wrote is reported back unchanged, which is what
    /// makes the card able to say whether a business's queue has been verified at all (issue #1106).
    /// This surface writes neither that column nor the last-message one.
    /// </summary>
    [Fact]
    public async Task The_response_reports_the_last_successful_reconciliation()
    {
        var succeededAt = new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);

        await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, SenderId, isEnabled: true,
            expectedRevision: null);
        await _factory.RecordReconciliationSuccessAsync(_factory.OtherBusinessId, succeededAt);

        var body = await ReadAsync(
            await _factory.AsAdministrator().GetAsync(SenderUrl(_factory.OtherBusinessId)));

        Assert.Equal(
            succeededAt, body.GetProperty("lastReconciliationSucceededAtUtc").GetDateTime());

        // The write path reports it too, and still does not write it: a "Turn off" leaves it as it is.
        var afterWrite = await ReadAsync(await PutAsync(
            _factory.AsAdministrator(), _factory.OtherBusinessId, SenderId, isEnabled: false,
            expectedRevision: 1));

        Assert.Equal(
            succeededAt, afterWrite.GetProperty("lastReconciliationSucceededAtUtc").GetDateTime());
    }

    #endregion

    private static string SenderUrl(int businessId) =>
        $"/api/super-admin/businesses/{businessId}/nayax-sqs-sender";

    private static Task<HttpResponseMessage> PutAsync(
        HttpClient client,
        int businessId,
        string? awsSenderId,
        bool isEnabled,
        int? expectedRevision) =>
        client.PutAsJsonAsync(
            SenderUrl(businessId),
            new { awsSenderId, isEnabled, expectedRevision });

    /// <summary>
    /// Both endpoints of the surface, called once - so a claim about who may reach it is made about
    /// both rather than about the one a test happened to pick.
    /// </summary>
    private async Task<IReadOnlyList<HttpResponseMessage>> BothEndpointsAsync(
        HttpClient client,
        int? businessId = null)
    {
        var target = businessId ?? _factory.OtherBusinessId;

        return
        [
            await client.GetAsync(SenderUrl(target)),
            await PutAsync(client, target, SenderId, isEnabled: true, expectedRevision: null),
        ];
    }

    private async Task AssertNothingWasRecordedAsync()
    {
        Assert.Empty(await _factory.SendersOfAsync(_factory.OtherBusinessId));
        Assert.Empty(await _factory.SendersOfAsync(_factory.TradingBusinessId));
        Assert.Empty(await _factory.AuditsOfAsync(_factory.OtherBusinessId));
        Assert.Empty(await _factory.AuditsOfAsync(_factory.TradingBusinessId));
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<string?> MessageAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("message", out var message) ? message.GetString() : null;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return body.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>
    /// The Super Admin fixture's world, plus a second <em>active</em> business with an Owner of its
    /// own and an Operator in the trading business - so every refusal above is asked of a real
    /// business role, and every duplicate claim is made across two businesses that both trade.
    /// </summary>
    public sealed class ApiFactory : SuperAdminBusinessesApiFactory
    {
        public const string OtherBusinessOwnerObjectId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        public const string TradingOperatorObjectId = "66666666-6666-6666-6666-666666666666";
        public const string SessionId = "the-support-session-id-the-sqs-sender-tests-present";

        /// <summary>The business most decisions in this file are about - never the administrator's own.</summary>
        public int OtherBusinessId { get; private set; }

        public HttpClient Previewing(string role)
        {
            var client = Authenticated(AdministratorObjectId);
            client.DefaultRequestHeaders.TryAddWithoutValidation(RolePreviewHeader.Name, role);
            return client;
        }

        /// <summary>
        /// An ordinary member who presents a seeded support-session id. The session belongs to the
        /// administrator, so this is the "a member who sets the header is still a member" case.
        /// </summary>
        public async Task<HttpClient> MemberPresentingASupportSession()
        {
            await SeedSupportSessionAsync(TradingBusinessId, AdministratorObjectId, SessionId);

            var client = Authenticated(TradingOwnerObjectId);
            client.DefaultRequestHeaders.Add(SupportSessionHeader.Name, SessionId);
            return client;
        }

        public async Task<IReadOnlyList<BusinessNayaxSqsSender>> SendersOfAsync(int businessId)
        {
            await using var db = Database();
            return await db.BusinessNayaxSqsSenders
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .OrderBy(row => row.Id)
                .ToListAsync();
        }

        /// <summary>
        /// Stamps "the hourly reconciliation fully succeeded" on that business's sender row, as the
        /// hourly run of issue #1104 does - arranged directly here, because this surface never writes
        /// that column and issue #1106 only reports it.
        /// </summary>
        public async Task RecordReconciliationSuccessAsync(int businessId, DateTime succeededAtUtc)
        {
            await using var db = Database();
            var sender = await db.BusinessNayaxSqsSenders
                .SingleAsync(row => row.BusinessId == businessId);
            sender.LastReconciliationSucceededAtUtc = succeededAtUtc;
            await db.SaveChangesAsync();
        }

        public async Task<IReadOnlyList<BusinessNayaxSqsSenderAudit>> AuditsOfAsync(int businessId)
        {
            await using var db = Database();
            return await db.BusinessNayaxSqsSenderAudits
                .AsNoTracking()
                .Where(row => row.BusinessId == businessId)
                .OrderBy(row => row.Id)
                .ToListAsync();
        }

        public new async Task ResetAsync()
        {
            // Before the base reset deletes the businesses: a sender row left behind would hold its
            // id against the global unique index for every test that followed.
            await ClearSenderStateAsync();

            await base.ResetAsync();

            OtherBusinessId = await SeedBusinessAsync("Second Vending Co", BusinessStatus.Active);
            await SeedMembershipAsync(
                OtherBusinessId, OtherBusinessOwnerObjectId, BusinessRole.Owner, isActive: true);
            await SeedMembershipAsync(
                TradingBusinessId, TradingOperatorObjectId, BusinessRole.Operator, isActive: true);
        }

        private async Task ClearSenderStateAsync()
        {
            await using var db = Database();
            db.BusinessNayaxSqsSenderAudits.RemoveRange(
                await db.BusinessNayaxSqsSenderAudits.ToListAsync());
            db.BusinessNayaxSqsSenders.RemoveRange(await db.BusinessNayaxSqsSenders.ToListAsync());
            await db.SaveChangesAsync();
        }
    }
}
