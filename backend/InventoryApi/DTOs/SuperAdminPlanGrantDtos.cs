using System.Text.Json.Serialization;
using Inventory.Application.PlatformPlans;
using Inventory.Domain.Subscriptions;

namespace InventoryApi.DTOs;

/// <summary>
/// What a Super Admin submits to <c>PUT /api/super-admin/businesses/{businessId}/plan-grant</c>
/// (issue #982): the plan the business is to be granted, the optional last day it applies to, the
/// reason, and the revision the caller read.
///
/// <para><strong>It carries no payment, price, billing status, subscription field or business
/// id.</strong> The business is the route's subject, the plan is granted rather than sold, and the
/// business's paid subscription is not reachable from this endpoint at all - the two records stay
/// apart (#965, decided 29 October 2026).</para>
/// </summary>
/// <param name="Plan">
/// The plan's published name: <c>Starter</c> or <c>Pro</c>. A name rather than a number, for the
/// reason a role is a name - a caller names a plan and never supplies a stored representation - and
/// <c>Free</c>, a name this code does not declare and a numeric string are each refused with
/// <c>400</c>.
/// </param>
/// <param name="EndsOnUtc">
/// The last UTC day the grant applies to, or omitted/<c>null</c> for no recorded end. It is a
/// <em>date</em> because that is what an administrator decides; the server converts it to the
/// exclusive instant a grant is stored with
/// (<c>Inventory.Domain.Subscriptions.ManualPlanGrantPolicy.ExclusiveEndOf</c>), so a grant ending on
/// 31 December covers every moment of it. A date already over is refused with <c>400</c>.
/// </param>
/// <param name="Reason">Why the grant was decided. Required, and recorded.</param>
/// <param name="Revision">
/// The revision the caller read, and <c>0</c> for a business that holds no grant. Marked
/// <see cref="JsonRequiredAttribute"/> so a body that omits it is refused rather than silently
/// treated as <c>0</c> - which would make every omitted revision an attempt to create a first grant
/// and defeat the concurrency check the field exists for.
/// </param>
public sealed record SuperAdminPlanGrantRequest(
    string? Plan,
    DateOnly? EndsOnUtc,
    string? Reason,
    [property: JsonRequired] int Revision);

/// <summary>
/// What a Super Admin submits to
/// <c>POST /api/super-admin/businesses/{businessId}/plan-grant/end</c> (issue #982): why the grant is
/// being ended, and the revision they read. There is no plan and no date - ending a grant stops it at
/// the server's own instant, and a caller never chooses when something already happened.
/// </summary>
/// <param name="Reason">Why it was ended. Required, and recorded on the audit row.</param>
/// <param name="Revision">The revision the caller read, for the reason above.</param>
public sealed record SuperAdminEndPlanGrantRequest(
    string? Reason,
    [property: JsonRequired] int Revision);

/// <summary>
/// One business's manual plan grant as this surface reports it (issue #982).
///
/// <para>It carries the business id for the reason the directory response does - a platform decision
/// names the business it is about - and nothing else about the business: no contact detail, no
/// member, no Nayax value, and nothing from its billing subscription. <c>hasGrant</c> is stated
/// rather than inferred from a null plan, so "this business has never been granted anything" cannot
/// be read as "it was granted Free".</para>
/// </summary>
/// <param name="BusinessId">The business the grant belongs to.</param>
/// <param name="Name">Its display name, so the page needs no second read.</param>
/// <param name="HasGrant">Whether the business holds a manual grant at all.</param>
/// <param name="Plan">
/// The granted plan's published name, or <c>null</c> when there is no grant - or when the stored
/// value is one this code does not declare, which is reported as unknown rather than guessed at.
/// </param>
/// <param name="StartsAtUtc">The instant the grant applies from, inclusive, or <c>null</c>.</param>
/// <param name="EndsAtUtc">
/// The instant it stops at, <strong>exclusive</strong>, or <c>null</c> for an open-ended grant. It is
/// the same value <c>GET /api/me/access</c> reports to the business's own members.
/// </param>
/// <param name="EndsOnUtc">
/// The last UTC day it applies to - the same stored instant read back as the date it was chosen as,
/// by the one Domain rule that converts between them, so the page never does that arithmetic itself.
/// </param>
/// <param name="Reason">Why it was granted, as the administrator typed it.</param>
/// <param name="Revision">
/// The revision to present on the next write. A client that holds an older one is refused with
/// <c>409</c>.
/// </param>
/// <param name="GrantedAtUtc">When the grant was last written.</param>
/// <param name="Active">
/// Whether it entitles anything right now - at or after its start and strictly before its end. An
/// ended grant is reported as inactive and is still reported, because the trail of what was granted
/// is the point of keeping the row.
/// </param>
public sealed record SuperAdminPlanGrantResponse(
    int BusinessId,
    string Name,
    bool HasGrant,
    string? Plan,
    DateTime? StartsAtUtc,
    DateTime? EndsAtUtc,
    DateOnly? EndsOnUtc,
    string? Reason,
    int Revision,
    DateTime? GrantedAtUtc,
    bool Active)
{
    /// <summary>
    /// Projects the use case's answer. Every judgement on it - which plan, which instants, and
    /// whether the grant currently applies - was made by the use case and the Domain rules behind it;
    /// this mapper only renames and formats.
    /// </summary>
    public static SuperAdminPlanGrantResponse From(BusinessPlanGrantView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        if (view.Grant is not { } grant)
        {
            return new SuperAdminPlanGrantResponse(
                view.BusinessId,
                view.Name,
                HasGrant: false,
                Plan: null,
                StartsAtUtc: null,
                EndsAtUtc: null,
                EndsOnUtc: null,
                Reason: null,
                Revision: 0,
                GrantedAtUtc: null,
                Active: false);
        }

        return new SuperAdminPlanGrantResponse(
            view.BusinessId,
            view.Name,
            HasGrant: true,
            // A stored value this code does not declare is reported as unknown rather than as the
            // nearest plan - the same restraint the members list shows about a role nobody granted.
            SubscriptionPlans.IsDeclared(grant.Plan) ? grant.Plan.ToString() : null,
            grant.Period.StartsAtUtc,
            grant.Period.EndsAtUtc,
            grant.Period.EndsAtUtc is { } endsAtUtc
                ? ManualPlanGrantPolicy.InclusiveLastDayOf(endsAtUtc)
                : null,
            grant.Reason,
            grant.Revision,
            grant.GrantedAtUtc,
            view.Active);
    }
}
