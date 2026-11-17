using System.Globalization;

namespace Inventory.Domain.Imports;

/// <summary>
/// How large an uploaded Nayax sales file may be before the import refuses it (issue #1300, the
/// repository owner's limits of 14 November 2026).
///
/// <para><strong>Both numbers are here and nowhere else</strong>, for the reason
/// <see cref="ReimbursementArchivePolicy"/> states for its own: a limit written at the place the
/// bytes happen to be read is a limit the next reader forgets. The workbook adapter enforces them -
/// it is the only code that can, because the memory is spent while the file is being read - and the
/// use case turns an exceeded limit into the operator's refusal; this type says what they are and
/// what the operator is told.</para>
///
/// <para><strong>They exist because one upload used to decide how much memory the whole server
/// had.</strong> A 9.9 MB CSV of 114,250 rows allocated about 1 GB just to be read, and the save it
/// then ran held the database's single write lock for minutes, which every other write - a purchase,
/// a stock adjustment, the Nayax SQS queue reader's own save - waits on and then fails after its
/// 30-second busy timeout. The cap is what makes the cost of one upload bounded and the lock it
/// takes short; a longer history is a different operation, which is why the refusal names it.</para>
/// </summary>
public static class NayaxSalesFilePolicy
{
    /// <summary>
    /// How many data rows an uploaded file may carry: 20,000. A busy machine fleet exports a few
    /// thousand transactions a month, so this is a quarter or more of real history in one file, and
    /// it is the figure the measured write-lock budget below was chosen against.
    /// </summary>
    public const int MaxRows = 20_000;

    /// <summary>
    /// How far an uploaded XLSX's sheet data may expand: 50 MB uncompressed, summed from the ZIP
    /// entry sizes of the worksheet parts and the shared string table <em>before</em> the workbook is
    /// opened.
    ///
    /// <para>It is a second, independent limit rather than a restatement of <see cref="MaxRows"/>,
    /// because the row count of an XLSX is not knowable until it has been loaded - and loading it is
    /// exactly the cost the limit exists to refuse. A spreadsheet is a compressed archive, so 10 MB
    /// of upload is hundreds of megabytes of XML; this limit is generous for any file inside the row
    /// limit (20,000 rows of this export is a few megabytes of sheet XML) and refuses the file that
    /// would be read into memory before anything could count it.</para>
    /// </summary>
    public const long MaxUncompressedSheetBytes = 50L * 1024 * 1024;

    /// <summary>
    /// How long the import's write-locked save may hold SQLite's write lock at
    /// <see cref="MaxRows"/>: 10 seconds, the owner's decision of 14 November 2026. Every other
    /// write waits on that lock and gives up after the 30-second busy timeout, so this is the budget
    /// <see cref="MaxRows"/> is chosen to fit - if a measured save at the row limit exceeds it, the
    /// row limit comes down, not this.
    /// </summary>
    public const int MaxWriteLockHoldSeconds = 10;

    /// <summary>
    /// What an operator whose file is beyond either limit is told. It names the row limit because
    /// that is the limit a person can act on: a file whose sheet data expands past
    /// <see cref="MaxUncompressedSheetBytes"/> carries far more rows than
    /// <see cref="MaxRows"/>, and "split it, or import a history" is the same answer in both cases.
    /// The number comes from the constant, so lowering the limit cannot leave the sentence saying
    /// something the import no longer does.
    /// </summary>
    public static string TooLargeMessage =>
        $"This file has more than {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} sales. "
        + "For a longer history, export one file per quarter, or use the sales history import.";
}
