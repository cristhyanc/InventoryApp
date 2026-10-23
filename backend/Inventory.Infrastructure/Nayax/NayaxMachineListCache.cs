using Inventory.Application.Nayax;
using Inventory.Application.Time;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The business and credential revision one cached machine list belongs to (issue #840).
///
/// <para>The business id is in the key because a cache that held one list for the whole process
/// would be the one place in this application where one business could be served another's data.
/// The credential revision is in the key because replacing a connection may point the same business
/// at a different Nayax operator account: a saved connection therefore produces a new key, and the
/// next read goes to Nayax rather than answering from a list the previous credentials fetched.</para>
/// </summary>
public readonly record struct NayaxMachineListCacheKey(int BusinessId, int CredentialRevision);

/// <summary>
/// The in-memory machine lists <c>GET /v1/machines</c> answered with, one per business and
/// credential revision, kept for <see cref="Lifetime"/> (issue #840, step 1 of #839).
///
/// <para><strong>Why it exists.</strong> Twelve use cases read the machine list and nothing cached
/// it, so one Dashboard visit spent five requests on it - several of them simultaneously - against
/// somebody else's API. Machine ids, names and site <c>CustomerID</c>s change rarely, so those
/// repeats bought nothing.</para>
///
/// <para>The mechanism - one shared request per key, nothing failed ever held, the explicit ordering
/// between an older read, a newer fresh read and an invalidation, and the refusals that run before
/// any of it - is <see cref="NayaxReadCache{TKey,TValue}"/>, which it shares with
/// <see cref="NayaxMachineProductsCache"/>. What this class states is what is specific to the
/// machine list: its key and how long one answer is reused.</para>
/// </summary>
public sealed class NayaxMachineListCache(IClock clock)
    : NayaxReadCache<NayaxMachineListCacheKey, NayaxMachine>(clock, Lifetime, MaxTrackedKeys)
{
    /// <summary>
    /// How long one answer is reused. Decided by the repository owner on 23 October 2026: a machine
    /// added in Nayax can take up to ten minutes to appear on the pages that read through this
    /// cache, while provider machine review and the latest-sales sync - which read fresh - see it at
    /// once.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How many keys may be remembered before the ones holding nothing usable are cleared out. One
    /// key per business and credential revision, so this is generous for the fleet lists of a great
    /// many businesses. It is a hint about memory, never a cap.
    /// </summary>
    public const int MaxTrackedKeys = 256;
}
