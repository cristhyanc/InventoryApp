using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/site-commissions")]
[Authorize]
[RequiredScope("access_as_user")]
public sealed class SiteCommissionsController : ControllerBase
{
    private readonly IGetSiteCommissionReport _getReport;
    private readonly GetSiteCommissionAgreements _getAgreements;
    private readonly SaveSiteCommissionAgreement _saveAgreement;
    private readonly RecordSiteCommissionPayment _recordPayment;

    public SiteCommissionsController(
        IGetSiteCommissionReport getReport,
        GetSiteCommissionAgreements getAgreements,
        SaveSiteCommissionAgreement saveAgreement,
        RecordSiteCommissionPayment recordPayment)
    {
        _getReport = getReport;
        _getAgreements = getAgreements;
        _saveAgreement = saveAgreement;
        _recordPayment = recordPayment;
    }

    [HttpGet]
    public async Task<SiteCommissionReportDto> Get(
        [FromQuery] DateTime from,
        [FromQuery] DateTime to,
        [FromQuery] long? siteId,
        CancellationToken ct)
    {
        var report = await _getReport.Handle(from, to, siteId, ct);
        return new SiteCommissionReportDto(report.From, report.To, report.Rows.Select(row =>
            new SiteCommissionRowDto(
                row.SiteId,
                row.SiteName,
                row.PeriodStart,
                row.PeriodEnd,
                row.Frequency,
                row.Basis,
                row.GrossSales,
                row.CardSales,
                row.CashSales,
                row.EligibleSales,
                row.CommissionRate,
                row.CommissionDue,
                row.Paid,
                row.Outstanding,
                row.DueDate,
                row.Status,
                row.Machines.Select(machine => new SiteCommissionMachineDto(
                    machine.MachineId,
                    machine.MachineName,
                    machine.TransactionCount,
                    machine.GrossSales,
                    machine.CardSales,
                    machine.CashSales,
                    machine.EligibleSales,
                    machine.CommissionDue,
                    machine.IsComplete,
                    machine.HasConfigurationGap,
                    machine.HasOverlap)).ToList(),
                row.Products.Select(product => new SiteCommissionProductDto(
                    product.ProductName, product.TotalVends, product.TotalSales)).ToList(),
                row.Payments,
                row.DataQuality)
            {
                HasConfigurationGap = row.HasConfigurationGap,
                HasOverlap = row.HasOverlap,
                UsesMultipleRates = row.UsesMultipleRates
            }).ToList());
    }

    [HttpGet("agreements")]
    public Task<IReadOnlyList<CommissionAgreement>> Agreements([FromQuery] long? siteId, CancellationToken ct) =>
        _getAgreements.Handle(siteId, ct);

    [HttpPost("agreements")]
    public async Task<ActionResult<CommissionAgreement>> SaveAgreement(SiteCommissionAgreementDto dto, CancellationToken ct)
    {
        if (dto.CommissionRate < 0 || dto.CommissionRate > 1 || dto.PaymentDueDaysAfterPeriodEnd < 0 || dto.EffectiveTo < dto.EffectiveFrom)
            return BadRequest("Commission agreement values are invalid.");
        var agreement = await _saveAgreement.Handle(
            new SaveSiteCommissionAgreementInput(
                dto.SiteId,
                dto.EffectiveFrom,
                dto.EffectiveTo,
                dto.CommissionRate,
                dto.Frequency,
                dto.Basis,
                dto.PaymentDueDaysAfterPeriodEnd),
            ct);
        return CreatedAtAction(nameof(Agreements), new { siteId = agreement.SiteId }, agreement);
    }

    [HttpPost("{siteId:long}/payments")]
    public async Task<ActionResult<CommissionPayment>> RecordPayment(
        long siteId,
        [FromQuery] DateTime periodStart,
        [FromQuery] DateTime periodEnd,
        CommissionPaymentDto dto,
        CancellationToken ct)
    {
        if (dto.Amount <= 0 || periodEnd < periodStart) return BadRequest("Payment amount and period are invalid.");
        var result = await _recordPayment.Handle(
            new RecordSiteCommissionPaymentInput(
                siteId, periodStart, periodEnd, dto.PaymentDate, dto.Amount, dto.Notes),
            ct);
        if (result.Payment is null)
            return BadRequest(result.Error);
        return CreatedAtAction(nameof(Get), new { periodStart, periodEnd, siteId }, result.Payment);
    }
}
