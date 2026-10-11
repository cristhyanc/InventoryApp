using Inventory.Domain.Tenancy;
using Xunit;

namespace InventoryApi.Tests.Domain.Tenancy;

/// <summary>
/// The role vocabulary and the checks that keep an undeclared value out of an access decision
/// (issue #521).
///
/// These are security boundary tests rather than enum trivia: every value that reaches a role
/// comes from outside the code - a stored integer column or an operator's configuration string -
/// and the only safe answer for a value that is not a declared role is to refuse it.
/// </summary>
public class BusinessRolesTests
{
    [Fact]
    public void The_vocabulary_is_exactly_the_three_agreed_roles()
    {
        Assert.Equal(
            [BusinessRole.Operator, BusinessRole.Manager, BusinessRole.Owner],
            BusinessRoles.All);
    }

    /// <summary>
    /// <c>0</c> must not be a role. It is the value an unset integer column and a
    /// default-constructed enum both hold, so a role declared at <c>0</c> would be handed out by
    /// omission - and it would be handed out as whichever role happened to sit there.
    /// </summary>
    [Fact]
    public void Zero_is_not_a_declared_role()
    {
        Assert.False(BusinessRoles.IsSupported(default));
        Assert.DoesNotContain(0, BusinessRoles.All.Select(role => (int)role));
    }

    /// <summary>
    /// The stored values are spaced so the read-only Viewer role #328 anticipates can be added
    /// below <see cref="BusinessRole.Operator"/> without renumbering a value that is already
    /// persisted in a membership row.
    /// </summary>
    [Fact]
    public void The_stored_values_leave_room_for_a_less_privileged_role()
    {
        Assert.True((int)BusinessRole.Operator > 1);
        Assert.Equal(
            BusinessRoles.All.Select(role => (int)role).Distinct().Count(),
            BusinessRoles.All.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(21)]
    [InlineData(99)]
    [InlineData(-1)]
    public void An_undeclared_value_is_unsupported_and_is_refused(int value)
    {
        var role = (BusinessRole)value;

        Assert.False(BusinessRoles.IsSupported(role));
        Assert.Throws<ArgumentOutOfRangeException>(() => BusinessRoles.Require(role));
    }

    [Theory]
    [InlineData(BusinessRole.Operator)]
    [InlineData(BusinessRole.Manager)]
    [InlineData(BusinessRole.Owner)]
    public void A_declared_role_is_supported_and_passes_through(BusinessRole role)
    {
        Assert.True(BusinessRoles.IsSupported(role));
        Assert.Equal(role, BusinessRoles.Require(role));
    }

    [Theory]
    [InlineData("Owner", BusinessRole.Owner)]
    [InlineData("owner", BusinessRole.Owner)]
    [InlineData("MANAGER", BusinessRole.Manager)]
    [InlineData("Operator", BusinessRole.Operator)]
    [InlineData("  Manager  ", BusinessRole.Manager)]
    public void A_declared_role_name_parses_case_insensitively(string name, BusinessRole expected)
    {
        Assert.True(BusinessRoles.TryParse(name, out var role));
        Assert.Equal(expected, role);
    }

    /// <summary>
    /// A numeric string is deliberately refused. <c>Enum.TryParse</c> on its own would turn
    /// <c>"40"</c> into <see cref="BusinessRole.Owner"/> and <c>"99"</c> into an undeclared role,
    /// which would let configuration supply a stored representation instead of naming a role.
    /// </summary>
    [Theory]
    [InlineData("40")]
    [InlineData("20")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("Boss")]
    [InlineData("Manger")]
    [InlineData("Owner, Manager")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Anything_that_is_not_a_declared_role_name_does_not_parse(string? name)
    {
        Assert.False(BusinessRoles.TryParse(name, out var role));
        Assert.False(BusinessRoles.IsSupported(role));
    }

    [Fact]
    public void The_rejection_message_names_the_whole_vocabulary()
    {
        foreach (var role in BusinessRoles.All)
        {
            Assert.Contains(role.ToString(), BusinessRoles.UnsupportedMessage, StringComparison.Ordinal);
        }
    }
}
