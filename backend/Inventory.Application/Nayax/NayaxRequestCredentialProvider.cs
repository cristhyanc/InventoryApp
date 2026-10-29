using Inventory.Application.Subscriptions;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Nayax;
using Inventory.Domain.Subscriptions;

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
///
/// <para><strong>A fan-out resolves its credentials one call at a time, and still one call per
/// call</strong> (issue #667). The bounded parallel Nayax fan-outs put several of these reads - and
/// several of the 401 writes below - on the request's one persistence scope at the same time;
/// <see cref="INayaxConnectionStore"/> admits one of them at a time, and this provider still caches
/// nothing for the ones that waited. Each call is gated on the status its own snapshot carried, so a
/// gate that closes part-way through a fan-out refuses the calls that have not resolved yet instead
/// of handing them a usable credential the request read earlier.</para>
///
/// <para><strong>A support session resolves no credentials at all (issue #551).</strong> The
/// business's operator id and access token are the business's own, and a platform administrator
/// reading its data to help with a problem is not a reason to spend them: nobody outside the
/// business has agreed to its Nayax account being used on their behalf, and a call made in a
/// support view would be indistinguishable, at Nayax, from one the business made itself. The
/// refusal is made here, before the stored connection is read and before any token could be
/// decrypted, so it holds for every ordinary Nayax call - read and write alike - and no request
/// leaves the API. Being here rather than in each of the ~27 consumers is the whole point: a
/// per-controller check is a check somebody can forget to add to the next controller.</para>
///
/// <para><strong>A business whose plan does not include the sales provider resolves no credentials
/// either (issue #967).</strong> The check is made here, for exactly the reason the support-session
/// one is: this is the one place every ordinary Nayax call passes through, so every mixed surface -
/// the dashboard's fleet sections, the machine reads, reorder, the Pick List matrix, the
/// inventory-cost transition previews, the catalogue review and Sync Restock - gets the
/// no-usable-provider behaviour it already has for an unusable connection, without a check being
/// added to any of them. It is made <em>before</em> the stored connection is read, so a downgraded
/// business with perfectly valid saved credentials makes no Nayax request at all and its credentials,
/// their status and its stored sales, costs and history are all left exactly as they are. The
/// refusal carries the distinct plan reason (<see cref="NayaxNotConnectedException.PlanRequired"/>),
/// so a surface can say "needs Pro" instead of "connect Nayax". While plan enforcement is off -
/// the shipped state - nothing about the business's plan is read and nothing changes.</para>
///
/// <para>The credential actions an Owner uses to <em>set up</em> the connection - test, save and
/// re-test - deliberately do not come through here (they call Nayax with pasted credentials, or run
/// the connection check themselves), so they declare the plan requirement at their own endpoints.
/// Reading the connection status does not come through here either, and stays available on every
/// plan.</para>
/// </summary>
public sealed class NayaxRequestCredentialProvider : INayaxRequestCredentialProvider
{
    private readonly INayaxConnectionStore _store;
    private readonly IBusinessScope _businessScope;
    private readonly CurrentPlan _plan;
    private readonly IClock _clock;

    public NayaxRequestCredentialProvider(
        INayaxConnectionStore store,
        IBusinessScope businessScope,
        CurrentPlan plan,
        IClock clock)
    {
        _store = store;
        _businessScope = businessScope;
        _plan = plan;
        _clock = clock;
    }

    /// <inheritdoc />
    public async Task<NayaxRequestCredential> GetForOperationAsync(CancellationToken cancellationToken)
    {
        if (_businessScope.State == BusinessScopeState.SupportReadOnly)
        {
            // First, and before anything is read: a support session never authenticates to Nayax as
            // the business it is looking at.
            throw new SupportSessionNayaxUnavailableException();
        }

        // Then the plan, and still before anything about the connection is read: a business that has
        // not bought the sales provider spends no Nayax request and has no token decrypted, however
        // usable the credentials it saved while it was on a plan that included them.
        if (!await _plan
                .AllowsAsync(PlanFeature.SalesProviderIntegration, cancellationToken)
                .ConfigureAwait(false))
        {
            throw NayaxNotConnectedException.PlanRequired(
                PlanFeaturePolicy.RequiredPlan(PlanFeature.SalesProviderIntegration));
        }

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
