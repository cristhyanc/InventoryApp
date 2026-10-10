using Inventory.Application.Time;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// Resolves the current business's own Nayax credentials for one ordinary call, applying the status
/// gate, and records a 401 against the revision that call used (issue #520).
///
/// It is the whole answer to "where does a Nayax call get its operator id and token from" and it is
/// deliberately boring: two reads through #518's store and one Domain rule. It resolves no business
/// itself and holds no predicate - the store reads the business the
/// <c>AppDbContext</c> tenant filters already resolved, so a caller with no business reads nothing
/// and this provider has nothing to fall back to. There is no global operator id or token left to
/// fall back to either.
///
/// **The status is read before the token is decrypted, on purpose.** A refused state never touches
/// the secret at all, so the gate is a decision taken before the risky step rather than after it.
/// The two reads are also why <see cref="NayaxRequestCredential.CredentialRevision"/> comes from the
/// credential rather than from the status read: the revision a 401 is attributed to has to be the
/// revision of the token that was actually sent.
///
/// It is registered per request scope (<c>AddApplicationServices</c>) and caches nothing. Every
/// ordinary Nayax call therefore re-reads the connection, which is what makes the gate close
/// immediately: the call after a 401 sees <see cref="NayaxConnectionStatus.NeedsAttention"/> and
/// fails closed rather than reusing a credential snapshot the same request took earlier.
/// </summary>
public sealed class NayaxRequestCredentialProvider : INayaxRequestCredentialProvider
{
    private readonly INayaxConnectionStore _store;
    private readonly IClock _clock;

    public NayaxRequestCredentialProvider(INayaxConnectionStore store, IClock clock)
    {
        _store = store;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
    {
        var connection = await _store.FindAsync(cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            // No record at all, or no resolved business to have one. Both are "not connected"; the
            // store is what refuses to answer for a caller it cannot scope.
            throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);
        }

        if (!NayaxConnectionStatusGate.AllowsOrdinaryOperations(connection.Status))
        {
            throw new NayaxNotConnectedException(connection.Status);
        }

        var credential = await _store.FindCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            // The row was there when the status was read and gone when the token was. Reporting it
            // as not connected is the fail-closed answer; a credential with an empty token would
            // reach Nayax as an unauthenticated request.
            throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);
        }

        return new NayaxRequestCredential(credential, connection.Status);
    }

    /// <inheritdoc />
    public async Task ReportUnauthorizedAsync(int credentialRevision, CancellationToken cancellationToken)
    {
        // The store's conditional update is what makes this safe: the result is written only while
        // the stored revision is still the one the call used, so a 401 answering a superseded token
        // is discarded (and logged by the store) instead of overwriting the status of the
        // credentials that replaced it. The instant is recorded as the last test result, because a
        // 401 is exactly a test of those credentials - and a real one, in production traffic.
        await _store
            .TryApplyStatusResultAsync(
                new NayaxConnectionStatusResult(
                    credentialRevision,
                    NayaxConnectionStatus.NeedsAttention,
                    _clock.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
