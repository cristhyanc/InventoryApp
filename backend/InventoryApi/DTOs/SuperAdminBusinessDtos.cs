using Inventory.Application.PlatformAdmin;

namespace InventoryApi.DTOs;

/// <summary>
/// One business as <c>GET /api/super-admin/businesses</c> reports it (issue #544).
///
/// <para>These ten fields are the whole contract, and what is missing from it is the point. There is
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
