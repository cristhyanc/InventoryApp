namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>migrate-nayax-connection</c> command line (issue #519, a slice of #500).
///
/// Separate from the command itself so the rules are testable without a database, a configured
/// token or an encryption key, and strict for the same reason
/// <see cref="BusinessBootstrapArguments"/> is: this command moves a production credential, so an
/// argument list whose meaning is not obvious is refused rather than resolved by precedence.
/// <c>--apply --dry-run</c> is an error, not an apply.
///
/// It follows <c>bootstrap-business</c> and <c>migrate-database</c> rather than
/// <c>migrate-documents</c> in treating a bare invocation as a dry run: a half-remembered command
/// must not write a credential.
/// </summary>
public static class NayaxConnectionMigrationArguments
{
    public const string CommandName = "migrate-nayax-connection";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the requested mode.
    /// </summary>
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
                // An unrecognised flag most likely means the operator believed this command does
                // something it does not - naming a business or a token on the command line, for
                // example, neither of which it accepts. Guessing is not worth the convenience.
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
