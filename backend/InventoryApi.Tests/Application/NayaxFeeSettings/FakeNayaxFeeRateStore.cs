using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.NayaxFeeSettings;

namespace InventoryApi.Tests.Application.NayaxFeeSettings;

/// <summary>
/// In-memory fake of the persistence port, so Application use-case tests exercise orchestration
/// without depending on EF Core or SQLite.
/// </summary>
public sealed class FakeNayaxFeeRateStore : INayaxFeeRateStore
{
    private readonly List<NayaxFeeRateRecord> _rates = [];
    private int _nextId = 1;

    public bool AddWasCalled { get; private set; }
    public bool UpdateWasCalled { get; private set; }

    public Task<IReadOnlyList<NayaxFeeRateRecord>> ListOrderedByEffectiveDateDescendingAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NayaxFeeRateRecord>>(
            _rates.OrderByDescending(x => x.EffectiveFrom).ToList());

    public Task<NayaxFeeRateRecord> FindByEffectiveDateAsync(DateTime effectiveDate, CancellationToken cancellationToken) =>
        Task.FromResult(_rates.SingleOrDefault(x => x.EffectiveFrom.Date == effectiveDate.Date));

    public Task<NayaxFeeRateRecord> AddAsync(DateTime effectiveDate, decimal feeExGst, DateTime createdAtUtc, CancellationToken cancellationToken)
    {
        AddWasCalled = true;
        var record = new NayaxFeeRateRecord(_nextId++, effectiveDate, feeExGst, createdAtUtc);
        _rates.Add(record);
        return Task.FromResult(record);
    }

    public Task<NayaxFeeRateRecord> UpdateFeeAsync(int id, decimal feeExGst, CancellationToken cancellationToken)
    {
        UpdateWasCalled = true;
        var existing = _rates.Single(x => x.Id == id);
        var updated = existing with { FeeExGst = feeExGst };
        _rates[_rates.IndexOf(existing)] = updated;
        return Task.FromResult(updated);
    }
}
