using Inventory.Domain.Subscriptions;

namespace Inventory.Application.Subscriptions;

/// <summary>
/// "What does each plan cost and include?" (issue #970) - the Application-layer read behind
/// <c>GET /api/me/plan-catalogue</c>, and the one way the HTTP boundary reaches
/// <see cref="PlanCatalogue"/>.
///
/// <para><strong>It takes no identifier and answers nothing about a business.</strong> The
/// catalogue is the same compiled table for every caller, so there is no business, no membership
/// and no entitlement in the question or the answer; what a particular business has bought is
/// <see cref="GetEffectiveEntitlement"/>'s separate answer, published by
/// <c>GET /api/me/access</c>. Nothing here reads a request value, a database row or a
/// configuration setting.</para>
///
/// <para><strong>It exists so the controller does not read the Domain directly.</strong> The read
/// is trivial today - returning <see cref="PlanCatalogue.All"/> - and that is precisely why it is
/// worth stating once in the layer every other read goes through: a thin HTTP action maps a use
/// case's result (AGENTS.md § Architecture rules), and the catalogue gaining a rule later (a
/// currency, a regional price list, a plan withdrawn from sale) then has one place to gain it in
/// rather than a controller to grow into.</para>
/// </summary>
public sealed class GetPlanCatalogue
{
    /// <summary>
    /// Every plan's definition, in the catalogue's own order. Synchronous and allocation-free: the
    /// catalogue is a compiled table, so promising a <see cref="Task"/> here would claim an
    /// asynchrony that does not exist.
    /// </summary>
    public IReadOnlyList<PlanDefinition> Handle() => PlanCatalogue.All;
}
