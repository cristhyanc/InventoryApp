using Inventory.Domain.Legal;
using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What state the signed-in person's account is in (issue #523), as
/// <c>GET /api/me/account-state</c> reports it.
///
/// It is the complete answer: there is deliberately nothing else in it. A caller in this situation
/// has not earned any detail about a business - not its name, not its id, not whether it exists -
/// and the whole point of the endpoint is to say which screen to show without leaking that. What
/// issue #1259 added is about the <em>deployment</em> rather than the caller, like
/// <see cref="OnboardingEnabled"/> beside it.
/// </summary>
/// <param name="State">The caller's own account state, from <see cref="AccountStatePolicy"/>.</param>
/// <param name="OnboardingEnabled">
/// Whether this deployment lets a person apply for a business themselves: the
/// <c>SelfServiceOnboarding:Enabled</c> flag, read through
/// <see cref="Onboarding.ISelfServiceOnboardingFlag"/> (issue #541), which is the same value
/// <c>POST /api/onboarding/business-applications</c> itself reads. It describes the deployment and
/// never the caller: whether <em>this</em> account may apply is decided by
/// <see cref="Onboarding.ApplyForBusiness"/> from their own membership records, so a member or a
/// pending applicant sees <see langword="true"/> here too while the endpoint still refuses them.
/// </param>
/// <param name="RejectionReason">
/// Why a business application was rejected, and <see langword="null"/> for every other state
/// (issue #540). It comes from the business's stored reason through
/// <see cref="AccountStateDecision"/>, together with the state it belongs to, so a reason is never
/// reported beside a state it does not describe.
/// </param>
/// <param name="LegalVersion">
/// The version of the published Terms of Use and Privacy Policy that is in force (issue #1259),
/// straight from <see cref="LegalTerms.CurrentVersion"/>. It is reported here, on the one endpoint
/// every signed-in person can call whether or not they belong to a business, because the two pages
/// that have to collect an agreement - the business application and the invite acceptance - are
/// reached by exactly the callers who have no other endpoint. The frontend sends back what it read
/// here, so the version it shows and the version the server accepts cannot drift; a page that read
/// an older value is refused with <see cref="LegalTerms.StaleVersionMessage"/> rather than silently
/// recording an agreement to the wrong documents.
/// </param>
public sealed record AccountStateResult(
    AccountState State,
    bool OnboardingEnabled,
    string? RejectionReason,
    string LegalVersion);
