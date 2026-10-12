using Inventory.Application.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the Nayax connection-test draft store (issue #536). SQLite rather than
/// InMemory on purpose: the one-draft-per-business unique index, the replacing <c>DELETE</c> that
/// runs in the database rather than through <c>SaveChanges</c>, and the expiry comparison inside the
/// query are relational behaviour InMemory would not prove.
///
/// The properties under test are the ones a reviewer cannot read off the adapter: no plaintext token
/// reaches the database, a new test replaces the previous draft rather than leaving a second
/// decryptable credential behind, an expired draft stops existing, and one business can neither
/// read nor replace another's draft.
/// </summary>
public class EfNayaxConnectionTestDraftStoreTests
{
    private const string Token = "nayax-lynx-draft-token-value";
    private const string OtherToken = "nayax-lynx-second-draft-token";
    private const string OperatorId = "2002736764";
    private const string OtherOperatorId = "9009009009";
    private const string ActiveKeyId = "2026-10";
    private static readonly DateTime CreatedAt = new(2026, 10, 11, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime ExpiresAt = CreatedAt.AddMinutes(15);

    private static readonly ActorIdentity Owner = Actor("11111111-1111-1111-1111-111111111111");
    private static readonly ActorIdentity OtherOwner = Actor("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task A_stored_draft_is_readable_by_its_creator_without_its_token()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var draftId = Guid.NewGuid();

        await store.ReplaceAsync(Draft(draftId), CancellationToken.None);

        var stored = await store.FindAsync(draftId, Owner, CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal(draftId, stored!.Id);
        Assert.Equal(OperatorId, stored.OperatorId);
        Assert.Equal("{\"checks\":\"ok\"}", stored.ResultsJson);
        Assert.Equal(CreatedAt, stored.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAtUtc.Kind);
        Assert.Equal(ExpiresAt, stored.ExpiresAtUtc);
        Assert.Equal(DateTimeKind.Utc, stored.ExpiresAtUtc.Kind);

        // The read carries no token and no ciphertext: the type it returns cannot hold either.
        Assert.DoesNotContain(Token, stored.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stored_token_is_ciphertext_and_the_row_names_the_key_that_encrypted_it()
    {
        await using var fixture = await DraftFixture.CreateAsync();

        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None);

        var row = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(fixture.BusinessA, row.BusinessId);
        Assert.Equal(ActiveKeyId, row.EncryptionKeyId);
        Assert.NotEqual(Token, row.AccessTokenCiphertext);
        Assert.DoesNotContain(Token, row.AccessTokenCiphertext, StringComparison.Ordinal);
        Assert.Equal(Token, Protector(ActiveKeyId).Unprotect(
            new ProtectedNayaxToken(row.EncryptionKeyId, row.AccessTokenCiphertext)));
    }

    /// <summary>
    /// No plaintext token anywhere in the draft table, in any column - not only in the column the
    /// adapter meant to encrypt.
    /// </summary>
    [Fact]
    public async Task No_column_of_the_draft_table_holds_the_plaintext_token()
    {
        await using var fixture = await DraftFixture.CreateAsync();

        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None);

        Assert.DoesNotContain(Token, await fixture.DumpDraftsAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A new test replaces the previous draft: one row per business, the earlier id gone rather than
    /// both staying usable, and no superseded credential ciphertext left behind.
    /// </summary>
    [Fact]
    public async Task A_new_test_replaces_the_business_s_one_draft()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        await store.ReplaceAsync(Draft(first), CancellationToken.None);
        await store.ReplaceAsync(
            Draft(second, OtherOperatorId, OtherToken), CancellationToken.None);

        var rows = await fixture.AllRowsAsync();
        Assert.Single(rows);
        Assert.Equal(second, rows[0].Id);
        Assert.Equal(OtherOperatorId, rows[0].OperatorId);
        Assert.Null(await store.FindAsync(first, Owner, CancellationToken.None));
        Assert.NotNull(await store.FindAsync(second, Owner, CancellationToken.None));
        Assert.DoesNotContain(Token, await fixture.DumpDraftsAsync(), StringComparison.Ordinal);
    }

    /// <summary>
    /// A replacement also clears an <em>expired</em> draft, so a business that abandoned a test does
    /// not keep a stale credential ciphertext once it tests again.
    /// </summary>
    [Fact]
    public async Task A_replacement_clears_an_expired_draft_too()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA, readAt: ExpiresAt.AddHours(1));

        await store.ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None);
        var second = Guid.NewGuid();
        await store.ReplaceAsync(
            Draft(second, createdAt: ExpiresAt.AddHours(1), expiresAt: ExpiresAt.AddHours(2)),
            CancellationToken.None);

        var rows = await fixture.AllRowsAsync();
        Assert.Single(rows);
        Assert.Equal(second, rows[0].Id);
    }

    /// <summary>
    /// Past its expiry the draft stops existing as far as every caller is concerned, even though the
    /// row is still there until the next test replaces it. The boundary is exclusive: the instant it
    /// expires, it is gone.
    /// </summary>
    [Fact]
    public async Task An_expired_draft_does_not_exist()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        Assert.NotNull(await fixture.StoreFor(fixture.BusinessA, readAt: ExpiresAt.AddSeconds(-1))
            .FindAsync(draftId, Owner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessA, readAt: ExpiresAt)
            .FindAsync(draftId, Owner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessA, readAt: ExpiresAt.AddHours(1))
            .FindAsync(draftId, Owner, CancellationToken.None));

        // The row itself is untouched: expiry is a read rule, not a sweep.
        Assert.Single(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// The draft is bound to the Owner who created it. Another member of the same business naming
    /// the right id is told the draft does not exist.
    /// </summary>
    [Fact]
    public async Task Another_actor_in_the_same_business_cannot_read_the_draft()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var draftId = Guid.NewGuid();

        await store.ReplaceAsync(Draft(draftId), CancellationToken.None);

        Assert.Null(await store.FindAsync(draftId, OtherOwner, CancellationToken.None));
    }

    [Fact]
    public async Task An_id_no_draft_has_reads_as_absent()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        await store.ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None);

        Assert.Null(await store.FindAsync(Guid.NewGuid(), Owner, CancellationToken.None));
    }

    /// <summary>
    /// Two businesses, each holding its own draft under its own credentials: neither can read the
    /// other's, even knowing its exact id and even with the same actor identity presented.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_read_another_s_draft()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(a), CancellationToken.None);
        await fixture.StoreFor(fixture.BusinessB).ReplaceAsync(
            Draft(b, OtherOperatorId, OtherToken), CancellationToken.None);

        Assert.Null(await fixture.StoreFor(fixture.BusinessA).FindAsync(b, Owner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessB).FindAsync(a, Owner, CancellationToken.None));

        var own = await fixture.StoreFor(fixture.BusinessB).FindAsync(b, Owner, CancellationToken.None);
        Assert.Equal(OtherOperatorId, own!.OperatorId);
    }

    /// <summary>
    /// A business's test does not disturb another's draft. The replacing <c>DELETE</c> runs in the
    /// database, so this is the test that proves the query filter really is in its <c>WHERE</c>
    /// clause.
    /// </summary>
    [Fact]
    public async Task Replacing_one_business_s_draft_leaves_another_s_alone()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(a), CancellationToken.None);
        await fixture.StoreFor(fixture.BusinessB).ReplaceAsync(
            Draft(b, OtherOperatorId, OtherToken), CancellationToken.None);

        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None);

        var rows = await fixture.AllRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Id == b && row.BusinessId == fixture.BusinessB);
    }

    [Fact]
    public async Task A_caller_with_no_resolved_business_reads_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        Assert.Null(await fixture.DeniedStore().FindAsync(draftId, Owner, CancellationToken.None));
    }

    /// <summary>
    /// A write without a single resolved business is refused rather than running unscoped: the
    /// replacing <c>DELETE</c> does not pass through <c>BusinessOwnershipEnforcer</c>, so an unscoped
    /// statement would reach every business's draft.
    /// </summary>
    [Fact]
    public async Task A_write_without_a_resolved_business_is_refused_and_deletes_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var existing = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(existing), CancellationToken.None);

        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => fixture.DeniedStore().ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => fixture.UnscopedStore().ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None));

        var rows = await fixture.AllRowsAsync();
        Assert.Single(rows);
        Assert.Equal(existing, rows[0].Id);
    }

    /// <summary>
    /// A key-configuration failure stores nothing at all rather than discarding a usable draft and
    /// leaving the business with none.
    /// </summary>
    [Fact]
    public async Task A_protector_that_cannot_encrypt_leaves_the_previous_draft_in_place()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var existing = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(existing), CancellationToken.None);

        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessA, new UnconfiguredNayaxTokenProtector())
                .ReplaceAsync(Draft(Guid.NewGuid()), CancellationToken.None));

        var rows = await fixture.AllRowsAsync();
        Assert.Single(rows);
        Assert.Equal(existing, rows[0].Id);
    }

    /// <summary>
    /// A claim hands back the decrypted credential and the stored results, and removes the row in
    /// the same breath (issue #537): the draft is single-use, and the delete is what enforces it.
    /// </summary>
    [Fact]
    public async Task A_claim_returns_the_decrypted_credential_and_deletes_the_draft()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var draftId = Guid.NewGuid();
        await store.ReplaceAsync(Draft(draftId), CancellationToken.None);

        var claimed = await store.TryClaimAsync(draftId, Owner, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(OperatorId, claimed!.Credential.OperatorId);
        Assert.Equal(Token, claimed.Credential.AccessToken);
        Assert.Equal(0, claimed.Credential.CredentialRevision);
        Assert.Equal("{\"checks\":\"ok\"}", claimed.ResultsJson);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// The second claim of one draft finds nothing, which is what makes two saves of one test result
    /// end as one save and one refusal however they interleave.
    /// </summary>
    [Fact]
    public async Task A_draft_can_be_claimed_only_once()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var draftId = Guid.NewGuid();
        await store.ReplaceAsync(Draft(draftId), CancellationToken.None);

        Assert.NotNull(await store.TryClaimAsync(draftId, Owner, CancellationToken.None));
        Assert.Null(await store.TryClaimAsync(draftId, Owner, CancellationToken.None));
    }

    /// <summary>
    /// Every way a claim can fail reads the same, and none of them removes a row: an id no draft
    /// has, another member's draft, and an expired one. Reporting which would tell a caller that
    /// somebody else's draft exists.
    /// </summary>
    [Fact]
    public async Task An_unknown_draft_another_actor_s_draft_and_an_expired_one_all_claim_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        Assert.Null(await fixture.StoreFor(fixture.BusinessA)
            .TryClaimAsync(Guid.NewGuid(), Owner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessA)
            .TryClaimAsync(draftId, OtherOwner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessA, readAt: ExpiresAt)
            .TryClaimAsync(draftId, Owner, CancellationToken.None));

        Assert.Single(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// One business cannot claim another's draft even knowing its exact id and presenting the same
    /// actor identity - and the attempt deletes nothing, which is the part the central query filter
    /// has to carry into the <c>DELETE</c> itself.
    /// </summary>
    [Fact]
    public async Task One_business_cannot_claim_another_s_draft()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(a), CancellationToken.None);
        await fixture.StoreFor(fixture.BusinessB).ReplaceAsync(
            Draft(b, OtherOperatorId, OtherToken), CancellationToken.None);

        Assert.Null(await fixture.StoreFor(fixture.BusinessA).TryClaimAsync(b, Owner, CancellationToken.None));
        Assert.Null(await fixture.StoreFor(fixture.BusinessB).TryClaimAsync(a, Owner, CancellationToken.None));

        var rows = await fixture.AllRowsAsync();
        Assert.Equal(2, rows.Count);
    }

    /// <summary>
    /// A claim without a single resolved business is refused rather than running unscoped: the
    /// conditional <c>DELETE</c> does not pass through <c>BusinessOwnershipEnforcer</c>, so an
    /// unscoped statement would reach - and consume - every business's draft.
    /// </summary>
    [Fact]
    public async Task A_claim_without_a_resolved_business_is_refused_and_deletes_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => fixture.DeniedStore().TryClaimAsync(draftId, Owner, CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => fixture.UnscopedStore().TryClaimAsync(draftId, Owner, CancellationToken.None));

        Assert.Single(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// A ciphertext whose key is not configured fails closed instead of reporting an absent draft -
    /// an undecryptable credential must never look like one that was never tested - and the draft
    /// is left in place, because the decryption happens before the row is removed.
    /// </summary>
    [Fact]
    public async Task A_claim_whose_token_cannot_be_decrypted_fails_closed_and_consumes_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessA, new UnconfiguredNayaxTokenProtector())
                .TryClaimAsync(draftId, Owner, CancellationToken.None));

        Assert.Single(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// The claim is decided by its <c>DELETE</c> removing exactly one row, not by the read that
    /// preceded it. Here the row disappears between the two - which is what a second save doing the
    /// same thing looks like from inside this one - and the claim reports no draft rather than
    /// handing out a credential whose single-use row somebody else already took.
    ///
    /// <para>The disappearance is staged through the protector, which the adapter calls between the
    /// read and the delete. That is a seam rather than a behaviour, and it is used deliberately:
    /// the alternative is leaving the one statement that makes two simultaneous saves safe with no
    /// test of its own.</para>
    /// </summary>
    [Fact]
    public async Task A_draft_that_disappears_between_the_read_and_the_delete_claims_nothing()
    {
        await using var fixture = await DraftFixture.CreateAsync();
        var draftId = Guid.NewGuid();
        await fixture.StoreFor(fixture.BusinessA).ReplaceAsync(Draft(draftId), CancellationToken.None);

        var racing = new RowRemovingProtector(Protector(ActiveKeyId), fixture.DeleteEveryDraft);
        var claimed = await fixture.StoreFor(fixture.BusinessA, racing)
            .TryClaimAsync(draftId, Owner, CancellationToken.None);

        Assert.Null(claimed);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    private static NewNayaxConnectionTestDraft Draft(
        Guid id,
        string operatorId = OperatorId,
        string token = Token,
        DateTime? createdAt = null,
        DateTime? expiresAt = null) =>
        new(
            id,
            new NayaxConnectionCredential(operatorId, token, credentialRevision: 0),
            "{\"checks\":\"ok\"}",
            Owner,
            createdAt ?? CreatedAt,
            expiresAt ?? ExpiresAt);

    private static ActorIdentity Actor(string objectId)
    {
        ActorIdentity.TryCreate("99999999-9999-9999-9999-999999999999", objectId, out var actor);
        return actor!;
    }

    private static AesGcmNayaxTokenProtector Protector(string activeKeyId)
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = activeKeyId };
        options.Keys[activeKeyId] = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

        return new AesGcmNayaxTokenProtector(options);
    }

    /// <summary>
    /// A protector that removes the draft row while it decrypts, so the adapter's "exactly one row
    /// was claimed" check can be observed. It decrypts normally otherwise.
    /// </summary>
    private sealed class RowRemovingProtector : INayaxTokenProtector
    {
        private readonly INayaxTokenProtector _inner;
        private readonly Action _whileDecrypting;

        public RowRemovingProtector(INayaxTokenProtector inner, Action whileDecrypting)
        {
            _inner = inner;
            _whileDecrypting = whileDecrypting;
        }

        public ProtectedNayaxToken Protect(string accessToken) => _inner.Protect(accessToken);

        public string Unprotect(ProtectedNayaxToken protectedToken)
        {
            _whileDecrypting();
            return _inner.Unprotect(protectedToken);
        }
    }

    /// <summary>
    /// One in-memory SQLite database with two synthetic businesses and the real migration-free schema
    /// the model describes, so the unique ownership index is really in force.
    /// </summary>
    private sealed class DraftFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private DraftFixture(
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

        public static async Task<DraftFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .Options;

            await using var setup = TestAppDbContext.Unrestricted(options);
            await setup.Database.EnsureCreatedAsync();
            var a = new Business { Name = "Vending A", CreatedAtUtc = CreatedAt };
            var b = new Business { Name = "Vending B", CreatedAtUtc = CreatedAt };
            setup.Businesses.AddRange(a, b);
            await setup.SaveChangesAsync();

            return new DraftFixture(connection, options, a.Id, b.Id);
        }

        public EfNayaxConnectionTestDraftStore StoreFor(
            int businessId,
            INayaxTokenProtector? protector = null,
            DateTime? readAt = null)
        {
            var context = TestAppDbContext.For(_options, businessId);
            _contexts.Add(context);

            return new EfNayaxConnectionTestDraftStore(
                context,
                protector ?? Protector(ActiveKeyId),
                new FakeClock(readAt ?? CreatedAt));
        }

        public EfNayaxConnectionTestDraftStore DeniedStore() => StoreForContext(TestAppDbContext.Denied(_options));

        public EfNayaxConnectionTestDraftStore UnscopedStore() =>
            StoreForContext(TestAppDbContext.Unrestricted(_options));

        public async Task<NayaxConnectionTestDraft> ReadRowAsync(int businessId)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.NayaxConnectionTestDrafts
                .AsNoTracking()
                .SingleAsync(draft => draft.BusinessId == businessId);
        }

        public async Task<List<NayaxConnectionTestDraft>> AllRowsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.NayaxConnectionTestDrafts.AsNoTracking().ToListAsync();
        }

        /// <summary>
        /// Removes every draft row directly, for the one test that needs the row to disappear in
        /// the middle of a claim. Synchronous, because it is called from inside the adapter.
        /// </summary>
        public void DeleteEveryDraft()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM NayaxConnectionTestDrafts;";
            command.ExecuteNonQuery();
        }

        /// <summary>Every stored value of the draft table, as text, for a plaintext sweep.</summary>
        public async Task<string> DumpDraftsAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT Id || '|' || BusinessId || '|' || OperatorId || '|' || AccessTokenCiphertext || '|' || "
                    + "EncryptionKeyId || '|' || ResultsJson || '|' || CreatedByDirectoryTenantId || '|' || "
                    + "CreatedByObjectId || '|' || CreatedAt || '|' || ExpiresAt FROM NayaxConnectionTestDrafts;";
            await using var reader = await command.ExecuteReaderAsync();

            var dump = new List<string>();
            while (await reader.ReadAsync())
            {
                dump.Add(reader.GetString(0));
            }

            return string.Join("\n", dump);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private EfNayaxConnectionTestDraftStore StoreForContext(AppDbContext context)
        {
            _contexts.Add(context);

            return new EfNayaxConnectionTestDraftStore(
                context,
                Protector(ActiveKeyId),
                new FakeClock(CreatedAt));
        }
    }
}
