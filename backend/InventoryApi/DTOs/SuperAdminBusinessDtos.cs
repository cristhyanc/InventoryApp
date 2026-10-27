using Inventory.Application.PlatformAdmin;
using Inventory.Domain.Tenancy;

namespace InventoryApi.DTOs;

/// <summary>
/// One business as <c>GET /api/super-admin/businesses</c> reports it (issue #544).
///
/// <para>These eleven fields are the whole contract, and what is missing from it is the point. There is
/// no sale, stock, cost, purchase, product, machine, site or report value - nothing tenant-owned at
/// all - and no member's display name, email address or Entra identifier. The Super Admin decides
/// whether a business may trade; this response is the whole of what that decision needs.</para>
///
/// <para><see cref="BusinessId"/> is reported, which no tenant-facing DTO in this application does
/// (AGENTS.md § Tenant ownership). Here it is the subject of the decision rather than a tenancy
/// scope: it is what <c>POST .../{businessId}/approve</c> names, it resolves no business for any
/// request, and it is never stamped on a row.</para>
/// </summary>
/// <param name="BusinessId">The business's own key, which the approve and reject calls name.</param>
/// <param name="Name">Its display name, as stored.</param>
/// <param name="Status">
/// The published <c>Inventory.Domain.Tenancy.BusinessStatus</c> name, rather than a number a client
/// would have to map and a renumbering would silently change.
/// </param>
/// <param name="CreatedAtUtc">When the business row was created.</param>
/// <param name="AppliedAtUtc">
/// When it applied, or <c>null</c> for a business that never went through an application.
/// </param>
/// <param name="DecidedAtUtc">When its application was decided, or <c>null</c> while it was not.</param>
/// <param name="ContactEmail">
/// The business's own contact address as its application recorded it, or <c>null</c>. It is the
/// business's published detail, not a member's personal address.
/// </param>
/// <param name="Abn">Its stored 11-digit ABN, or <c>null</c>.</param>
/// <param name="State">
/// The Australian state or territory it recorded - one of <c>NSW</c>, <c>VIC</c>, <c>QLD</c>,
/// <c>SA</c>, <c>WA</c>, <c>TAS</c>, <c>ACT</c>, <c>NT</c> - or <c>null</c> for a business that has
/// not recorded one (issue #933). Shown when an application is reviewed.
/// </param>
/// <param name="ActiveMemberCount">
/// How many of its memberships are currently active - how many people can reach it at all. A
/// rejected application reads <c>0</c>, because the rejection released its applicant.
/// </param>
/// <param name="NayaxConfigured">
/// Whether the business has stored Nayax credentials at all. A yes/no and nothing more: never an
/// operator id, a connection status or any part of a token.
/// </param>
public sealed record SuperAdminBusinessResponse(
    int BusinessId,
    string Name,
    string Status,
    DateTime CreatedAtUtc,
    DateTime? AppliedAtUtc,
    DateTime? DecidedAtUtc,
    string? ContactEmail,
    string? Abn,
    string? State,
    int ActiveMemberCount,
    bool NayaxConfigured)
{
    public static SuperAdminBusinessResponse From(PlatformBusinessSummary business)
    {
        ArgumentNullException.ThrowIfNull(business);

        return new SuperAdminBusinessResponse(
            business.BusinessId,
            business.Name,
            business.Status.ToString(),
            business.CreatedAtUtc,
            business.AppliedAtUtc,
            business.DecidedAtUtc,
            business.ContactEmail,
            business.Abn,
            business.State,
            business.ActiveMemberCount,
            business.NayaxConfigured);
    }
}

/// <summary>
/// How many business applications are waiting for a decision (issue #544). One number, so the
/// badge a page shows cannot be mistaken for a list it could read instead.
/// </summary>
public sealed record SuperAdminPendingCountResponse(int PendingCount);

/// <summary>
/// The optional body of <c>POST /api/super-admin/businesses/{businessId}/reject</c> (issue #544).
///
/// The reason is the only thing a caller may send, and it is optional: a rejection with no reason is
/// still a rejection. Nothing here names a status, an instant, an applicant or a membership - the
/// server decides all four.
/// </summary>
/// <param name="Reason">
/// The operator's reason, at most 500 characters once trimmed. A blank value is stored as "no
/// reason", which has exactly one representation. The applicant reads it back through
/// <c>GET /api/me/account-state</c>, and nobody else does.
/// </param>
public sealed record SuperAdminRejectBusinessRequest(string? Reason);

/// <summary>
/// A recorded decision, as the approve and reject endpoints answer (issue #544).
/// </summary>
/// <param name="BusinessId">The business that was decided.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The published status name it now holds - <c>Active</c> or <c>Rejected</c>.</param>
/// <param name="DecidedAtUtc">The instant the decision was recorded at.</param>
/// <param name="RejectionReason">
/// The stored reason, or <c>null</c> for an approval and for a rejection recorded without one.
/// </param>
public sealed record SuperAdminBusinessDecisionResponse(
    int BusinessId,
    string Name,
    string Status,
    DateTime DecidedAtUtc,
    string? RejectionReason)
{
    public static SuperAdminBusinessDecisionResponse From(PlatformBusinessDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);

        return new SuperAdminBusinessDecisionResponse(
            decision.BusinessId,
            decision.Name,
            decision.Status.ToString(),
            decision.DecidedAtUtc,
            decision.RejectionReason);
    }
}

/// <summary>
/// A recorded deactivation, as <c>POST /api/super-admin/businesses/{businessId}/deactivate</c>
/// answers (issue #545).
/// </summary>
/// <param name="BusinessId">The business that was switched off.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The published status name it now holds - always <c>Deactivated</c>.</param>
/// <param name="InvitesCancelled">
/// How many of its invite links were still live and have now been cancelled permanently. It is
/// reported because it is the one part of a deactivation that the business row does not show
/// afterwards, and "no outstanding link survived this" is a fact an operator needs to be able to
/// check. Already-used, already-cancelled and expired invites are not counted: they were not
/// cancelled by this action.
/// </param>
public sealed record SuperAdminBusinessDeactivationResponse(
    int BusinessId,
    string Name,
    string Status,
    int InvitesCancelled)
{
    public static SuperAdminBusinessDeactivationResponse From(PlatformBusinessDeactivation deactivation)
    {
        ArgumentNullException.ThrowIfNull(deactivation);

        return new SuperAdminBusinessDeactivationResponse(
            deactivation.BusinessId,
            deactivation.Name,
            deactivation.Status.ToString(),
            deactivation.InvitesCancelled);
    }
}

/// <summary>
/// A recorded reactivation, as <c>POST /api/super-admin/businesses/{businessId}/reactivate</c>
/// answers (issue #545). It carries no invite count and no membership, because a reactivation
/// restores the business and changes nothing else about it.
/// </summary>
/// <param name="BusinessId">The business that was switched back on.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Status">The published status name it now holds - always <c>Active</c>.</param>
public sealed record SuperAdminBusinessReactivationResponse(int BusinessId, string Name, string Status)
{
    public static SuperAdminBusinessReactivationResponse From(PlatformBusinessReactivation reactivation)
    {
        ArgumentNullException.ThrowIfNull(reactivation);

        return new SuperAdminBusinessReactivationResponse(
            reactivation.BusinessId,
            reactivation.Name,
            reactivation.Status.ToString());
    }
}

/// <summary>
/// One membership of a deactivated business, as <c>GET .../{businessId}/members</c> and the release
/// beside it report it (issue #545).
///
/// <para>These four fields are the whole contract, and what is missing from it is the point: no
/// email address, no Entra <c>(tid, oid)</c> pair, and nothing tenant-owned. The Super Admin is here
/// to free somebody from a business nobody can reach; they are not thereby a reader of that
/// business's people. <see cref="DisplayName"/> is present because choosing which membership to
/// release is impossible from a column of keys alone, and it is the same display-only value an
/// Owner's own members list reports (issue #524) - never an identifier.</para>
/// </summary>
/// <param name="MembershipId">The membership's own key, which the release endpoint names.</param>
/// <param name="DisplayName">
/// The member's display name as their own sign-in token last reported it, or <c>null</c> when no
/// token has reported one.
/// </param>
/// <param name="Role">
/// The published <c>Inventory.Domain.Tenancy.BusinessRole</c> name, or <c>null</c> when the stored
/// value is one this code does not declare - reported as unknown rather than guessed at, because
/// naming a role nobody granted is how somebody comes to believe a person holds access they do not.
/// </param>
/// <param name="IsActive">
/// Whether the membership is current. A released or revoked membership stays on the list: it is the
/// record of who used to be able to reach the business.
/// </param>
public sealed record SuperAdminBusinessMemberResponse(
    int MembershipId,
    string? DisplayName,
    string? Role,
    bool IsActive)
{
    public static SuperAdminBusinessMemberResponse From(PlatformBusinessMember member)
    {
        ArgumentNullException.ThrowIfNull(member);

        return new SuperAdminBusinessMemberResponse(
            member.MembershipId,
            member.DisplayName,
            BusinessRoles.IsSupported(member.StoredRole) ? member.StoredRole.ToString() : null,
            member.IsActive);
    }
}
