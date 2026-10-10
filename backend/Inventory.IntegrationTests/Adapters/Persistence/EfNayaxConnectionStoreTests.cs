using System.Data.Common;
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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Relational tests for the per-business Nayax connection store (issue #518). SQLite rather than
/// InMemory on purpose: the one-row-per-business unique index, the conditional
/// <c>UPDATE ... WHERE</c> that discards a stale status result, and the ordering of two overlapping
/// saves are relational behaviour that InMemory would not prove.
///
/// The security properties under test are the ones a reviewer cannot read off the adapter: that no
/// plaintext token reaches the database or a log line, that one business can neither read, decrypt
/// nor overwrite another's credentials, and that a credential that cannot be decrypted fails closed
/// rather than being reported as absent.
/// </summary>
public class EfNayaxConnectionStoreTests
{
    private const string Token = "nayax-lynx-access-token-value";
    private const string OtherToken = "nayax-lynx-second-token-value";
    private const string OperatorId = "2002736764";
    private const string OtherOperatorId = "9009009009";
    private const string ActiveKeyId = "2026-10";
    private static readonly DateTime SavedAt = new(2026, 10, 10, 1, 2, 3, DateTimeKind.Utc);
    private static readonly DateTime TestedAt = new(2026, 10, 10, 4, 5, 6, DateTimeKind.Utc);

    [Fact]
    public async Task A_first_save_creates_the_record_at_revision_1_awaiting_a_permission_test()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.Equal(
            (OperatorId, NayaxConnectionStatus.PendingPermissions, 1, (DateTime?)null, SavedAt),
            (saved.OperatorId, saved.Status, saved.CredentialRevision, saved.LastTestedAtUtc, saved.UpdatedAtUtc));

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(fixture.BusinessA, stored.BusinessId);
        Assert.Equal(ActiveKeyId, stored.EncryptionKeyId);
        Assert.NotEqual(Token, stored.AccessTokenCiphertext);
        Assert.DoesNotContain(Token, stored.AccessTokenCiphertext, StringComparison.Ordinal);
    }

    /// <summary>
    /// No plaintext token anywhere in the database file, in any column of any table - not only in
    /// the column the adapter meant to encrypt.
    /// </summary>
    [Fact]
    public async Task No_table_holds_the_plaintext_token_after_a_save()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.DoesNotContain(Token, await fixture.DumpBusinessNayaxConnectionsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_later_save_increments_the_revision_and_clears_the_previous_test_result()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var first = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(first.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None));

        var resaved = await store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(
            (OtherOperatorId, NayaxConnectionStatus.PendingPermissions, 2, (DateTime?)null),
            (resaved.OperatorId, resaved.Status, resaved.CredentialRevision, resaved.LastTestedAtUtc));

        // Still one row: a save replaces the credentials rather than appending a second record.
        var credential = await store.FindCredentialAsync(CancellationToken.None);
        Assert.NotNull(credential);
        Assert.Equal(OtherToken, credential!.AccessToken);
        Assert.Equal(2, credential.CredentialRevision);
    }

    [Fact]
    public async Task A_business_with_no_record_reads_as_absent_rather_than_as_an_empty_connection()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        Assert.Null(await store.FindAsync(CancellationToken.None));
        Assert.Null(await store.FindCredentialAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_connection_read_reports_the_stored_state_as_utc_instants()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.NeedsAttention, TestedAt),
            CancellationToken.None);

        var connection = await fixture.StoreFor(fixture.BusinessA).FindAsync(CancellationToken.None);

        Assert.NotNull(connection);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, connection!.Status);
        Assert.Equal(TestedAt, connection.LastTestedAtUtc);
        Assert.Equal(DateTimeKind.Utc, connection.LastTestedAtUtc!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, connection.UpdatedAtUtc.Kind);
    }

    [Fact]
    public async Task A_status_result_for_the_stored_revision_is_applied()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var applied = await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        Assert.True(applied);
        var row = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.Ready, row.Status);
        Assert.Equal(TestedAt, row.LastTestedAtUtc);
        Assert.Equal(1, row.CredentialRevision);
    }

    /// <summary>
    /// The stale-result rule: credentials saved after a permission test started must keep their own
    /// status. The comparison and the write are one statement, so the result is discarded rather
    /// than describing a credential pair that no longer exists.
    /// </summary>
    [Fact]
    public async Task A_status_result_for_a_superseded_revision_is_discarded_and_changes_nothing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var tested = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        var applied = await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(tested.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        Assert.False(applied);
        var row = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, row.Status);
        Assert.Null(row.LastTestedAtUtc);
        Assert.Equal(2, row.CredentialRevision);

        var discarded = Assert.Single(fixture.Logger.Entries);
        Assert.Equal(LogLevel.Warning, discarded.Level);
        Assert.Contains("Stale Nayax status result discarded", discarded.Message, StringComparison.Ordinal);
        Assert.Equal(fixture.BusinessA.ToString(), discarded.Properties["BusinessId"]);
        Assert.Equal("1", discarded.Properties["ResultCredentialRevision"]);
        Assert.Equal("2", discarded.Properties["StoredCredentialRevision"]);

        // Identifiers and revisions only: no token, no operator id, no status in the record of a
        // discarded result.
        Assert.DoesNotContain(Token, discarded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(OperatorId, discarded.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            nameof(NayaxConnectionStatus.Ready),
            discarded.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_status_result_for_a_business_with_no_record_is_discarded()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var applied = await fixture.StoreFor(fixture.BusinessA).TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(1, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        Assert.False(applied);
        var discarded = Assert.Single(fixture.Logger.Entries);
        Assert.Null(discarded.Properties["StoredCredentialRevision"]);
    }

    /// <summary>
    /// The acceptance criterion's statement shape, pinned against the SQL the provider actually
    /// sends: both the owning business and the expected revision are in the <c>WHERE</c> clause of
    /// one <c>UPDATE</c>. The business predicate is contributed by the central
    /// <c>AppDbContext</c> query filter, not by a predicate written in the adapter, which is why
    /// asserting it here matters - a change that dropped the filter would otherwise be invisible
    /// until it let a status write cross a business.
    /// </summary>
    [Fact]
    public async Task The_status_write_is_one_update_conditional_on_the_business_and_the_revision()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        fixture.Commands.Clear();

        await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        var update = Assert.Single(fixture.Commands.Where(command =>
            command.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase)
            && command.Contains("BusinessNayaxConnections", StringComparison.Ordinal)));
        var where = update[update.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains("BusinessId", where, StringComparison.Ordinal);
        Assert.Contains("CredentialRevision", where, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, update, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_business_can_neither_read_nor_decrypt_another_businesss_credentials()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessB).SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        var storeA = fixture.StoreFor(fixture.BusinessA);

        Assert.Null(await storeA.FindAsync(CancellationToken.None));
        Assert.Null(await storeA.FindCredentialAsync(CancellationToken.None));
    }

    [Fact]
    public async Task One_businesss_save_leaves_another_businesss_credentials_untouched()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessB).SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);
        var before = await fixture.ReadRowAsync(fixture.BusinessB);

        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var after = await fixture.ReadRowAsync(fixture.BusinessB);
        Assert.Equal(
            (before.OperatorId, before.AccessTokenCiphertext, before.CredentialRevision, before.UpdatedAtUtc),
            (after.OperatorId, after.AccessTokenCiphertext, after.CredentialRevision, after.UpdatedAtUtc));

        // Two rows now, one per business, each at its own first revision.
        Assert.Equal(1, (await fixture.ReadRowAsync(fixture.BusinessA)).CredentialRevision);
        Assert.Equal(1, after.CredentialRevision);
    }

    [Fact]
    public async Task One_businesss_status_result_cannot_be_applied_to_another_businesss_matching_revision()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var savedForB = await fixture.StoreFor(fixture.BusinessB)
            .SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        var applied = await fixture.StoreFor(fixture.BusinessA).TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(savedForB.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        Assert.False(applied);
        var rowB = await fixture.ReadRowAsync(fixture.BusinessB);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, rowB.Status);
        Assert.Null(rowB.LastTestedAtUtc);
    }

    /// <summary>
    /// Two callers that both read the connection before either saves - the shape of two overlapping
    /// saves. Neither save is lost: the revision counts both, and the row that remains is wholly the
    /// one that committed last rather than a mix of the two credentials.
    /// </summary>
    [Fact]
    public async Task Two_overlapping_saves_leave_the_last_committed_credentials_and_count_both()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync("1111111111", "first-token", CancellationToken.None);

        var firstCaller = fixture.StoreFor(fixture.BusinessA);
        var secondCaller = fixture.StoreFor(fixture.BusinessA);
        var firstCallerView = await firstCaller.FindAsync(CancellationToken.None);
        var secondCallerView = await secondCaller.FindAsync(CancellationToken.None);
        Assert.Equal(1, firstCallerView!.CredentialRevision);
        Assert.Equal(1, secondCallerView!.CredentialRevision);

        await firstCaller.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var last = await secondCaller.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(3, last.CredentialRevision);
        var credential = await fixture.StoreFor(fixture.BusinessA).FindCredentialAsync(CancellationToken.None);
        Assert.Equal(OtherOperatorId, credential!.OperatorId);
        Assert.Equal(OtherToken, credential.AccessToken);
        Assert.Equal(3, credential.CredentialRevision);
    }

    [Fact]
    public async Task A_credential_whose_key_is_no_longer_configured_fails_closed_instead_of_reading_as_absent()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var afterTheKeyWasRetired = fixture.StoreFor(fixture.BusinessA, Protector("2027-01"));

        // The token-free read still works - a status page must not break because a key is missing -
        // but the credential read refuses.
        Assert.NotNull(await afterTheKeyWasRetired.FindAsync(CancellationToken.None));
        var failure = await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => afterTheKeyWasRetired.FindCredentialAsync(CancellationToken.None));
        Assert.DoesNotContain(Token, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tampered_stored_ciphertext_fails_closed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.TamperWithStoredCiphertextAsync(fixture.BusinessA);

        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessA).FindCredentialAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_save_with_no_encryption_key_configured_stores_nothing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA, new UnconfiguredNayaxTokenProtector());

        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None));

        Assert.Empty(await fixture.AllRowsAsync());
    }

    [Fact]
    public async Task A_caller_with_no_resolved_business_reads_nothing_and_writes_nothing()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var denied = fixture.DeniedStore();

        Assert.Null(await denied.FindAsync(CancellationToken.None));
        Assert.Null(await denied.FindCredentialAsync(CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => denied.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => denied.TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(1, NayaxConnectionStatus.Ready, TestedAt),
                CancellationToken.None));

        var untouched = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(
            (OperatorId, NayaxConnectionStatus.PendingPermissions, 1),
            (untouched.OperatorId, untouched.Status, untouched.CredentialRevision));
    }

    /// <summary>
    /// An unscoped context is the one place a conditional <c>UPDATE</c> could carry no business
    /// predicate at all, because <c>ExecuteUpdate</c> does not pass through
    /// <c>BusinessOwnershipEnforcer</c>. This adapter refuses to write from one, so every
    /// business's row cannot be rewritten by a single statement.
    /// </summary>
    [Fact]
    public async Task An_unscoped_context_cannot_write_or_read_through_this_adapter()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.StoreFor(fixture.BusinessB).SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);
        var unscoped = fixture.UnscopedStore();

        Assert.Null(await unscoped.FindAsync(CancellationToken.None));
        Assert.Null(await unscoped.FindCredentialAsync(CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => unscoped.SaveCredentialAsync("7777777777", "another-token", CancellationToken.None));
        await Assert.ThrowsAsync<CrossBusinessAccessException>(
            () => unscoped.TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(1, NayaxConnectionStatus.Ready, TestedAt),
                CancellationToken.None));

        var rows = await fixture.AllRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(NayaxConnectionStatus.PendingPermissions, row.Status));
    }

    [Theory]
    [InlineData("", Token)]
    [InlineData("   ", Token)]
    [InlineData(OperatorId, "")]
    [InlineData(OperatorId, "   ")]
    public async Task A_blank_operator_id_or_token_is_refused_before_anything_is_written(
        string operatorId,
        string accessToken)
    {
        await using var fixture = await StoreFixture.CreateAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(operatorId, accessToken, CancellationToken.None));

        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// A second row for one business is impossible at the schema level, not only by convention, so
    /// a future write path cannot append credentials instead of replacing them.
    /// </summary>
    [Fact]
    public async Task The_schema_refuses_a_second_connection_for_one_business()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        await using var unrestricted = fixture.UnrestrictedContext();
        unrestricted.BusinessNayaxConnections.Add(new BusinessNayaxConnection
        {
            BusinessId = fixture.BusinessA,
            OperatorId = OtherOperatorId,
            AccessTokenCiphertext = "AAAA",
            EncryptionKeyId = ActiveKeyId,
            Status = NayaxConnectionStatus.Ready,
            CredentialRevision = 1,
            UpdatedAtUtc = SavedAt,
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => unrestricted.SaveChangesAsync());
    }

    private static AesGcmNayaxTokenProtector Protector(string activeKeyId)
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = activeKeyId };
        options.Keys[activeKeyId] = Convert.ToBase64String(
            Enumerable.Repeat((byte)7, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

        return new AesGcmNayaxTokenProtector(options);
    }

    /// <summary>
    /// One in-memory SQLite database with two synthetic businesses, the shared capturing logger the
    /// discarded-result assertions read, and the executed command text the statement-shape test
    /// reads.
    /// </summary>
    private sealed class StoreFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];

        private StoreFixture(
            SqliteConnection connection,
            DbContextOptions<AppDbContext> options,
            List<string> commands,
            int businessA,
            int businessB)
        {
            _connection = connection;
            _options = options;
            Commands = commands;
            BusinessA = businessA;
            BusinessB = businessB;
        }

        public int BusinessA { get; }

        public int BusinessB { get; }

        public List<string> Commands { get; }

        public CapturingLogger<EfNayaxConnectionStore> Logger { get; } = new();

        public static async Task<StoreFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var commands = new List<string>();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(new CommandTextInterceptor(commands))
                .Options;

            await using var setup = TestAppDbContext.Unrestricted(options);
            await setup.Database.EnsureCreatedAsync();
            var a = new Business { Name = "Vending A", CreatedAtUtc = SavedAt };
            var b = new Business { Name = "Vending B", CreatedAtUtc = SavedAt };
            setup.Businesses.AddRange(a, b);
            await setup.SaveChangesAsync();
            commands.Clear();

            return new StoreFixture(connection, options, commands, a.Id, b.Id);
        }

        public EfNayaxConnectionStore StoreFor(int businessId, INayaxTokenProtector? protector = null)
        {
            var context = TestAppDbContext.For(_options, businessId);
            _contexts.Add(context);

            return new EfNayaxConnectionStore(
                context,
                protector ?? Protector(ActiveKeyId),
                new FakeClock(SavedAt),
                Logger);
        }

        public EfNayaxConnectionStore DeniedStore() => StoreForContext(TestAppDbContext.Denied(_options));

        public EfNayaxConnectionStore UnscopedStore() => StoreForContext(UnrestrictedContext());

        public AppDbContext UnrestrictedContext()
        {
            var context = TestAppDbContext.Unrestricted(_options);
            _contexts.Add(context);
            return context;
        }

        public async Task<BusinessNayaxConnection> ReadRowAsync(int businessId)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections
                .AsNoTracking()
                .SingleAsync(connection => connection.BusinessId == businessId);
        }

        public async Task<List<BusinessNayaxConnection>> AllRowsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections.AsNoTracking().ToListAsync();
        }

        /// <summary>Every stored value of the credentials table, as text, for a plaintext sweep.</summary>
        public async Task<string> DumpBusinessNayaxConnectionsAsync()
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                "SELECT BusinessId || '|' || OperatorId || '|' || AccessTokenCiphertext || '|' || "
                    + "EncryptionKeyId || '|' || Status || '|' || CredentialRevision || '|' || "
                    + "COALESCE(LastTestedAtUtc, '') || '|' || UpdatedAtUtc FROM BusinessNayaxConnections;";
            await using var reader = await command.ExecuteReaderAsync();

            var dump = new List<string>();
            while (await reader.ReadAsync())
            {
                dump.Add(reader.GetString(0));
            }

            return string.Join("\n", dump);
        }

        public async Task TamperWithStoredCiphertextAsync(int businessId)
        {
            var row = await ReadRowAsync(businessId);
            var stored = Convert.FromBase64String(row.AccessTokenCiphertext);
            stored[^1] ^= 0xFF;

            await using var context = TestAppDbContext.Unrestricted(_options);
            var tracked = await context.BusinessNayaxConnections
                .SingleAsync(connection => connection.BusinessId == businessId);
            tracked.AccessTokenCiphertext = Convert.ToBase64String(stored);
            await context.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var context in _contexts)
            {
                await context.DisposeAsync();
            }

            await _connection.DisposeAsync();
        }

        private EfNayaxConnectionStore StoreForContext(AppDbContext context)
        {
            if (!_contexts.Contains(context))
            {
                _contexts.Add(context);
            }

            return new EfNayaxConnectionStore(
                context,
                Protector(ActiveKeyId),
                new FakeClock(SavedAt),
                Logger);
        }
    }

    private sealed class CommandTextInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands;

        public CommandTextInterceptor(List<string> commands) => _commands = commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            _commands.Add(command.CommandText);
            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
