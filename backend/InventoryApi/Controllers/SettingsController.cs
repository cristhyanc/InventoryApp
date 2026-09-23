using Inventory.Application.NayaxFeeSettings;
using InventoryApi.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Identity.Web.Resource;

namespace InventoryApi.Controllers;

[ApiController]
[Route("api/settings/nayax-processing-fee-rates")]
[Authorize]
[RequiredScope("access_as_user")]
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
