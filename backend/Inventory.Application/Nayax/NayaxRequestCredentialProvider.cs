using Inventory.Application.Time;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Nayax;

/// <summary>
/// Resolves the current business's own Nayax credentials for one ordinary call, applying the status
/// gate, and records a 401 against the revision that call used (issue #520).
///
/// It is the whole answer to "where does a Nayax call get its operator id and token from" and it is
/// deliberately boring: one read through #518's store and one Domain rule. It resolves no business
/// itself and holds no predicate - the store reads the business the
/// <c>AppDbContext</c> tenant filters already resolved, so a caller with no business reads nothing
/// and this provider has nothing to fall back to. There is no global operator id or token left to
/// fall back to either.
///
/// **The status, the operator id, the token and the revision come from one snapshot, on purpose.**
/// Read separately, a credential save landing between a status read and a token read would produce a
/// pair the record never held - the previous status beside the new token - and that pair decides both
/// whether the call may run and whether a Nayax 403 is a per-feature permission answer. So the gate
/// is handed to <see cref="INayaxConnectionStore.FindForOperationAsync"/> and applied to the status
/// in that same snapshot, which also keeps the refusal ahead of the risky step: **a refused status
/// never decrypts the token at all**, and the revision a 401 is attributed to is always the revision
/// of the token that was actually sent.
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
        // The gate travels into the read, so the status it decides on and the token that is
        // decrypted come from the same snapshot of the same row.
        var snapshot = await _store
            .FindForOperationAsync(NayaxConnectionStatusGate.AllowsOrdinaryOperations, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            // No record at all, or no resolved business to have one. Both are "not connected"; the
            // store is what refuses to answer for a caller it cannot scope.
            throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);
        }

        if (!NayaxConnectionStatusGate.AllowsOrdinaryOperations(snapshot.Status))
        {
            // The same rule the store was handed, applied again here because refusing the operation
            // is this provider's decision to take and to report. The store only used it to decide
            // whether it was allowed to decrypt.
            throw new NayaxNotConnectedException(snapshot.Status);
        }

        if (snapshot.Credential is null)
        {
            // The gate allowed this snapshot's status, so the store had no reason to withhold the
            // token: a snapshot that still carries none describes a row without usable credentials.
            // Reporting it as not connected is the fail-closed answer; a credential with an empty
            // token would reach Nayax as an unauthenticated request.
            throw new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured);
        }

        return new NayaxRequestCredential(snapshot.Credential, snapshot.Status);
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
