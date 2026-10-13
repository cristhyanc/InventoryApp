using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The Super Admin's support-session endpoints over real HTTP, against the real migration history
/// (issue #550): who may reach them, what a start stores and answers, what the control surface
/// reports, what each refusal leaves behind, and what a business deactivation does to a session in
/// progress.
///
/// <para>Everything here goes through the complete pipeline - authentication, <c>[Authorize]</c>, the
/// platform-admin policy, <c>[RequiredScope]</c>, <c>BusinessScopeMiddleware</c>'s independent
/// re-check of the same policy, the tenant query filters and <c>BusinessOwnershipEnforcer</c> - so
/// the authorization under test is the real one. Only the authentication is synthetic.</para>
///
/// <para>The surface-wide authorization criterion ("non-admins denied", for every endpoint including
/// these three) is asserted in <c>SuperAdminBusinessesApiTests</c>, which calls the whole
/// platform-admin business surface for every caller who is not the configured administrator. What is
/// here is this feature's own behaviour.</para>
/// </summary>
public class SuperAdminSupportSessionsApiTests
    : IClassFixture<SuperAdminBusinessesApiFactory>, IAsyncLifetime
{
    private const string SupportSessionsUrl = "/api/super-admin/support-sessions";
    private const string Reason = "Investigating the reported dashboard error";

    private readonly SuperAdminBusinessesApiFactory _factory;

    public SuperAdminSupportSessionsApiTests(SuperAdminBusinessesApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    #region Starting a session

    /// <summary>
    /// The criterion: a start for an active business stores the session server-side with the actor,
    /// the business, the reason and both instants, and answers with the session id, the business name
    /// and the expiry. The row holds the id only as a hash - a database copy yields nothing usable -
    /// and the audit event records the start without the id.
    /// </summary>
    [Fact]
    public async Task Starting_a_session_stores_it_and_returns_the_id_name_and_expiry()
    {
        var client = _factory.AsAdministrator();

        var response = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = await ReadJsonAsync(response);
        var body = json.RootElement;
        var sessionId = body.GetProperty("sessionId").GetString();

        Assert.False(string.IsNullOrWhiteSpace(sessionId));
        Assert.Equal(_factory.TradingBusinessId, body.GetProperty("businessId").GetInt32());
        Assert.Equal(SuperAdminBusinessesApiFactory.TradingBusinessName, body.GetProperty("businessName").GetString());

        var startedAt = body.GetProperty("startedAtUtc").GetDateTime();
        var expiresAt = body.GetProperty("expiresAtUtc").GetDateTime();
        Assert.Equal(SupportSessionPolicy.ExpiresAt(startedAt), expiresAt);
        Assert.Equal(TimeSpan.FromMinutes(60), expiresAt - startedAt);

        // The contract is exactly these five properties: no actor id, no reason echoed back, and
        // nothing about the business beyond its key and its name.
        Assert.Equal(
            ["sessionId", "businessId", "businessName", "startedAtUtc", "expiresAtUtc"],
            body.EnumerateObject().Select(property => property.Name).ToArray());

        var session = Assert.Single(await _factory.SupportSessionsAsync());
        Assert.Equal(_factory.TradingBusinessId, session.BusinessId);
        Assert.Equal(SupportSessionId.Hash(sessionId!), session.SessionIdHash);
        Assert.Equal(
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            session.ActorObjectId,
            ignoreCase: true);
        Assert.Equal(Reason, session.Reason);
        Assert.Null(session.EndedAtUtc);

        // The stored row holds no copy of the id itself, under any column.
        Assert.DoesNotContain(sessionId!, session.SessionIdHash, StringComparison.Ordinal);
        Assert.DoesNotContain(sessionId!, session.Reason, StringComparison.Ordinal);

        var audit = AuditEntry();
        Assert.Contains("event Started", audit, StringComparison.Ordinal);
        Assert.Contains($"business {_factory.TradingBusinessId}", audit, StringComparison.Ordinal);
        Assert.Contains(SuperAdminBusinessesApiFactory.AdministratorObjectId, audit, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Reason, audit, StringComparison.Ordinal);
        // The one thing the trail must never carry.
        Assert.DoesNotContain(sessionId!, audit, StringComparison.Ordinal);
        Assert.DoesNotContain(session.SessionIdHash, audit, StringComparison.Ordinal);
        // And no business name, in this trail as in the two beside it.
        Assert.DoesNotContain(SuperAdminBusinessesApiFactory.TradingBusinessName, audit, StringComparison.Ordinal);
    }

    /// <summary>
    /// The criterion: a session may be started only for an <c>Active</c> business. A pending
    /// application, a refused one and a deactivated business are each <c>409</c> with the Domain's
    /// sentence, and nothing is written or audited.
    /// </summary>
    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public async Task A_session_may_be_started_only_for_an_active_business(BusinessStatus status)
    {
        await _factory.SetBusinessStatusAsync(_factory.TradingBusinessId, status);
        var client = _factory.AsAdministrator();

        var response = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(SupportSessionPolicy.BusinessNotActiveMessage, await DetailAsync(response));
        await AssertNothingWasStartedAsync();
    }

    /// <summary>The business id has to name a business that exists: an unknown one is 404.</summary>
    [Fact]
    public async Task An_unknown_business_is_not_found()
    {
        var client = _factory.AsAdministrator();

        var response = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/987654",
            new { reason = Reason });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await AssertNothingWasStartedAsync();
    }

    /// <summary>
    /// The criterion: one active session per administrator. A second start - even for a different
    /// business - is <c>409</c>, the first session is untouched, and only the first start was audited.
    /// </summary>
    [Fact]
    public async Task A_second_session_is_refused_while_one_is_still_active()
    {
        var otherBusinessId = await _factory.SeedBusinessAsync("Coastal Vending", BusinessStatus.Active);
        var client = _factory.AsAdministrator();

        var first = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        _factory.ClearLog();

        var second = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{otherBusinessId}",
            new { reason = "A second look" });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(SupportSessionPolicy.AlreadyActiveMessage, await DetailAsync(second));

        var session = Assert.Single(await _factory.SupportSessionsAsync());
        Assert.Equal(_factory.TradingBusinessId, session.BusinessId);
        Assert.Null(session.EndedAtUtc);
        Assert.Empty(AuditEntries());
    }

    /// <summary>
    /// An expired session does not block a new one, and nothing had to run for that to be true: the
    /// stored row still records no end, because expiry is derived rather than swept.
    /// </summary>
    [Fact]
    public async Task An_expired_session_does_not_block_a_new_start()
    {
        var expired = await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.AdministratorObjectId,
            startedAtUtc: DateTime.UtcNow.AddHours(-3),
            expiresAtUtc: DateTime.UtcNow.AddHours(-2));
        var client = _factory.AsAdministrator();

        var response = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null((await _factory.SupportSessionAsync(expired)).EndedAtUtc);
        Assert.Equal(2, (await _factory.SupportSessionsAsync()).Count);
    }

    /// <summary>
    /// A reason is required and bounded, and neither refusal writes or audits anything: a session is
    /// read access to somebody else's business, so an unexplained one is not created at all.
    /// </summary>
    [Fact]
    public async Task A_start_without_a_reason_is_refused_and_writes_nothing()
    {
        var client = _factory.AsAdministrator();

        var missing = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(SupportSessionPolicy.ReasonRequiredMessage, await DetailAsync(missing));

        // No body at all is the same refusal, rather than a model-binding error that says less.
        var noBody = await client.PostAsync($"{SupportSessionsUrl}/{_factory.TradingBusinessId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(SupportSessionPolicy.ReasonRequiredMessage, await DetailAsync(noBody));

        var tooLong = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = new string('x', SupportSessionPolicy.MaxReasonLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(SupportSessionPolicy.ReasonTooLongMessage, await DetailAsync(tooLong));

        await AssertNothingWasStartedAsync();
    }

    /// <summary>
    /// Starting a session does not make the administrator a member of anything, and *holding* one
    /// grants nothing: a request that does not present the session id in the
    /// <c>X-Support-Session</c> header runs as the administrator's own (non-existent) membership, so
    /// the business endpoints refuse it exactly as they always did.
    ///
    /// <para>This was #550's "a session grants no access at all" and is now #551's "a session grants
    /// nothing until it is presented" - the same assertions, and still the one worth pinning here:
    /// access that leaked from merely having a session would make the enforcement of #551
    /// unnecessary and unreviewed. What presenting it does grant is
    /// <c>SupportSessionRequestApiTests</c>.</para>
    /// </summary>
    [Fact]
    public async Task A_session_grants_nothing_until_it_is_presented()
    {
        var client = _factory.AsAdministrator();
        await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/products")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/business/current")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/dashboard/summary")).StatusCode);
    }

    #endregion

    #region Reading and ending

    /// <summary>
    /// The active read reports the session the administrator holds - business, name, reason and both
    /// instants, and never the session id - and answers <c>204</c> when they hold none.
    /// </summary>
    [Fact]
    public async Task The_active_session_is_reported_without_the_session_id()
    {
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{SupportSessionsUrl}/active")).StatusCode);

        var started = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        using var startedJson = await ReadJsonAsync(started);
        var sessionId = startedJson.RootElement.GetProperty("sessionId").GetString();

        var response = await client.GetAsync($"{SupportSessionsUrl}/active");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var body = json.RootElement;

        Assert.Equal(
            ["businessId", "businessName", "reason", "startedAtUtc", "expiresAtUtc"],
            body.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(_factory.TradingBusinessId, body.GetProperty("businessId").GetInt32());
        Assert.Equal(SuperAdminBusinessesApiFactory.TradingBusinessName, body.GetProperty("businessName").GetString());
        Assert.Equal(Reason, body.GetProperty("reason").GetString());

        // The credential is never re-issued: a lost id means starting a new session, which is
        // audited, not asking for the old one back.
        Assert.DoesNotContain(
            sessionId!,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A session belongs to the administrator it was granted to. Another administrator's session is
    /// not reported to this one, is not ended by this one's end call, and does not stop this one
    /// starting their own - which is also what makes a change of the configured administrator the end
    /// of every session it granted: the previous pair no longer passes the policy at all, and the new
    /// one's own reads never see the old row.
    /// </summary>
    [Fact]
    public async Task Another_administrators_session_is_neither_reported_nor_ended()
    {
        var theirs = await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.StrangerObjectId);
        var client = _factory.AsAdministrator();

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{SupportSessionsUrl}/active")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync($"{SupportSessionsUrl}/active/end", null)).StatusCode);
        Assert.Null((await _factory.SupportSessionAsync(theirs)).EndedAtUtc);

        var started = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
    }

    /// <summary>
    /// The criterion "sessions end on explicit end": the end records the instant on the row, reports
    /// what it closed, is audited once as ended by the administrator, and leaves the active read
    /// answering nothing. A second end writes nothing and audits nothing.
    /// </summary>
    [Fact]
    public async Task Ending_a_session_records_it_and_a_second_end_changes_nothing()
    {
        var client = _factory.AsAdministrator();
        await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        _factory.ClearLog();

        var response = await client.PostAsync($"{SupportSessionsUrl}/active/end", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await ReadJsonAsync(response);
        var body = json.RootElement;
        Assert.Equal(
            ["businessId", "businessName", "startedAtUtc", "expiresAtUtc", "endedAtUtc"],
            body.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(_factory.TradingBusinessId, body.GetProperty("businessId").GetInt32());

        var session = Assert.Single(await _factory.SupportSessionsAsync());
        Assert.NotNull(session.EndedAtUtc);
        Assert.Equal(body.GetProperty("endedAtUtc").GetDateTime(), session.EndedAtUtc);

        var audit = AuditEntry();
        Assert.Contains("event Ended", audit, StringComparison.Ordinal);
        Assert.Contains("end reason EndedByAdministrator", audit, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{SupportSessionsUrl}/active")).StatusCode);

        _factory.ClearLog();
        var endedAt = session.EndedAtUtc;
        Assert.Equal(
            HttpStatusCode.NoContent,
            (await client.PostAsync($"{SupportSessionsUrl}/active/end", null)).StatusCode);
        Assert.Equal(endedAt, (await _factory.SupportSessionAsync(session.Id)).EndedAtUtc);
        Assert.Empty(AuditEntries());
    }

    /// <summary>
    /// Ending a session the administrator holds frees them to start another one, which is the whole
    /// point of the one-session rule being a refusal rather than a replacement.
    /// </summary>
    [Fact]
    public async Task A_new_session_may_be_started_once_the_previous_one_is_ended()
    {
        var client = _factory.AsAdministrator();
        await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        await client.PostAsync($"{SupportSessionsUrl}/active/end", null);

        var again = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = "Looking again" });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var sessions = await _factory.SupportSessionsAsync();
        Assert.Equal(2, sessions.Count);
        Assert.NotNull(sessions[0].EndedAtUtc);
        Assert.Null(sessions[1].EndedAtUtc);
        // Two sessions, two different ids - and neither row holds a usable one.
        Assert.NotEqual(sessions[0].SessionIdHash, sessions[1].SessionIdHash);
    }

    #endregion

    #region Deactivation ends sessions

    /// <summary>
    /// The criterion, end to end over HTTP: deactivating a business ends every active support session
    /// for it in the same transaction, each end is audited, and a reactivation does not revive them.
    /// Another business's session keeps running.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_ends_its_sessions_and_reactivation_does_not_revive_them()
    {
        var otherBusinessId = await _factory.SeedBusinessAsync("Coastal Vending", BusinessStatus.Active);
        var otherBusinessSession = await _factory.SeedSupportSessionAsync(
            otherBusinessId,
            SuperAdminBusinessesApiFactory.StrangerObjectId,
            sessionId: "another-businesss-session");
        var anotherAdministratorsSession = await _factory.SeedSupportSessionAsync(
            _factory.TradingBusinessId,
            SuperAdminBusinessesApiFactory.TradingManagerObjectId,
            sessionId: "another-administrators-session");

        var client = _factory.AsAdministrator();
        var started = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = Reason });
        Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        _factory.ClearLog();

        var deactivation = await client.PostAsync(
            $"/api/super-admin/businesses/{_factory.TradingBusinessId}/deactivate",
            null);
        Assert.Equal(HttpStatusCode.OK, deactivation.StatusCode);

        // Both sessions of that business are closed; the other business's is untouched.
        var mine = Assert.Single((await _factory.SupportSessionsAsync())
            .Where(session => session.BusinessId == _factory.TradingBusinessId
                && string.Equals(
                    session.ActorObjectId,
                    SuperAdminBusinessesApiFactory.AdministratorObjectId,
                    StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(mine.EndedAtUtc);
        Assert.NotNull((await _factory.SupportSessionAsync(anotherAdministratorsSession)).EndedAtUtc);
        Assert.Null((await _factory.SupportSessionAsync(otherBusinessSession)).EndedAtUtc);

        // One end event per session closed, each attributed to the administrator whose session it
        // was, beside the deactivation's own lifecycle event.
        var ends = AuditEntries().Where(entry => entry.Contains("event Ended", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, ends.Length);
        Assert.All(ends, entry =>
            Assert.Contains("end reason BusinessDeactivated", entry, StringComparison.Ordinal));
        Assert.Contains(
            ends,
            entry => entry.Contains(
                SuperAdminBusinessesApiFactory.TradingManagerObjectId,
                StringComparison.OrdinalIgnoreCase));

        // The administrator's own read now reports nothing, and the reactivation that follows does
        // not bring the session back: a business returning is not a reason to restore somebody
        // else's access to it.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{SupportSessionsUrl}/active")).StatusCode);

        var reactivation = await client.PostAsync(
            $"/api/super-admin/businesses/{_factory.TradingBusinessId}/reactivate",
            null);
        Assert.Equal(HttpStatusCode.OK, reactivation.StatusCode);

        Assert.NotNull((await _factory.SupportSessionAsync(mine.Id)).EndedAtUtc);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync($"{SupportSessionsUrl}/active")).StatusCode);

        // And the administrator may start a fresh session for the business that came back, which is
        // the only way to look again.
        var again = await client.PostAsJsonAsync(
            $"{SupportSessionsUrl}/{_factory.TradingBusinessId}",
            new { reason = "Looking again after reactivation" });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    /// <summary>
    /// A deactivation with no session to end audits no end at all, so an ordinary deactivation never
    /// reads as one that cut somebody's access off.
    /// </summary>
    [Fact]
    public async Task Deactivating_a_business_with_no_session_audits_no_end()
    {
        var client = _factory.AsAdministrator();

        var deactivation = await client.PostAsync(
            $"/api/super-admin/businesses/{_factory.TradingBusinessId}/deactivate",
            null);

        Assert.Equal(HttpStatusCode.OK, deactivation.StatusCode);
        Assert.Empty(AuditEntries());
    }

    #endregion

    #region Helpers

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("detail")
            .GetString();

    /// <summary>Every support-session audit event the request under test emitted.</summary>
    private IReadOnlyList<string> AuditEntries() =>
    [
        .. _factory.LoggedEntries()
            .Where(entry => entry.StartsWith("Support session audit.", StringComparison.Ordinal)),
    ];

    private string AuditEntry() => Assert.Single(AuditEntries());

    private async Task AssertNothingWasStartedAsync()
    {
        Assert.Empty(await _factory.SupportSessionsAsync());
        Assert.Empty(AuditEntries());
    }

    #endregion
}
