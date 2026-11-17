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

    /// <summary>
    /// Issue #1257: the three amounts have to describe one expense, because profit uses the total
    /// and the GST credit uses the GST. A cent of rounding either way is the tolerance a supplier's
    /// own document legitimately needs; two cents is a typo. The sub-cent row is the one the
    /// expense form is held to as well: the rule reads the submitted values, so a difference of
    /// `0.009` spread over three decimals is inside the tolerance and is accepted here and there.
    /// </summary>
    [Theory]
    [InlineData(10, 1, 11)]
    [InlineData(10, 1, 11.01)]
    [InlineData(10, 1, 10.99)]
    [InlineData(100, 10, 110.01)]
    [InlineData(1.006, 0.006, 1.003)]
    [InlineData(0, 0, 0)]
    public void TryCreate_amounts_within_a_cent_of_the_total_succeed(
        decimal amountExGst, decimal gstAmount, decimal totalAmount)
    {
        var created = OperatingExpenseDetails.TryCreate(
            "Insurance", amountExGst, gstAmount, totalAmount, null, null, out var details, out var error);

        Assert.True(created);
        Assert.Null(error);
        Assert.NotNull(details);
    }

    [Theory]
    [InlineData(10, 1, 11.02)]
    [InlineData(10, 1, 10.98)]
    [InlineData(100, 50, 110)]
    [InlineData(10, 0, 11)]
    // A sub-cent discrepancy that is still more than a cent (`0.015`), refused here and by the form.
    [InlineData(1.004, 0.004, 0.993)]
    public void TryCreate_amounts_more_than_a_cent_from_the_total_fail(
        decimal amountExGst, decimal gstAmount, decimal totalAmount)
    {
        var created = OperatingExpenseDetails.TryCreate(
            "Insurance", amountExGst, gstAmount, totalAmount, null, null, out var details, out var error);

        Assert.False(created);
        Assert.Null(details);
        Assert.Equal(OperatingExpenseDetails.AmountsMustAddUpMessage, error);
    }

    /// <summary>
    /// A negative amount keeps its own refusal: it is the more specific thing wrong with the
    /// submission, and the sentence a caller already receives for it does not change.
    /// </summary>
    [Fact]
    public void TryCreate_negative_amount_keeps_its_own_message_even_when_the_amounts_disagree()
    {
        var created = OperatingExpenseDetails.TryCreate(
            "Insurance", -10m, 1m, 11m, null, null, out var details, out var error);

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
