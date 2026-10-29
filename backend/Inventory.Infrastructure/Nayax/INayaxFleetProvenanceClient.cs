using Inventory.Application.Nayax;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// One fresh whole-fleet read, together with the Nayax account that authenticated it (issue #978).
/// </summary>
/// <param name="Machines">The fleet exactly as the Lynx call answered it.</param>
/// <param name="AccountSeries">
/// The account series of the credential snapshot this request was sent with - the business's own
/// internal series, never the operator id, a token or any part of a credential - or
/// <see langword="null"/> when the client cannot say which account it authenticated as.
/// </param>
public sealed record NayaxFleetReadResult(List<NayaxMachine> Machines, int? AccountSeries);

/// <summary>
/// The Lynx port plus the one read that reports <em>which Nayax account</em> answered it (issue
/// #978).
///
/// <para><strong>Why this exists.</strong> The connected-machine snapshot is billing evidence about
/// one account on one UTC day, so it has to be stamped with the account whose credentials
/// authenticated the fleet request it came from. Reading the connection again after the request - or
/// before it, on the job's path - answers a different question: an Owner can save a different Nayax
/// account while a fleet request is in flight, and a series read on either side of that save
/// describes the account the fleet was not read from. The only moment at which the two facts are the
/// same fact is the moment the credential is resolved, so the resolution and the request are one
/// operation and the series travels out with the answer.</para>
///
/// <para>It is the same reasoning, and the same shape, as
/// <see cref="INayaxLynxCachedReadClient"/>: there, one credential resolution has to decide both the
/// cache key and the request; here, it has to decide both the request and the provenance of what the
/// request returned. Neither adds a connection read - each implementation resolves exactly one
/// credential per call, which is what <c>NayaxParallelCredentialAccessTests</c> pins.</para>
///
/// <para>It lives in <c>Inventory.Infrastructure</c> because it is about how the Nayax adapter is
/// composed rather than about what a use case may ask for: nothing in <c>Inventory.Application</c>
/// knows that a credential revision, a cache key or this member exists. What reaches the Application
/// layer is <see cref="Domain.ConnectedMachines.ConnectedMachineFleetRead"/>, which carries the
/// series and no credential at all.</para>
/// </summary>
public interface INayaxFleetProvenanceClient : INayaxLynxClient
{
    /// <summary>
    /// The business's whole fleet, read now (never from the machine-list cache), with the account
    /// series of the credentials the request was authenticated with.
    ///
    /// <para>Every refusal is unchanged and happens in exactly the same place: the stored
    /// connection, the status gate, the plan requirement and the support-session rule all belong to
    /// <see cref="INayaxRequestCredentialProvider"/>, which each implementation resolves once per
    /// call exactly as <see cref="INayaxLynxClient.GetMachinesFreshAsync"/> does.</para>
    /// </summary>
    Task<NayaxFleetReadResult> ReadFleetFreshAsync(CancellationToken ct = default);
}
