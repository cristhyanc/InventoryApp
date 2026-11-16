using Inventory.Domain.Gst;
using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// What makes one purchase create request the same as another (issue #1247), which is what decides
/// whether a reused idempotency key is a retry to replay or a different purchase to refuse.
///
/// <para>Each case below changes exactly one thing about a request that is otherwise identical, and
/// asserts the fingerprint moves: a value that does not change the fingerprint is a value a second,
/// different purchase could be saved under a spent key without anybody noticing - or, for the
/// unchanged case, a retry that would be refused as a conflict.</para>
/// </summary>
public class PurchaseCreateFingerprintTests
{
    [Fact]
    public void The_same_request_renders_the_same_fingerprint()
    {
        Assert.Equal(PurchaseCreateFingerprint.Compute(Content()), PurchaseCreateFingerprint.Compute(Content()));
    }

    /// <summary>It is lowercase hexadecimal SHA-256, which is what the stored column is sized for.</summary>
    [Fact]
    public void The_fingerprint_is_64_hexadecimal_characters()
    {
        var fingerprint = PurchaseCreateFingerprint.Compute(Content());

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, character => Assert.Contains(character, "0123456789abcdef"));
    }

    /// <summary>
    /// Every single-value change, each against the same base request. They are one test rather than
    /// a <c>Theory</c> because the cases carry a whole request object, which xUnit would have to
    /// serialise to enumerate them.
    /// </summary>
    [Fact]
    public void A_changed_request_renders_a_different_fingerprint()
    {
        var baseline = PurchaseCreateFingerprint.Compute(Content());

        var unchanged = ChangedRequests()
            .Where(changed => PurchaseCreateFingerprint.Compute(changed.Value) == baseline)
            .Select(changed => changed.Key)
            .ToList();

        Assert.True(
            unchanged.Count == 0,
            "These changes left the fingerprint alone, so a different purchase could be saved under "
                + $"a spent idempotency key: {string.Join(", ", unchanged)}.");
    }

    private static Dictionary<string, PurchaseFingerprintContent> ChangedRequests() => new()
    {
        { "title", Content() with { Title = "Thursday restock" } },
        { "notes", Content() with { Notes = "left at the door" } },
        { "invoice number", Content() with { InvoiceNumber = "INV-2" } },
        { "invoice number cleared", Content() with { InvoiceNumber = null } },
        { "receipt total", Content() with { TotalAmount = 30.01m } },
        { "delivery cost", Content() with { DeliveryCost = 5.01m } },
        { "delivery classification", Content() with { DeliveryGstClassification = GstClassification.GstFree } },
        { "delivery classification cleared", Content() with { DeliveryGstClassification = null } },
        { "package cost", Content() with { PackageCost = 2.01m } },
        { "package classification", Content() with { PackageGstClassification = GstClassification.Taxable } },
        { "purchase date", Content() with { PurchaseDate = new DateTime(2026, 4, 2, 10, 0, 0, DateTimeKind.Utc) } },
        { "no purchase date", Content() with { PurchaseDate = null } },
        { "supplier", Content() with { SupplierId = 5 } },
        { "no supplier", Content() with { SupplierId = null } },
        { "document name", Content() with { DocumentFileName = "other.jpg" } },
        { "document length", Content() with { DocumentLength = 2049 } },
        // A second receipt photographed under the same name at the same size: the name and the
        // length say it is the same document and only its bytes say it is not.
        { "document bytes", Content() with { DocumentDigest = new string('b', 64) } },
        { "no document", Content() with { DocumentFileName = null, DocumentLength = null, DocumentDigest = null } },
        { "supplier order completed", Content() with { CompleteSupplierOrderId = 9, CompleteSupplierOrderFingerprint = "abc" } },
        { "a line's quantity", Content() with { Lines = [Line() with { Quantity = 25m }, SecondLine()] } },
        { "a line's unit cost", Content() with { Lines = [Line() with { UnitCost = 1.16m }, SecondLine()] } },
        { "a line's product", Content() with { Lines = [Line() with { ProductId = 99 }, SecondLine()] } },
        { "a line's classification", Content() with { Lines = [Line() with { GstClassification = GstClassification.Taxable }, SecondLine()] } },
        { "a line removed", Content() with { Lines = [Line()] } },
        { "a line added", Content() with { Lines = [Line(), SecondLine(), SecondLine()] } },
        // A purchase may legitimately hold two lines for one product, so there is no total ordering
        // to sort into and the submitted order is part of the request.
        { "the lines reordered", Content() with { Lines = [SecondLine(), Line()] } },
    };

    /// <summary>
    /// A value cannot borrow a field separator and make two different requests render identically:
    /// every text value is rendered with its own length.
    /// </summary>
    [Fact]
    public void A_value_holding_the_separator_cannot_impersonate_another_request()
    {
        var left = Content() with { Title = "Weekly", Notes = "restock" };
        var right = Content() with { Title = "Weekly|restock", Notes = null };

        Assert.NotEqual(PurchaseCreateFingerprint.Compute(left), PurchaseCreateFingerprint.Compute(right));
    }

    private static PurchaseFingerprintContent Content() => new(
        "Weekly restock",
        null,
        "INV-1",
        30.00m,
        5.00m,
        GstClassification.Taxable,
        2.00m,
        GstClassification.GstFree,
        new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc),
        4,
        "scan.jpg",
        2048,
        new string('a', 64),
        null,
        null,
        [Line(), SecondLine()]);

    private static PurchaseFingerprintLine Line() => new(3, 24m, 1.15m, GstClassification.GstFree);

    private static PurchaseFingerprintLine SecondLine() => new(4, 6m, 2.50m, null);
}
