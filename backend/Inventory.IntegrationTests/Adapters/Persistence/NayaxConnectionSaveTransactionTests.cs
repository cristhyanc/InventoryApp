using System.Text.Json;
using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// The atomicity of a save-from-draft (issue #537), against relational SQLite with the real stores,
/// the real AES-GCM protector and a real transaction.
///
/// <para>This is the file that proves the acceptance criterion no unit test can: <strong>two
/// concurrent saves of one draft end as exactly one save and one refusal, with the credential
/// revision increased exactly once.</strong> Each save runs on its own
/// <see cref="AppDbContext"/> over its own connection to a database <em>file</em>, so the two
/// transactions really are concurrent - an in-memory SQLite database shared through one connection
/// could not have two of them at all, and that is precisely the thing under test.</para>
///
/// <para>It also pins the two halves of "one change": a refused save leaves the draft in place
/// rather than consuming a test result that was never accepted, and a credential write that fails
/// leaves no claimed draft behind either.</para>
/// </summary>
public class NayaxConnectionSaveTransactionTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "nayax-lynx-draft-token-value";
    private const string OtherToken = "a-second-fake-token";
    private const string ActiveKeyId = "2026-10";
    private const string DirectoryTenantId = "99999999-9999-9999-9999-999999999999";

    private static readonly DateTime Now = new(2026, 10, 12, 1, 15, 0, DateTimeKind.Utc);
    private static readonly ActorIdentity Owner = Actor("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// Two saves of one draft, started together: one stores the credential, the other is told the
    /// test result was already used, and the connection is at revision 1 - not 2. The draft is gone
    /// exactly once, so a tested token cannot be accepted twice.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_saves_of_one_draft_store_it_once_and_refuse_the_other()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        var draftId = await fixture.StoreDraftAsync(AllPassed());

        var outcomes = await Task.WhenAll(
            Task.Run(() => fixture.SaveAsync(draftId)),
            Task.Run(() => fixture.SaveAsync(draftId)));

        var saved = outcomes.Where(outcome => outcome.Summary is not null).ToArray();
        var refused = outcomes.Where(outcome => outcome.Failure is not null).ToArray();

        Assert.Single(saved);
        Assert.Equal(NayaxConnectionStatus.Ready, saved[0].Summary!.Status);
        var failure = Assert.IsType<DomainConflictException>(Assert.Single(refused).Failure);
        Assert.Equal(SaveNayaxConnection.DraftUnavailableMessage, failure.Message);

        var connection = Assert.Single(await fixture.ConnectionsAsync());
        Assert.Equal(1, connection.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.Ready, connection.Status);
        Assert.Equal(OperatorId, connection.OperatorId);
        Assert.Empty(await fixture.DraftsAsync());
    }

    /// <summary>
    /// The credential that lands in the connection is the draft's own, decrypted from the draft and
    /// re-encrypted into the connection - never stored in plaintext, and never the previous one.
    /// </summary>
    [Fact]
    public async Task A_save_stores_the_draft_s_own_credential_as_ciphertext()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        var draftId = await fixture.StoreDraftAsync(AllPassed());

        var outcome = await fixture.SaveAsync(draftId);

        Assert.Null(outcome.Failure);
        Assert.NotNull(outcome.Summary);
        var connection = Assert.Single(await fixture.ConnectionsAsync());
        Assert.NotEqual(Token, connection.AccessTokenCiphertext);
        Assert.DoesNotContain(Token, connection.AccessTokenCiphertext, StringComparison.Ordinal);
        Assert.Equal(ActiveKeyId, connection.EncryptionKeyId);
        Assert.Equal(Token, Decrypt(connection));
        Assert.Equal(Now, connection.LastTestedAtUtc);
    }

    /// <summary>
    /// A run whose sign-in did not pass is refused - and the claim is rolled back with it, so the
    /// Owner still has the draft and nothing was stored.
    /// </summary>
    [Fact]
    public async Task A_refused_save_leaves_the_draft_in_place_and_stores_nothing()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        var draftId = await fixture.StoreDraftAsync(new NayaxConnectionCheckReport(
            [
                NayaxConnectionCheckLine.Failed(
                    NayaxConnectionCheck.SignIn,
                    NayaxConnectionCheckFailure.InvalidOrExpiredToken),
            ],
            MachineCount: null));

        var outcome = await fixture.SaveAsync(draftId);

        Assert.IsType<DomainValidationException>(outcome.Failure);
        Assert.Empty(await fixture.ConnectionsAsync());
        Assert.Equal(draftId, Assert.Single(await fixture.DraftsAsync()).Id);
    }

    /// <summary>
    /// The other half of "one change": the claim commits only because the credential write did. A
    /// protector that cannot encrypt fails the connection write, and the draft the claim removed
    /// comes back - rather than an Owner losing a tested token to a key-configuration problem.
    /// </summary>
    [Fact]
    public async Task A_failed_credential_write_puts_the_claimed_draft_back()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        var draftId = await fixture.StoreDraftAsync(AllPassed());

        var outcome = await fixture.SaveAsync(
            draftId, connectionProtector: new UnconfiguredNayaxTokenProtector());

        Assert.IsType<NayaxTokenProtectionException>(outcome.Failure);
        Assert.Empty(await fixture.ConnectionsAsync());
        Assert.Equal(draftId, Assert.Single(await fixture.DraftsAsync()).Id);
    }

    /// <summary>
    /// A business with no machines in Nayax yet is stored <c>Ready</c> with the machine checks
    /// recorded as pending, in the column the status endpoint reads.
    /// </summary>
    [Fact]
    public async Task A_save_with_no_machines_records_the_pending_machine_checks()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        var draftId = await fixture.StoreDraftAsync(new NayaxConnectionCheckReport(
            [
                .. NayaxConnectionCheckPolicy.All.Select(check =>
                    NayaxConnectionCheckPolicy.RequiresMachine(check)
                        ? NayaxConnectionCheckLine.NotChecked(check)
                        : NayaxConnectionCheckLine.Passed(check)),
            ],
            MachineCount: 0));

        var outcome = await fixture.SaveAsync(draftId);

        Assert.Equal(
            new NayaxConnectionStatusSummary(NayaxConnectionStatus.Ready, true),
            outcome.Summary);
        var connection = Assert.Single(await fixture.ConnectionsAsync());
        Assert.True(connection.MachineChecksPending);
        Assert.Equal(NayaxConnectionStatus.Ready, connection.Status);
    }

    /// <summary>
    /// Saving again replaces the credential and increments the revision - the ordinary path an Owner
    /// takes when a token is rotated.
    /// </summary>
    [Fact]
    public async Task A_second_save_from_a_second_draft_supersedes_the_first()
    {
        await using var fixture = await SaveFixture.CreateAsync();
        Assert.Null((await fixture.SaveAsync(await fixture.StoreDraftAsync(AllPassed()))).Failure);

        var outcome = await fixture.SaveAsync(await fixture.StoreDraftAsync(AllPassed(), OtherToken));

        Assert.Null(outcome.Failure);
        var connection = Assert.Single(await fixture.ConnectionsAsync());
        Assert.Equal(2, connection.CredentialRevision);
        Assert.Equal(OtherToken, Decrypt(connection));
    }

    private static NayaxConnectionCheckReport AllPassed() =>
        new([.. NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed)], 1);

    private static string Decrypt(BusinessNayaxConnection connection) =>
        Protector().Unprotect(
            new ProtectedNayaxToken(connection.EncryptionKeyId, connection.AccessTokenCiphertext));

    private static ActorIdentity Actor(string objectId)
    {
        Assert.True(ActorIdentity.TryCreate(DirectoryTenantId, objectId, out var actor));
        return actor!;
    }

    private static AesGcmNayaxTokenProtector Protector()
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[ActiveKeyId] = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

        return new AesGcmNayaxTokenProtector(options);
    }

    /// <summary>
    /// One SQLite database <em>file</em> with one business, so each save can hold its own connection
    /// and its own transaction. Pooling is off for the same reason.
    /// </summary>
    private sealed class SaveFixture : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly int _businessId;

        private SaveFixture(string directory, DbContextOptions<AppDbContext> options, int businessId)
        {
            _directory = directory;
            _options = options;
            _businessId = businessId;
        }

        public static async Task<SaveFixture> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "nayax-save-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);

            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "save.db")};Pooling=False")
                .Options;

            await using var setup = TestAppDbContext.Unrestricted(options);
            await setup.Database.EnsureCreatedAsync();
            var business = new Business { Name = "Vending Co", Status = BusinessStatus.Active, CreatedAtUtc = Now };
            setup.Businesses.Add(business);
            await setup.SaveChangesAsync();

            return new SaveFixture(directory, options, business.Id);
        }

        /// <summary>Stores a draft the way a completed test does, through the real adapter.</summary>
        public async Task<Guid> StoreDraftAsync(NayaxConnectionCheckReport report, string token = Token)
        {
            await using var context = TestAppDbContext.For(_options, _businessId);
            var drafts = new EfNayaxConnectionTestDraftStore(context, Protector(), new FakeClock(Now));
            var draftId = Guid.NewGuid();

            await drafts.ReplaceAsync(
                new NewNayaxConnectionTestDraft(
                    draftId,
                    new NayaxConnectionCredential(OperatorId, token, credentialRevision: 0),
                    JsonSerializer.Serialize(report),
                    Owner,
                    Now,
                    Now + TestNayaxConnection.DraftLifetime),
                CancellationToken.None);

            return draftId;
        }

        /// <summary>
        /// One save over its own context, connection and transaction, reporting what happened rather
        /// than throwing - so two of them can be awaited together and compared.
        /// </summary>
        public async Task<SaveOutcome> SaveAsync(
            Guid draftId,
            INayaxTokenProtector? connectionProtector = null)
        {
            await using var context = TestAppDbContext.For(_options, _businessId);
            var save = new SaveNayaxConnection(
                new EfNayaxConnectionTestDraftStore(context, Protector(), new FakeClock(Now)),
                new EfNayaxConnectionStore(
                    context,
                    connectionProtector ?? Protector(),
                    new FakeClock(Now),
                    NullLogger<EfNayaxConnectionStore>.Instance),
                new EfNayaxConnectionSaveTransactions(context),
                new FakeCurrentBusinessProvider(_businessId),
                FakeAuthenticatedActorAccessor.Identified(Owner),
                new FakeClock(Now));

            try
            {
                return new SaveOutcome(await save.Handle(draftId, CancellationToken.None), null);
            }
            catch (Exception exception)
            {
                return new SaveOutcome(null, exception);
            }
        }

        public async Task<List<BusinessNayaxConnection>> ConnectionsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections.AsNoTracking().ToListAsync();
        }

        public async Task<List<NayaxConnectionTestDraft>> DraftsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.NayaxConnectionTestDrafts.AsNoTracking().ToListAsync();
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed record SaveOutcome(NayaxConnectionStatusSummary? Summary, Exception? Failure);
}
