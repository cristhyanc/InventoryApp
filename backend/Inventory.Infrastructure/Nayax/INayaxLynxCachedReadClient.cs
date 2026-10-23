using Inventory.Application.Nayax;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The Lynx HTTP client as <see cref="CachingNayaxLynxClient"/> needs it: every member of the
/// <see cref="INayaxLynxClient"/> port, plus the three machine-list and machine-product calls that
/// authenticate with a credential the caller has <em>already</em> resolved and gated (issues #840
/// and #842).
///
/// <para><strong>Why the extra members exist.</strong> The caches key an answer by the business and
/// the credential revision it belongs to, and that claim has to be true of the request that actually
/// fetched it. If the decorator resolved the credential for the key and the client then resolved its
/// own for the request - the provider caches nothing, deliberately - a connection saved between the
/// two would have the new credentials' data stored under the old revision's key. So the resolution
/// and the request are one operation: the decorator resolves once, gates once, and hands that
/// credential down. It is also what keeps the number of connection reads per Nayax call at exactly
/// one, which <c>NayaxParallelCredentialAccessTests</c> pins.</para>
///
/// <para>The machine-product <em>write</em> is here for the same reason, even though nothing is
/// cached on the way out: the entry it invalidates has to be the one belonging to the credentials the
/// write was really made with (issue #842).</para>
///
/// <para>These members live in <c>Inventory.Infrastructure</c> because they are about how the Nayax
/// adapter is composed, not about what a use case may ask for: nothing in
/// <c>Inventory.Application</c> knows that a credential revision or a cache key exists.</para>
/// </summary>
public interface INayaxLynxCachedReadClient : INayaxLynxClient
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

    /// <summary>
    /// One machine's current selections (<c>GET /v1/machines/{MachineID}/machineProducts</c>),
    /// authenticated with <paramref name="credential"/> rather than with a credential resolved here.
    /// The caller owns the refusal, exactly as for <see cref="ReadMachinesAsync"/>.
    /// </summary>
    Task<List<NayaxMachineProduct>> ReadMachineProductsAsync(
        long machineId, NayaxRequestCredential credential, CancellationToken ct = default);

    /// <summary>
    /// Creates selections on one machine (<c>POST /v1/machines/{MachineID}/machineProducts</c>),
    /// authenticated with <paramref name="credential"/> rather than with a credential resolved here.
    /// The caller owns the refusal, exactly as for <see cref="ReadMachinesAsync"/>, and owns
    /// forgetting that machine's cached selections once the write has succeeded.
    /// </summary>
    Task<List<NayaxMachineProduct>> WriteMachineProductsAsync(
        long machineId,
        List<NayaxMachineProduct> products,
        NayaxRequestCredential credential,
        CancellationToken ct = default);
}
