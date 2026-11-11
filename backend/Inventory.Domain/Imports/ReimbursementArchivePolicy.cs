namespace Inventory.Domain.Imports;

/// <summary>
/// What an uploaded Nayax reimbursement report archive may contain (issue #1149, the repository
/// owner's limits of 9 November 2026).
///
/// <para><strong>Every limit is here and nowhere else.</strong> A decompression limit that is
/// written at the place the bytes are read is a limit the next reader forgets, so the numbers, the
/// nesting rule and the refusal sentences live in one deterministic Domain type that neither the
/// HTTP boundary nor the archive adapter may restate. The adapter enforces them; this type says what
/// they are.</para>
///
/// <para><strong>They exist because an archive is attacker-controlled input.</strong> The file
/// arrives as an email attachment a person forwards, so the application must assume a hostile one:
/// an entry whose declared size is small and whose expansion is unbounded, thousands of tiny
/// entries, archives nested inside archives, and entry names holding <c>../</c> or an absolute path.
/// Breaking any one of these rejects the whole upload and imports nothing - a partially accepted
/// settlement archive is worse than a refused one, because the operator would have no way to tell
/// which half landed.</para>
///
/// <para><strong>Nothing here extracts anything.</strong> Entry names are untrusted and are never
/// used as a path: <see cref="SanitiseEntryName"/> reduces a name to a display label, which is what
/// a job's per-file outcome records, and the bytes are read in memory.</para>
/// </summary>
public static class ReimbursementArchivePolicy
{
    /// <summary>
    /// The largest upload the endpoint accepts: 10 MB, the same ceiling the Nayax sales file upload
    /// has carried since issue #301. The real archive is about 1.3 MB.
    /// </summary>
    public const int MaxUploadBytes = 10 * 1024 * 1024;

    /// <summary>
    /// How many archives nested inside the uploaded one may be opened: 20. One per report month is
    /// the real shape; twenty is generous and still bounded.
    /// </summary>
    public const int MaxInnerArchives = 20;

    /// <summary>
    /// How many entries may be examined in total, across the uploaded archive and every inner one:
    /// 2,000. A report day contributes three entries, so this is years of reports.
    /// </summary>
    public const int MaxEntries = 2_000;

    /// <summary>
    /// The largest any single entry may expand to: 5 MB. A report XML is about 3.6 KB and the inner
    /// archive about 1.3 MB.
    /// </summary>
    public const int MaxEntryExpandedBytes = 5 * 1024 * 1024;

    /// <summary>
    /// The largest everything this application expands may add up to: 100 MB. Only the entries that
    /// are actually read count - the inner archives and the XML files - because a PDF or an XLSX is
    /// never expanded at all.
    /// </summary>
    public const long MaxTotalExpandedBytes = 100L * 1024 * 1024;

    /// <summary>
    /// The largest ratio between what an expanded entry weighed compressed and what it expanded to:
    /// 200:1. It is the check that makes "a small file cannot expand without bound" true even while
    /// each individual size limit is respected - a 50 KB entry that expands to 5 MB is a 100:1 entry
    /// and is fine; one that expands 1,000-fold is a bomb, whatever its absolute size.
    /// </summary>
    public const int MaxCompressionRatio = 200;

    /// <summary>
    /// The expanded size an entry has to reach before its ratio is judged at all: 64 KB.
    ///
    /// <para>It exists so the ratio check refuses bombs rather than small files. A 2 KB report that
    /// happens to compress 300-fold is harmless - everything below this floor is bounded by the
    /// entry and total limits several times over - while a ratio applied to it would refuse a
    /// perfectly ordinary upload. What the check is actually for is the entry that expands from
    /// almost nothing into megabytes, and that entry is always above this floor.</para>
    /// </summary>
    public const int CompressionRatioFloorBytes = 64 * 1024;

    /// <summary>
    /// How long a recorded entry label may be. Long enough for the real
    /// <c>MM-YYYY/Reimbursments.zip/report_&lt;account&gt;_&lt;date&gt;.xml</c> shape and short
    /// enough that a hostile name cannot fill a column.
    /// </summary>
    public const int MaxEntryLabelLength = 400;

    /// <summary>How long a recorded upload file name may be, on the same terms.</summary>
    public const int MaxFileNameLength = 200;

    /// <summary>The extension of the files this import reads, compared case-insensitively.</summary>
    public const string XmlExtension = ".xml";

    /// <summary>The extension of an archive, compared case-insensitively.</summary>
    public const string ArchiveExtension = ".zip";

    /// <summary>What a caller who uploaded nothing is told.</summary>
    public const string EmptyUploadMessage = "A ZIP file is required.";

    /// <summary>What a caller who uploaded something that is not a readable archive is told.</summary>
    public const string UnreadableArchiveMessage =
        "The uploaded file could not be read as a ZIP archive.";

    /// <summary>What a caller whose archive holds no reimbursement XML is told.</summary>
    public const string NoXmlMessage =
        "The archive contains no reimbursement XML file, so there is nothing to import.";

    /// <summary>What a caller whose upload is over the size limit is told.</summary>
    public static string TooLargeMessage =>
        $"The uploaded file is larger than the {MaxUploadBytes / (1024 * 1024)} MB limit.";

    /// <summary>What a caller whose archive holds too many inner archives is told.</summary>
    public static string TooManyInnerArchivesMessage =>
        $"The archive contains more than {MaxInnerArchives} archives inside it.";

    /// <summary>What a caller whose archive holds too many entries is told.</summary>
    public static string TooManyEntriesMessage =>
        $"The archive contains more than {MaxEntries} entries.";

    /// <summary>What a caller is told when one entry expands past its own limit.</summary>
    /// <param name="entryLabel">
    /// The sanitised entry label, so the person can find the offending file. A file name is the one
    /// thing about an import that may appear in a message or a log line.
    /// </param>
    public static string EntryTooLargeMessage(string entryLabel) =>
        $"'{entryLabel}' expands to more than {MaxEntryExpandedBytes / (1024 * 1024)} MB.";

    /// <summary>What a caller is told when everything expanded adds up past the total limit.</summary>
    public static string TotalTooLargeMessage =>
        $"The files in the archive expand to more than {MaxTotalExpandedBytes / (1024 * 1024)} MB in total.";

    /// <summary>What a caller is told when one entry expands out of all proportion to its size.</summary>
    /// <param name="entryLabel">The sanitised entry label.</param>
    public static string CompressionRatioMessage(string entryLabel) =>
        $"'{entryLabel}' expands more than {MaxCompressionRatio} times its compressed size.";

    /// <summary>Whether this entry name is one of the XML files the import reads.</summary>
    public static bool IsXmlEntry(string? entryName) =>
        HasExtension(entryName, XmlExtension);

    /// <summary>Whether this entry name is an archive.</summary>
    public static bool IsArchiveEntry(string? entryName) =>
        HasExtension(entryName, ArchiveExtension);

    /// <summary>
    /// Reduces an untrusted archive entry name to a label that is safe to record and display.
    ///
    /// <para>It is <strong>not</strong> a path and is never used as one: both separators are
    /// normalised to <c>/</c>, every <c>.</c> and <c>..</c> segment is dropped, a leading separator
    /// and a Windows drive prefix are removed, control characters become <c>_</c>, and the result is
    /// capped. A name of nothing but traversal segments therefore reduces to the empty string, which
    /// the caller replaces with a positional label - never with a guess at what the file was called.</para>
    /// </summary>
    public static string SanitiseEntryName(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return string.Empty;

        var segments = entryName
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(segment => segment is not "." and not "..")
            .Select(StripDrivePrefix)
            .Select(Clean)
            .Where(segment => segment.Length > 0)
            .ToList();

        var label = string.Join('/', segments);

        return label.Length <= MaxEntryLabelLength ? label : label[^MaxEntryLabelLength..];
    }

    /// <summary>
    /// Reduces an untrusted uploaded file name to the leaf label the job records. The same rules as
    /// <see cref="SanitiseEntryName"/>, plus "only the last segment": what is recorded is what the
    /// operator sent, never a directory a client claimed it lived in.
    /// </summary>
    public static string SanitiseFileName(string? fileName)
    {
        var label = SanitiseEntryName(fileName);
        var leaf = label.Length == 0 ? string.Empty : label[(label.LastIndexOf('/') + 1)..];

        return leaf.Length <= MaxFileNameLength ? leaf : leaf[..MaxFileNameLength];
    }

    private static bool HasExtension(string? entryName, string extension) =>
        entryName is not null && entryName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static string StripDrivePrefix(string segment) =>
        segment.Length >= 2 && segment[1] == ':' && char.IsLetter(segment[0])
            ? segment[2..]
            : segment;

    private static string Clean(string segment) =>
        new([.. segment.Select(character => char.IsControl(character) ? '_' : character)]);
}
