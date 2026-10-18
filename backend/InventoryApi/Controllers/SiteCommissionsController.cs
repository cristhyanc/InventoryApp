using Inventory.Application.Access;
using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The site commission report, the effective-dated commission agreements behind it, and the
/// payments recorded against it.
///
/// Every action requires <c>SiteCommissions.Manage</c> (issue #526). An agreement is effective-dated
/// financial configuration that decides what every later period owes a site, and the report is what
/// the business pays from, so #328's table gives the whole surface - report, agreements and payments
/// alike - to a Manager and an Owner. The report is not split onto <c>Reports.View</c>: it is the
/// same surface the agreements are maintained from, and a role that may read what a site is owed but
/// not why would be told a number it cannot check.
/// </summary>
[ApiController]
[Route("api/site-commissions")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.SiteCommissionsManage)]
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
            }).ToList())
        {
            // An unusable provider is this business's state, not a failed call (issue #731): the
            // response is 200 and says the report is unavailable and why, rather than an error that
            // tells the page nothing it can present.
            IsAvailable = report.IsAvailable,
            UnavailableReason = report.UnavailableReason is SiteCommissionUnavailableReason reason
                ? SiteCommissionUnavailableReasons.Code(reason)
                : null,
            UnavailableDetail = report.UnavailableReason is SiteCommissionUnavailableReason detail
                ? SiteCommissionUnavailableReasons.Describe(detail)
                : null
        };
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
