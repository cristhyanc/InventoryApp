using Inventory.Application.Nayax;
using Inventory.Application.Tenancy;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The Nayax Lynx port as the composition root registers it: <see cref="NayaxLynxClient"/> with the
/// machine list of <see cref="NayaxMachineListCache"/> in front of it (issue #840, step 1 of #839).
///
/// <para><strong>One method is cached and every other one is not.</strong>
/// <see cref="GetMachinesAsync"/> answers from memory for <see cref="NayaxMachineListCache.Lifetime"/>
/// per business and credential revision; <see cref="GetMachinesFreshAsync"/> always reads Nayax and
/// replaces what is held; everything else - machine products, stock, last sales, alerts, one machine,
/// the product catalogue, the product groups, the devices and the machine-product write - passes
/// straight through unchanged. The machine list is the only read that repeats this much and changes
/// this rarely: one Dashboard visit used to spend five requests on it, several of them at the same
/// moment.</para>
///
/// <para><strong>Every refusal still happens, before every answer.</strong> The connection, the
/// status gate and the support-session refusal are resolved through
/// <see cref="INayaxRequestCredentialProvider.GetForOperationAsync"/> first, exactly as
/// <see cref="NayaxLynxClient"/> does before a request leaves the API - so a business whose
/// connection is now <c>NeedsAttention</c> or disconnected, and a platform administrator reading in
/// a support session, get today's refusal even while a list is held for that key. A cache that
/// answered before that check would be a way to read a business's Nayax data after its connection
/// stopped allowing it.</para>
///
/// <para><strong>The key is the business and the credential revision</strong> (see
/// <see cref="NayaxMachineListCacheKey"/>), both taken from the server's own side of the request -
/// the resolved <see cref="IBusinessScope"/> and the credential snapshot that was just gated, never
/// anything a caller passed in. A scope with no business id at all caches nothing and reads through,
/// which is the fail-closed answer rather than a shared key.</para>
///
/// <para><strong>The connection test is not here, by construction.</strong>
/// <see cref="NayaxConnectionCheckRunner"/> builds a <see cref="NayaxLynxClient"/> itself over the
/// candidate credentials an Owner pasted, so it never resolves this decorator and can neither read
/// nor write the cache. That matters: the pair being tested may belong to a different Nayax account
/// entirely, and its fleet is not the business's fleet.</para>
/// </summary>
public sealed class MachineListCachingNayaxLynxClient : INayaxLynxClient
{
    private readonly INayaxLynxClient _inner;
    private readonly INayaxRequestCredentialProvider _credentials;
    private readonly IBusinessScope _businessScope;
    private readonly NayaxMachineListCache _cache;

    public MachineListCachingNayaxLynxClient(
        INayaxLynxClient inner,
        INayaxRequestCredentialProvider credentials,
        IBusinessScope businessScope,
        NayaxMachineListCache cache)
    {
        _inner = inner;
        _credentials = credentials;
        _businessScope = businessScope;
        _cache = cache;
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(ct).ConfigureAwait(false);

        return key is { } cacheKey
            ? await _cache.GetOrReadAsync(cacheKey, _inner.GetMachinesAsync, ct).ConfigureAwait(false)
            : await _inner.GetMachinesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<List<NayaxMachine>> GetMachinesFreshAsync(CancellationToken ct = default)
    {
        var key = await ResolveKeyAsync(ct).ConfigureAwait(false);

        return key is { } cacheKey
            ? await _cache.ReadFreshAsync(cacheKey, _inner.GetMachinesAsync, ct).ConfigureAwait(false)
            : await _inner.GetMachinesAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) =>
        _inner.GetDevicesAsync(ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) =>
        _inner.GetMachineProductsAsync(machineId, ct);

    /// <inheritdoc />
    public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(
        long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) =>
        _inner.CreateMachineProductsAsync(machineId, products, ct);

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

    // The gate first, the key second, and in that order on every machine-list call: the refusal is
    // what decides whether this caller may have an answer at all, and the credential revision it
    // returns is what decides which answer belongs to it.
    private async Task<NayaxMachineListCacheKey?> ResolveKeyAsync(CancellationToken ct)
    {
        var credential = await _credentials.GetForOperationAsync(ct).ConfigureAwait(false);

        return _businessScope.BusinessId is { } businessId
            ? new NayaxMachineListCacheKey(businessId, credential.CredentialRevision)
            : null;
    }
}
