using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The business, credential revision and machine one cached machine-product list belongs to
/// (issue #842).
///
/// <para>All three are in the key for the same reason, and each of them is taken from the server's
/// own side of the request. The business id, because a cache that held one list for the whole
/// process would be the one place in this application where one business could be served another's
/// stock figures; it comes from the resolved <c>IBusinessScope</c>. The credential revision, because
/// replacing a connection may point the same business at a different Nayax operator account, whose
/// machines are not these machines. The machine id, because this read is per machine: one entry per
/// machine is what makes a Dashboard visit cost one request per machine instead of three.</para>
/// </summary>
public readonly record struct NayaxMachineProductsCacheKey(
    int BusinessId, int CredentialRevision, long MachineId);

/// <summary>
/// The in-memory selection lists <c>GET /v1/machines/{MachineID}/machineProducts</c> answered with,
/// one per business, credential revision and machine, kept for <see cref="Lifetime"/> (issue #842,
/// step 2 of #839).
///
/// <para><strong>Why it exists.</strong> One Dashboard visit read every machine's products three
/// times, largely at the same moment: <c>GET api/dashboard/summary</c> and
/// <c>GET api/products/alerts/low-stock</c> (both <c>CalculateReorderNeeds</c>) and
/// <c>GET api/sites</c> (<c>GetSiteSummaries</c>). With ten machines that was thirty requests where
/// ten would do.</para>
///
/// <para><strong>Why sixty seconds and not ten minutes.</strong> This payload is not the fleet list:
/// it carries the machine's live stock fields - <c>PAR</c>, <c>MissingStockByMDB</c> and
/// <c>VendOutAlertThreshold</c> - and an operator reads those to decide what to restock. So the
/// figure a read-only page shows is never more than a minute old (decided by the repository owner on
/// 23 October 2026), and the callers whose correctness depends on the current moment - the pick
/// list, Sync Restock, the machine-stock ledger and its counts, and the costing transition preview -
/// read fresh and replace what is held rather than reading through it at all.</para>
///
/// <para>The mechanism - one shared request per key, nothing failed ever held, the explicit ordering
/// between an older read, a newer fresh read and an invalidation, and the refusals that run before
/// any of it - is <see cref="NayaxReadCache{TKey,TValue}"/>, which it shares with
/// <see cref="NayaxMachineListCache"/>. What this class states is what is specific to the machine's
/// products: its key and how long one answer is reused.</para>
/// </summary>
public sealed class NayaxMachineProductsCache(IClock clock)
    : NayaxReadCache<NayaxMachineProductsCacheKey, NayaxMachineProduct>(clock, Lifetime, MaxTrackedKeys)
{
    /// <summary>
    /// How long one answer is reused. Sixty seconds, decided by the repository owner on 23 October
    /// 2026, because the list carries live stock levels: the Dashboard, the sites page and the
    /// machine's product list may show a figure up to a minute old, and nothing that plans a refill
    /// or records a count reads through this cache at all.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many keys may be remembered before the ones holding nothing usable are cleared out.
    /// Deliberately larger than the machine list's bound: there is one key per machine here rather
    /// than one per business, so a single business's fleet occupies as many keys as it has machines.
    /// It is a hint about memory, never a cap.
    /// </summary>
    public const int MaxTrackedKeys = 1_024;
}
