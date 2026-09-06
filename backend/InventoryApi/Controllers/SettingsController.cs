using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/settings/nayax-processing-fee-rates")]
public sealed class SettingsController : ControllerBase
{
    private readonly AppDbContext _db;
    public SettingsController(AppDbContext db) => _db = db;

    [HttpGet]
    public Task<List<NayaxProcessingFeeRate>> Get(CancellationToken cancellationToken) =>
        _db.NayaxProcessingFeeRates.AsNoTracking().OrderByDescending(x => x.EffectiveFrom).ToListAsync(cancellationToken);

    [HttpPost]
    public async Task<ActionResult<NayaxProcessingFeeRate>> Save(NayaxProcessingFeeRate rate, CancellationToken cancellationToken)
    {
        if (rate.FeeExGst < 0m || decimal.Round(rate.FeeExGst, 4) != rate.FeeExGst)
            return BadRequest("Fee must be required, non-negative, and have no more than four decimal places.");

        var existing = await _db.NayaxProcessingFeeRates.SingleOrDefaultAsync(
            x => x.EffectiveFrom.Date == rate.EffectiveFrom.Date, cancellationToken);
        if (existing is null)
        {
            existing = new NayaxProcessingFeeRate { EffectiveFrom = rate.EffectiveFrom.Date, FeeExGst = rate.FeeExGst, CreatedAt = DateTime.UtcNow };
            _db.NayaxProcessingFeeRates.Add(existing);
        }
        else
            existing.FeeExGst = rate.FeeExGst;
        await _db.SaveChangesAsync(cancellationToken);
        return Ok(existing);
    }
}
