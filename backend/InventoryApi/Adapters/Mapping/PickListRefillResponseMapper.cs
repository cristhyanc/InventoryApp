using Inventory.Application.PickList;
using Inventory.Domain.Stock;
using InventoryApi.DTOs;
using InventoryApi.Http;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects a batch refill's two answers onto HTTP (issue #743): the recorded refill, and the
/// refusal that names what was wrong with each part of the request and with each row.
///
/// <para>The refusal is a <see cref="ValidationProblemDetails"/>, which is the shape
/// <c>[ApiController]</c> already produces for a request that fails model binding and the shape
/// <c>DomainExceptionHandler</c>'s title comes from, so a client reads one error contract rather
/// than two. It is built here rather than through <c>ControllerBase.ValidationProblem</c> so the
/// mapping is a pure function of the Domain's rejection, with no dependency on a request's service
/// provider.</para>
///
/// <para>The error keys are where the Domain's
/// <see cref="PickListRefillRequestPart"/> vocabulary becomes a JSON path. That translation lives
/// here, at the boundary, because a field name is a transport fact: <c>Inventory.Domain</c> states
/// what was wrong and nothing about how a client addresses it.</para>
/// </summary>
public static class PickListRefillResponseMapper
{
    /// <summary>
    /// The error key a per-row refusal is reported under: the row's position in the request the
    /// caller sent, so a client can put the message back on the line the operator typed.
    /// </summary>
    public static string RowKey(int rowIndex) => $"rows[{rowIndex}]";

    /// <summary>The error key for a refusal about one part of the request itself.</summary>
    public static string RequestKey(PickListRefillRequestPart part) => part switch
    {
        PickListRefillRequestPart.IdempotencyKey => "idempotencyKey",
        PickListRefillRequestPart.Machine => "machineId",
        PickListRefillRequestPart.Notes => "notes",
        PickListRefillRequestPart.Rows => "rows",

        // A part this mapper has not been taught is a contract decision, not a runtime fallback: a
        // silent default would publish a refusal under a key no client knows to read.
        _ => throw new ArgumentOutOfRangeException(nameof(part), part, "Unknown batch refill request part."),
    };

    public static PickListRefillResponse ToResponse(PickListRefillRecord record, bool replayed)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new(
            record.IdempotencyKey,
            replayed,
            record.MachineId,
            record.Notes,
            record.RecordedAtUtc,
            [.. record.Rows.Select(row => new PickListRefillRecordedRowResponse(
                row.ProductId, row.QuantityTaken, row.StockAdjustmentId))]);
    }

    /// <summary>
    /// The refusal as a <c>400</c> problem document. Every refusal is reported, not only the first:
    /// an operator correcting one row per round trip is the workflow the batch endpoint replaces.
    /// </summary>
    public static ValidationProblemDetails ToProblem(PickListRefillRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        var errors = rejection.Request
            .Select(refusal => (Key: RequestKey(refusal.Part), refusal.Message))
            .Concat(rejection.Rows.Select(refusal => (Key: RowKey(refusal.RowIndex), refusal.Message)))
            .GroupBy(error => error.Key, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.Message).ToArray(),
                StringComparer.Ordinal);

        return new ValidationProblemDetails(errors)
        {
            Title = DomainExceptionHandler.ValidationTitle,
            Status = StatusCodes.Status400BadRequest,
        };
    }
}
