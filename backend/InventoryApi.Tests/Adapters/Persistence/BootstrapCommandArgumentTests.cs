using InventoryApi.Bootstrap;
using Xunit;

namespace InventoryApi.Tests.Adapters.Persistence;

/// <summary>
/// Both operator commands rewrite production state - one the schema, one ownership of every
/// financial record - so an argument list whose meaning is not obvious must be refused rather
/// than resolved by precedence (issue #64).
///
/// The case that matters most is <c>--apply --dry-run</c>. A reader of that command cannot tell
/// which the author intended, and letting <c>--apply</c> quietly win would turn a typo made while
/// reaching for safety into a live mutation.
/// </summary>
public class BootstrapCommandArgumentTests
{
    #region bootstrap-business

    [Fact]
    public void Bootstrap_with_no_flags_is_a_dry_run()
    {
        Assert.True(BusinessBootstrapArguments.TryParse(["bootstrap-business"], out var apply, out var error));

        Assert.False(apply);
        Assert.Empty(error);
    }

    [Fact]
    public void Bootstrap_dry_run_flag_is_a_dry_run()
    {
        Assert.True(BusinessBootstrapArguments.TryParse(
            ["bootstrap-business", "--dry-run"], out var apply, out _));

        Assert.False(apply);
    }

    [Fact]
    public void Bootstrap_apply_flag_alone_applies()
    {
        Assert.True(BusinessBootstrapArguments.TryParse(
            ["bootstrap-business", "--apply"], out var apply, out _));

        Assert.True(apply);
    }

    /// <summary>Both flags together is an error, and specifically not an apply.</summary>
    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--dry-run", "--apply")]
    public void Bootstrap_with_both_flags_is_refused_and_does_not_apply(string first, string second)
    {
        Assert.False(BusinessBootstrapArguments.TryParse(
            ["bootstrap-business", first, second], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("mutually exclusive", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--yes")]
    [InlineData("-a")]
    [InlineData("apply")]
    [InlineData("--APPLY=true")]
    public void Bootstrap_with_an_unrecognised_argument_is_refused(string argument)
    {
        Assert.False(BusinessBootstrapArguments.TryParse(
            ["bootstrap-business", argument], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("Unrecognised argument", error, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unrecognised argument is refused even when a valid --apply is also present, so a
    /// mistyped flag cannot ride along with a real one.
    /// </summary>
    [Fact]
    public void Bootstrap_refuses_an_unrecognised_argument_even_alongside_apply()
    {
        Assert.False(BusinessBootstrapArguments.TryParse(
            ["bootstrap-business", "--apply", "--force"], out var apply, out _));

        Assert.False(apply);
    }

    [Theory]
    [InlineData("--APPLY")]
    [InlineData("--Dry-Run")]
    public void Bootstrap_flags_are_case_insensitive(string flag)
    {
        Assert.True(BusinessBootstrapArguments.TryParse(["bootstrap-business", flag], out _, out var error));

        Assert.Empty(error);
    }

    [Fact]
    public void Bootstrap_matches_only_its_own_command_name()
    {
        Assert.True(BusinessBootstrapArguments.Matches(["bootstrap-business"]));
        Assert.False(BusinessBootstrapArguments.Matches(["migrate-database"]));
        Assert.False(BusinessBootstrapArguments.Matches([]));
    }

    #endregion

    #region migrate-database

    [Fact]
    public void Migration_with_no_flags_only_lists_pending_migrations()
    {
        Assert.True(DatabaseMigrationArguments.TryParse(["migrate-database"], out var apply, out _));

        Assert.False(apply);
    }

    [Fact]
    public void Migration_apply_flag_alone_applies()
    {
        Assert.True(DatabaseMigrationArguments.TryParse(["migrate-database", "--apply"], out var apply, out _));

        Assert.True(apply);
    }

    [Theory]
    [InlineData("--apply", "--dry-run")]
    [InlineData("--dry-run", "--apply")]
    public void Migration_with_both_flags_is_refused_and_does_not_apply(string first, string second)
    {
        Assert.False(DatabaseMigrationArguments.TryParse(
            ["migrate-database", first, second], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("mutually exclusive", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--force")]
    [InlineData("--target")]
    public void Migration_with_an_unrecognised_argument_is_refused(string argument)
    {
        Assert.False(DatabaseMigrationArguments.TryParse(
            ["migrate-database", argument], out var apply, out var error));

        Assert.False(apply);
        Assert.Contains("Unrecognised argument", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Migration_matches_only_its_own_command_name()
    {
        Assert.True(DatabaseMigrationArguments.Matches(["migrate-database"]));
        Assert.False(DatabaseMigrationArguments.Matches(["bootstrap-business"]));
        Assert.False(DatabaseMigrationArguments.Matches([]));
    }

    #endregion
}
