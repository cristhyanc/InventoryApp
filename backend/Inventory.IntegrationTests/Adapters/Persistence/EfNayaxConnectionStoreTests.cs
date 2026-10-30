using System.Data.Common;
using Inventory.Application.Nayax;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
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
    /// Storing a provider's credentials is what records that the business sells through it (issue
    /// #759): the sales provider is what makes the external ids on that business's products and
    /// categories Nayax ids, so it cannot be left for a separate step somebody might not take.
    /// </summary>
    [Fact]
    public async Task A_first_save_records_that_the_business_sells_through_Nayax()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        Assert.Null(await fixture.ReadSalesProviderAsync(fixture.BusinessA));

        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.Equal(ExternalProvider.Nayax, await fixture.ReadSalesProviderAsync(fixture.BusinessA));
        // Only the business whose credentials were stored: the claim is scoped to the context's own
        // resolved business, not to whichever row came first.
        Assert.Null(await fixture.ReadSalesProviderAsync(fixture.BusinessB));
    }

    /// <summary>
    /// A later save of the same provider's credentials is the ordinary case - a rotated token - and
    /// leaves the recorded provider exactly as it is rather than rewriting it.
    /// </summary>
    [Fact]
    public async Task A_later_save_leaves_the_recorded_sales_provider_alone()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var resaved = await store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(2, resaved.CredentialRevision);
        Assert.Equal(ExternalProvider.Nayax, await fixture.ReadSalesProviderAsync(fixture.BusinessA));
    }

    /// <summary>
    /// The first account a business stores credentials for is account series 1, and the series began
    /// when they were stored (issue #978). The series is what connected-machine snapshots belong to,
    /// and it is an internal counter: it carries no part of the operator id, the token or the key.
    /// </summary>
    [Fact]
    public async Task A_first_save_starts_the_first_account_series()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.Equal(1, saved.AccountSeries);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(1, stored.AccountSeries);
        Assert.Equal(SavedAt, stored.AccountSeriesChangedAtUtc);
    }

    /// <summary>
    /// Re-saving the <em>same</em> Nayax account keeps the series, so a rotated token does not split
    /// the business's connected-machine evidence in two (issue #978): a new token for the same account
    /// is the same fleet. The credential revision still moves, because that counts saves.
    /// </summary>
    [Fact]
    public async Task Re_saving_the_same_account_keeps_the_account_series()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var resaved = await store.SaveCredentialAsync(OperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(1, resaved.AccountSeries);
        Assert.Equal(2, resaved.CredentialRevision);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(1, stored.AccountSeries);
        Assert.Equal(SavedAt, stored.AccountSeriesChangedAtUtc);
    }

    /// <summary>
    /// Saving a <em>different</em> Nayax account starts a new series and records when it changed
    /// (issue #978): the previous account's snapshots stay exactly as they are, and the new account's
    /// own count is recorded against the new series rather than inheriting the old one's figure.
    /// </summary>
    [Fact]
    public async Task Saving_a_different_account_starts_a_new_series_and_records_the_change()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);

        await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var moved = await store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(2, moved.AccountSeries);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(2, stored.AccountSeries);
        Assert.Equal(SavedAt, stored.AccountSeriesChangedAtUtc);
        Assert.Equal(OtherOperatorId, stored.OperatorId);
    }

    /// <summary>
    /// A permission test result changes the status, not the account: re-testing the stored
    /// credentials leaves the series exactly as it is (issue #978).
    /// </summary>
    [Fact]
    public async Task A_status_result_leaves_the_account_series_alone()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var applied = await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(1, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None);

        Assert.True(applied);

        var stored = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(1, stored.AccountSeries);
        Assert.Equal(SavedAt, stored.AccountSeriesChangedAtUtc);
        Assert.Equal(NayaxConnectionStatus.Ready, stored.Status);
    }

    /// <summary>
    /// A business has one sales provider, and changing it is a separate design (#761), so credentials
    /// for a different provider are refused - and refused <em>before</em> anything is written, which
    /// is what the absent credential row proves: a token that may not be used must not be sitting
    /// encrypted in the database either.
    /// </summary>
    [Fact]
    public async Task A_save_is_refused_when_the_business_sells_through_another_provider()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.ForceSalesProviderAsync(fixture.BusinessA, (ExternalProvider)99);

        var refused = await Assert.ThrowsAsync<DomainConflictException>(
            () => fixture.StoreFor(fixture.BusinessA)
                .SaveCredentialAsync(OperatorId, Token, CancellationToken.None));

        Assert.Contains("one sales provider", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, refused.Message, StringComparison.Ordinal);
        Assert.Empty(await fixture.AllRowsAsync());
        Assert.Equal((ExternalProvider)99, await fixture.ReadSalesProviderAsync(fixture.BusinessA));
    }

    /// <summary>
    /// The same refusal must not destroy a credential that is already stored, which is the case that
    /// matters in production: a business that is working today keeps working. The state is arranged
    /// out of band, because nothing in the application can produce it - which is the point: even
    /// then, the refusal lands before the write.
    /// </summary>
    [Fact]
    public async Task A_refused_save_leaves_an_existing_credential_untouched()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var stored = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.ForceSalesProviderAsync(fixture.BusinessA, (ExternalProvider)99);

        await Assert.ThrowsAsync<DomainConflictException>(
            () => store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None));

        var row = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(
            (OperatorId, stored.CredentialRevision),
            (row.OperatorId, row.CredentialRevision));
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
        Assert.Null(await store.FindForOperationAsync(Allowed, CancellationToken.None));
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

    /// <summary>
    /// The read an ordinary Nayax call resolves its credentials through: the status, the operator
    /// id, the token and the revision all describe the same row as it is now, including after a
    /// save has replaced the credentials and reset the status.
    /// </summary>
    [Fact]
    public async Task The_operation_read_reports_the_status_and_the_credential_of_the_same_row()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
            CancellationToken.None));

        var ready = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.NotNull(ready);
        Assert.Equal(NayaxConnectionStatus.Ready, ready!.Status);
        Assert.Equal((OperatorId, Token, 1), Values(ready.Credential));

        // A save replaces the credentials and returns the connection to PendingPermissions, and the
        // next snapshot reports both together - never the previous status beside the new token.
        await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        var replaced = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.NotNull(replaced);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, replaced!.Status);
        Assert.Equal((OtherOperatorId, OtherToken, 2), Values(replaced.Credential));
    }

    /// <summary>
    /// That same read reports the account series of the row it read (issue #978), because that is the
    /// account whose credentials the call is about to be made with: the connected-machine snapshot
    /// taken from a fleet read is stamped with it, so reading it here rather than in a second query
    /// is what makes the evidence attributable to the account that authenticated the request.
    ///
    /// <para>It moves with the account and not with the token: a different operator id starts a new
    /// series, and rotating the same account's token keeps it - which is the difference between a
    /// business's fleet evidence continuing and being split.</para>
    /// </summary>
    [Fact]
    public async Task The_operation_read_reports_the_account_series_of_the_same_row()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var first = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.Equal(1, first!.AccountSeries);

        // The same Nayax account with a fresh token: a new revision, the same series.
        await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OperatorId, OtherToken, CancellationToken.None);

        var rotated = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.Equal(1, rotated!.AccountSeries);
        Assert.Equal(2, rotated.Credential!.CredentialRevision);

        // A different Nayax account: the next series.
        await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        var switched = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.Equal(2, switched!.AccountSeries);
    }

    /// <summary>
    /// And a status the caller's gate refuses still reports the series, because the series is not a
    /// secret and is never the thing being withheld: what a refused status withholds is the token,
    /// which stays undecrypted.
    /// </summary>
    [Fact]
    public async Task A_refused_status_still_reports_the_account_series_and_no_credential()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        var snapshot = await fixture.StoreFor(fixture.BusinessA)
            .FindForOperationAsync(_ => false, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot!.Credential);
        Assert.Equal(1, snapshot.AccountSeries);
    }

    /// <summary>
    /// The property the snapshot exists for, pinned against the SQL the provider actually sends:
    /// one <c>SELECT</c>, so no credential save can commit between reading the status and reading
    /// the token and hand a caller a pair the row never held.
    /// </summary>
    [Fact]
    public async Task The_operation_read_resolves_the_status_and_the_credential_in_one_statement()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        var store = fixture.StoreFor(fixture.BusinessA);
        fixture.Commands.Clear();

        await store.FindForOperationAsync(Allowed, CancellationToken.None);

        var select = Assert.Single(fixture.Commands.Where(command =>
            command.Contains("BusinessNayaxConnections", StringComparison.Ordinal)));
        Assert.StartsWith("SELECT", select, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Status", select, StringComparison.Ordinal);
        Assert.Contains("AccessTokenCiphertext", select, StringComparison.Ordinal);
        Assert.Contains("CredentialRevision", select, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, select, StringComparison.Ordinal);
    }

    /// <summary>
    /// A status the caller's gate refuses never reaches the ciphertext. The proof is a store whose
    /// encryption key has been retired: decrypting would throw, so a snapshot that comes back
    /// cleanly with no credential is a snapshot that never tried.
    /// </summary>
    [Fact]
    public async Task A_status_the_gate_refuses_is_reported_without_touching_the_ciphertext()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(
                saved.CredentialRevision, NayaxConnectionStatus.NeedsAttention, TestedAt),
            CancellationToken.None));
        var afterTheKeyWasRetired = fixture.StoreFor(fixture.BusinessA, Protector("2027-01"));

        var snapshot = await afterTheKeyWasRetired.FindForOperationAsync(Allowed, CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, snapshot!.Status);
        Assert.Null(snapshot.Credential);

        // The same row, read with the gate open, does try - and fails closed.
        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => afterTheKeyWasRetired.FindForOperationAsync(
                _ => true, CancellationToken.None));
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
        Assert.Null(await storeA.FindForOperationAsync(Allowed, CancellationToken.None));
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

    /// <summary>
    /// A save takes part in a transaction its caller already began instead of committing on its
    /// own. That is what lets a caller make the save and the status write that follows it one
    /// change - the issue-#519 migration command stores a production credential and marks the
    /// connection <c>Ready</c>, and "both or neither" is only true if nothing inside commits early.
    /// Rolled back, the save leaves no row at all; committed, exactly the same sequence lands.
    /// </summary>
    [Fact]
    public async Task A_save_inside_a_callers_transaction_lands_only_when_that_caller_commits()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var (rollingBackDb, rollingBack) = fixture.TransactionalStoreFor(fixture.BusinessA);

        await using (var transaction = await rollingBackDb.Database.BeginTransactionAsync())
        {
            var saved = await rollingBack.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
            Assert.True(await rollingBack.TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
                CancellationToken.None));

            await transaction.RollbackAsync();
        }

        Assert.Empty(await fixture.AllRowsAsync());

        var (committingDb, committing) = fixture.TransactionalStoreFor(fixture.BusinessA);

        await using (var transaction = await committingDb.Database.BeginTransactionAsync())
        {
            var saved = await committing.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
            Assert.True(await committing.TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt),
                CancellationToken.None));

            await transaction.CommitAsync();
        }

        var row = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.Ready, row.Status);
        Assert.Equal(1, row.CredentialRevision);
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

    /// <summary>
    /// Issue #681, end to end through the adapter: a stored ciphertext copied onto another
    /// business's row does not decrypt for that business.
    ///
    /// <para>Encryption on its own answered "is this a genuine ciphertext?" and said nothing about
    /// <em>whose</em>, so until the business id became associated data this read succeeded and
    /// business B authenticated to Nayax with business A's token. Both reads that handle the secret
    /// are asserted, because an ordinary Nayax call uses the snapshot one.</para>
    /// </summary>
    [Fact]
    public async Task A_ciphertext_copied_onto_another_businesss_row_does_not_decrypt_for_it()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.StoreFor(fixture.BusinessB)
            .SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        await fixture.CopyStoredCiphertextAsync(fixture.BusinessA, fixture.BusinessB);

        var credentialFailure = await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessB).FindCredentialAsync(CancellationToken.None));
        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessB).FindForOperationAsync(Allowed, CancellationToken.None));

        Assert.DoesNotContain(Token, credentialFailure.Message, StringComparison.Ordinal);

        // Business A's own row is untouched and still perfectly usable: the binding refuses the copy
        // rather than breaking the credential it was made from.
        var owner = await fixture.StoreFor(fixture.BusinessA).FindCredentialAsync(CancellationToken.None);
        Assert.Equal(Token, owner!.AccessToken);
    }

    [Fact]
    public async Task A_tampered_stored_ciphertext_fails_closed()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        await fixture.StoreFor(fixture.BusinessA).SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await fixture.TamperWithStoredCiphertextAsync(fixture.BusinessA);

        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessA).FindCredentialAsync(CancellationToken.None));

        // The snapshot read an ordinary call uses fails closed the same way: a usable status with a
        // credential that cannot be decrypted is a failure, not an absent connection.
        await Assert.ThrowsAsync<NayaxTokenProtectionException>(
            () => fixture.StoreFor(fixture.BusinessA).FindForOperationAsync(Allowed, CancellationToken.None));
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
        Assert.Null(await denied.FindForOperationAsync(Allowed, CancellationToken.None));
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
        Assert.Null(await unscoped.FindForOperationAsync(Allowed, CancellationToken.None));
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

    /// <summary>
    /// A status result that says something about the machine checks writes it, and one that says
    /// nothing leaves the column exactly as it is (issue #537). The second half is what keeps a
    /// 401 from an ordinary Nayax call - which never ran those checks - from claiming they passed.
    /// </summary>
    [Fact]
    public async Task A_status_result_writes_the_pending_machine_checks_only_when_it_states_them()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var saved = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt)
            {
                MachineChecksPending = true,
            },
            CancellationToken.None));
        Assert.True((await fixture.ReadRowAsync(fixture.BusinessA)).MachineChecksPending);
        Assert.True((await store.FindAsync(CancellationToken.None))!.MachineChecksPending);

        // The shape an ordinary call's 401 report uses: a status and nothing about the machines.
        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(
                saved.CredentialRevision, NayaxConnectionStatus.NeedsAttention, TestedAt),
            CancellationToken.None));
        var afterUnauthorized = await fixture.ReadRowAsync(fixture.BusinessA);
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, afterUnauthorized.Status);
        Assert.True(afterUnauthorized.MachineChecksPending);

        Assert.True(await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(saved.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt)
            {
                MachineChecksPending = false,
            },
            CancellationToken.None));
        Assert.False((await fixture.ReadRowAsync(fixture.BusinessA)).MachineChecksPending);
    }

    /// <summary>
    /// A credential save clears the pending-machine-checks flag for the same reason it clears the
    /// last-tested instant: those checks were run - or not run - against the token being replaced.
    /// </summary>
    [Fact]
    public async Task A_later_save_clears_the_previous_credential_s_pending_machine_checks()
    {
        await using var fixture = await StoreFixture.CreateAsync();
        var store = fixture.StoreFor(fixture.BusinessA);
        var first = await store.SaveCredentialAsync(OperatorId, Token, CancellationToken.None);
        await store.TryApplyStatusResultAsync(
            new NayaxConnectionStatusResult(first.CredentialRevision, NayaxConnectionStatus.Ready, TestedAt)
            {
                MachineChecksPending = true,
            },
            CancellationToken.None);

        var second = await store.SaveCredentialAsync(OtherOperatorId, OtherToken, CancellationToken.None);

        Assert.Equal(2, second.CredentialRevision);
        Assert.False(second.MachineChecksPending);
        Assert.False((await fixture.ReadRowAsync(fixture.BusinessA)).MachineChecksPending);
    }

    /// <summary>A first save starts with nothing pending, like every other test fact.</summary>
    [Fact]
    public async Task A_first_save_reports_no_pending_machine_checks()
    {
        await using var fixture = await StoreFixture.CreateAsync();

        var saved = await fixture.StoreFor(fixture.BusinessA)
            .SaveCredentialAsync(OperatorId, Token, CancellationToken.None);

        Assert.False(saved.MachineChecksPending);
        Assert.False((await fixture.ReadRowAsync(fixture.BusinessA)).MachineChecksPending);
    }

    /// <summary>The gate an ordinary Nayax call hands to the snapshot read (issue #520).</summary>
    private static bool Allowed(NayaxConnectionStatus status) =>
        NayaxConnectionStatusGate.AllowsOrdinaryOperations(status);

    /// <summary>The credential's values, or nulls, for comparing a snapshot in one assertion.</summary>
    private static (string? OperatorId, string? AccessToken, int? CredentialRevision) Values(
        NayaxConnectionCredential? credential) =>
        (credential?.OperatorId, credential?.AccessToken, credential?.CredentialRevision);

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
            var a = new Business { Name = "Vending A", Status = BusinessStatus.Active, CreatedAtUtc = SavedAt };
            var b = new Business { Name = "Vending B", Status = BusinessStatus.Active, CreatedAtUtc = SavedAt };
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

        /// <summary>
        /// A business-scoped context and the store built over it, so a test can own the transaction
        /// the store's writes have to take part in.
        /// </summary>
        public (AppDbContext Db, EfNayaxConnectionStore Store) TransactionalStoreFor(int businessId)
        {
            var context = TestAppDbContext.For(_options, businessId);
            return (context, StoreForContext(context));
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

        /// <summary>The business's recorded sales provider (issue #759), or <c>null</c> for none.</summary>
        public async Task<ExternalProvider?> ReadSalesProviderAsync(int businessId)
        {
            await using var context = TestAppDbContext.Unrestricted(_options);
            return await context.Businesses
                .AsNoTracking()
                .Where(business => business.Id == businessId)
                .Select(business => business.SalesProvider)
                .SingleAsync();
        }

        /// <summary>
        /// Writes a business's sales provider with raw SQL, which is the only way to arrange a
        /// business whose provider is not Nayax: a claim may only fill an empty column and
        /// <c>BusinessSalesProviderEnforcer</c> refuses every other change. Nayax is also the one
        /// provider this application implements, so a second one is written as the stored value it
        /// would have - what is under test is that a save refuses a provider that is not the
        /// business's, not how many members the enum has.
        /// </summary>
        public async Task ForceSalesProviderAsync(int businessId, ExternalProvider provider)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText =
                $"UPDATE Businesses SET SalesProvider = {(int)provider} WHERE Id = {businessId};";
            await command.ExecuteNonQueryAsync();
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
            // The version marker issue #681 added is part of the stored text, not of the base64, so
            // it is split off before the body is corrupted and put back afterwards.
            var marker = row.AccessTokenCiphertext.StartsWith("v2:", StringComparison.Ordinal) ? "v2:" : string.Empty;
            var stored = Convert.FromBase64String(row.AccessTokenCiphertext[marker.Length..]);
            stored[^1] ^= 0xFF;

            await using var context = TestAppDbContext.Unrestricted(_options);
            var tracked = await context.BusinessNayaxConnections
                .SingleAsync(connection => connection.BusinessId == businessId);
            tracked.AccessTokenCiphertext = marker + Convert.ToBase64String(stored);
            await context.SaveChangesAsync();
        }

        /// <summary>
        /// Copies one business's stored ciphertext and key id onto another business's row, without
        /// going through any adapter - the shape a hand-written <c>UPDATE</c>, a restored backup or a
        /// mistaken repair has (issue #681).
        /// </summary>
        public async Task CopyStoredCiphertextAsync(int fromBusinessId, int toBusinessId)
        {
            var source = await ReadRowAsync(fromBusinessId);

            await using var context = TestAppDbContext.Unrestricted(_options);
            var target = await context.BusinessNayaxConnections
                .SingleAsync(connection => connection.BusinessId == toBusinessId);
            target.AccessTokenCiphertext = source.AccessTokenCiphertext;
            target.EncryptionKeyId = source.EncryptionKeyId;
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
