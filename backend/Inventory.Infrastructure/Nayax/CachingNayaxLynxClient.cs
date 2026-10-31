using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The Nayax Lynx port as the composition root registers it: <see cref="NayaxLynxClient"/> with the
/// machine list of <see cref="NayaxMachineListCache"/> (issue #840, step 1 of #839) and each
/// machine's selections of <see cref="NayaxMachineProductsCache"/> (issue #842, step 2) in front of
/// it.
///
/// <para><strong>Two reads are cached and every other one is not.</strong>
/// <see cref="GetMachinesAsync"/> answers from memory for <see cref="NayaxMachineListCache.Lifetime"/>
/// per business and credential revision, and <see cref="GetMachineProductsAsync"/> for
/// <see cref="NayaxMachineProductsCache.Lifetime"/> per business, credential revision and machine;
/// <see cref="GetMachinesFreshAsync"/> and <see cref="GetMachineProductsFreshAsync"/> always read
/// Nayax and replace what is held; everything else - stock, last sales, alerts, one machine, the
/// product catalogue, the product groups and the devices - passes straight through unchanged. Those
/// two are the reads that repeat: one Dashboard visit used to spend five requests on the fleet list
/// and three per machine on the selections, several of them at the same moment.</para>
///
/// <para><strong>A machine-product write forgets that machine's entry.</strong>
/// <see cref="CreateMachineProductsAsync"/> passes the write through and then invalidates the key for
/// the machine it changed, so the next cached caller reads the selections as they now are rather than
/// as they were up to a minute before they were rewritten.</para>
///
/// <para><strong>Every refusal still happens, before every answer.</strong> The connection, the
/// status gate and the support-session refusal are resolved through
/// <see cref="INayaxRequestCredentialProvider.GetForOperationAsync"/> first, exactly as
/// <see cref="NayaxLynxClient"/> does before a request leaves the API - so a business whose
/// connection is now <c>NeedsAttention</c> or disconnected, and a platform administrator reading in
/// a support session, get today's refusal even while an answer is held for that key. A cache that
/// answered before that check would be a way to read a business's Nayax data after its connection
/// stopped allowing it.</para>
///
/// <para><strong>The keys are the business, the credential revision and - for the selections - the
/// machine</strong> (see <see cref="NayaxMachineListCacheKey"/> and
/// <see cref="NayaxMachineProductsCacheKey"/>), all taken from the server's own side of the request -
/// the resolved <see cref="IBusinessScope"/>, the credential snapshot that was just gated, and the
/// machine the caller is reading - never anything a caller passed in as an owner. A scope with no
/// business id at all caches nothing and reads through, which is the fail-closed answer rather than
/// a shared key.</para>
///
/// <para><strong>The key names the credentials the request actually carried.</strong> One credential
/// is resolved per cached call and that same credential is what the upstream request is sent with
/// (<see cref="INayaxLynxCachedReadClient"/>). Resolving once for the key and letting the client
/// resolve again for the request would leave a window - the provider caches nothing, on purpose - in
/// which a connection saved in between put the new credentials' data under the old revision's key,
/// and it would double the connection reads each Nayax call makes.</para>
///
/// <para><strong>The connection test is not here, by construction.</strong>
/// <see cref="NayaxConnectionCheckRunner"/> builds a <see cref="NayaxLynxClient"/> itself over the
/// candidate credentials an Owner pasted, so it never resolves this decorator and can neither read
/// nor write either cache. That matters: the pair being tested may belong to a different Nayax
/// account entirely, and neither its fleet nor its machines' selections are the business's.</para>
/// </summary>
public sealed class CachingNayaxLynxClient : INayaxFleetProvenanceClient
{
    private readonly INayaxLynxCachedReadClient _inner;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly IBusinessScope _businessScope;
    private readonly NayaxMachineListCache _machineLists;
    private readonly NayaxMachineProductsCache _machineProducts;

    public CachingNayaxLynxClient(
        INayaxLynxCachedReadClient inner,
        INayaxRequestCredentialProvider credentials,
        IBusinessScope businessScope,
        NayaxMachineListCache machineLists,
        NayaxMachineProductsCache machineProducts)
    {
        _inner = inner;
        _credentials = credentials;
        _businessScope = businessScope;
        _machineLists = machineLists;
        _machineProducts = machineProducts;
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
    {
        var (businessId, credential) = await ResolveAsync(ct).ConfigureAwait(false);

        return businessId is { } business
            ? await _machineLists
                .GetOrReadAsync(
                    new NayaxMachineListCacheKey(business, credential.CredentialRevision),
                    readCt => _inner.ReadMachinesAsync(credential, readCt),
                    ct)
                .ConfigureAwait(false)
            : await _inner.ReadMachinesAsync(credential, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default) =>
        (await ReadFleetFreshAsync(ct).ConfigureAwait(false)).Machines;

    /// <inheritdoc />
    public async Task<NayaxFleetReadResult> ReadFleetFreshAsync(CancellationToken ct = default)
    {
        var (businessId, credential) = await ResolveAsync(ct).ConfigureAwait(false);

        var machines = businessId is { } business
            ? await _machineLists
                .ReadFreshAsync(
                    new NayaxMachineListCacheKey(business, credential.CredentialRevision),
                    readCt => _inner.ReadMachinesAsync(credential, readCt),
                    ct)
                .ConfigureAwait(false)
            : await _inner.ReadMachinesAsync(credential, ct).ConfigureAwait(false);

        // The series of the one credential this call resolved, gated and sent the request with
        // (issue #978). A caller that joined another caller's in-flight read shares that caller's
        // credential and therefore its revision, and the series moves only when the revision does,
        // so the key it joined on already names the same account.
        return new NayaxFleetReadResult(machines, credential.AccountSeries);
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachineProduct>> GetMachineProductsAsync(
        long machineId, CancellationToken ct = default)
    {
        var (businessId, credential) = await ResolveAsync(ct).ConfigureAwait(false);

        return businessId is { } business
            ? await _machineProducts
                .GetOrReadAsync(
                    MachineProductsKey(business, credential, machineId),
                    readCt => _inner.ReadMachineProductsAsync(machineId, credential, readCt),
                    ct)
                .ConfigureAwait(false)
            : await _inner.ReadMachineProductsAsync(machineId, credential, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachineProduct>> GetMachineProductsFreshAsync(
        long machineId, CancellationToken ct = default)
    {
        var (businessId, credential) = await ResolveAsync(ct).ConfigureAwait(false);

        return businessId is { } business
            ? await _machineProducts
                .ReadFreshAsync(
                    MachineProductsKey(business, credential, machineId),
                    readCt => _inner.ReadMachineProductsAsync(machineId, credential, readCt),
                    ct)
                .ConfigureAwait(false)
            : await _inner.ReadMachineProductsAsync(machineId, credential, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default)
    {
        var (businessId, credential) = await ResolveAsync(ct).ConfigureAwait(false);

        var created = await _inner
            .WriteMachineProductsAsync(machineId, products, credential, ct)
            .ConfigureAwait(false);

        // After the write and only after it succeeded: a failed write changed nothing in Nayax, so
        // forgetting what is held would cost a request and buy nothing, and a write that threw must
        // leave the rest of the request behaving exactly as it does today. The key names the
        // credentials this write was really made with, which is the whole reason the credential is
        // resolved here and handed down.
        if (businessId is { } business)
        {
            _machineProducts.Invalidate(MachineProductsKey(business, credential, machineId));
        }

        return created;
    }

    /// <inheritdoc />
    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        _inner.GetDevicesAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) =>
        _inner.GetProductsAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) =>
        _inner.GetProductGroupssAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) =>
        _inner.GetMachineLastSalesAsync(machineId, ct);

    /// <inheritdoc />
    public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) =>
        _inner.GetMachineAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineAlert>> GetMachineLastAlertsAsync(long machineId, CancellationToken ct = default) =>
        _inner.GetMachineLastAlertsAsync(machineId, ct);

    private static NayaxMachineProductsCacheKey MachineProductsKey(
        int businessId, NayaxRequestCredential credential, long machineId) =>
        new(businessId, credential.CredentialRevision, machineId);

    // The gate first, the key second, and in that order on every cached call: the refusal is what
    // decides whether this caller may have an answer at all, and the credential revision it returns
    // is what decides which answer belongs to it.
    //
    // The credential is returned with the business id because it is also what the upstream request is
    // sent with when this call turns out to need one. One resolution, one gate, one revision - for
    // the key, for the request and for the 401 that request may report. A caller that joins a read
    // somebody else started shares that caller's credential, which carries the same revision by
    // construction: the key it joined on names it.
    private async Task<(int? BusinessId, NayaxRequestCredential Credential)> ResolveAsync(
        CancellationToken ct)
    {
        var credential = await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);

        return (_businessScope.BusinessId, credential);
    }
}
