namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>grandfather-business-plans</c> command line (issue #966).
///
/// <para>Separate from the command itself so the rules are testable without a database, and strict
/// for the reason <see cref="NayaxConnectionMigrationArguments"/> is: this command writes an
/// entitlement every existing business then depends on, so an argument list whose meaning is not
/// obvious is refused rather than resolved by precedence. <c>--apply --dry-run</c> is an error, not
/// an apply, and an invocation with neither flag is a dry run - a half-remembered command must not
/// start granting plans.</para>
///
/// <para>There is deliberately no flag at all for the cutoff, the plan, a business or a date.
/// The cutoff is the instant the first run recorded and nothing can move it; the plan is the one in
/// <c>GrandfatheringEligibilityPolicy</c>; and which businesses are eligible is that policy's
/// answer, never an operator's list. A command line that could name a business would be a way to
/// grant Pro to one that was not entitled to it.</para>
/// </summary>
public static class BusinessPlanGrandfatheringArguments
{
    public const string CommandName = "grandfather-business-plans";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the requested mode.</summary>
    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">
    /// True only when <c>--apply</c> was given on its own. Omitting both flags is a dry run.
    /// </param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out bool apply, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        apply = false;
        error = string.Empty;

        var sawApply = false;
        var sawDryRun = false;

        foreach (var argument in args.Skip(1))
        {
            if (string.Equals(argument, ApplyFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawApply = true;
            }
            else if (string.Equals(argument, DryRunFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawDryRun = true;
            }
            else
            {
                // An unrecognised flag most likely means the operator believed this command takes
                // something it does not - a cutoff date, a plan or a business. Guessing is not worth
                // the convenience.
                error = $"Unrecognised argument '{argument}'. "
                    + $"Usage: {CommandName} [{DryRunFlag} | {ApplyFlag}]";
                return false;
            }
        }

        if (sawApply && sawDryRun)
        {
            error = $"{ApplyFlag} and {DryRunFlag} are mutually exclusive. "
                + "Pass exactly one, or neither for a dry run.";
            return false;
        }

        apply = sawApply;
        return true;
    }
}
