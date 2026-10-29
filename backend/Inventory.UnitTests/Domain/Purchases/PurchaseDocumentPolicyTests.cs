using Inventory.Domain.Purchases;
using Xunit;

namespace InventoryApi.Tests.Domain.Purchases;

/// <summary>
/// The rules about a purchase's supporting document (issue #976): what an uploaded one must be, and
/// when a <em>stored</em> one is coherent.
///
/// <para>The acceptance rules are the ones the create path has always applied; they are stated here
/// so the create and the later attach cannot drift apart. The metadata rule is the new one, and it is
/// the application's half of the database's all-or-nothing <c>CHECK</c> constraint.</para>
/// </summary>
public class PurchaseDocumentPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1024)]
    [InlineData(10 * 1024 * 1024)]
    public void A_document_from_one_byte_up_to_the_ceiling_is_an_acceptable_size(long length) =>
        Assert.True(PurchaseDocumentPolicy.IsSizeValid(length));

    /// <summary>
    /// Empty is not "no document": a purchase with no document carries no file at all, while a
    /// zero-byte upload is a document that cannot be read, and both edges of the ceiling matter.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10 * 1024 * 1024 + 1)]
    public void An_empty_negative_or_oversized_document_is_refused(long length) =>
        Assert.False(PurchaseDocumentPolicy.IsSizeValid(length));

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".pdf")]
    [InlineData(".webp")]
    [InlineData(".heic")]
    [InlineData(".JPG")]
    [InlineData(".PDF")]
    public void An_image_or_pdf_extension_is_allowed_whatever_its_case(string extension) =>
        Assert.True(PurchaseDocumentPolicy.IsExtensionAllowed(extension));

    /// <summary>
    /// A file with no extension at all is the case <c>Path.GetExtension</c> reports as an empty
    /// string, and a null is what a caller with nothing to offer hands over; neither is an image.
    /// </summary>
    [Theory]
    [InlineData(".txt")]
    [InlineData(".exe")]
    [InlineData("jpg")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_refused(string? extension) =>
        Assert.False(PurchaseDocumentPolicy.IsExtensionAllowed(extension));

    /// <summary>The published list is the one the policy decides from, so a caller can offer it in a picker.</summary>
    [Fact]
    public void The_allowed_extensions_are_the_ones_the_policy_accepts() =>
        Assert.All(
            PurchaseDocumentPolicy.AllowedExtensions,
            extension => Assert.True(PurchaseDocumentPolicy.IsExtensionAllowed(extension)));

    /// <summary>All four present, or all four absent - those are the only two coherent states.</summary>
    [Fact]
    public void A_fully_recorded_document_and_no_document_at_all_are_both_consistent()
    {
        Assert.True(PurchaseDocumentPolicy.IsMetadataConsistent("abc.jpg", "image/jpeg", "scan.jpg", 2048));
        Assert.True(PurchaseDocumentPolicy.IsMetadataConsistent(null, null, null, null));
    }

    /// <summary>
    /// Any one value on its own is a row claiming bytes nothing can open, or bytes nothing names.
    /// </summary>
    [Theory]
    [InlineData("abc.jpg", null, null, null)]
    [InlineData(null, "image/jpeg", null, null)]
    [InlineData(null, null, "scan.jpg", null)]
    [InlineData(null, null, null, 2048L)]
    [InlineData("abc.jpg", "image/jpeg", "scan.jpg", null)]
    [InlineData(null, "image/jpeg", "scan.jpg", 2048L)]
    public void A_half_recorded_document_is_not_consistent(
        string? storedFileName, string? contentType, string? fileName, long? fileSizeBytes) =>
        Assert.False(
            PurchaseDocumentPolicy.IsMetadataConsistent(storedFileName, contentType, fileName, fileSizeBytes));

    /// <summary>
    /// A blank stored name names no document, so it counts as absent rather than present - which is
    /// what keeps a row written by something that wrote <c>""</c> from reading as "has a document".
    /// </summary>
    [Fact]
    public void A_blank_value_counts_as_absent()
    {
        Assert.True(PurchaseDocumentPolicy.IsMetadataConsistent("", "  ", null, null));
        Assert.False(PurchaseDocumentPolicy.IsMetadataConsistent("", "image/jpeg", "scan.jpg", 2048));
    }
}
