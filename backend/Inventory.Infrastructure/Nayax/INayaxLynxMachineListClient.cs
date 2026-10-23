using Inventory.Application.Nayax;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The Lynx HTTP client as <see cref="MachineListCachingNayaxLynxClient"/> needs it: every member of
/// the <see cref="INayaxLynxClient"/> port, plus the one machine-list read that authenticates with a
/// credential the caller has <em>already</em> resolved and gated (issue #840).
///
/// <para><strong>Why the extra member exists.</strong> The cache keys a machine list by the business
/// and the credential revision it belongs to, and that claim has to be true of the request that
/// actually fetched it. If the decorator resolved the credential for the key and the client then
/// resolved its own for the request - the provider caches nothing, deliberately - a connection saved
/// between the two would have the new credentials' fleet stored under the old revision's key. So the
/// resolution and the request are one operation: the decorator resolves once, gates once, and hands
/// that credential down.</para>
///
/// <para>It lives in <c>Inventory.Infrastructure</c> because it is about how the Nayax adapter is
/// composed, not about what a use case may ask for: nothing in <c>Inventory.Application</c> knows
/// that a credential revision or a cache key exists.</para>
/// </summary>
public interface INayaxLynxMachineListClient : INayaxLynxClient
{
    /// <summary>
    /// The business's machine list (<c>GET /v1/machines</c>), authenticated with
    /// <paramref name="credential"/> rather than with a credential resolved here.
    ///
    /// <para>The caller owns the refusal: it must have obtained
    /// <paramref name="credential"/> from
    /// <see cref="INayaxRequestCredentialProvider.GetForOperationAsync"/> for this very operation, so
    /// that the stored connection, the status gate and the support-session refusal have already been
    /// applied exactly once. The request, its <c>401</c> report and the cache key then all name the
    /// same credential revision.</para>
    /// </summary>
    Task<List<NayaxMachine>> ReadMachinesAsync(
        NayaxRequestCredential credential, CancellationToken ct = default);
}
