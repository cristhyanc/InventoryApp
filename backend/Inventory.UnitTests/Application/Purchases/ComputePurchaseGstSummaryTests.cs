using Inventory.Application.Purchases;
using Inventory.Domain.Gst;
using Xunit;

namespace InventoryApi.Tests.Application.Purchases;

/// <summary>
/// The saved purchase's input-GST summary (issue #431): the projection from a persisted
/// <see cref="PurchaseRecord"/> onto the Domain policy's component inputs, which is the only new
/// decision this use case makes. The rounding and unresolved rules themselves stay pinned by
/// <c>PurchaseGstPolicyTests</c>.
/// </summary>
public class ComputePurchaseGstSummaryTests
{
    [Fact]
    public void Handle_sums_the_lines_and_both_charges_of_the_saved_purchase()
    {
        var result = new ComputePurchaseGstSummary().Handle(Record(
            deliveryCost: 5m,
            delivery: GstClassification.Taxable,
            packageCost: 2m,
            package: GstClassification.GstFree,
            lines: [(2m, 1.10m, GstClassification.Taxable)]));

        // 0.20 for the 2.20 line plus 0.45 for the 5.00 delivery; the GST-free package adds $0.
        Assert.Equal(0.65m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    [Fact]
    public void Handle_reports_unclassified_lines_and_charges_as_unresolved()
    {
        var result = new ComputePurchaseGstSummary().Handle(Record(
            deliveryCost: 5m,
            delivery: GstClassification.Unknown,
            packageCost: null,
            package: GstClassification.Unknown,
            lines: [(2m, 1.10m, GstClassification.Taxable), (1m, 3m, GstClassification.Unknown)]));

        Assert.Equal(0.20m, result.InputGst);
        Assert.Equal(2, result.UnresolvedComponentCount);
        Assert.Equal(8m, result.UnresolvedAmount);
    }

    /// <summary>
    /// An absent charge is not an unresolved component (parent issue #62, decision D3), so a
    /// purchase with no delivery or package charge at all is complete once its lines are classified.
    /// </summary>
    [Fact]
    public void Handle_never_counts_an_absent_charge_as_unresolved()
    {
        var result = new ComputePurchaseGstSummary().Handle(Record(
            deliveryCost: null,
            delivery: GstClassification.Unknown,
            packageCost: 0m,
            package: GstClassification.Unknown,
            lines: [(2m, 1.10m, GstClassification.GstFree)]));

        Assert.Equal(0m, result.InputGst);
        Assert.Equal(0, result.UnresolvedComponentCount);
        Assert.Equal(0m, result.UnresolvedAmount);
    }

    private static PurchaseRecord Record(
        decimal? deliveryCost,
        GstClassification delivery,
        decimal? packageCost,
        GstClassification package,
        IReadOnlyList<(decimal Quantity, decimal UnitCost, GstClassification Classification)> lines) => new(
            Id: 7,
            BusinessId: 1,
            Title: "Weekly restock",
            Notes: null,
            TotalAmount: null,
            DeliveryCost: deliveryCost,
            DeliveryGstClassification: delivery,
            DeliveryGstClassificationSource: GstClassificationSource.Manual,
            PackageCost: packageCost,
            PackageGstClassification: package,
            PackageGstClassificationSource: GstClassificationSource.Manual,
            PurchaseDate: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc),
            SupplierId: null,
            Supplier: null,
            Items: lines
                .Select((line, index) => new PurchaseItemRecord(
                    index + 1, 7, 3, line.Quantity, line.UnitCost, line.Classification,
                    GstClassificationSource.Manual, null))
                .ToList(),
            FileName: "scan.jpg",
            StoredFileName: "abc.jpg",
            ContentType: "image/jpeg",
            FileSizeBytes: 3,
            CreatedAt: new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc));
}
