using Inventory.Application.Nayax;
using Inventory.Domain.Providers;
using Inventory.Infrastructure.Data;

namespace Inventory.Infrastructure.Persistence;

/// <summary>
/// The EF Core implementation of <see cref="INayaxMachineLinkStore"/> (issue #780): it reads
/// <c>LocalMachine.ExternalMachineId</c>, the column that holds the business's sales provider's id
/// for the machine, and nothing else. It is <see cref="EfNayaxProductLinkStore"/> over machines,
/// deliberately the same shape, because the question is the same question.
///
/// <para><strong>Only while the business sells through Nayax.</strong> A business has zero or one
/// sales provider, and the column holds that provider's id without naming it, so the read asks for
/// the rows whose owning business's <c>SalesProvider</c> is <see cref="ExternalProvider.Nayax"/> -
/// in the same statement, so the lookup is one round trip on the request's scoped context. A
/// business with no provider, or with another one, resolves no Nayax number at all rather than
/// reading some other system's identifier as a Nayax one.</para>
///
/// <para>The read itself is <see cref="EfMachineLinkQueries"/>, which the reporting fact providers
/// also call: there is one translation between a Nayax machine number and a local machine, not one
/// per caller.</para>
/// </summary>
public sealed class EfNayaxMachineLinkStore : INayaxMachineLinkStore
{
    private readonly AppDbContext _db;

    public EfNayaxMachineLinkStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<long, long>> GetLocalMachineIdsAsync(
        IReadOnlyCollection<long> nayaxMachineIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(nayaxMachineIds);

        return await EfMachineLinkQueries.LocalMachineIdsByNayaxIdAsync(
            _db, nayaxMachineIds, cancellationToken);
    }
}
