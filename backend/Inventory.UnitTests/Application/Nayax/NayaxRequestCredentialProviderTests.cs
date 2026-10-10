using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// The status gate and the 401 rule of issue #520, over a fake #518 store.
///
/// These are the two decisions that keep a per-business Nayax credential safe to use, so they are
/// tested where they are made rather than only through the HTTP client: an ordinary call may use
/// credentials only in <see cref="NayaxConnectionStatus.Ready"/> or
/// <see cref="NayaxConnectionStatus.PendingPermissions"/>, a refused state never decrypts the token
/// at all, and a 401 writes its status for the revision the call started with.
///
/// Both decisions are taken on one snapshot of the connection, so the status that is gated and the
/// token that is sent can never describe different states of the record. Two of the tests below
/// drive a store that answers the separate status and credential reads inconsistently - the shape of
/// a credential replaced underneath a call - and pin that the provider resolves neither from it.
/// </summary>
public class NayaxRequestCredentialProviderTests
{
    // Obviously fake values. No real token or operator account belongs in a test.
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";
    private static readonly DateTime Now = new(2026, 10, 10, 11, 12, 13, DateTimeKind.Utc);

    [Theory]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    public async Task A_usable_status_resolves_the_business_s_own_operator_id_token_and_revision(
        NayaxConnectionStatus status)
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, status, credentialRevision: 4);
        var provider = CreateProvider(store);

        var credential = await provider.GetForOperationAsync(CancellationToken.None);

        Assert.Equal(OperatorId, credential.OperatorId);
        Assert.Equal(Token, credential.AccessToken);
        Assert.Equal(4, credential.CredentialRevision);
        Assert.Equal(status, credential.Status);
    }

    [Fact]
    public async Task Stored_but_untested_credentials_are_flagged_as_having_unverified_permissions()
    {
        var store = new FakeNayaxConnectionStore(
            OperatorId, Token, NayaxConnectionStatus.PendingPermissions);

        var credential = await CreateProvider(store).GetForOperationAsync(CancellationToken.None);

        Assert.True(credential.PermissionsAreUnverified);
    }

    [Fact]
    public async Task Tested_credentials_are_not_flagged_as_having_unverified_permissions()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.Ready);

        var credential = await CreateProvider(store).GetForOperationAsync(CancellationToken.None);

        Assert.False(credential.PermissionsAreUnverified);
    }

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task An_unusable_status_fails_closed_with_the_stable_not_connected_error(
        NayaxConnectionStatus status)
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, status);
        var provider = CreateProvider(store);

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => provider.GetForOperationAsync(CancellationToken.None));

        Assert.Equal(status, exception.Status);
        Assert.Equal(NayaxNotConnectedException.StableMessage, exception.Message);
    }

    /// <summary>
    /// The refusal must happen before the token is decrypted: a state an operator has to fix is no
    /// reason to handle the secret at all, and decrypting first would make the gate a decision
    /// taken after the risky step rather than before it.
    /// </summary>
    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task A_refused_status_never_decrypts_the_stored_token(NayaxConnectionStatus status)
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, status);

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));

        Assert.Equal(0, store.CredentialReads);
    }

    /// <summary>
    /// An enum is a compile-time constraint, not a validation: a value read from the database can
    /// be anything. A status this application does not know must fail closed rather than pass a
    /// gate it was never considered by.
    /// </summary>
    [Fact]
    public async Task A_status_value_outside_the_declared_vocabulary_fails_closed()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, (NayaxConnectionStatus)99);

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));

        Assert.Equal(0, store.CredentialReads);
    }

    [Fact]
    public async Task A_business_with_no_stored_connection_fails_closed_as_not_configured()
    {
        var store = new FakeNayaxConnectionStore();

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NotConfigured, exception.Status);
        Assert.Equal(0, store.CredentialReads);
    }

    /// <summary>
    /// A credential that cannot be decrypted - a tampered ciphertext, or a key that has been
    /// retired - must keep failing closed rather than being reported as a missing connection. The
    /// store guarantees it throws; the provider must not soften that into a "not connected" answer
    /// an operator would try to fix by reconnecting.
    /// </summary>
    [Fact]
    public async Task An_undecryptable_credential_propagates_rather_than_reading_as_not_connected()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.Ready)
        {
            CredentialReadFailure = new InvalidOperationException("ciphertext cannot be decrypted"),
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));
    }

    /// <summary>
    /// A snapshot that reports a usable status and yet carries no credential - a row without usable
    /// credentials, whatever produced it. It must not surface as a credential with an empty token.
    /// </summary>
    [Fact]
    public async Task A_usable_status_with_no_credential_in_the_snapshot_fails_closed()
    {
        var store = new CredentiallessSnapshotStore(NayaxConnectionStatus.Ready);

        await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));
    }

    /// <summary>
    /// The race the snapshot read exists for. This store answers the *separate* reads the way a
    /// connection being replaced underneath a call would - the status read sees the old row, the
    /// credential read sees the new one - while its snapshot read answers from one state, as the
    /// relational adapter's single statement does.
    ///
    /// So the provider must return a status that belongs to the token it actually got: resolving the
    /// two separately would return <see cref="NayaxConnectionStatus.Ready"/> beside revision 2's
    /// token, a pair the record never held, and a Nayax 403 against it would then be classified as a
    /// generic upstream failure instead of the per-feature permission answer
    /// <see cref="NayaxConnectionStatus.PendingPermissions"/> calls for.
    /// </summary>
    [Fact]
    public async Task The_status_and_the_revision_describe_the_token_that_was_actually_read()
    {
        var store = new ReplacedBetweenReadsStore(
            statusRead: new NayaxConnection(OperatorId, NayaxConnectionStatus.Ready, 1, null, Now),
            snapshotStatus: NayaxConnectionStatus.PendingPermissions,
            snapshotCredential: new NayaxConnectionCredential(OperatorId, "fake-replacement-token", 2));

        var credential = await CreateProvider(store).GetForOperationAsync(CancellationToken.None);

        Assert.Equal("fake-replacement-token", credential.AccessToken);
        Assert.Equal(2, credential.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, credential.Status);
        Assert.True(credential.PermissionsAreUnverified);
    }

    /// <summary>
    /// The more serious half of the same race: the connection became unusable, so the credential
    /// stored with it must not be sent under the status an earlier read happened to see. The gate
    /// applies to the snapshot's own status, which also means the token is never decrypted.
    /// </summary>
    [Fact]
    public async Task A_connection_that_became_unusable_is_refused_on_the_snapshot_s_own_status()
    {
        var store = new ReplacedBetweenReadsStore(
            statusRead: new NayaxConnection(OperatorId, NayaxConnectionStatus.Ready, 1, null, Now),
            snapshotStatus: NayaxConnectionStatus.NeedsAttention,
            snapshotCredential: new NayaxConnectionCredential(OperatorId, Token, 2));

        var exception = await Assert.ThrowsAsync<NayaxNotConnectedException>(
            () => CreateProvider(store).GetForOperationAsync(CancellationToken.None));

        Assert.Equal(NayaxConnectionStatus.NeedsAttention, exception.Status);
        Assert.False(store.DecryptedTheToken);
    }

    [Fact]
    public async Task A_401_marks_the_connection_as_needing_attention_at_the_revision_it_used()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.Ready, 7);
        var provider = CreateProvider(store);

        await provider.ReportUnauthorizedAsync(7, CancellationToken.None);

        var applied = Assert.Single(store.AppliedResults);
        Assert.Equal(
            (7, NayaxConnectionStatus.NeedsAttention, Now),
            (applied.CredentialRevision, applied.Status, applied.TestedAtUtc));
        Assert.Equal(NayaxConnectionStatus.NeedsAttention, store.Connection!.Status);
    }

    /// <summary>
    /// A 401 answering a call that started on revision N, arriving after revision N+1 was saved,
    /// describes credentials that no longer exist. The result is discarded and N+1 keeps its own
    /// status - which is what stops a slow in-flight call from marking a token an operator has just
    /// fixed as broken.
    /// </summary>
    [Fact]
    public async Task A_401_from_a_superseded_revision_leaves_the_newer_revision_s_status_alone()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.Ready, 7);
        var provider = CreateProvider(store);
        await store.SaveCredentialAsync(OperatorId, "fake-replacement-token", CancellationToken.None);

        await provider.ReportUnauthorizedAsync(7, CancellationToken.None);

        Assert.Empty(store.AppliedResults);
        var discarded = Assert.Single(store.DiscardedResults);
        Assert.Equal(7, discarded.CredentialRevision);
        Assert.Equal(8, store.Connection!.CredentialRevision);
        Assert.Equal(NayaxConnectionStatus.PendingPermissions, store.Connection.Status);
    }

    /// <summary>
    /// Two businesses, two providers, two stores: each resolves its own operator id and token, and
    /// neither can reach the other's. The relational proof that the boundary is the
    /// <c>AppDbContext</c> tenant filter rather than this type is in
    /// <c>NayaxLynxClientPerBusinessCredentialTests</c>; this pins that the provider adds no shared
    /// state of its own.
    /// </summary>
    [Fact]
    public async Task Two_businesses_each_resolve_their_own_credentials()
    {
        var providerA = CreateProvider(
            new FakeNayaxConnectionStore("operator-a", "fake-token-a", NayaxConnectionStatus.Ready));
        var providerB = CreateProvider(
            new FakeNayaxConnectionStore("operator-b", "fake-token-b", NayaxConnectionStatus.Ready));

        var a = await providerA.GetForOperationAsync(CancellationToken.None);
        var b = await providerB.GetForOperationAsync(CancellationToken.None);

        Assert.Equal(("operator-a", "fake-token-a"), (a.OperatorId, a.AccessToken));
        Assert.Equal(("operator-b", "fake-token-b"), (b.OperatorId, b.AccessToken));
    }

    /// <summary>
    /// The credential is the one object in the application that holds a Nayax token in plaintext,
    /// and <c>ToString</c> is what a log line, a structured logging property or an assertion message
    /// reaches for. Rendering the wrapper must stay as redacted as rendering the credential itself.
    /// </summary>
    [Fact]
    public async Task Rendering_the_resolved_credential_never_prints_the_token()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.Ready);

        var credential = await CreateProvider(store).GetForOperationAsync(CancellationToken.None);

        var rendered = credential.ToString();
        Assert.DoesNotContain(Token, rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[redacted]", rendered, StringComparison.Ordinal);
        Assert.Contains(OperatorId, rendered, StringComparison.Ordinal);
    }

    private static NayaxRequestCredentialProvider CreateProvider(INayaxConnectionStore store) =>
        new(store, new FakeClock(Now));

    /// <summary>
    /// Reports a usable status in a snapshot that carries no credential at all.
    /// </summary>
    private sealed class CredentiallessSnapshotStore : INayaxConnectionStore
    {
        private readonly NayaxConnectionStatus _status;

        public CredentiallessSnapshotStore(NayaxConnectionStatus status) => _status = status;

        public Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult<NayaxConnection?>(new NayaxConnection(OperatorId, _status, 1, null, Now));

        public Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken) =>
            Task.FromResult<NayaxConnectionCredential?>(null);

        public Task<NayaxConnectionSnapshot?> FindForOperationAsync(
            Func<NayaxConnectionStatus, bool> mayDecryptToken, CancellationToken cancellationToken) =>
            Task.FromResult<NayaxConnectionSnapshot?>(
                new NayaxConnectionSnapshot(_status, credential: null));

        public Task<NayaxConnection> SaveCredentialAsync(
            string operatorId, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryApplyStatusResultAsync(
            NayaxConnectionStatusResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A store whose row is replaced while a call is resolving its credentials: the separate status
    /// read still answers from the row as it was, and the snapshot read answers from the row as it
    /// now is - one state, status and credential together, like the adapter's single statement.
    ///
    /// Resolving the status and the token through the two separate reads therefore produces a
    /// mismatched pair here, which is exactly what these tests are for. The gate is applied to the
    /// snapshot's status before anything is decrypted, so <see cref="DecryptedTheToken"/> records
    /// whether the refused path touched the credential.
    /// </summary>
    private sealed class ReplacedBetweenReadsStore : INayaxConnectionStore
    {
        private readonly NayaxConnection _statusRead;
        private readonly NayaxConnectionStatus _snapshotStatus;
        private readonly NayaxConnectionCredential _snapshotCredential;

        public ReplacedBetweenReadsStore(
            NayaxConnection statusRead,
            NayaxConnectionStatus snapshotStatus,
            NayaxConnectionCredential snapshotCredential)
        {
            _statusRead = statusRead;
            _snapshotStatus = snapshotStatus;
            _snapshotCredential = snapshotCredential;
        }

        /// <summary>Whether the stored token was decrypted at all.</summary>
        public bool DecryptedTheToken { get; private set; }

        public Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken) =>
            Task.FromResult<NayaxConnection?>(_statusRead);

        public Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken)
        {
            DecryptedTheToken = true;
            return Task.FromResult<NayaxConnectionCredential?>(_snapshotCredential);
        }

        public Task<NayaxConnectionSnapshot?> FindForOperationAsync(
            Func<NayaxConnectionStatus, bool> mayDecryptToken, CancellationToken cancellationToken)
        {
            if (!mayDecryptToken(_snapshotStatus))
            {
                return Task.FromResult<NayaxConnectionSnapshot?>(
                    new NayaxConnectionSnapshot(_snapshotStatus, credential: null));
            }

            DecryptedTheToken = true;

            return Task.FromResult<NayaxConnectionSnapshot?>(
                new NayaxConnectionSnapshot(_snapshotStatus, _snapshotCredential));
        }

        public Task<NayaxConnection> SaveCredentialAsync(
            string operatorId, string accessToken, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> TryApplyStatusResultAsync(
            NayaxConnectionStatusResult result, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
