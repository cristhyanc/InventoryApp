using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Inventory.Application.Members;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
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

// The entity and the Application's reported invite share a name, in different layers. The row is
// what these tests inspect outside the boundary, so it gets the alias and the Application type
// keeps its own name.
using MemberInviteRow = Inventory.Infrastructure.Models.MemberInvite;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The invite-link API of issue #531, exercised through the real request pipeline: authentication,
/// the required scope, the <c>Members.Manage</c> capability policy, the business scope middleware,
/// the controller and relational SQLite with the real migration history.
///
/// Two businesses are seeded throughout, which is what makes the isolation assertions meaningful:
/// an invite list that could only have come from the caller's own membership, and another
/// business's invite id that is simply not found rather than refused in a way that confirms it
/// exists.
/// </summary>
public sealed class MemberInvitesApiTests : IClassFixture<MemberInvitesApiTests.ApiFactory>
{
    private const string Uri = "/api/members/invites";

    private readonly ApiFactory _factory;

    public MemberInvitesApiTests(ApiFactory factory) => _factory = factory;

    private static string CancelUri(int inviteId) => $"{Uri}/{inviteId}/cancel";

    #region Creating

    /// <summary>
    /// The Owner's whole workflow: create an invite with a role and a note, receive the token once,
    /// and see the invite on the list as pending with the seven-day expiry.
    /// </summary>
    [Fact]
    public async Task An_owner_creates_an_invite_and_receives_the_token_once()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);

        var response = await client.PostAsJsonAsync(Uri, new { role = "Manager", note = "New site manager" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await ReadJsonAsync(response);
        var token = created.GetProperty("token").GetString();
        Assert.False(string.IsNullOrWhiteSpace(token));

        var invite = created.GetProperty("invite");
        Assert.Equal("Manager", invite.GetProperty("role").GetString());
        Assert.Equal("New site manager", invite.GetProperty("note").GetString());
        Assert.Equal("Pending", invite.GetProperty("status").GetString());
        Assert.Equal(
            invite.GetProperty("createdAtUtc").GetDateTime().ToUniversalTime().AddDays(7),
            invite.GetProperty("expiresAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(JsonValueKind.Null, invite.GetProperty("usedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, invite.GetProperty("cancelledAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, invite.GetProperty("redeemedByMembershipId").ValueKind);

        // The token is returned once, at creation, and the list never carries it again.
        var listed = await (await client.GetAsync(Uri)).Content.ReadAsStringAsync();
        Assert.DoesNotContain(token!, listed, StringComparison.Ordinal);
        Assert.DoesNotContain("token", listed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The token is stored only as its SHA-256 hash: the stored row holds the digest and no column
    /// of it holds the token itself, so a copy of the database - a backup, a restored snapshot -
    /// yields no usable invite link.
    /// </summary>
    [Fact]
    public async Task The_token_is_stored_only_as_a_hash()
    {
        await _factory.ResetAsync();

        var created = await ReadJsonAsync(
            await _factory.Authenticated(ApiFactory.OwnerObjectId)
                .PostAsJsonAsync(Uri, new { role = "Operator", note = "Weekend relief" }));
        var token = created.GetProperty("token").GetString()!;
        var inviteId = created.GetProperty("invite").GetProperty("inviteId").GetInt32();

        var stored = await _factory.InviteAsync(inviteId);

        Assert.Equal(MemberInviteToken.Hash(token), stored.TokenHash);
        Assert.NotEqual(token, stored.TokenHash);
        Assert.Equal(MemberInviteToken.HashLength, stored.TokenHash.Length);
        foreach (var value in new[]
        {
            stored.TokenHash,
            stored.Note,
            stored.CreatedByDirectoryTenantId,
            stored.CreatedByObjectId,
        })
        {
            Assert.DoesNotContain(token, value ?? string.Empty, StringComparison.Ordinal);
        }

        // And the business, the creating Owner, the role and the fixed lifetime are what the server
        // decided - none of them came from the request.
        Assert.Equal(ApiFactory.BusinessId, stored.BusinessId);
        Assert.Equal(ApiFactory.OwnerObjectId, stored.CreatedByObjectId);
        Assert.Equal(BusinessRole.Operator, stored.Role);
        Assert.Equal(MemberInviteStatus.Pending, stored.Status);
        Assert.Equal(stored.CreatedAtUtc.AddDays(7), stored.ExpiresAtUtc);
    }

    /// <summary>
    /// The token is drawn from the cryptographic generator, so two invites never share one. The
    /// decoded length is asserted against the agreed 128-bit floor rather than the current choice,
    /// which is what the contract actually promises.
    /// </summary>
    [Fact]
    public async Task Every_invite_gets_its_own_token_of_at_least_the_agreed_entropy()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);

        var tokens = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            tokens.Add((await ReadJsonAsync(await client.PostAsJsonAsync(Uri, new { role = "Operator" })))
                .GetProperty("token")
                .GetString()!);
        }

        Assert.Equal(3, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            tokens,
            token => Assert.True(
                Base64Url.DecodeFromChars(token).Length >= MemberInviteToken.MinimumEntropyBytes,
                $"An invite token carried fewer than {MemberInviteToken.MinimumEntropyBytes} random bytes."));
    }

    /// <summary>
    /// An unsupported role is refused before anything is stored. An enum is a compile-time
    /// constraint, not a validation: a JSON string and a hand-built request both submit arbitrary
    /// text. A numeric spelling is refused with the rest, so a client cannot supply a stored
    /// representation, and so is <c>Viewer</c> - the read-only role #328 anticipates but this
    /// deployment does not declare.
    /// </summary>
    [Theory]
    [InlineData("Administrator")]
    [InlineData("Viewer")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("40")]
    [InlineData(null)]
    public async Task An_unsupported_role_is_rejected_and_stores_nothing(string? role)
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsJsonAsync(Uri, new { role });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.InvitesAsync());
    }

    [Fact]
    public async Task A_note_longer_than_the_limit_is_rejected_and_stores_nothing()
    {
        await _factory.ResetAsync();

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId)
            .PostAsJsonAsync(Uri, new { role = "Manager", note = new string('n', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _factory.InvitesAsync());
    }

    /// <summary>
    /// A blank note is stored as no note at all, so an absent field and an empty one cannot become
    /// two different rows, and a note is trimmed rather than stored with the whitespace a form sent.
    /// </summary>
    [Fact]
    public async Task A_blank_note_is_stored_as_no_note_and_a_note_is_trimmed()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);

        var blank = await ReadJsonAsync(await client.PostAsJsonAsync(Uri, new { role = "Manager", note = "   " }));
        Assert.Equal(JsonValueKind.Null, blank.GetProperty("invite").GetProperty("note").ValueKind);

        var padded = await ReadJsonAsync(await client.PostAsJsonAsync(Uri, new { role = "Manager", note = "  Tuesday run  " }));
        Assert.Equal("Tuesday run", padded.GetProperty("invite").GetProperty("note").GetString());
    }

    #endregion

    #region Listing and expiry

    /// <summary>
    /// The list carries every state, and an expiry needs no row to change: a pending invite whose
    /// expiry has passed is reported as <c>Expired</c> while the stored status is still
    /// <c>Pending</c>, so there is no sweep to run and nothing that could leave an expired invite
    /// looking usable.
    /// </summary>
    [Fact]
    public async Task The_list_reports_every_state_and_derives_expiry_without_writing()
    {
        await _factory.ResetAsync();
        var pending = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager);
        var expired = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Operator,
            expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));
        var used = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Operator,
            status: MemberInviteStatus.Used,
            usedAtUtc: ApiFactory.CreatedAt,
            redeemedByMembershipId: await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId));
        var cancelled = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Manager,
            status: MemberInviteStatus.Cancelled,
            cancelledAtUtc: ApiFactory.CreatedAt);

        var listed = (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri)))
            .EnumerateArray()
            .ToArray();

        Assert.Equal(
            ["cancelledAtUtc", "createdAtUtc", "expiresAtUtc", "inviteId", "note", "redeemedByMembershipId", "role", "status", "usedAtUtc"],
            listed[0].EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

        Assert.Equal(
            [(pending, "Pending"), (expired, "Expired"), (used, "Used"), (cancelled, "Cancelled")],
            listed.Select(invite => (invite.GetProperty("inviteId").GetInt32(), invite.GetProperty("status").GetString())));

        // The expired one was reported, not rewritten.
        Assert.Equal(MemberInviteStatus.Pending, (await _factory.InviteAsync(expired)).Status);

        // The used one reports the membership its redemption created.
        var usedInvite = Assert.Single(listed, invite => invite.GetProperty("inviteId").GetInt32() == used);
        Assert.Equal(
            await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId),
            usedInvite.GetProperty("redeemedByMembershipId").GetInt32());
    }

    #endregion

    #region Cancelling

    [Fact]
    public async Task Cancelling_a_pending_invite_marks_it_cancelled()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);
        var inviteId = (await ReadJsonAsync(
                await client.PostAsJsonAsync(Uri, new { role = "Operator", note = (string?)null })))
            .GetProperty("invite")
            .GetProperty("inviteId")
            .GetInt32();

        var response = await client.PostAsync(CancelUri(inviteId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cancelled = await ReadJsonAsync(response);
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, cancelled.GetProperty("cancelledAtUtc").ValueKind);

        var stored = await _factory.InviteAsync(inviteId);
        Assert.Equal(MemberInviteStatus.Cancelled, stored.Status);
        Assert.NotNull(stored.CancelledAtUtc);
        Assert.Null(stored.UsedAtUtc);
        Assert.Null(stored.RedeemedByMembershipId);
    }

    /// <summary>
    /// Cancelling an already-cancelled invite is accepted and writes nothing, so a client that
    /// replays the request does not re-stamp the instant the invite was withdrawn at.
    /// </summary>
    [Fact]
    public async Task Cancelling_an_already_cancelled_invite_changes_nothing()
    {
        await _factory.ResetAsync();
        var inviteId = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Manager,
            status: MemberInviteStatus.Cancelled,
            cancelledAtUtc: ApiFactory.CreatedAt);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).PostAsync(CancelUri(inviteId), null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Cancelled", (await ReadJsonAsync(response)).GetProperty("status").GetString());
        Assert.Equal(ApiFactory.CreatedAt, (await _factory.InviteAsync(inviteId)).CancelledAtUtc);
    }

    /// <summary>
    /// A used invite is not cancellable: the membership it created exists, and recording the invite
    /// as withdrawn would make the record disown that access. An expired one is refused too, because
    /// a cancellation records a deliberate decision and an invite that ran out was nobody's.
    /// </summary>
    [Fact]
    public async Task A_used_invite_cannot_be_cancelled()
    {
        await _factory.ResetAsync();
        var inviteId = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Operator,
            status: MemberInviteStatus.Used,
            usedAtUtc: ApiFactory.CreatedAt,
            redeemedByMembershipId: await _factory.MembershipIdOfAsync(ApiFactory.OperatorObjectId));

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).PostAsync(CancelUri(inviteId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            CancelMemberInvite.AlreadyUsedMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var stored = await _factory.InviteAsync(inviteId);
        Assert.Equal(MemberInviteStatus.Used, stored.Status);
        Assert.Null(stored.CancelledAtUtc);
    }

    [Fact]
    public async Task An_expired_invite_cannot_be_cancelled()
    {
        await _factory.ResetAsync();
        var inviteId = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Manager,
            expiresAtUtc: DateTime.UtcNow.AddSeconds(-1));

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).PostAsync(CancelUri(inviteId), null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(
            CancelMemberInvite.AlreadyExpiredMessage,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var stored = await _factory.InviteAsync(inviteId);
        Assert.Equal(MemberInviteStatus.Pending, stored.Status);
        Assert.Null(stored.CancelledAtUtc);
    }

    [Fact]
    public async Task An_invite_id_that_exists_nowhere_is_not_found()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await _factory.Authenticated(ApiFactory.OwnerObjectId).PostAsync(CancelUri(999_999), null)).StatusCode);
    }

    #endregion

    #region Two-business isolation

    /// <summary>
    /// The list is an answer about one business, and it could only have come from the caller's own
    /// membership: nothing in the request chose a business, and the other business's Owner sees
    /// their own invite and none of this one's.
    /// </summary>
    [Fact]
    public async Task The_list_never_carries_another_businesss_invite()
    {
        await _factory.ResetAsync();
        var mine = await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, note: "Mine");
        var theirs = await _factory.SeedInviteAsync(ApiFactory.OtherBusinessId, BusinessRole.Owner, note: "Theirs");

        var ownerSees = (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri)))
            .EnumerateArray()
            .Select(invite => invite.GetProperty("inviteId").GetInt32())
            .ToArray();
        var otherSees = (await ReadJsonAsync(await _factory.Authenticated(ApiFactory.OtherOwnerObjectId).GetAsync(Uri)))
            .EnumerateArray()
            .Select(invite => invite.GetProperty("inviteId").GetInt32())
            .ToArray();

        Assert.Equal([mine], ownerSees);
        Assert.Equal([theirs], otherSees);

        var body = await (await _factory.Authenticated(ApiFactory.OwnerObjectId).GetAsync(Uri)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Theirs", body, StringComparison.Ordinal);
        Assert.DoesNotContain("businessId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tokenHash", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The isolation rule on the write: another business's invite id is not found, however complete
    /// the caller's own capability is. An Owner holds every capability there is and still reaches
    /// nothing of another business's, and the refusal is the boundary's <c>404</c> rather than
    /// anything that would confirm the id exists.
    /// </summary>
    [Fact]
    public async Task Another_businesss_invite_cannot_be_cancelled_and_is_left_untouched()
    {
        await _factory.ResetAsync();
        var theirs = await _factory.SeedInviteAsync(ApiFactory.OtherBusinessId, BusinessRole.Manager);

        var response = await _factory.Authenticated(ApiFactory.OwnerObjectId).PostAsync(CancelUri(theirs), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var stored = await _factory.InviteAsync(theirs);
        Assert.Equal(MemberInviteStatus.Pending, stored.Status);
        Assert.Null(stored.CancelledAtUtc);
    }

    #endregion

    #region Authorization

    /// <summary>
    /// Owner-only, on every action. A Manager and an Operator hold no <c>Members.Manage</c>
    /// capability, so the authorization middleware refuses them before the controller runs, with a
    /// bare <c>403</c> that describes neither the capability nor their role.
    /// </summary>
    [Theory]
    [InlineData(ApiFactory.ManagerObjectId)]
    [InlineData(ApiFactory.OperatorObjectId)]
    public async Task A_member_without_the_members_capability_is_refused_every_action(string objectId)
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(objectId);

        foreach (var response in new[]
        {
            await client.GetAsync(Uri),
            await client.PostAsJsonAsync(Uri, new { role = "Operator" }),
            await client.PostAsync(CancelUri(1), null),
        })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("Members.Manage", body, StringComparison.OrdinalIgnoreCase);
        }

        // And the refusal really was a refusal: no invite was created by any of it.
        Assert.Empty(await _factory.InvitesAsync());
    }

    [Fact]
    public async Task An_unauthenticated_caller_is_refused_with_401()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await _factory.CreateClient().GetAsync(Uri)).StatusCode);
    }

    [Fact]
    public async Task An_authenticated_caller_without_the_required_scope_is_refused()
    {
        await _factory.ResetAsync();

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await _factory.WithoutScope(ApiFactory.OwnerObjectId).GetAsync(Uri)).StatusCode);
    }

    [Fact]
    public async Task A_caller_with_no_membership_is_refused_and_told_nothing()
    {
        await _factory.ResetAsync();
        await _factory.SeedInviteAsync(ApiFactory.BusinessId, BusinessRole.Manager, note: "Mine");

        var response = await _factory.Authenticated(ApiFactory.StrangerObjectId).GetAsync(Uri);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Mine", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Vending Co", body, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region Audit

    /// <summary>
    /// One audit event per committed change, carrying the actor, the business, the invite and the
    /// role - and never the token or the Owner's note. The token is the secret the whole feature is
    /// built around, so the one assertion that matters most here is that no part of it reached a
    /// log, in plaintext or hashed.
    /// </summary>
    [Fact]
    public async Task A_creation_and_a_cancellation_are_each_audited_without_the_token()
    {
        await _factory.ResetAsync();
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);
        _factory.ClearLog();

        var created = await ReadJsonAsync(
            await client.PostAsJsonAsync(Uri, new { role = "Manager", note = "Invite for Ada" }));
        var token = created.GetProperty("token").GetString()!;
        var inviteId = created.GetProperty("invite").GetProperty("inviteId").GetInt32();

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(CancelUri(inviteId), null)).StatusCode);

        var logged = _factory.LoggedText();
        Assert.Contains("Member invite audit", logged, StringComparison.Ordinal);
        Assert.Contains($"invite {inviteId}; change Created; role Manager", logged, StringComparison.Ordinal);
        Assert.Contains($"invite {inviteId}; change Cancelled; role Manager", logged, StringComparison.Ordinal);
        Assert.Contains(ApiFactory.OwnerObjectId, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ApiFactory.DirectoryTenantId, logged, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"business {ApiFactory.BusinessId}", logged, StringComparison.Ordinal);

        Assert.DoesNotContain(token, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(MemberInviteToken.Hash(token), logged, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Invite for Ada", logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refused change is not an applied one, and a change that changed nothing is not one either:
    /// the audit records state transitions, and neither request produced any.
    /// </summary>
    [Fact]
    public async Task A_refused_or_replayed_cancellation_is_not_audited_as_a_change()
    {
        await _factory.ResetAsync();
        var used = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Operator,
            status: MemberInviteStatus.Used,
            usedAtUtc: ApiFactory.CreatedAt);
        var alreadyCancelled = await _factory.SeedInviteAsync(
            ApiFactory.BusinessId,
            BusinessRole.Manager,
            status: MemberInviteStatus.Cancelled,
            cancelledAtUtc: ApiFactory.CreatedAt);
        var client = _factory.Authenticated(ApiFactory.OwnerObjectId);
        _factory.ClearLog();

        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync(CancelUri(used), null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync(CancelUri(alreadyCancelled), null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Uri, new { role = "Viewer" })).StatusCode);

        Assert.DoesNotContain("Member invite audit", _factory.LoggedText(), StringComparison.Ordinal);
    }

    #endregion

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

        public const string OwnerObjectId = "11111111-1111-1111-1111-111111111111";
        public const string ManagerObjectId = "33333333-3333-3333-3333-333333333333";
        public const string OperatorObjectId = "44444444-4444-4444-4444-444444444444";
        public const string OtherOwnerObjectId = "77777777-7777-7777-7777-777777777777";
        public const string StrangerObjectId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

        public const int BusinessId = 1;
        public const int OtherBusinessId = 2;

        public static readonly DateTime CreatedAt = new(2026, 2, 10, 4, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly RecordingLoggerProvider _logs = new();

        public ApiFactory() => _connection.Open();

        /// <summary>An unrestricted context for arranging and verifying state outside the boundary.</summary>
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

        public HttpClient WithoutScope(string objectId)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.DirectoryTenantIdHeader, DirectoryTenantId);
            client.DefaultRequestHeaders.Add(TestActorAuthenticationHandler.ObjectIdHeader, objectId);
            return client;
        }

        public void ClearLog() => _logs.Clear();

        public string LoggedText() => _logs.Text();

        public async Task<int> MembershipIdOfAsync(string objectId)
        {
            await using var db = Database();
            return (await db.BusinessMemberships.AsNoTracking().SingleAsync(row => row.ObjectId == objectId)).Id;
        }

        /// <summary>The stored invite row, for asserting what a request did and did not write.</summary>
        public async Task<MemberInviteRow> InviteAsync(int inviteId)
        {
            await using var db = Database();
            return await db.MemberInvites.AsNoTracking().SingleAsync(invite => invite.Id == inviteId);
        }

        /// <summary>Every stored invite, of every business.</summary>
        public async Task<IReadOnlyList<MemberInviteRow>> InvitesAsync()
        {
            await using var db = Database();
            return await db.MemberInvites.AsNoTracking().ToListAsync();
        }

        /// <summary>
        /// Writes one invite from outside the request boundary, so a test can arrange a state the
        /// API itself will not create: another business's invite, an expired one, a used one, or one
        /// that is already cancelled.
        /// </summary>
        public async Task<int> SeedInviteAsync(
            int businessId,
            BusinessRole role,
            string? note = null,
            MemberInviteStatus status = MemberInviteStatus.Pending,
            DateTime? expiresAtUtc = null,
            DateTime? usedAtUtc = null,
            DateTime? cancelledAtUtc = null,
            int? redeemedByMembershipId = null)
        {
            await using var db = Database();
            var createdAtUtc = DateTime.UtcNow;
            var invite = new MemberInviteRow
            {
                BusinessId = businessId,
                Role = role,
                Note = note,
                // A seeded row holds a hash of something nobody knows, exactly as a real one holds a
                // hash of a token only its creation response saw.
                TokenHash = MemberInviteToken.Hash(Guid.NewGuid().ToString()),
                Status = status,
                CreatedByDirectoryTenantId = DirectoryTenantId,
                CreatedByObjectId = OwnerObjectId,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = expiresAtUtc ?? MemberInvitePolicy.ExpiresAt(createdAtUtc),
                UsedAtUtc = usedAtUtc,
                CancelledAtUtc = cancelledAtUtc,
                RedeemedByMembershipId = redeemedByMembershipId,
            };

            db.MemberInvites.Add(invite);
            await db.SaveChangesAsync();
            return invite.Id;
        }

        /// <summary>
        /// Puts the two businesses and their memberships back exactly as seeded, with no invite at
        /// all. These tests create and cancel invites, so each one starts from the same world rather
        /// than from whatever its predecessor left behind.
        /// </summary>
        public async Task ResetAsync()
        {
            await using var db = Database();
            // Invites first: one may point at a membership, and that foreign key is restricted.
            db.MemberInvites.RemoveRange(await db.MemberInvites.ToListAsync());
            await db.SaveChangesAsync();
            db.BusinessMemberships.RemoveRange(await db.BusinessMemberships.ToListAsync());
            await db.SaveChangesAsync();
            db.Businesses.RemoveRange(await db.Businesses.ToListAsync());
            await db.SaveChangesAsync();

            db.Businesses.AddRange(
                Business(BusinessId, "Vending Co"),
                Business(OtherBusinessId, "Second Vending Co"));
            await db.SaveChangesAsync();

            db.BusinessMemberships.AddRange(
                Membership(BusinessId, OwnerObjectId, BusinessRole.Owner),
                Membership(BusinessId, ManagerObjectId, BusinessRole.Manager),
                Membership(BusinessId, OperatorObjectId, BusinessRole.Operator),
                Membership(OtherBusinessId, OtherOwnerObjectId, BusinessRole.Owner));
            await db.SaveChangesAsync();
        }

        private static Business Business(int id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                CreatedAtUtc = CreatedAt,
            };

        private static BusinessMembership Membership(int businessId, string objectId, BusinessRole role) =>
            new()
            {
                BusinessId = businessId,
                DirectoryTenantId = DirectoryTenantId,
                ObjectId = objectId,
                Role = role,
                IsActive = true,
                CreatedAtUtc = CreatedAt,
                StatusChangedAtUtc = CreatedAt,
            };

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema under these tests is
            // the migration history rather than a model snapshot.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));

            builder.ConfigureTestServices(services =>
                services.AddAuthentication(TestActorAuthenticationHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestActorAuthenticationHandler>(
                        TestActorAuthenticationHandler.SchemeName, _ => { }));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }
    }

    /// <summary>
    /// Stands in for Entra: the caller's <c>(tid, oid)</c> pair and delegated scope come from
    /// request headers, so membership resolution, the capability policy and the scope requirement
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

    /// <summary>
    /// Captures everything the host logs, from every category and including log scopes, so the
    /// audit assertions are made over the whole request's logging rather than one logger's.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _entries = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Clear()
        {
            lock (_entries)
            {
                _entries.Clear();
            }
        }

        public string Text()
        {
            lock (_entries)
            {
                return string.Join('\n', _entries);
            }
        }

        public void Dispose()
        {
        }

        private void Add(string entry)
        {
            lock (_entries)
            {
                _entries.Add(entry);
            }
        }

        private sealed class Recording(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                provider.Add(state.ToString() ?? string.Empty);
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                provider.Add(formatter(state, exception));
                if (exception is not null)
                {
                    provider.Add(exception.ToString());
                }
            }
        }
    }
}
