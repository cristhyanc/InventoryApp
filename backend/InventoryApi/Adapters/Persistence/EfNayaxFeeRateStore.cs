using Inventory.Application.NayaxFeeSettings;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Adapters.Persistence;

/// <summary>
/// Temporary EF Core implementation of <see cref="INayaxFeeRateStore"/>. It lives in InventoryApi,
/// not Inventory.Infrastructure, because it depends on <see cref="AppDbContext"/> and
/// <see cref="NayaxProcessingFeeRate"/>, which still live in InventoryApi. Move it into
/// Inventory.Infrastructure once the shared AppDbContext and persistence models relocate there.
/// </summary>
public sealed class EfNayaxFeeRateStore : INayaxFeeRateStore
{
    private readonly AppDbContext _db;

    public EfNayaxFeeRateStore(AppDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<NayaxFeeRateRecord>> ListOrderedByEffectiveDateDescendingAsync(CancellationToken cancellationToken) =>
        await _db.NayaxProcessingFeeRates
            .AsNoTracking()
            .OrderByDescending(x => x.EffectiveFrom)
            .Select(x => new NayaxFeeRateRecord(x.Id, x.EffectiveFrom, x.FeeExGst, x.CreatedAt))
            .ToListAsync(cancellationToken);

    public async Task<NayaxFeeRateRecord?> FindByEffectiveDateAsync(DateTime effectiveDate, CancellationToken cancellationToken)
    {
        var entity = await _db.NayaxProcessingFeeRates
            .SingleOrDefaultAsync(x => x.EffectiveFrom.Date == effectiveDate.Date, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<NayaxFeeRateRecord> AddAsync(DateTime effectiveDate, decimal feeExGst, DateTime createdAtUtc, CancellationToken cancellationToken)
    {
        var entity = new NayaxProcessingFeeRate { EffectiveFrom = effectiveDate, FeeExGst = feeExGst, CreatedAt = createdAtUtc };
        _db.NayaxProcessingFeeRates.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    public async Task<NayaxFeeRateRecord> UpdateFeeAsync(int id, decimal feeExGst, CancellationToken cancellationToken)
    {
        var entity = await _db.NayaxProcessingFeeRates.SingleAsync(x => x.Id == id, cancellationToken);
        entity.FeeExGst = feeExGst;
        await _db.SaveChangesAsync(cancellationToken);
        return ToRecord(entity);
    }

    private static NayaxFeeRateRecord ToRecord(NayaxProcessingFeeRate entity) =>
        new(entity.Id, entity.EffectiveFrom, entity.FeeExGst, entity.CreatedAt);
}
