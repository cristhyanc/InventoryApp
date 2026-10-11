using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// An in-memory <see cref="INayaxConnectionStore"/> for one business, recording what was asked of
/// it (issue #520).
///
/// It records the reads as well as the writes, because two of the properties under test are about a
/// call that must <em>not</em> happen: a status the gate refuses must never decrypt the stored token
/// (<see cref="CredentialReads"/> stays at zero), and a 403 must write no status at all
/// (<see cref="AppliedResults"/> stays empty).
///
/// <see cref="TryApplyStatusResultAsync"/> reproduces the real adapter's conditional update - the
/// result is applied only while the stored revision still matches the one it was produced for -
/// because that is the behaviour the 401 rule depends on. The relational proof that the real
/// <c>UPDATE ... WHERE</c> does the same lives in the integration tests; this keeps the unit tests
/// honest about which revision they pass.
/// </summary>
internal sealed class FakeNayaxConnectionStore : INayaxConnectionStore
{
    private NayaxConnection? _connection;
    private string? _accessToken;

    public FakeNayaxConnectionStore()
    {
    }

    public FakeNayaxConnectionStore(
        string operatorId,
        string accessToken,
        NayaxConnectionStatus status,
        int credentialRevision = 1)
    {
        _connection = new NayaxConnection(
            operatorId,
            status,
            credentialRevision,
            LastTestedAtUtc: null,
            UpdatedAtUtc: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        _accessToken = accessToken;
    }

    /// <summary>How many times the stored token was decrypted.</summary>
    public int CredentialReads { get; private set; }

    /// <summary>How many times the status was read.</summary>
    public int ConnectionReads { get; private set; }

    /// <summary>Every status result that was accepted, in order.</summary>
    public List<NayaxConnectionStatusResult> AppliedResults { get; } = new();

    /// <summary>Every status result that was discarded as stale, in order.</summary>
    public List<NayaxConnectionStatusResult> DiscardedResults { get; } = new();

    /// <summary>The exception the next credential read should throw, if any.</summary>
    public Exception? CredentialReadFailure { get; set; }

    public NayaxConnection? Connection => _connection;

    public Task<NayaxConnection?> FindAsync(CancellationToken cancellationToken)
    {
        ConnectionReads++;
        return Task.FromResult(_connection);
    }

    public Task<NayaxConnectionCredential?> FindCredentialAsync(CancellationToken cancellationToken)
    {
        CredentialReads++;

        if (CredentialReadFailure is not null)
        {
            throw CredentialReadFailure;
        }

        if (_connection is null || _accessToken is null)
        {
            return Task.FromResult<NayaxConnectionCredential?>(null);
        }

        return Task.FromResult<NayaxConnectionCredential?>(
            new NayaxConnectionCredential(_connection.OperatorId, _accessToken, _connection.CredentialRevision));
    }

    /// <summary>
    /// One snapshot of the stored state, like the real adapter's single statement: the status and
    /// the credential cannot disagree here either, and the token counts as decrypted
    /// (<see cref="CredentialReads"/>) only when <paramref name="mayDecryptToken"/> accepted that
    /// same snapshot's status.
    /// </summary>
    public Task<NayaxConnectionSnapshot?> FindForOperationAsync(
        Func<NayaxConnectionStatus, bool> mayDecryptToken,
        CancellationToken cancellationToken)
    {
        ConnectionReads++;

        if (_connection is null)
        {
            return Task.FromResult<NayaxConnectionSnapshot?>(null);
        }

        if (!mayDecryptToken(_connection.Status))
        {
            return Task.FromResult<NayaxConnectionSnapshot?>(
                new NayaxConnectionSnapshot(_connection.Status, credential: null));
        }

        CredentialReads++;

        if (CredentialReadFailure is not null)
        {
            throw CredentialReadFailure;
        }

        var credential = _accessToken is null
            ? null
            : new NayaxConnectionCredential(
                _connection.OperatorId, _accessToken, _connection.CredentialRevision);

        return Task.FromResult<NayaxConnectionSnapshot?>(
            new NayaxConnectionSnapshot(_connection.Status, credential));
    }

    public Task<NayaxConnection> SaveCredentialAsync(
        string operatorId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        _connection = new NayaxConnection(
            operatorId,
            NayaxConnectionStatus.PendingPermissions,
            (_connection?.CredentialRevision ?? 0) + 1,
            LastTestedAtUtc: null,
            UpdatedAtUtc: new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc));
        _accessToken = accessToken;

        return Task.FromResult(_connection);
    }

    public Task<bool> TryApplyStatusResultAsync(
        NayaxConnectionStatusResult result,
        CancellationToken cancellationToken)
    {
        if (_connection is null || _connection.CredentialRevision != result.CredentialRevision)
        {
            DiscardedResults.Add(result);
            return Task.FromResult(false);
        }

        AppliedResults.Add(result);
        _connection = _connection with
        {
            Status = result.Status,
            LastTestedAtUtc = result.TestedAtUtc,
            UpdatedAtUtc = result.TestedAtUtc,
        };

        return Task.FromResult(true);
    }
}
