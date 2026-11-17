using Inventory.Application.Access;
using Inventory.Application.Commissions;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The site commission report and the effective-dated commission agreements behind it.
///
/// Every action requires <c>SiteCommissions.Manage</c> (issue #526). An agreement is effective-dated
/// financial configuration that decides what every later period owes a site, and the report is what
/// the business pays from, so #328's table gives the whole surface - report and agreements alike -
/// to a Manager and an Owner. The report is not split onto <c>Reports.View</c>: it is the
/// same surface the agreements are maintained from, and a role that may read what a site is owed but
/// not why would be told a number it cannot check.
///
/// <para>Every action also requires <see cref="PlanFeature.SiteCommissions"/> (issue #967), declared
/// on the controller for the same reason the capability is: a site is known only from the live fleet
/// read's <c>CustomerID</c>, so commission is a sales-provider feature throughout, and the
/// agreements are maintained from the surface the report is read on. The plan is
/// evaluated after the capability, so a role refusal is unchanged; nothing stored is deleted or
/// altered when a business's plan stops including this - the agreements stay exactly as
/// they are and become readable again with the plan.</para>
///
/// <para><b>There is no payment route</b> (issue #1251, the repository owner's decision of 13
/// November 2026): the application does not record commission payments, so this controller reports
/// commission due and maintains the agreements, and nothing here writes a payment.</para>
///
/// <para><b>An agreement may be ended but never edited</b> (issue #1254, the repository owner's
/// decision of 14 November 2026). <see cref="EndAgreement"/> sets an agreement's end date, which is
/// what lets a site's rate change without a database edit; correcting a mistyped rate or start date,
/// and deleting an agreement, stay out, because they would rewrite commission already reported.</para>
/// </summary>
[ApiController]
[Route("api/site-commissions")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.SiteCommissionsManage)]
[RequirePlan(PlanFeature.SiteCommissions)]
public sealed class SiteCommissionsController : ControllerBase
{
    private readonly IGetSiteCommissionReport _getReport;
    private readonly GetSiteCommissionAgreements _getAgreements;
    private readonly SaveSiteCommissionAgreement _saveAgreement;
    private readonly EndSiteCommissionAgreement _endAgreement;

    public SiteCommissionsController(
        IGetSiteCommissionReport getReport,
        GetSiteCommissionAgreements getAgreements,
        SaveSiteCommissionAgreement saveAgreement,
        EndSiteCommissionAgreement endAgreement)
    {
        _getReport = getReport;
        _getAgreements = getAgreements;
        _saveAgreement = saveAgreement;
        _endAgreement = endAgreement;
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

    /// <summary>
    /// Ends one of the business's own agreements on a date (issue #1254), so a site's rate can
    /// change in the application instead of in the database: the add above refuses any overlap, so
    /// an open-ended agreement blocked every later one for ever.
    ///
    /// <para>It is the same capability as the rest of this controller - a Manager and an Owner may
    /// both add an agreement, so both may end one - and the same plan. A read-only support session
    /// is refused by the central method rule, as every other write on this surface is.</para>
    ///
    /// <para>The end date must be on or after the agreement's start date and may not be later than
    /// an end date it already carries; either breach is a <c>400</c> carrying the rule's own
    /// sentence, through <c>DomainExceptionHandler</c>. An agreement the caller's business does not
    /// hold - another business's key included - is <c>404</c>.</para>
    /// </summary>
    [HttpPost("agreements/{agreementId:int}/end")]
    public async Task<ActionResult<CommissionAgreement>> EndAgreement(
        int agreementId,
        EndSiteCommissionAgreementDto dto,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(dto);

        var ended = await _endAgreement.Handle(
            new EndSiteCommissionAgreementInput(agreementId, dto.EndDate), ct);
        return ended is null ? NotFound() : Ok(ended);
    }
}
