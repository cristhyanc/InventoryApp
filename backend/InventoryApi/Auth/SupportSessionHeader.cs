namespace InventoryApi.Auth;

/// <summary>
/// The request header that marks a support session (issue #512), defined here by issue #524
/// because #524 is the first thing that has to behave differently when it is present.
///
/// A support session is a person from outside the business looking at the business's data to help
/// with a problem. Issue #550 is what creates one -
/// <c>Inventory.Application.SupportSessions</c>, behind the Super Admin's own control endpoints -
/// but <em>nothing validates this header yet</em>: no request resolves a business from a session,
/// which is issue #551's work. So today the header still does exactly one thing: it suppresses the
/// member-profile refresh, so a support session cannot overwrite a member's stored display name and
/// email address with the supporter's own claims. That is a correctness rule, not a convenience: the
/// row says who the <em>member</em> is, and a request made on their behalf by somebody else does
/// not.
///
/// <para>Its presence alone is what suppresses the refresh - the value is not read and not
/// validated. That is the fail-safe direction for this one decision: the header can only ever stop
/// a cosmetic write, so a caller who sets it gains nothing, and nothing else in the application
/// reads it. When issue #551 makes a request <em>join</em> a session by presenting this value, the
/// authorisation goes there - the session id hashed and matched against an active server-side
/// session for that same platform administrator - and the header name stays here as the one spelling
/// both sides use.</para>
/// </summary>
public static class SupportSessionHeader
{
    /// <summary>The header name, as the one spelling every reader of it must use.</summary>
    public const string Name = "X-Support-Session";
}
