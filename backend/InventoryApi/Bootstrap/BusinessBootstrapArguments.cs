namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>bootstrap-business</c> command line (issue #64, checkpoint 3).
///
/// Separate from the command itself so the rules are testable without touching a database, and
/// strict because this command writes to real financial history: an argument list whose meaning
/// is not obvious is rejected rather than resolved by precedence. In particular
/// <c>--apply --dry-run</c> is an error, not an apply - a reader of that command cannot tell
/// which the author meant, so neither should the program.
/// </summary>
public static class BusinessBootstrapArguments
{
    public const string CommandName = "bootstrap-business";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the requested mode.
    /// </summary>
    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">
    /// True only when <c>--apply</c> was given on its own. Omitting both flags is a dry run, so
    /// a half-remembered command cannot mutate data.
    /// </param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(string[] args, out bool apply, out string error)
    {
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
                // An unrecognised flag most likely means the operator believed they were asking
                // for something this command does not do. Guessing what they meant, on an
                // operation that rewrites ownership, is not worth the convenience.
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
