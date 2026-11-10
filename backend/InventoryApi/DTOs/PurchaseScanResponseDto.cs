namespace InventoryApi.DTOs;

/// <summary>
/// One value of a scanned draft on the wire (issue #1163): the value, and whether it was read off
/// the receipt or suggested by this application.
///
/// <para><see cref="Provenance"/> is the published token - <c>read</c> or <c>suggested</c> - rather
/// than an enum's number, for the reason every other published vocabulary in this API is a token: a
/// client branches on a word, and renaming a C# member must not change the contract.</para>
///
/// <para><strong>Every published type here is named <c>PurchaseScan*</c>, never <c>Receipt*</c></strong>
/// - the rule issue #127 established and `PublishedResponseSchemaContractTests` enforces: no schema
/// or tag in the published document may carry a Receipt name, because the purchase persistence model
/// is full of deliberately unrenamed Receipt-named surfaces and a client must never be given a
/// vocabulary in which a purchase is a receipt. The scanned document itself is of course a receipt,
/// which is what the properties and the documentation call it.</para>
/// </summary>
/// <typeparam name="T">The value's type.</typeparam>
public sealed record PurchaseScanValueDto<T>(T Value, string Provenance);

/// <summary>What the receipt said about one line's GST (issue #1163), as published tokens.</summary>
/// <param name="PriceIncludesGst"><c>yes</c>, <c>no</c> or <c>unknown</c>.</param>
/// <param name="Taxable"><c>yes</c>, <c>no</c> or <c>unknown</c>.</param>
/// <param name="Marker">The GST marker the reader saw, as printed, or <see langword="null"/>.</param>
public sealed record PurchaseScanGstHintDto(string PriceIncludesGst, string Taxable, string? Marker);

/// <summary>
/// One line of a scanned draft on the wire (issue #1163). Delivery and package charges are not lines:
/// they are summed into <see cref="PurchaseScanResponseDto.DeliveryCost"/> and
/// <see cref="PurchaseScanResponseDto.PackageCost"/>.
/// </summary>
/// <param name="Kind"><c>stock</c>, <c>levy</c> or <c>other</c>.</param>
/// <param name="Text">The line as printed.</param>
/// <param name="Packs">Packs delivered.</param>
/// <param name="UnitsPerPack">Units per pack as printed - <see langword="null"/>, never <c>1</c>, when not printed.</param>
/// <param name="Units">Packs times units per pack.</param>
/// <param name="LineTotal">The line's GST-inclusive amount.</param>
/// <param name="UnitCost">The amount over the units, unrounded.</param>
/// <param name="ProductId">A suggested product of this business, when one was matched.</param>
/// <param name="Gst">What the receipt said about the line's GST.</param>
/// <param name="LevyUnresolved">Whether this is a levy the receipt's totals could not place.</param>
/// <param name="Missing">Which values the line could not be completed with, as published tokens.</param>
public sealed record PurchaseScanLineDto(
    string Kind,
    PurchaseScanValueDto<string> Text,
    PurchaseScanValueDto<int>? Packs,
    PurchaseScanValueDto<int>? UnitsPerPack,
    PurchaseScanValueDto<int>? Units,
    PurchaseScanValueDto<decimal>? LineTotal,
    PurchaseScanValueDto<decimal>? UnitCost,
    PurchaseScanValueDto<long>? ProductId,
    PurchaseScanGstHintDto Gst,
    bool LevyUnresolved,
    IReadOnlyList<string> Missing);

/// <summary>
/// The suggested purchase one scanned receipt produced (issue #1163).
///
/// <para><strong>Nothing about it has been stored</strong>: not the file, not this draft, not a
/// purchase. It is a suggestion the operator edits and then submits through the ordinary
/// <c>POST /api/purchases</c>, which is where a purchase is created and where its own validation
/// applies.</para>
/// </summary>
public sealed record PurchaseScanResponseDto(
    PurchaseScanValueDto<string>? Supplier,
    PurchaseScanValueDto<int>? SupplierId,
    PurchaseScanValueDto<DateOnly>? PurchaseDate,
    PurchaseScanValueDto<string>? InvoiceNumber,
    PurchaseScanValueDto<decimal>? ReceiptTotal,
    PurchaseScanValueDto<decimal>? GstTotal,
    PurchaseScanValueDto<decimal>? DeliveryCost,
    PurchaseScanValueDto<decimal>? PackageCost,
    IReadOnlyList<PurchaseScanLineDto> Lines);
