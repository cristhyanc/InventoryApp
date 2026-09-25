using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Documents;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// The blob name is the tenant boundary in Azure Blob storage: there is no per-blob access
/// control, so what keeps business A away from business B's document is that A's requests can
/// only ever name <c>tenants/A/...</c>. These tests pin the construction down exactly.
/// </summary>
public sealed class BlobDocumentPathTests
{
    [Fact]
    public void A_purchase_document_is_keyed_under_the_business_purchases_prefix()
    {
        var name = BlobDocumentPath.For(
            BusinessId.From(7), DocumentCategory.PurchaseDocument, "9f0c.jpg");

        Assert.Equal("tenants/7/purchases/9f0c.jpg", name);
    }

    [Fact]
    public void An_expense_attachment_is_keyed_under_the_business_expenses_prefix()
    {
        var name = BlobDocumentPath.For(
            BusinessId.From(7), DocumentCategory.ExpenseAttachment, "9f0c.pdf");

        Assert.Equal("tenants/7/expenses/9f0c.pdf", name);
    }

    /// <summary>
    /// The same stored name under two businesses must be two different blobs. Stored names are
    /// server-generated GUIDs, but nothing about the scheme may depend on that: a collision has
    /// to be two documents, never one shared document.
    /// </summary>
    [Fact]
    public void The_same_stored_name_keys_a_different_blob_for_each_business()
    {
        var first = BlobDocumentPath.For(BusinessId.From(1), DocumentCategory.PurchaseDocument, "same.jpg");
        var second = BlobDocumentPath.For(BusinessId.From(2), DocumentCategory.PurchaseDocument, "same.jpg");

        Assert.NotEqual(first, second);
        Assert.Equal("tenants/1/purchases/same.jpg", first);
        Assert.Equal("tenants/2/purchases/same.jpg", second);
    }

    [Fact]
    public void The_two_categories_never_share_a_prefix()
    {
        var purchases = BlobDocumentPath.PrefixFor(BusinessId.From(3), DocumentCategory.PurchaseDocument);
        var expenses = BlobDocumentPath.PrefixFor(BusinessId.From(3), DocumentCategory.ExpenseAttachment);

        Assert.Equal("tenants/3/purchases/", purchases);
        Assert.Equal("tenants/3/expenses/", expenses);
    }

    /// <summary>
    /// A stored name is metadata, not input - no endpoint accepts one - but it is still reduced
    /// to a single segment before it can reach the service. The Blob service canonicalises
    /// nothing, so <c>tenants/1/purchases/../../2/purchases/x</c> would be a real blob under
    /// another business's prefix rather than an error.
    /// </summary>
    [Theory]
    [InlineData("../../2/purchases/stolen.jpg", "stolen.jpg")]
    [InlineData("..\\..\\2\\purchases\\stolen.jpg", "stolen.jpg")]
    [InlineData("tenants/2/expenses/invoice.pdf", "invoice.pdf")]
    [InlineData("/etc/passwd", "passwd")]
    [InlineData("C:\\secrets\\key.txt", "key.txt")]
    public void A_stored_name_is_reduced_to_its_last_segment(string storedFileName, string expectedFileName)
    {
        var name = BlobDocumentPath.For(BusinessId.From(1), DocumentCategory.PurchaseDocument, storedFileName);

        Assert.Equal($"tenants/1/purchases/{expectedFileName}", name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("purchases/")]
    [InlineData("name\u0000.jpg")]
    public void A_stored_name_that_reduces_to_nothing_usable_has_no_blob_name(string? storedFileName)
    {
        Assert.Null(BlobDocumentPath.For(BusinessId.From(1), DocumentCategory.PurchaseDocument, storedFileName));
        Assert.Null(BlobDocumentPath.ReduceToFileName(storedFileName));
    }
}
