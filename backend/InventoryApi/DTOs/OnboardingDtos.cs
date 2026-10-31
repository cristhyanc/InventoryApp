using Inventory.Application.Onboarding;

namespace InventoryApi.DTOs;

/// <summary>
/// The business application a person submits to <c>POST /api/onboarding/business-applications</c>
/// (issue #541).
///
/// <para>These six fields are the whole contract, and they are the same six an Owner can later
/// edit through <c>PUT /api/business/settings</c>, under the same rules. The request deliberately
/// carries <strong>no</strong> business identifier, no status, no role and no applicant: which
/// business this becomes, that it starts as <c>Pending</c>, that the applicant becomes its Owner,
/// and who the applicant is are all decided by the server - the last from the validated
/// <c>(tid, oid)</c> claim pair of the token the request authenticated with.</para>
///
/// <para>Every member is nullable so that "not sent" is distinguishable from "sent empty". Both are
/// refused with <c>400</c> rather than defaulted: a <see cref="GstRegistered"/> that bound to
/// <see langword="false"/> because a form field was missing would record a tax fact nobody
/// stated.</para>
/// </summary>
/// <param name="Name">The business's display name. Required, at most 200 characters.</param>
/// <param name="ContactEmail">
/// The business's own contact address. Required, and checked for format only - nothing here claims
/// the mailbox exists or belongs to this business.
/// </param>
/// <param name="Abn">
/// The Australian Business Number, which may be typed with spaces. Optional - <see langword="null"/>
/// is "not recorded", and is its only representation - and checksum-validated whenever it is
/// present.
/// </param>
/// <param name="GstRegistered">
/// Whether the business is registered for GST. Required, and never inferred from an
/// <paramref name="Abn"/>, which an unregistered entity may legitimately hold.
/// </param>
/// <param name="TimeZoneId">
/// The IANA time zone the business's days are derived in, for example <c>Australia/Sydney</c>.
/// Required, because a business with no zone has no derivable business dates at all.
/// </param>
/// <param name="State">
/// The Australian state or territory the business trades in (issue #933). Required, and one of
/// <c>NSW</c>, <c>VIC</c>, <c>QLD</c>, <c>SA</c>, <c>WA</c>, <c>TAS</c>, <c>ACT</c> or <c>NT</c>:
/// a supplier that serves only some states has to be offered to the right businesses, and
/// <paramref name="TimeZoneId"/> cannot answer that question - <c>Australia/Sydney</c> is both New
/// South Wales and the Australian Capital Territory.
/// </param>
public sealed record BusinessApplicationRequest(
    string? Name,
    string? ContactEmail,
    string? Abn,
    bool? GstRegistered,
    string? TimeZoneId,
    string? State);

/// <summary>
/// The application as it now stands, as that endpoint returns it (issue #541).
///
/// <para>These three fields are the whole contract, and what is missing from it is deliberate. The
/// response carries <strong>no business identifier</strong>: an application and the business it
/// becomes are one record, so its id is the tenant key, and no DTO here carries one (AGENTS.md
/// § Tenant ownership) - the invite redemption's response, the account state and the business
/// settings all leave it out for the same reason. It carries no membership key and no role either:
/// the applicant is the Owner of what they applied for, and that is not news.</para>
///
/// <para><see cref="AppliedAtUtc"/> is what makes a resubmission visibly the <em>same</em>
/// application rather than a new one: submitting the identical form again answers with the instant
/// the first submission was recorded at, unchanged. The applicant's own
/// <c>GET /api/me/account-state</c> is where they then read <c>ApplicationPending</c>, and that -
/// not this response - is what the waiting screen is driven by.</para>
/// </summary>
/// <param name="Name">The business's display name, as it was stored.</param>
/// <param name="Status">
/// The published <c>Inventory.Domain.Tenancy.BusinessStatus</c> name, which is <c>Pending</c> for a
/// newly recorded application, rather than a number a client would have to map and a renumbering
/// would silently change.
/// </param>
/// <param name="AppliedAtUtc">
/// When the application was submitted. Nullable because the field reports the stored instant rather
/// than inventing one: a business record that predates the application form has none (issue #540).
/// </param>
public sealed record BusinessApplicationResponse(string Name, string Status, DateTime? AppliedAtUtc)
{
    public static BusinessApplicationResponse From(BusinessApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);

        return new BusinessApplicationResponse(
            application.Settings.Name,
            application.Status.ToString(),
            application.AppliedAtUtc);
    }
}
