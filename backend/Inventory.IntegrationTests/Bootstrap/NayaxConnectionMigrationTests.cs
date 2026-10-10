using System.Security.Cryptography;
using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using InventoryApi.Bootstrap;
using InventoryApi.Tests.Application.Time;
using InventoryApi.Tests.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace InventoryApi.Tests.Bootstrap;

/// <summary>
/// The human-run command that moves the globally configured Nayax operator id and access token
/// into the existing business's own encrypted record (issue #519, a slice of #500).
///
/// Relational SQLite against the migrated schema rather than a model double, for the same reason
/// the #518 store tests are: the credential lands in a real table behind a real unique index, the
/// refusals are decided from real tenancy rows, and "no plaintext token anywhere" is a claim about
/// what a database file and a console transcript actually contain.
///
/// Two properties get asserted far more directly than a reviewer could read off the code. First,
/// that a dry run writes **nothing** - no row, no revision, no status - so an operator can run it
/// against production without a decision. Second, that the token appears in no result member, no
/// message, no log entry and no printed line; the one place it is allowed to exist is the
/// ciphertext column, and even there it must not appear in plaintext.
/// </summary>
public sealed class NayaxConnectionMigrationTests
{
    private const string OperatorId = "2002736764";
    private const string Token = "nayax-lynx-access-token-value-519";
    private const string OtherOperatorId = "9009009009";
    private const string OtherToken = "nayax-lynx-other-token-value-519";
    private const string ActiveKeyId = "2026-10";
    private const string Tid = "11111111-1111-1111-1111-111111111111";
    private const string Oid = "22222222-2222-2222-2222-222222222222";

    private static readonly DateTime MigratedAt = new(2026, 10, 10, 9, 30, 0, DateTimeKind.Utc);

    #region Arguments

    [Fact]
    public void The_command_is_recognised_by_name()
    {
        Assert.True(NayaxConnectionMigrationCommand.Matches(["migrate-nayax-connection", "--apply"]));
        Assert.False(NayaxConnectionMigrationCommand.Matches(["migrate-documents"]));
        Assert.False(NayaxConnectionMigrationCommand.Matches([]));
    }

    [Fact]
    public void An_invocation_with_no_flag_is_a_dry_run()
    {
        Assert.True(NayaxConnectionMigrationArguments.TryParse(
            ["migrate-nayax-connection"], out var apply, out var error));

        Assert.False(apply);
        Assert.Empty(error);
    }

    [Fact]
    public void The_apply_flag_alone_applies()
    {
        Assert.True(NayaxConnectionMigrationArguments.TryParse(
            ["migrate-nayax-connection", "--apply"], out var apply, out _));

        Assert.True(apply);
    }

    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--dry-run", "--apply")]
    public void Both_flags_together_are_refused_and_do_not_apply(string first, string second)
    {
        Assert.False(NayaxConnectionMigrationArguments.TryParse(
            ["migrate-nayax-connection", first, second], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("mutually exclusive", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// There is no flag that names a business or supplies a credential, and an operator who thinks
    /// there is must be told so rather than have the command run with its own idea of the target.
    /// </summary>
    [Theory]
    [InlineData("--force")]
    [InlineData("--business-id")]
    [InlineData("--token")]
    public void An_unrecognised_argument_is_refused(string argument)
    {
        Assert.False(NayaxConnectionMigrationArguments.TryParse(
            ["migrate-nayax-connection", argument], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("Unrecognised argument", error, StringComparison.Ordinal);
    }

    #endregion

    #region Dry run

    [Fact]
    public async Task A_dry_run_reports_the_credential_it_would_store_and_writes_nothing()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(dryRun: true);

        Assert.True(result.Succeeded, result.Message);
        Assert.True(result.DryRun);
        Assert.Equal(NayaxConnectionMigrationChange.CredentialsStored, result.Change);
        Assert.Equal(fixture.BusinessId, result.BusinessId);
        Assert.Equal(OperatorId, result.ConfiguredOperatorId);
        Assert.Null(result.StoredOperatorId);
        Assert.Null(result.StoredTokenMatchesConfigured);
        Assert.Null(result.StatusBefore);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusAfter);
        Assert.Null(result.CredentialRevisionBefore);
        Assert.Equal(1, result.CredentialRevisionAfter);

        Assert.Empty(await fixture.AllRowsAsync());
    }

    [Fact]
    public async Task A_dry_run_after_an_apply_reports_that_nothing_would_change()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        Assert.True((await fixture.RunAsync(dryRun: false)).Succeeded);
        var applied = await fixture.SingleRowAsync();

        var result = await fixture.RunAsync(dryRun: true);

        Assert.Equal(NayaxConnectionMigrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.True(result.StoredTokenMatchesConfigured);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusBefore);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusAfter);

        var after = await fixture.SingleRowAsync();
        Assert.Equal(applied.CredentialRevision, after.CredentialRevision);
        Assert.Equal(applied.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(applied.AccessTokenCiphertext, after.AccessTokenCiphertext);
    }

    #endregion

    #region Apply

    [Fact]
    public async Task An_apply_stores_the_configured_credential_encrypted_and_marks_it_ready()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.Succeeded, result.Outcome);
        Assert.False(result.DryRun);
        Assert.Equal(NayaxConnectionMigrationChange.CredentialsStored, result.Change);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusAfter);
        Assert.Equal(1, result.CredentialRevisionAfter);

        var row = await fixture.SingleRowAsync();
        Assert.Equal(fixture.BusinessId, row.BusinessId);
        Assert.Equal(OperatorId, row.OperatorId);
        Assert.Equal(NayaxConnectionStatus.Ready, row.Status);
        Assert.Equal(1, row.CredentialRevision);
        Assert.Equal(ActiveKeyId, row.EncryptionKeyId);
        Assert.Equal(MigratedAt, row.LastTestedAtUtc);
        Assert.NotEqual(Token, row.AccessTokenCiphertext);

        // The stored ciphertext is the configured token, decryptable through the same store the
        // Nayax client reads it with (issue #520).
        var credential = await fixture.StoreFor(fixture.BusinessId).FindCredentialAsync(CancellationToken.None);
        Assert.NotNull(credential);
        Assert.Equal(OperatorId, credential!.OperatorId);
        Assert.Equal(Token, credential.AccessToken);
    }

    [Fact]
    public async Task An_apply_leaves_no_plaintext_token_in_any_column_of_the_connection_table()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        await fixture.RunAsync(dryRun: false);

        Assert.DoesNotContain(Token, await fixture.DumpConnectionsAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_re_run_of_an_applied_migration_changes_nothing()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        Assert.True((await fixture.RunAsync(dryRun: false)).Succeeded);
        var applied = await fixture.SingleRowAsync();

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.Equal(1, result.CredentialRevisionBefore);
        Assert.Equal(1, result.CredentialRevisionAfter);

        var after = await fixture.SingleRowAsync();
        Assert.Equal(1, after.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.Ready, after.Status);
        Assert.Equal(applied.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.Equal(applied.LastTestedAtUtc, after.LastTestedAtUtc);

        // Not merely "a token that decrypts to the same value": the row was not rewritten at all,
        // so a re-run cannot even produce fresh ciphertext for the same secret.
        Assert.Equal(applied.AccessTokenCiphertext, after.AccessTokenCiphertext);
    }

    /// <summary>
    /// The same credential stored but never marked ready - the state #518's store leaves after any
    /// save - converges on <see cref="NayaxConnectionStatus.Ready"/> without re-saving the secret,
    /// so the revision does not move and the second run is still a no-op.
    /// </summary>
    [Fact]
    public async Task A_matching_credential_that_is_not_yet_ready_is_marked_ready_without_a_new_revision()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessId).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var stored = await fixture.SingleRowAsync();
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, stored.Status);

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.Succeeded, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.StatusMarkedReady, result.Change);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, result.StatusBefore);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusAfter);
        Assert.Equal(1, result.CredentialRevisionAfter);

        var after = await fixture.SingleRowAsync();
        Assert.Equal(NayaxConnectionStatus.Ready, after.Status);
        Assert.Equal(1, after.CredentialRevision);
        Assert.Equal(stored.AccessTokenCiphertext, after.AccessTokenCiphertext);

        Assert.Equal(
            NayaxConnectionMigrationChange.None,
            (await fixture.RunAsync(dryRun: false)).Change);
    }

    /// <summary>
    /// A dry run of that same state must report the status write it would make, and still write
    /// nothing.
    /// </summary>
    [Fact]
    public async Task A_dry_run_reports_a_pending_status_it_would_mark_ready_and_writes_nothing()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessId).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var result = await fixture.RunAsync(dryRun: true);

        Assert.Equal(NayaxConnectionMigrationChange.StatusMarkedReady, result.Change);
        Assert.Equal(NayaxConnectionStatus.Ready, result.StatusAfter);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, (await fixture.SingleRowAsync()).Status);
    }

    #endregion

    #region An apply is one transaction

    /// <summary>
    /// The partial state the apply's transaction exists to rule out. The credential is saved, and
    /// before the <c>Ready</c> status is written something else replaces it, so the conditional
    /// status write matches no row. A command that reported that refusal over a database it had
    /// already changed would be telling the operator something false, so the whole apply is rolled
    /// back instead: there is no row at all afterwards, and the report says the database is
    /// unchanged.
    /// </summary>
    [Fact]
    public async Task A_credential_replaced_between_the_save_and_the_ready_write_rolls_the_whole_apply_back()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(
            dryRun: false,
            interleave: store => new ReplacingStore(store, OtherOperatorId, OtherToken));

        Assert.Equal(NayaxConnectionMigrationOutcome.StatusNotApplied, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.Equal(NayaxConnectionMigrationDatabaseState.Unchanged, result.DatabaseState);
        Assert.Contains("nothing was written", result.Message, StringComparison.Ordinal);

        // Not "the credential is stored, only its status is missing": nothing was stored at all.
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// The same change between the read and the mutation on the status-only path, where the command
    /// deliberately does not re-save the credential: the row that was committed before the apply
    /// comes out of the refused run byte-for-byte as it went in.
    /// </summary>
    [Fact]
    public async Task A_credential_replaced_during_a_status_only_apply_leaves_the_committed_row_untouched()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessId).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var stored = await fixture.SingleRowAsync();

        var result = await fixture.RunAsync(
            dryRun: false,
            interleave: store => new ReplacingStore(store, OtherOperatorId, OtherToken));

        Assert.Equal(NayaxConnectionMigrationOutcome.StatusNotApplied, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationDatabaseState.Unchanged, result.DatabaseState);

        var after = await fixture.SingleRowAsync();
        Assert.Equal(stored.OperatorId, after.OperatorId);
        Assert.Equal(stored.AccessTokenCiphertext, after.AccessTokenCiphertext);
        Assert.Equal(stored.CredentialRevision, after.CredentialRevision);
        Assert.Equal(stored.Status, after.Status);
        Assert.Equal(stored.UpdatedAtUtc, after.UpdatedAtUtc);
    }

    /// <summary>
    /// A status write that fails outright rather than being discarded. The credential save must go
    /// back with it, and the run must report the failure as the no-write it is.
    /// </summary>
    [Fact]
    public async Task A_failed_status_write_rolls_the_credential_save_back_and_reports_it()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(dryRun: false, interleave: store => new FailingStatusWriteStore(store));

        Assert.Equal(NayaxConnectionMigrationOutcome.RolledBack, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.Equal(NayaxConnectionMigrationDatabaseState.Unchanged, result.DatabaseState);
        Assert.Contains("nothing was written", result.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// Recovering from a failed apply is simply running the command again: because the failure left
    /// nothing behind, the dry run still reports <c>CredentialsStored</c> and the retry is a first
    /// apply rather than the repair of a half-finished one.
    /// </summary>
    [Fact]
    public async Task An_apply_after_a_rolled_back_one_stores_the_credential_at_revision_1()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        Assert.Equal(
            NayaxConnectionMigrationOutcome.RolledBack,
            (await fixture.RunAsync(
                dryRun: false,
                interleave: store => new FailingStatusWriteStore(store))).Outcome);

        var dryRun = await fixture.RunAsync(dryRun: true);
        Assert.Equal(NayaxConnectionMigrationChange.CredentialsStored, dryRun.Change);
        Assert.Null(dryRun.StatusBefore);

        var result = await fixture.RunAsync(dryRun: false);

        Assert.True(result.Succeeded, result.Message);
        Assert.Equal(NayaxConnectionMigrationChange.CredentialsStored, result.Change);
        Assert.Equal(NayaxConnectionMigrationDatabaseState.Changed, result.DatabaseState);

        var row = await fixture.SingleRowAsync();
        Assert.Equal(1, row.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.Ready, row.Status);
    }

    /// <summary>
    /// Every ending states what happened to the database, in the result and in the printed report.
    /// No refusal may read as a partial write, and the committed apply is the only one that reports
    /// a change.
    /// </summary>
    [Fact]
    public async Task Every_outcome_states_what_happened_to_the_database()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var unchanged = new List<NayaxConnectionMigrationResult>
        {
            await fixture.RunAsync(dryRun: true),
            await fixture.RunAsync(dryRun: false, accessToken: null),
            await fixture.RunAsync(dryRun: false, protector: new UnconfiguredNayaxTokenProtector()),
            await fixture.RunAsync(dryRun: false, interleave: store => new FailingStatusWriteStore(store)),
            await fixture.RunAsync(
                dryRun: false,
                interleave: store => new ReplacingStore(store, OtherOperatorId, OtherToken)),
            await MigrationFixture.RunWithoutAuditAsync(),
        };

        foreach (var result in unchanged)
        {
            Assert.Equal(NayaxConnectionMigrationDatabaseState.Unchanged, result.DatabaseState);

            using var printed = new StringWriter();
            NayaxConnectionMigrationCommand.Write(result, printed);
            Assert.Contains("unchanged - nothing was written", printed.ToString(), StringComparison.Ordinal);
        }

        Assert.Empty(await fixture.AllRowsAsync());

        var applied = await fixture.RunAsync(dryRun: false);
        using var appliedReport = new StringWriter();
        NayaxConnectionMigrationCommand.Write(applied, appliedReport);

        Assert.Equal(NayaxConnectionMigrationDatabaseState.Changed, applied.DatabaseState);
        Assert.Contains("changed, as reported below", appliedReport.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The one ending that cannot say what the database holds says exactly that instead of the
    /// "nothing was written" every other failure reports: a commit that fails leaves either the
    /// whole change or none of it, and only a fresh dry run can say which.
    /// </summary>
    [Fact]
    public void A_failed_commit_is_reported_as_an_unknown_database_state()
    {
        var result = new NayaxConnectionMigrationResult
        {
            Outcome = NayaxConnectionMigrationOutcome.CommitFailed,
            DryRun = false,
            BusinessId = 1,
            ConfiguredOperatorId = OperatorId,
            Message = "The apply's transaction failed to commit.",
        };

        Assert.Equal(NayaxConnectionMigrationDatabaseState.Unknown, result.DatabaseState);

        using var printed = new StringWriter();
        NayaxConnectionMigrationCommand.Write(result, printed);

        Assert.Contains("UNKNOWN", printed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was written", printed.ToString(), StringComparison.Ordinal);
    }

    #endregion

    #region Refusals

    [Theory]
    [InlineData(null, Token)]
    [InlineData("", Token)]
    [InlineData("   ", Token)]
    [InlineData(OperatorId, null)]
    [InlineData(OperatorId, "")]
    [InlineData(OperatorId, "   ")]
    public async Task It_refuses_when_the_configuration_has_no_operator_id_or_no_token(
        string? operatorId,
        string? accessToken)
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(dryRun: false, operatorId: operatorId, accessToken: accessToken);

        Assert.Equal(NayaxConnectionMigrationOutcome.ConfigurationInvalid, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    [Fact]
    public async Task It_refuses_when_more_than_one_business_exists()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.AddBusinessAsync("Second Vending Business");

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.TenantOwnershipNotBootstrapped, result.Outcome);
        Assert.Null(result.BusinessId);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// A dry run refuses identically. An apply is meant to be gated on a clean dry run, so a dry
    /// run that reported a plan the apply would refuse would make that gate worthless.
    /// </summary>
    [Fact]
    public async Task A_dry_run_refuses_a_second_business_too()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.AddBusinessAsync("Second Vending Business");

        var result = await fixture.RunAsync(dryRun: true);

        Assert.Equal(NayaxConnectionMigrationOutcome.TenantOwnershipNotBootstrapped, result.Outcome);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task It_refuses_when_the_business_has_no_usable_membership()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.RevokeEveryMembershipAsync();

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.TenantOwnershipNotBootstrapped, result.Outcome);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// "The target business isn't the bootstrapped one": nothing records that
    /// <c>bootstrap-business</c> ever adopted this business, so its provenance cannot be shown and
    /// a production credential is not written onto it.
    /// </summary>
    [Fact]
    public async Task It_refuses_when_no_backfill_audit_names_the_business()
    {
        await using var fixture = await MigrationFixture.CreateAsync(withBackfillAudit: false);

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.BusinessNotBootstrapped, result.Outcome);
        Assert.Equal(fixture.BusinessId, result.BusinessId);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    [Fact]
    public async Task It_refuses_when_an_audit_names_only_a_different_business()
    {
        await using var fixture = await MigrationFixture.CreateAsync(withBackfillAudit: false);
        await fixture.AddBackfillAuditAsync(fixture.BusinessId + 7);

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.BusinessNotBootstrapped, result.Outcome);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    [Theory]
    [InlineData(OtherOperatorId, Token)]
    [InlineData(OperatorId, OtherToken)]
    [InlineData(OtherOperatorId, OtherToken)]
    public async Task It_refuses_to_replace_a_credential_that_is_already_different(
        string storedOperatorId,
        string storedToken)
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessId)
            .SaveCredentialAsync(storedOperatorId, storedToken, CancellationToken.None);
        var stored = await fixture.SingleRowAsync();

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.DifferentCredentialsStored, result.Outcome);
        Assert.Equal(NayaxConnectionMigrationChange.None, result.Change);
        Assert.Equal(storedOperatorId, result.StoredOperatorId);
        Assert.Equal(OperatorId, result.ConfiguredOperatorId);

        var after = await fixture.SingleRowAsync();
        Assert.Equal(storedOperatorId, after.OperatorId);
        Assert.Equal(stored.AccessTokenCiphertext, after.AccessTokenCiphertext);
        Assert.Equal(stored.CredentialRevision, after.CredentialRevision);
        Assert.Equal(stored.Status, after.Status);
    }

    [Fact]
    public async Task It_refuses_when_no_encryption_key_is_configured_and_stores_nothing()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var result = await fixture.RunAsync(dryRun: false, protector: new UnconfiguredNayaxTokenProtector());

        Assert.Equal(NayaxConnectionMigrationOutcome.TokenProtectionUnavailable, result.Outcome);
        Assert.Contains(NayaxTokenProtectionOptions.SectionName, result.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.AllRowsAsync());
    }

    /// <summary>
    /// A stored ciphertext the configured keys cannot decrypt must not be read as "different
    /// credentials" and overwritten, nor as "no credentials" and replaced. It refuses, because
    /// whether that row is the credential in production use is exactly what nothing can tell.
    /// </summary>
    [Fact]
    public async Task It_refuses_when_the_stored_ciphertext_cannot_be_decrypted()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessId).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.TamperWithStoredCiphertextAsync();
        var tampered = await fixture.SingleRowAsync();

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.TokenProtectionUnavailable, result.Outcome);
        Assert.Equal(tampered.AccessTokenCiphertext, (await fixture.SingleRowAsync()).AccessTokenCiphertext);
    }

    [Fact]
    public async Task It_refuses_when_the_schema_has_pending_migrations()
    {
        await using var fixture = await MigrationFixture.CreateAsync(migrateTo: MigrationFixture.PreviousMigration);

        var result = await fixture.RunAsync(dryRun: false);

        Assert.Equal(NayaxConnectionMigrationOutcome.SchemaNotReady, result.Outcome);
    }

    #endregion

    #region The token never leaves the ciphertext column

    /// <summary>
    /// Every outcome this command can reach, swept for the token: its message, its members, the
    /// report it prints, and everything the store logged while it ran. The sweep is the test
    /// because the leak would not be in the code a reviewer reads - it would be in a convenient
    /// diagnostic message added later that interpolated the value it had in hand.
    /// </summary>
    [Fact]
    public async Task No_result_message_log_entry_or_printed_line_ever_contains_the_token()
    {
        await using var fixture = await MigrationFixture.CreateAsync();

        var results = new List<NayaxConnectionMigrationResult>
        {
            await fixture.RunAsync(dryRun: true),
            await fixture.RunAsync(dryRun: false),
            await fixture.RunAsync(dryRun: false),
            await fixture.RunAsync(dryRun: false, protector: new UnconfiguredNayaxTokenProtector()),
            await fixture.RunAsync(dryRun: false, accessToken: null),
        };

        await using var different = await MigrationFixture.CreateAsync();
        await different.StoreFor(different.BusinessId)
            .SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);
        results.Add(await different.RunAsync(dryRun: false));
        results.Add(await MigrationFixture.RunWithoutAuditAsync());

        foreach (var result in results)
        {
            using var printed = new StringWriter();
            NayaxConnectionMigrationCommand.Write(result, printed);

            Assert.DoesNotContain(Token, result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Token, result.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token, printed.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(OtherToken, printed.ToString(), StringComparison.Ordinal);
        }

        var logged = string.Join(
            "\n",
            fixture.LogEntries.Concat(different.LogEntries)
                .Select(entry => $"{entry.Message} {string.Join(" ", entry.Properties.Values)} {entry.Exception}"));

        Assert.DoesNotContain(Token, logged, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherToken, logged, StringComparison.Ordinal);
    }

    /// <summary>
    /// The successful report shows the operator id - a remote identity the operator has to be able
    /// to confirm - and says in plain words that the token is not printed.
    /// </summary>
    [Fact]
    public async Task The_printed_report_names_the_operator_id_and_states_that_the_token_is_not_printed()
    {
        await using var fixture = await MigrationFixture.CreateAsync();
        var result = await fixture.RunAsync(dryRun: true);

        using var printed = new StringWriter();
        NayaxConnectionMigrationCommand.Write(result, printed);
        var report = printed.ToString();

        Assert.Contains("DRY RUN", report, StringComparison.Ordinal);
        Assert.Contains(OperatorId, report, StringComparison.Ordinal);
        Assert.Contains("never printed, never logged", report, StringComparison.Ordinal);
    }

    #endregion

    /// <summary>
    /// Replaces the stored credential at the moment the command tries to mark the connection
    /// <c>Ready</c> - the change between the authoritative read and the mutation that the credential
    /// revision guard exists for. It writes through the store it wraps, and therefore through the
    /// apply's own transaction, which is the only way one SQLite connection can be made to change
    /// underneath a run in progress; a second connection would be blocked by the write lock rather
    /// than interleave.
    /// </summary>
    private sealed class ReplacingStore : INayaxConnectionStore
    {
        private readonly INayaxConnectionStore _inner;
        private readonly string _operatorId;
        private readonly string _accessToken;

        public ReplacingStore(INayaxConnectionStore inner, string operatorId, string accessToken)
        {
            _inner = inner;
            _operatorId = operatorId;
            _accessToken = accessToken;
        }

        public Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken) =>
            _inner.FindAsync(cancellationToken);

        public Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken) =>
            _inner.FindCredentialAsync(cancellationToken);

        public Task<NayaxConnectionSnapshot?> FindForOperationAsync(
            Func<NayaxConnectionStatus, bool> mayDecryptToken,
            CancellationToken cancellationToken) =>
            _inner.FindForOperationAsync(mayDecryptToken, cancellationToken);

        public Task<NayaxConnection> SaveCredentialAsync(
            string operatorId,
            string accessToken,
            CancellationToken cancellationToken) =>
            _inner.SaveCredentialAsync(operatorId, accessToken, cancellationToken);

        public async Task<bool> TryApplyStatusResultAsync(
            NayaxConnectionStatusResult result,
            CancellationToken cancellationToken)
        {
            await _inner.SaveCredentialAsync(_operatorId, _accessToken, cancellationToken);

            return await _inner.TryApplyStatusResultAsync(result, cancellationToken);
        }
    }

    /// <summary>
    /// A status write that fails outright, which is the other way the second half of an apply can
    /// not happen: the save has already run, so what the command does next is what decides whether
    /// a credential is left behind with no status.
    /// </summary>
    private sealed class FailingStatusWriteStore : INayaxConnectionStore
    {
        private readonly INayaxConnectionStore _inner;

        public FailingStatusWriteStore(INayaxConnectionStore inner) => _inner = inner;

        public Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken) =>
            _inner.FindAsync(cancellationToken);

        public Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken) =>
            _inner.FindCredentialAsync(cancellationToken);

        public Task<NayaxConnectionSnapshot?> FindForOperationAsync(
            Func<NayaxConnectionStatus, bool> mayDecryptToken,
            CancellationToken cancellationToken) =>
            _inner.FindForOperationAsync(mayDecryptToken, cancellationToken);

        public Task<NayaxConnection> SaveCredentialAsync(
            string operatorId,
            string accessToken,
            CancellationToken cancellationToken) =>
            _inner.SaveCredentialAsync(operatorId, accessToken, cancellationToken);

        public Task<bool> TryApplyStatusResultAsync(
            NayaxConnectionStatusResult result,
            CancellationToken cancellationToken) =>
            throw new DbUpdateException("The Nayax connection status could not be written.");
    }

    /// <summary>
    /// One in-memory SQLite database migrated to the current schema, holding the single
    /// bootstrapped business the production database holds: active, with an active membership and
    /// with the <c>BusinessBackfillAudit</c> row <c>bootstrap-business</c> leaves behind.
    ///
    /// The migrator is constructed exactly as <see cref="NayaxConnectionMigrationCommand"/>
    /// constructs it - a *denied* context for the unfiltered tenancy reads and a per-business
    /// scoped context behind the store - so the test exercises the same tenant boundary the command
    /// runs under rather than an unrestricted shortcut.
    /// </summary>
    private sealed class MigrationFixture : IAsyncDisposable
    {
        /// <summary>The migration immediately before the one that created the connection table.</summary>
        public const string PreviousMigration = "20261010025919_AddBusinessTimeZone";

        private readonly SqliteConnection _connection;
        private readonly DbContextOptions<AppDbContext> _options;
        private readonly List<AppDbContext> _contexts = [];
        private readonly CapturingLogger<EfNayaxConnectionStore> _logger = new();

        private MigrationFixture(SqliteConnection connection, DbContextOptions<AppDbContext> options, int businessId)
        {
            _connection = connection;
            _options = options;
            BusinessId = businessId;
        }

        public int BusinessId { get; }

        public IReadOnlyList<CapturingLogger<EfNayaxConnectionStore>.LogEntry> LogEntries => _logger.Entries;

        public static async Task<MigrationFixture> CreateAsync(
            bool withBackfillAudit = true,
            string? migrateTo = null)
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;

            // Migrate rather than EnsureCreated: the command refuses to run against a schema with
            // pending migrations, and that refusal is one of the behaviours under test.
            await using var setup = TestAppDbContext.Unrestricted(options);

            if (migrateTo is not null)
            {
                setup.GetService<IMigrator>().Migrate(migrateTo);
                return new MigrationFixture(connection, options, businessId: 0);
            }

            setup.GetService<IMigrator>().Migrate();

            // The bootstrapped database is produced by the real `bootstrap-business` command rather
            // than hand-seeded, because what "bootstrapped" means here is its outcome: the business,
            // its membership, its BusinessBackfillAudit rows, and no tenant-owned row left
            // unassigned. A migrated-but-unbootstrapped database is not merely empty - a migration
            // seeds a NayaxProcessingFeeRate with no owner - so hand-seeding a business would leave
            // a state the production database never passes through.
            var bootstrapOptions = new BusinessBootstrapOptions { BusinessName = "Existing Vending Business" };
            bootstrapOptions.Members.Add(
                new BusinessBootstrapMemberOptions { DirectoryTenantId = Tid, ObjectId = Oid });

            var bootstrap = await new BusinessBootstrapper(setup, bootstrapOptions, TimeProvider.System)
                .RunAsync(dryRun: false, CancellationToken.None);
            Assert.True(bootstrap.Succeeded, bootstrap.Message);

            var fixture = new MigrationFixture(connection, options, bootstrap.BusinessId!.Value);

            if (!withBackfillAudit)
            {
                await fixture.RemoveBackfillAuditsAsync();
            }

            return fixture;
        }

        /// <summary>A fixture whose business was never bootstrapped, for the sweep over every outcome.</summary>
        public static async Task<NayaxConnectionMigrationResult> RunWithoutAuditAsync()
        {
            await using var fixture = await CreateAsync(withBackfillAudit: false);
            return await fixture.RunAsync(dryRun: false);
        }

        public async Task<NayaxConnectionMigrationResult> RunAsync(
            bool dryRun,
            string? operatorId = OperatorId,
            string? accessToken = Token,
            INayaxTokenProtector? protector = null,
            Func<INayaxConnectionStore, INayaxConnectionStore>? interleave = null)
        {
            // A denied scope, exactly as the command uses: the tenancy tables it reads carry no
            // query filter, and nothing tenant-owned is reachable through this context at all.
            await using var tenancyDb = TestAppDbContext.Denied(_options);

            var migrator = new NayaxConnectionMigrator(
                tenancyDb,
                businessId => TargetFor(businessId, protector, interleave),
                operatorId,
                accessToken,
                new FakeClock(MigratedAt));

            return await migrator.RunAsync(dryRun, CancellationToken.None);
        }

        /// <summary>
        /// The scoped context and the store over it, exactly as the command pairs them, optionally
        /// with a store double wrapped around the real one so a test can make the database change
        /// in the middle of the apply.
        /// </summary>
        public NayaxConnectionMigrationTarget TargetFor(
            int businessId,
            INayaxTokenProtector? protector = null,
            Func<INayaxConnectionStore, INayaxConnectionStore>? interleave = null)
        {
            var context = TestAppDbContext.For(_options, businessId);
            _contexts.Add(context);

            INayaxConnectionStore store = new EfNayaxConnectionStore(
                context,
                protector ?? ConfiguredProtector(),
                new FakeClock(MigratedAt),
                _logger);

            return new NayaxConnectionMigrationTarget(context, interleave is null ? store : interleave(store));
        }

        public INayaxConnectionStore StoreFor(int businessId, INayaxTokenProtector? protector = null) =>
            TargetFor(businessId, protector).Store;

        public async Task AddBusinessAsync(string name)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            context.Businesses.Add(new Business
            {
                Name = name,
                TimeZoneId = "Australia/Sydney",
                IsActive = true,
                CreatedAtUtc = MigratedAt,
            });
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Erases the bootstrap's own record of which business it adopted, leaving a business whose
        /// provenance cannot be shown - the state the "not the bootstrapped one" refusal is about.
        /// </summary>
        public async Task RemoveBackfillAuditsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            await context.BusinessBackfillAudits.ExecuteDeleteAsync();
        }

        public async Task AddBackfillAuditAsync(int businessId)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            context.BusinessBackfillAudits.Add(new BusinessBackfillAudit
            {
                RunId = Guid.NewGuid(),
                BusinessId = businessId,
                TableName = "Products",
                UnassignedRowsBefore = 0,
                RowsAssigned = 0,
                UnassignedRowsAfter = 0,
                TotalRowsBefore = 0,
                TotalRowsAfter = 0,
                RecordedAtUtc = MigratedAt,
            });
            await context.SaveChangesAsync();
        }

        public async Task RevokeEveryMembershipAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            await context.BusinessMemberships.ExecuteUpdateAsync(
                setters => setters.SetProperty(membership => membership.IsActive, false));
        }

        public async Task<List<BusinessNayaxConnection>> AllRowsAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections.AsNoTracking().ToListAsync();
        }

        public async Task<BusinessNayaxConnection> SingleRowAsync()
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.BusinessNayaxConnections.AsNoTracking().SingleAsync();
        }

        /// <summary>Every stored value of the credential table, as text, for a plaintext sweep.</summary>
        public async Task<string> DumpConnectionsAsync()
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

        public async Task TamperWithStoredCiphertextAsync()
        {
            var row = await SingleRowAsync();
            var stored = Convert.FromBase64String(row.AccessTokenCiphertext);
            stored[^1] ^= 0xFF;

            await using var context = TestAppDbContext.Unrestricted(_options);
            var tracked = await context.BusinessNayaxConnections.SingleAsync();
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

        private static INayaxTokenProtector ConfiguredProtector()
        {
            var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
            options.Keys[ActiveKeyId] = Convert.ToBase64String(
                SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("issue-519-test-key")));

            return new AesGcmNayaxTokenProtector(options);
        }
    }
}
