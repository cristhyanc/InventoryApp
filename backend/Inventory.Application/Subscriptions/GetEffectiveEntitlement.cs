using Inventory.Application.Time;
using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Subscriptions;

/// <summary>
/// What the business this request acts in may currently use (issue #966).
///
/// <para><strong>It takes no identifier.</strong> The business is the one the request already
/// resolved - from the caller's membership, or, inside a support session, from the stored session row
/// (issue #551) - so no route, query, body or header value can choose whose entitlement is reported,
/// and there is no separate read endpoint a caller could point at another business. A support
/// session therefore reports the <em>viewed</em> business's own plan, which is the decision of 29
/// October 2026, and it falls out of the resolution rather than being coded for here.</para>
///
/// <para><strong>Computed per request, with no server cache.</strong> It is registered scoped, reads
/// the records on the request's own context, and takes its instant from the application clock, so a
/// grant that ends or a subscription that is cancelled applies on the next request instead of when
/// something expires a cache. The one thing it does not read is a record it does not need: while
/// enforcement is off, or while the grandfathering step has not completed, nothing is read about the
/// business at all - the answer is unrestricted whatever the records say.</para>
///
/// <para><strong>A role preview cannot change it.</strong> This use case never consults
/// <c>IRolePreviewAccessor</c>: a preview narrows what a role may do, and a plan is what the business
/// bought. The two are independent, and the preview middleware has nothing here to narrow.</para>
///
/// <para><strong>The rule itself is Domain.</strong> Every decision - the rollout gate, the
/// grandfathering gate, "the higher of the two records", the half-open period boundaries and the
/// fall back to Free - belongs to <see cref="EffectiveEntitlementPolicy"/>, which is where it is
/// tested row by row. This use case only supplies it with facts. A row the policy cannot interpret
/// is Free for paid features; a database that cannot be <em>reached</em> is an error, exactly as it
/// is for every other part of the access response, because answering Free over a transport failure
/// would lock a paying business out of what it bought.</para>
/// </summary>
public sealed class GetEffectiveEntitlement
{
    private readonly ISubscriptionEnforcementFlag _enforcement;
    private readonly ISubscriptionGrandfatheringStore _grandfathering;
    private readonly IManualPlanGrantStore _grants;
    private readonly IBillingSubscriptionStore _subscriptions;
    private readonly IClock _clock;

    public GetEffectiveEntitlement(
        ISubscriptionEnforcementFlag enforcement,
        ISubscriptionGrandfatheringStore grandfathering,
        IManualPlanGrantStore grants,
        IBillingSubscriptionStore subscriptions,
        IClock clock)
    {
        _enforcement = enforcement;
        _grandfathering = grandfathering;
        _grants = grants;
        _subscriptions = subscriptions;
        _clock = clock;
    }

    public async Task<EffectiveEntitlement> Handle(CancellationToken cancellationToken)
    {
        var enforcementEnabled = _enforcement.IsEnabled;

        // The grandfathering record is read only once the rollout setting is on: with it off the
        // answer is unrestricted whatever that record holds, and a deployment that has switched
        // nothing on must make no extra read per request.
        var grandfatheringCompleted = enforcementEnabled
            && await IsGrandfatheringCompleteAsync(cancellationToken).ConfigureAwait(false);

        // And the business's own two records only once enforcement may actually take effect. Until
        // then there is nothing to read them for, and reading them would publish a plan for a
        // business nothing is restricting.
        var enforcing = enforcementEnabled && grandfatheringCompleted;

        var grants = enforcing
            ? await _grants.ListForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false)
            : [];
        var subscription = enforcing
            ? await _subscriptions.FindForCurrentBusinessAsync(cancellationToken).ConfigureAwait(false)
            : null;

        return EffectiveEntitlementPolicy.Decide(
            enforcementEnabled,
            grandfatheringCompleted,
            grants,
            subscription,
            _clock.UtcNow);
    }

    private async Task<bool> IsGrandfatheringCompleteAsync(CancellationToken cancellationToken) =>
        await _grandfathering.FindAsync(cancellationToken).ConfigureAwait(false) is { IsComplete: true };
}
