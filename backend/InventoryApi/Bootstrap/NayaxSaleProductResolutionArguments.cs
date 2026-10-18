using System.Globalization;
using Inventory.Application.SaleProductResolution;

namespace InventoryApi.Bootstrap;

/// <summary>
/// Parses the <c>resolve-nayax-sale-products</c> command line (issue #758).
///
/// Separate from the command itself so the rules are testable without a database, and strict for
/// the reason <see cref="NayaxConnectionMigrationArguments"/> is: this command writes a financial
/// attribution, so an argument list whose meaning is not obvious is refused rather than resolved by
/// precedence. <c>--apply --dry-run</c> is an error, not an apply.
///
/// <para><strong>A decision names a sale, never a business.</strong> <c>--decide
/// &lt;saleId&gt;=&lt;productId&gt;</c> uses the sale's own local key, which the dry run prints and
/// which is unique across the database; the business is then the one the stored sale belongs to,
/// read by the command rather than typed by the operator. There is deliberately no
/// <c>--business-id</c> flag.</para>
/// </summary>
public static class NayaxSaleProductResolutionArguments
{
    public const string CommandName = "resolve-nayax-sale-products";
    public const string ApplyFlag = "--apply";
    public const string DryRunFlag = "--dry-run";
    public const string DecideFlag = "--decide";
    public const string DecidedByFlag = "--decided-by";

    public const string Usage =
        $"Usage: {CommandName} [{DryRunFlag} | {ApplyFlag}] "
            + $"[{DecideFlag} <saleId>=<productId>]... [{DecidedByFlag} <who>]";

    public static bool Matches(string[] args) =>
        args.Length > 0 && string.Equals(args[0], CommandName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Resolves the requested mode, decisions and decider.</summary>
    /// <param name="args">The full argument list, including the command name at position 0.</param>
    /// <param name="apply">True only when <c>--apply</c> was given. Omitting both flags is a dry run.</param>
    /// <param name="decisions">Each <c>--decide</c> pair, in the order given.</param>
    /// <param name="decidedBy">The identity recorded on every audit row this run writes.</param>
    /// <param name="error">Operator-facing reason the arguments were rejected, or empty.</param>
    public static bool TryParse(
        string[] args,
        out bool apply,
        out IReadOnlyList<NayaxSaleProductDecision> decisions,
        out string decidedBy,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        apply = false;
        decidedBy = string.Empty;
        error = string.Empty;
        var parsed = new List<NayaxSaleProductDecision>();
        decisions = parsed;

        var sawApply = false;
        var sawDryRun = false;

        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];

            if (string.Equals(argument, ApplyFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawApply = true;
            }
            else if (string.Equals(argument, DryRunFlag, StringComparison.OrdinalIgnoreCase))
            {
                sawDryRun = true;
            }
            else if (string.Equals(argument, DecideFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length)
                {
                    error = $"{DecideFlag} needs a <saleId>=<productId> pair. {Usage}";
                    return false;
                }

                if (!TryParsePair(args[++index], out var decision))
                {
                    error = $"'{args[index]}' is not a <saleId>=<productId> pair of positive whole "
                        + $"numbers. {Usage}";
                    return false;
                }

                parsed.Add(decision);
            }
            else if (string.Equals(argument, DecidedByFlag, StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
                {
                    error = $"{DecidedByFlag} needs the identity to record on the audit rows. {Usage}";
                    return false;
                }

                decidedBy = args[++index].Trim();
            }
            else
            {
                // An unrecognised flag most likely means the operator believed this command does
                // something it does not. Guessing is not worth the convenience when the result is
                // a financial attribution.
                error = $"Unrecognised argument '{argument}'. {Usage}";
                return false;
            }
        }

        if (sawApply && sawDryRun)
        {
            error = $"{ApplyFlag} and {DryRunFlag} are mutually exclusive. "
                + "Pass exactly one, or neither for a dry run.";
            return false;
        }

        if (parsed.Select(decision => decision.SaleId).Distinct().Count() != parsed.Count)
        {
            error = $"The same sale was decided more than once. Pass one {DecideFlag} per sale.";
            return false;
        }

        if (sawApply && parsed.Count > 0 && decidedBy.Length == 0)
        {
            // Who decided is part of the audit row, so an apply cannot write one without it.
            error = $"{DecidedByFlag} is required when applying a decision: the audit row records "
                + "who chose the product.";
            return false;
        }

        apply = sawApply;
        return true;
    }

    private static bool TryParsePair(string value, out NayaxSaleProductDecision decision)
    {
        decision = new NayaxSaleProductDecision(0, 0);

        var separator = value.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
            return false;

        if (!long.TryParse(value[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var saleId)
            || !long.TryParse(value[(separator + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var productId)
            || saleId <= 0
            || productId <= 0)
            return false;

        decision = new NayaxSaleProductDecision(saleId, productId);
        return true;
    }
}
