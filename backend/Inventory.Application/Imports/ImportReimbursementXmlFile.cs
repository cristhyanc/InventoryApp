using Inventory.Application.Providers;
using Inventory.Application.Time;
using Inventory.Domain.Providers;
using Inventory.Domain.Reporting.Availability;

namespace Inventory.Application.Imports;

/// <summary>What happened to one parsed reimbursement XML file (issue #1149).</summary>
/// <param name="WasImported">
/// Whether this file's rows were committed by this call. <see langword="false"/> means this business
/// had already imported a file with the same bytes and nothing was written.
/// </param>
/// <param name="ImportedReimbursements">
/// How many reimbursement rows were committed, which is zero for a duplicate.
/// </param>
public readonly record struct ReimbursementXmlFileImportOutcome(
    bool WasImported,
    int ImportedReimbursements)
{
    /// <summary>The outcome of a file whose bytes this business had already imported.</summary>
    public static ReimbursementXmlFileImportOutcome Skipped { get; } = new(false, 0);
}

/// <summary>
/// The one place a parsed reimbursement XML file becomes stored reimbursement data (issue #1149):
/// hash dedupe, then the atomic write of the file with its reimbursement graph, then the settlement
/// coverage the committed file proves.
///
/// <para><strong>Why it is its own type.</strong> It was extracted when a reimbursement report
/// reached this application two ways - the server's pending folder (issue #299, retired by issue
/// #1237) and an uploaded Nayax ZIP (issue #1149) - because both had to do exactly the same thing
/// with a file once it had been parsed. Two copies of "skip the duplicate, write the rows, record
/// the coverage" is how a settlement import quietly starts recording coverage on one path and not
/// the other. The upload is now the only caller, and the steps stay here: what a parsed file becomes
/// is one decision, separate from where the file came from and what the caller reports.</para>
///
/// <para><strong>It deliberately owns no transaction.</strong> The uploaded-archive worker wraps
/// this call and its own per-file outcome row in one explicit transaction, so a file is never
/// committed without the record of what happened to it. Which unit of work a caller needs is the
/// caller's own decision.</para>
///
/// <para><strong>A persistence failure is not caught here</strong>, exactly as it was not before: it
/// propagates to the caller, which lets the worker roll its transaction back and count an attempt
/// rather than record the file as one more failed entry.</para>
/// </summary>
public sealed class ImportReimbursementXmlFile
{
    private readonly IImportedReimbursementStore _store;
    private readonly IClock _clock;
    private readonly IProviderDataCoverageStore _coverage;

    public ImportReimbursementXmlFile(
        IImportedReimbursementStore store,
        IClock clock,
        IProviderDataCoverageStore coverage)
    {
        _store = store;
        _clock = clock;
        _coverage = coverage;
    }

    /// <summary>
    /// Imports one parsed file, or reports it as a duplicate this business already holds.
    /// </summary>
    public async Task<ReimbursementXmlFileImportOutcome> Handle(
        PendingReimbursementXmlFile file, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (await _store.HasFileWithContentHashAsync(file.ContentHash, cancellationToken))
            return ReimbursementXmlFileImportOutcome.Skipped;

        await _store.ImportAsync(file, _clock.UtcNow, cancellationToken);

        // The periods this file accounts for are recorded only now, which is the one circumstance a
        // coverage record may be written in: the whole file is committed at this point. A file that
        // could not be parsed never reaches this line, and a duplicate returns above - its coverage
        // was recorded by the import that first committed those same bytes.
        await RecordCoverageAsync(file, cancellationToken);

        return new ReimbursementXmlFileImportOutcome(true, file.Reimbursements.Count);
    }

    /// <summary>
    /// Records the settlement periods a committed file accounted for (issue #730, parent #329
    /// decision P9) - <b>one coverage row per period the file proves, never one row spanning them
    /// all</b>. Moved here unchanged in behaviour from the pending-folder import (issue #299, since
    /// retired) when issue #1149 gave the step a second caller.
    ///
    /// <para>Each reimbursement row declares its own settled period through
    /// <c>ReimbursementStartDate</c>/<c>ReimbursementEndDate</c>, which is the provider's own
    /// statement of what that row settles (AGENTS.md § Reimbursements and reconciliation: a
    /// reimbursement belongs to its coverage dates, not its payout or import date). Those declared
    /// intervals are the evidence, so they are recorded as they are: overlapping and exactly
    /// touching rows merge into one range through <see cref="ReportCoverageRange.Merge"/>, and a
    /// <b>gap between two rows is never bridged</b>. A file holding July and September settlements
    /// proves nothing whatsoever about August, and pooling the earliest start with the latest end
    /// would have claimed it - exactly the false completeness this record exists to prevent.</para>
    ///
    /// <para>Only a row carrying <b>both</b> of its own dates declares an interval; one carrying a
    /// start or an end alone declares none, and a period is never inferred from a payout date, an
    /// import instant or another row's dates. A file that declares nothing records nothing, and the
    /// affected report stays unverified instead of falsely complete. The end date is an inclusive
    /// calendar day, so the exclusive end of the coverage is the following midnight.</para>
    ///
    /// <para>The claim is business-wide because the row is: a reimbursement row is the operator
    /// account's own settlement statement for its period - it carries the account's company name,
    /// customer id and device counts, and every device it settles is nested beneath it - and the
    /// fee figures the reports show are summed from exactly these rows, for every device in them
    /// (<c>EfReportingSharedQueries.ImportedSummaryAsync</c>). Coverage therefore claims the same
    /// scope as the figures it governs, rather than a wider one. A machine-filtered report narrows
    /// which stored rows a figure counts and already says in a data-quality note that imported fees
    /// are account-level and unallocated; it never narrows what coverage was recorded.</para>
    /// </summary>
    private async Task RecordCoverageAsync(
        PendingReimbursementXmlFile file, CancellationToken cancellationToken)
    {
        var declared = file.Reimbursements
            .Where(row => row.ReimbursementStartDate.HasValue && row.ReimbursementEndDate.HasValue)
            .Select(row => new ReportCoverageRange(
                row.ReimbursementStartDate!.Value, row.ReimbursementEndDate!.Value.Date.AddDays(1)));

        foreach (var range in ReportCoverageRange.Merge(declared))
        {
            await _coverage.RecordAsync(
                ExternalProvider.Nayax,
                ProviderDataSource.SettlementImport,
                range.FromUtc,
                range.ToUtc,
                cancellationToken);
        }
    }
}
