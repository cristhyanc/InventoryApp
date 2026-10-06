using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Domain.Expenses;

public class ExpenseAttachmentPolicyTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ExpenseAttachmentPolicy.MaxFileSizeBytes + 1)]
    public void IsSizeValid_rejects_zero_negative_and_oversized_lengths(long length)
    {
        Assert.False(ExpenseAttachmentPolicy.IsSizeValid(length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(ExpenseAttachmentPolicy.MaxFileSizeBytes)]
    public void IsSizeValid_accepts_boundary_lengths(long length)
    {
        Assert.True(ExpenseAttachmentPolicy.IsSizeValid(length));
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".JPG")]
    [InlineData(".jpeg")]
    [InlineData(".png")]
    [InlineData(".pdf")]
    [InlineData(".webp")]
    [InlineData(".heic")]
    public void IsExtensionAllowed_accepts_images_and_pdf_case_insensitively(string extension)
    {
        Assert.True(ExpenseAttachmentPolicy.IsExtensionAllowed(extension));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData(".txt")]
    [InlineData("")]
    public void IsExtensionAllowed_rejects_everything_else(string extension)
    {
        Assert.False(ExpenseAttachmentPolicy.IsExtensionAllowed(extension));
    }

    [Theory]
    [InlineData(".jpg", "image/jpeg")]
    [InlineData(".jpeg", "image/jpeg")]
    [InlineData(".png", "image/png")]
    [InlineData(".pdf", "application/pdf")]
    [InlineData(".webp", "image/webp")]
    [InlineData(".heic", "image/heic")]
    [InlineData(".exe", "application/octet-stream")]
    public void ContentTypeFor_maps_each_allowed_extension_and_falls_back_for_unknown(string extension, string expected)
    {
        Assert.Equal(expected, ExpenseAttachmentPolicy.ContentTypeFor(extension));
    }
}
