using Inventory.Application.Exceptions;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/site-commissions")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class SiteCommissionsController : ControllerBase
{
    private const decimal Tolerance = 0.01m;
    private readonly AppDbContext _db;
    private readonly ISiteCommissionService _commissions;

    public SiteCommissionsController(AppDbContext db, ISiteCommissionService commissions) { _db = db; _commissions = commissions; }

    [HttpGet]
    public Task<SiteCommissionReportDto> Get([FromQuery] DateTime from, [FromQuery] DateTime to, [FromQuery] long? siteId, CancellationToken ct) =>
        _commissions.GetReportAsync(from, to, siteId, ct);

    [HttpGet("agreements")]
    public Task<List<SiteCommissionAgreement>> Agreements([FromQuery] long? siteId, CancellationToken ct) =>
        _db.SiteCommissionAgreements.AsNoTracking().Where(x => !siteId.HasValue || x.SiteId == siteId)
            .OrderByDescending(x => x.EffectiveFrom).ToListAsync(ct);

    [HttpPost("agreements")]
    public async Task<ActionResult<SiteCommissionAgreement>> SaveAgreement(SiteCommissionAgreementDto dto, CancellationToken ct)
    {
        if (dto.CommissionRate < 0 || dto.CommissionRate > 1 || dto.PaymentDueDaysAfterPeriodEnd < 0 || dto.EffectiveTo < dto.EffectiveFrom)
            return BadRequest("Commission agreement values are invalid.");
        var overlaps = await _db.SiteCommissionAgreements.AnyAsync(x => x.SiteId == dto.SiteId &&
            x.EffectiveFrom <= (dto.EffectiveTo ?? DateTime.MaxValue) && (x.EffectiveTo ?? DateTime.MaxValue) >= dto.EffectiveFrom, ct);
        // Mapped centrally by DomainExceptionHandler into the same 409 this action used to
        // return directly (see Http/DomainExceptionHandler.cs).
        if (overlaps) throw new DomainConflictException("The agreement overlaps an existing agreement for this site.");
        var agreement = new SiteCommissionAgreement
        {
            SiteId = dto.SiteId,
            EffectiveFrom = dto.EffectiveFrom.Date,
            EffectiveTo = dto.EffectiveTo?.Date,
            CommissionRate = dto.CommissionRate,
            Frequency = dto.Frequency,
            Basis = dto.Basis,
            PaymentDueDaysAfterPeriodEnd = dto.PaymentDueDaysAfterPeriodEnd
        };
        _db.SiteCommissionAgreements.Add(agreement); await _db.SaveChangesAsync(ct); return CreatedAtAction(nameof(Agreements), new { siteId = agreement.SiteId }, agreement);
    }

    [HttpPost("{siteId:long}/payments")]
    public async Task<ActionResult<CommissionPayment>> RecordPayment(long siteId, [FromQuery] DateTime periodStart, [FromQuery] DateTime periodEnd, CommissionPaymentDto dto, CancellationToken ct)
    {
        if (dto.Amount <= 0 || periodEnd < periodStart) return BadRequest("Payment amount and period are invalid.");
        var report = await _commissions.GetReportAsync(periodStart, periodEnd, siteId, ct);
        var row = report.Rows.SingleOrDefault();
        if (row is null) return BadRequest("The site has no current Nayax machine assignment.");
        if (row.Paid + dto.Amount > row.CommissionDue + Tolerance) return BadRequest("Payment exceeds commission due for this period.");
        var payment = new CommissionPayment { SiteId = siteId, PeriodStart = periodStart.Date, PeriodEnd = periodEnd.Date, PaymentDate = dto.PaymentDate.Date, Amount = dto.Amount, Notes = dto.Notes?.Trim() };
        _db.CommissionPayments.Add(payment); await _db.SaveChangesAsync(ct); return CreatedAtAction(nameof(Get), new { periodStart, periodEnd, siteId }, payment);
    }
}
