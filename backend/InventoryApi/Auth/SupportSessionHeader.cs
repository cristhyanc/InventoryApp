namespace InventoryApi.Auth;

/// <summary>
/// The request header that marks a support session (issue #512), defined here by issue #524
/// because #524 is the first thing that has to behave differently when it is present.
///
/// A support session is a person from outside the business looking at the business's data to help
/// with a problem. Issue #550 is what creates one -
/// <c>Inventory.Application.SupportSessions</c>, behind the Super Admin's own control endpoints -
/// and issue #551 is what makes a request <em>join</em> one by presenting its id here.
///
/// <para>The header therefore does two unrelated things, and the difference between them matters.
/// <see cref="RequestSupportSessionAccessor"/> reads its <strong>value</strong>: hashed, and matched
/// against an active, unexpired session of the <em>same</em> platform administrator, for a business
/// that is still active. Nothing less than that resolves a business, and a presented value that
/// matches nothing is a <c>403</c> rather than a fallback to the caller's own membership. The
/// member-profile refresh of issue #524 reads only its <strong>presence</strong>, because the only
/// thing that decision can do is stop a cosmetic write: the member row says who the <em>member</em>
/// is, and a request made on their behalf by somebody else does not.</para>
///
/// <para>A caller the platform-admin policy refuses has the value ignored entirely, so an ordinary
/// member who sets this header is still an ordinary member - and still has their profile refresh
/// skipped, exactly as before.</para>
/// </summary>
public static class SupportSessionHeader
{
    /// <summary>The header name, as the one spelling every reader of it must use.</summary>
    public const string Name = "X-Support-Session";
}
