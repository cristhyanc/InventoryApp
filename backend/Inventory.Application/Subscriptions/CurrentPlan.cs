using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Subscriptions;

/// <summary>
/// "Does the business this request acts in include this feature?" (issue #967) - the one place every
/// enforcement point asks, and the Application-layer companion to
/// <see cref="Access.CurrentCapabilities"/>.
///
/// <para><strong>It takes no identifier and no plan.</strong> The business is the one the request
/// already resolved, through <see cref="GetEffectiveEntitlement"/>, so no route, query, body or
/// header value can choose whose plan is evaluated; and a caller names a
/// <see cref="PlanFeature"/> rather than a plan, so which plan a feature needs stays
/// <see cref="PlanFeaturePolicy"/>'s single answer. An endpoint, the Nayax credential gate and the
/// report export all ask the same question in the same words.</para>
///
/// <para><strong>Read once per request, and shared.</strong> It is registered scoped and memoises the
/// one entitlement read, because a plan is a fact about the request's business rather than about the
/// call being made - the same reason the membership resolution behind
/// <see cref="Access.CurrentCapabilities"/> is memoised. That matters most for the bounded parallel
/// Nayax fan-outs: a fleet refresh resolves credentials once per machine, and without this each of
/// those would be a second entitlement read on the request's one persistence scope. The first caller
/// starts the read and every other caller - concurrent ones included - awaits that same read, so
/// exactly one is made. A failed read is not retried: a database that cannot be reached is an error
/// for every asker, exactly as it is for <c>GET /api/me/access</c>, because answering "not included"
/// over a transport failure would lock a paying business out of what it bought.</para>
///
/// <para><strong>Nothing it answers is a permission.</strong> A member's capabilities are identical
/// whatever the plan says, and a role preview cannot reach this at all
/// (<see cref="GetEffectiveEntitlement"/> consults no preview accessor). Where both a role and a plan
/// apply, the role is decided first by the endpoint's own authorization and this is asked afterwards,
/// so a plan never grants a role more access than it holds.</para>
/// </summary>
public sealed class CurrentPlan
{
    private readonly GetEffectiveEntitlement _entitlement;
    private readonly object _gate = new();

    private Task<EffectiveEntitlement>? _read;

    public CurrentPlan(GetEffectiveEntitlement entitlement) => _entitlement = entitlement;

    /// <summary>
    /// What the request's business may currently use, read once per request.
    ///
    /// <para>The first caller's <paramref name="cancellationToken"/> governs the read every caller
    /// awaits, which inside a request is the request's own token in every case.</para>
    /// </summary>
    public Task<EffectiveEntitlement> CurrentAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return _read ??= _entitlement.Handle(cancellationToken);
        }
    }

    /// <summary>
    /// Whether the request's business includes <paramref name="feature"/>. Always
    /// <see langword="true"/> while nothing is enforced, which is the shipped state.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="feature"/> is not a declared <see cref="PlanFeature"/>; see
    /// <see cref="PlanFeaturePolicy.RequiredPlan"/>.
    /// </exception>
    public async Task<bool> AllowsAsync(PlanFeature feature, CancellationToken cancellationToken) =>
        PlanFeaturePolicy.Allows(
            await CurrentAsync(cancellationToken).ConfigureAwait(false),
            feature);
}
