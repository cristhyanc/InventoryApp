using Inventory.Application.Access;
using Inventory.Application.NayaxFeeSettings;
using InventoryApi.Auth;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

/// <summary>
/// The business's effective-dated Nayax card-processing fee rates - the Settings page.
///
/// The whole controller, which is the whole <c>api/settings/nayax-processing-fee-rates</c> route,
/// requires <c>Integration.Manage</c> (issue #526): only an Owner holds it. A fee rate is part of
/// how this business's Nayax account is configured, and because it is effective-dated it rewrites
/// what every past period's reported fees and margins are, so it is not a figure a Manager changes
/// in passing.
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
