using Inventory.Application.Access;
using Inventory.Application.NayaxFeeSettings;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The business's effective-dated Nayax processing fee rates. The controller's route <em>is</em>
/// <c>api/settings/nayax-processing-fee-rates</c>, so the controller-level capability covers both
/// its actions and there is no other settings surface here.
///
/// Both require <c>Integration.Manage</c> (issue #526), the read included. A configured rate is what
/// every fee estimate for a date no imported reimbursement covers is calculated from, so it decides
/// reported profit; #328's table gives the business's Nayax integration configuration to an Owner
/// alone. A Manager may read the reports a rate feeds and may not change the rate - which is the
/// agreed table, not an oversight.
/// </summary>
[ApiController]
[Route("api/settings/nayax-processing-fee-rates")]
[Authorize]
[RequiredScope("access_as_user")]
[RequireCapability(Capability.IntegrationManage)]
public sealed class SettingsController : ControllerBase
{
    private readonly ListNayaxFeeRates _listRates;
    private readonly SaveNayaxFeeRate _saveRate;

    public SettingsController(ListNayaxFeeRates listRates, SaveNayaxFeeRate saveRate)
    {
        _listRates = listRates;
        _saveRate = saveRate;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<NayaxFeeRateResponse>>> Get(CancellationToken cancellationToken)
    {
        var rates = await _listRates.Handle(cancellationToken);
        return Ok(rates.Select(ToResponse).ToList());
    }

    [HttpPost]
    public async Task<ActionResult<NayaxFeeRateResponse>> Save(NayaxFeeRateRequest request, CancellationToken cancellationToken)
    {
        var result = await _saveRate.Handle(request.FeeExGst, request.EffectiveFrom, cancellationToken);
        if (!result.IsValid)
            return BadRequest(result.ValidationError);

        return Ok(ToResponse(result.Record!));
    }

    private static NayaxFeeRateResponse ToResponse(NayaxFeeRateRecord record) =>
        new(record.Id, record.EffectiveFrom, record.FeeExGst, record.CreatedAt);
}
