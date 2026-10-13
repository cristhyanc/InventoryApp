using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The lifecycle-status vocabulary and the check that keeps a status nobody declared out of a
/// stored business row (issue #584).
///
/// These are boundary tests rather than enum trivia: the status is the single thing that decides
/// whether a business's financial data may be reached, so "which values are a status" has to be a
/// question with one answer, and <c>0</c> - the value an omitted assignment and an unfilled integer
/// column both hold - must not be one of them.
/// </summary>
public class BusinessStatusesTests
{
    [Fact]
    public void The_vocabulary_is_exactly_the_four_lifecycle_statuses()
    {
        Assert.Equal(
            [BusinessStatus.Pending, BusinessStatus.Active, BusinessStatus.Rejected, BusinessStatus.Deactivated],
            BusinessStatuses.All);
    }

    /// <summary>
    /// <c>0</c> must not be a status. It is what a write path that forgot to state one hands over,
    /// so a status declared at <c>0</c> would be granted by omission - and <see cref="BusinessStatus"/>
    /// deliberately starts at <c>10</c> for exactly that reason.
    /// </summary>
    [Fact]
    public void Zero_is_not_a_declared_status()
    {
        Assert.False(BusinessStatuses.IsSupported(default));
        Assert.DoesNotContain(0, BusinessStatuses.All.Select(status => (int)status));
    }

    [Theory]
    [InlineData(BusinessStatus.Pending)]
    [InlineData(BusinessStatus.Active)]
    [InlineData(BusinessStatus.Rejected)]
    [InlineData(BusinessStatus.Deactivated)]
    public void Every_declared_status_is_supported(BusinessStatus status)
    {
        Assert.True(BusinessStatuses.IsSupported(status));
    }

    /// <summary>
    /// A value a newer deployment wrote, a negative number, and a plausible-looking one between two
    /// declared statuses are all refused: being close to a status is not being one.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-20)]
    [InlineData(15)]
    [InlineData(50)]
    [InlineData(99)]
    public void An_undeclared_stored_value_is_not_supported(int storedStatus)
    {
        Assert.False(BusinessStatuses.IsSupported((BusinessStatus)storedStatus));
    }

    /// <summary>
    /// The refusal message names every declared status, built from the enum's own member names so
    /// it cannot drift from the vocabulary, and carries no value of its own.
    /// </summary>
    [Fact]
    public void The_unsupported_message_names_the_declared_statuses()
    {
        foreach (var status in BusinessStatuses.All)
        {
            Assert.Contains(status.ToString(), BusinessStatuses.UnsupportedMessage, StringComparison.Ordinal);
        }
    }
}
