using Inventory.Domain.Tenancy;

namespace Inventory.Application.Access;

/// <summary>
/// What state the signed-in person's account is in (issue #523), as
/// <c>GET /api/me/account-state</c> reports it.
///
/// It is the complete answer: there is deliberately nothing else in it. A caller in this situation
/// has not earned any detail about a business - not its name, not its id, not whether it exists -
/// and the whole point of the endpoint is to say which screen to show without leaking that.
/// </summary>
/// <param name="State">The caller's own account state, from <see cref="AccountStatePolicy"/>.</param>
/// <param name="OnboardingEnabled">
/// Whether a person with no membership may apply for a business themselves. Always
/// <see langword="false"/> until the application endpoints of issue #541 add the
/// <c>SelfServiceOnboarding:Enabled</c> flag this will report.
/// </param>
/// <param name="RejectionReason">
/// Why a business application was rejected, and <see langword="null"/> for every other state
/// (issue #540). It comes from the business's stored reason through
/// <see cref="AccountStateDecision"/>, together with the state it belongs to, so a reason is never
/// reported beside a state it does not describe.
/// </param>
public sealed record AccountStateResult(
    AccountState State,
    bool OnboardingEnabled,
    string? RejectionReason);
