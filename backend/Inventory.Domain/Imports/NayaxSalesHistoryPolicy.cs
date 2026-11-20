using System.Globalization;

namespace Inventory.Domain.Imports;

/// <summary>
/// What an uploaded Nayax <em>sales history</em> file may be, and how much of it one write holds the
/// database for (issue #1304, the repository owner's decision of 14 November 2026 that a long sales
/// history is imported by a background job in small batches).
///
/// <para><strong>Every number is here and nowhere else</strong>, for the reason
/// <see cref="NayaxSalesFilePolicy"/> states for the normal upload's two: a limit written at the
/// place the bytes happen to be read is a limit the next reader forgets. The CSV reader enforces the
/// row ceiling while it is streaming, the upload use case turns an exceeded limit into the
/// operator's refusal, and the job reads and commits in batches of <see cref="BatchRows"/>; this type
/// says what the numbers are and what the operator is told.</para>
///
/// <para><strong>This is a different operation from the normal upload, not a wider version of
/// it.</strong> <see cref="NayaxSalesFilePolicy"/> keeps its 5,000-row ceiling exactly as issue #1300
/// delivered it, because that import reads the whole file and saves it in one write-locked
/// transaction. A history is accepted at fifty times that size precisely because nothing here does
/// either: the rows are streamed one at a time and committed <see cref="BatchRows"/> at a time, each
/// batch in its own short transaction, so what one upload costs in memory stops growing with the
/// rows and what it holds SQLite's single write lock for stays inside the same
/// <see cref="MaxWriteLockHoldSeconds"/> budget the normal upload is measured against.</para>
///
/// <para><strong>The row rules are not here at all.</strong> A history row is applied by exactly the
/// rules <c>Inventory.Application.Imports.ImportNayaxSales</c> applies - the same identity, instant,
/// blank-cell, conflict and costing rules, through the same code - so this file holds limits and
/// sentences and no notion of what a row means.</para>
/// </summary>
public static class NayaxSalesHistoryPolicy
{
    /// <summary>
    /// How many data rows an uploaded history may carry: 250,000 - comfortably one to three years of
    /// a real machine fleet, which is what the feature exists for.
    ///
    /// <para>It is a ceiling on the <em>file</em>, not on a write: nothing is ever read or committed
    /// 250,000 rows at a time (see <see cref="BatchRows"/>). What makes a number this large safe is
    /// that the cost of one more row is one more row - a streamed read and a place in the next batch -
    /// rather than a larger allocation and a longer lock.</para>
    /// </summary>
    public const int MaxRows = 250_000;

    /// <summary>
    /// The largest upload the endpoint accepts: 50 MB. The real export is about 87 bytes a row, so
    /// this is generous for a file inside <see cref="MaxRows"/> and still a bounded number of bytes
    /// the API reads, stores and later streams back out of the job row.
    /// </summary>
    public const long MaxUploadBytes = 50L * 1024 * 1024;

    /// <summary>
    /// How many rows one batch applies and commits: 500.
    ///
    /// <para><strong>It is the figure the measured write-lock budget allows, which is why it is not
    /// the 2,000 the issue suggested.</strong> That suggestion came with the budget below and the
    /// same rule issue #1300 applied to its own row limit: if the measured hold exceeds the budget,
    /// the number comes down. It did. At 2,000 rows a batch, a synthetic history measured 3.4 s,
    /// 5.6 s and 9.3 s for its first three batches on this repository's CI runners - growing, and the
    /// third already at the edge of <see cref="MaxWriteLockHoldSeconds"/>.</para>
    ///
    /// <para><strong>Why it grows, which is the thing to understand about this number.</strong> Each
    /// row's historical cost is decided by replaying that product's own cost ledger up to the sale's
    /// instant (<c>Inventory.Application.Costing.CostSale</c>, the same rule the uploaded import and
    /// the queue reader apply), and that replay reads the product's stored stock-consuming sales
    /// before that instant. So the cost of row <em>n</em> of a product grows with the sales of that
    /// product already stored, and a long history makes its own later rows slower. A batch size
    /// therefore buys margin rather than a constant: 500 rows is a quarter of the measured hold, and
    /// the pull request reports the figures it was chosen from.</para>
    ///
    /// <para>Between batches the lock is released, which is what lets the Nayax queue reader and an
    /// ordinary user save commit while a history is importing - and what makes a history of 250,000
    /// rows a long background job rather than a database nobody else can write to.</para>
    /// </summary>
    public const int BatchRows = 500;

    /// <summary>
    /// How long one batch may hold SQLite's write lock: the same 10-second budget the normal upload
    /// is measured against (<see cref="NayaxSalesFilePolicy.MaxWriteLockHoldSeconds"/>), because it
    /// is the same lock and the same thirty-second busy timeout every other writer in the process
    /// gives up after. Stated as its own constant so the batch size can be measured against it, and
    /// deliberately equal rather than larger: a background job is not a reason to make a purchase
    /// wait longer.
    /// </summary>
    public const int MaxWriteLockHoldSeconds = NayaxSalesFilePolicy.MaxWriteLockHoldSeconds;

    /// <summary>
    /// How many attempts one job is allowed in total, counted when an attempt <em>starts</em>: 3.
    /// Counting at the start is what bounds a job whose attempt kills the process - it has already
    /// spent one - and an attempt resumes from the rows already committed rather than redoing them.
    /// </summary>
    public const int MaxAttempts = 3;

    /// <summary>How long a recorded upload file name may be, on the reimbursement import's terms.</summary>
    public const int MaxFileNameLength = 200;

    /// <summary>The one format a history may be uploaded in, compared case-insensitively.</summary>
    public const string CsvExtension = ".csv";

    /// <summary>What a caller who uploaded nothing is told.</summary>
    public const string EmptyUploadMessage = "A CSV file is required.";

    /// <summary>
    /// What a caller who uploaded a spreadsheet is told. An XLSX is deliberately not accepted here:
    /// a compressed workbook cannot be read row by row without expanding it, which is the whole cost
    /// this import exists to avoid, so the refusal says what to do about it.
    /// </summary>
    public const string UnsupportedFormatMessage =
        "A sales history must be a .csv file. Export the history from Nayax Core as CSV.";

    /// <summary>
    /// What a caller whose file is readable but holds no sales row is told. It is a refusal rather
    /// than a job that completes having done nothing: an operator who uploaded the wrong file needs
    /// to be told so now, not by a job that reports zero of everything.
    /// </summary>
    public const string NoRowsMessage =
        "The uploaded file has no sales rows below its column headings.";

    /// <summary>What a caller whose file is not a readable Nayax sales export is told.</summary>
    public const string UnreadableMessage =
        "The uploaded file could not be read as a Nayax sales export. It must be a CSV whose first "
        + "row is the export's own column headings, including TransactionID.";

    /// <summary>
    /// What a caller whose header row names the same column twice is told.
    ///
    /// <para>It is a refusal rather than a reading, because the two readers must not disagree about
    /// what a file says: the uploaded import's workbook reader fails outright on a duplicated
    /// heading, so a history that quietly took the first occurrence would apply different values to
    /// the same sales than the same file uploaded the normal way - and a column named twice states
    /// nothing about which of the two the rows below it belong to. Fixing the header row is the
    /// operator's one-line change; guessing is nobody's.</para>
    /// </summary>
    public const string DuplicateColumnMessage =
        "The uploaded file names the same column twice in its first row. Remove the repeated column "
        + "heading and upload it again.";

    /// <summary>
    /// What a caller who already has a history importing is told. Only one runs per business at a
    /// time: a second one would contend for the same write lock and the same sales rows for no gain.
    /// </summary>
    public const string AlreadyRunningMessage =
        "A sales history import is already running for this business. Wait for it to finish, then "
        + "upload the next file.";

    /// <summary>The safe reason a job that has used every attempt records.</summary>
    public static string AttemptsExhaustedReason =>
        $"The import stopped after {MaxAttempts} attempts. The rows it had already committed are "
        + "imported; upload the file again to import the rest.";

    /// <summary>What a caller whose upload is over the byte limit is told.</summary>
    public static string TooLargeMessage =>
        $"The uploaded file is larger than the {MaxUploadBytes / (1024 * 1024)} MB limit.";

    /// <summary>
    /// What a caller whose file carries more rows than <see cref="MaxRows"/> is told. The number
    /// comes from the constant, so lowering the limit cannot leave the sentence saying something the
    /// import no longer does.
    /// </summary>
    public static string TooManyRowsMessage =>
        $"This file has more than {MaxRows.ToString("N0", CultureInfo.InvariantCulture)} sales. "
        + "Split the history into files of that size or smaller.";

    /// <summary>Whether this uploaded name is the one format a history may be uploaded in.</summary>
    public static bool IsCsv(string? fileName) =>
        fileName is not null && fileName.EndsWith(CsvExtension, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reduces an untrusted uploaded file name to the leaf label the job records.
    ///
    /// <para>It is <see cref="ReimbursementArchivePolicy.SanitiseFileName"/>, deliberately: "what is
    /// recorded is the leaf the operator sent, with traversal segments, drive prefixes and control
    /// characters gone, capped in length" is one rule about untrusted upload names and not a rule
    /// about reimbursement archives, so this import asks it rather than writing a second copy that
    /// could be subtly weaker. Nothing about the reimbursement import changes for it.</para>
    /// </summary>
    public static string SanitiseFileName(string? fileName)
    {
        var leaf = ReimbursementArchivePolicy.SanitiseFileName(fileName);

        return leaf.Length <= MaxFileNameLength ? leaf : leaf[..MaxFileNameLength];
    }
}
