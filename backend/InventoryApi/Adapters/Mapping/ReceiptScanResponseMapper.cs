using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;
using InventoryApi.DTOs;

namespace InventoryApi.Adapters.Mapping;

/// <summary>
/// Projects a scanned draft onto the API's own contract (issue #1163).
///
/// <para><strong>Every vocabulary is published as its declared token</strong> - the line kind, the
/// provenance, the two GST answers and each missing-value marker - through the Domain's own
/// <c>Name</c> functions, so what a client branches on is a stable word rather than an enum's number,
/// and renaming a C# member cannot change the contract.</para>
///
/// <para><strong>The published types are <c>PurchaseScan*</c>, not <c>ReceiptScan*</c></strong>: no
/// schema or tag in the published document may carry a Receipt name (issue #127's rule, enforced by
/// <c>PublishedResponseSchemaContractTests</c>), because the purchase persistence model keeps several
/// deliberately unrenamed Receipt-named surfaces and a client must never be handed a vocabulary in
/// which a purchase is a receipt. This mapper is the boundary where that rename happens.</para>
///
/// <para>It calculates nothing. Every figure here was decided by
/// <c>Inventory.Domain.ReceiptScanning</c> and is carried through unchanged (AGENTS.md
/// § Architecture rules: one authoritative calculation feeds all presentations).</para>
/// </summary>
public static class ReceiptScanResponseMapper
{
    /// <summary>The draft as the endpoint publishes it, with the month's allowance beside it.</summary>
    public static PurchaseScanResponseDto ToResponse(ReceiptScanDraft draft, ReceiptScanUsageView usage)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return new PurchaseScanResponseDto(
            Value(draft.SupplierText),
            Value(draft.SupplierId),
            Value(draft.PurchaseDate),
            Value(draft.InvoiceNumber),
            Value(draft.ReceiptTotal),
            Value(draft.GstTotal),
            Value(draft.DeliveryCost),
            Value(draft.PackageCost),
            draft.Lines.Select(ToLine).ToList(),
            ToUsage(usage));
    }

    /// <summary>
    /// The monthly allowance as the scan response and the usage endpoint both publish it (issue
    /// #1164). The month is published as <c>yyyy-MM</c> and the remaining count is the Domain's, not
    /// a subtraction done here.
    /// </summary>
    public static PurchaseScanUsageDto ToUsage(ReceiptScanUsageView usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        return new PurchaseScanUsageDto(usage.Month, usage.Used, usage.Remaining, usage.Limit);
    }

    private static PurchaseScanLineDto ToLine(ReceiptScanLine line) =>
        new(
            ReceiptLineKinds.Name(line.Kind),
            Value(line.Text)!,
            Value(line.Packs),
            Value(line.UnitsPerPack),
            Value(line.Units),
            Value(line.LineTotal),
            Value(line.UnitCost),
            Value(line.ProductId),
            new PurchaseScanGstHintDto(
                ReceiptGstAnswers.Name(line.Gst.PriceBasis),
                ReceiptGstAnswers.Name(line.Gst.Taxability),
                line.Gst.Marker),
            line.LevyUnresolved,
            line.Missing.Select(ReceiptLineMissingValues.Name).ToList());

    private static PurchaseScanValueDto<T>? Value<T>(ReceiptScanValue<T>? value) =>
        value is null
            ? null
            : new PurchaseScanValueDto<T>(value.Value, ReceiptValueProvenances.Name(value.Provenance));
}
