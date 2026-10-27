using Inventory.Application.PickList;
using Inventory.Domain.Stock;
using InventoryApi.DTOs;
using InventoryApi.Http;
using Microsoft.AspNetCore.Mvc;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects a multi-machine refill batch's two answers onto HTTP (issue #897): the recorded batch, and
/// the refusal that names what was wrong with each part of the request and with each allocation.
///
/// <para>The refusal is a <see cref="ValidationProblemDetails"/>, which is the shape
/// <c>[ApiController]</c> already produces for a request that fails model binding and the shape
/// <c>DomainExceptionHandler</c>'s title comes from, so a client reads one error contract rather than
/// two. It is built here rather than through <c>ControllerBase.ValidationProblem</c> so the mapping is
/// a pure function of the Domain's rejection, with no dependency on a request's service
/// provider.</para>
///
/// <para>The error keys are where the Domain's <see cref="MultiMachineRefillRequestPart"/> vocabulary
/// becomes a JSON path. That translation lives here, at the boundary, because a field name is a
/// transport fact: <c>Inventory.Domain</c> states what was wrong and nothing about how a client
/// addresses it.</para>
/// </summary>
public static class MultiMachinePickListRefillResponseMapper
{
    /// <summary>
    /// The error key a per-allocation refusal is reported under: the allocation's position in the
    /// request the caller sent, so a client can put the message back on the line the operator typed.
    /// </summary>
    public static string AllocationKey(int allocationIndex) => $"allocations[{allocationIndex}]";

    /// <summary>The error key for a refusal about one part of the request itself.</summary>
    public static string RequestKey(MultiMachineRefillRequestPart part) => part switch
    {
        MultiMachineRefillRequestPart.IdempotencyKey => "idempotencyKey",
        MultiMachineRefillRequestPart.Notes => "notes",
        MultiMachineRefillRequestPart.Allocations => "allocations",

        // A part this mapper has not been taught is a contract decision, not a runtime fallback: a
        // silent default would publish a refusal under a key no client knows to read.
        _ => throw new ArgumentOutOfRangeException(
            nameof(part), part, "Unknown multi-machine refill request part."),
    };

    public static MultiMachinePickListRefillResponse ToResponse(
        MultiMachinePickListRefillRecord record, bool replayed)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new(
            record.IdempotencyKey,
            replayed,
            record.Notes,
            record.RecordedAtUtc,
            [.. record.Allocations.Select(allocation =>
                new MultiMachinePickListRefillRecordedAllocationResponse(
                    allocation.MachineId,
                    allocation.ProductId,
                    allocation.QuantityTaken,
                    allocation.StockAdjustmentId))]);
    }

    /// <summary>
    /// The refusal as a <c>400</c> problem document. Every refusal is reported, not only the first:
    /// an operator correcting one line per round trip is the workflow the batch endpoint replaces, and
    /// a product whose allocations together exceed storage is reported on each of the lines that
    /// combined, because the combination is what has to change.
    /// </summary>
    public static ValidationProblemDetails ToProblem(MultiMachineRefillRejection rejection)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        var errors = rejection.Request
            .Select(refusal => (Key: RequestKey(refusal.Part), refusal.Message))
            .Concat(rejection.Allocations.Select(refusal =>
                (Key: AllocationKey(refusal.AllocationIndex), refusal.Message)))
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
