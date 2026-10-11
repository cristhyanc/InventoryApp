using Inventory.Domain.Tenancy;

namespace Inventory.Application.Tenancy;

/// <summary>
/// The caller's identity together with the display-only profile their validated token reported
/// (issue #524). The two travel together so the row that is refreshed and the claims it is
/// refreshed from always come from one principal.
/// </summary>
/// <param name="Identity">The validated <c>(tid, oid)</c> pair - the only thing that identifies.</param>
/// <param name="Profile">What the same token said about the person's name and email address.</param>
public sealed record ActorProfile(ActorIdentity Identity, MemberProfile Profile);

/// <summary>
/// Narrow port for the display-only claims of the current caller (issue #524), separate from
/// <see cref="IAuthenticatedActorAccessor"/> on purpose: that port answers "who is calling", which
/// every access decision depends on, and this one answers "what did their token say their name and
/// email address are", which no access decision may ever depend on.
///
/// Claims parsing stays at the InventoryApi boundary, so the only implementation is there and the
/// Application layer never sees a <c>ClaimsPrincipal</c>.
/// </summary>
public interface IActorProfileAccessor
{
    /// <summary>
    /// The current caller's identity and display-only profile, or <c>null</c> when the request has
    /// no identifiable actor. A caller whose token carries neither display claim is reported with
    /// <see cref="MemberProfile.None"/> rather than as nothing: they are still an identified actor.
    /// </summary>
    ActorProfile? GetCurrentActorProfile();
}
