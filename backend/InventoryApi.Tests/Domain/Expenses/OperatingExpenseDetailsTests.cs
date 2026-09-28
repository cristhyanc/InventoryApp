using Inventory.Domain.Expenses;
using Xunit;

namespace InventoryApi.Tests.Domain.Expenses;

public class OperatingExpenseDetailsTests
{
    [Fact]
    public void TryCreate_valid_fields_trims_description_and_succeeds()
    {
        var created = OperatingExpenseDetails.TryCreate(
            "  Monthly insurance  ", 10m, 1m, 11m, null, null, out var details, out var error);

        Assert.True(created);
        Assert.Null(error);
        Assert.Equal("Monthly insurance", details!.Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryCreate_blank_description_fails(string description)
    {
        var created = OperatingExpenseDetails.TryCreate(description, 10m, 1m, 11m, null, null, out var details, out var error);

        Assert.False(created);
        Assert.Null(details);
        Assert.Equal(OperatingExpenseDetails.DescriptionRequiredMessage, error);
    }

    [Theory]
    [InlineData(-0.01, 1, 11)]
    [InlineData(10, -0.01, 11)]
    [InlineData(10, 1, -0.01)]
    public void TryCreate_any_negative_amount_fails(decimal amountExGst, decimal gstAmount, decimal totalAmount)
    {
        var created = OperatingExpenseDetails.TryCreate(
            "Insurance", amountExGst, gstAmount, totalAmount, null, null, out var details, out var error);

        Assert.False(created);
        Assert.Null(details);
        Assert.Equal(OperatingExpenseDetails.NegativeAmountsMessage, error);
    }

    [Fact]
    public void TryCreate_service_period_end_before_start_fails()
    {
        var created = OperatingExpenseDetails.TryCreate(
            "Insurance", 10m, 1m, 11m,
            new DateTime(2026, 3, 1), new DateTime(2026, 2, 1),
            out var details, out var error);

        Assert.False(created);
        Assert.Null(details);
        Assert.Equal(OperatingExpenseDetails.ServicePeriodOrderMessage, error);
    }

    [Fact]
    public void TryCreate_service_period_end_equal_to_start_succeeds()
    {
        var period = new DateTime(2026, 3, 1);

        var created = OperatingExpenseDetails.TryCreate("Insurance", 10m, 1m, 11m, period, period, out var details, out var error);

        Assert.True(created);
        Assert.Null(error);
        Assert.NotNull(details);
    }
}
