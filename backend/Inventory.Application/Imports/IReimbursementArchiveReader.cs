namespace Inventory.Application.Imports;

/// <summary>
/// One XML file found inside an uploaded reimbursement report archive (issue #1149).
/// </summary>
/// <param name="Index">
/// The entry's position in the archive walk, counted from zero over the XML files alone. It is the
/// job's own per-file identity: the walk over the same bytes is deterministic, so a resumed job
/// matches its recorded outcomes by this number rather than by a name two inner archives could both
/// carry.
/// </param>
/// <param name="Label">
/// The sanitised path of the entry inside the archive, for display and for the recorded outcome. It
/// is never a filesystem path and is never used as one.
/// </param>
/// <param name="Parsed">
/// The parsed file, or <see langword="null"/> when this entry could not be read as a reimbursement
/// report. The adapter translates its own failures into that <see langword="null"/>, so no XML or
/// archive exception crosses this port.
/// </param>
public sealed record ReimbursementArchiveXmlEntry(
    int Index,
    string Label,
    PendingReimbursementXmlFile? Parsed);

/// <summary>
/// What an uploaded archive turned out to contain, or why it was refused (issue #1149).
///
/// <para>A refusal is a readable sentence from
/// <c>Inventory.Domain.Imports.ReimbursementArchivePolicy</c> and nothing else: it may name a file
/// inside the archive, and it carries no server path, no stack trace and no database detail.</para>
/// </summary>
public sealed record ReimbursementArchiveContents
{
    private ReimbursementArchiveContents(
        string? rejection,
        IReadOnlyList<ReimbursementArchiveXmlEntry> xmlFiles,
        int innerArchives,
        int ignoredEntries,
        int ignoredNestedArchives)
    {
        Rejection = rejection;
        XmlFiles = xmlFiles;
        InnerArchives = innerArchives;
        IgnoredEntries = ignoredEntries;
        IgnoredNestedArchives = ignoredNestedArchives;
    }

    /// <summary>Why the archive was refused, or <see langword="null"/> when it was accepted.</summary>
    public string? Rejection { get; }

    /// <summary>The XML files to import, in the deterministic order the walk found them.</summary>
    public IReadOnlyList<ReimbursementArchiveXmlEntry> XmlFiles { get; }

    /// <summary>How many archives inside the uploaded one were opened.</summary>
    public int InnerArchives { get; }

    /// <summary>
    /// How many entries were ignored because they are neither XML nor an archive one level deep -
    /// the PDFs, the XLSXs and the directory entries. Counted rather than silently dropped, so the
    /// numbers a job reports add up to what was in the archive.
    /// </summary>
    public int IgnoredEntries { get; }

    /// <summary>
    /// How many archives were found <em>inside</em> an inner archive and therefore not opened. One
    /// level of nesting is what Nayax sends; a third level is reported as ignored rather than walked,
    /// because unbounded recursion over attacker-supplied input is how a decompression bomb works.
    /// </summary>
    public int IgnoredNestedArchives { get; }

    /// <summary>Whether the archive was accepted.</summary>
    public bool IsAccepted => Rejection is null;

    /// <summary>An accepted archive.</summary>
    public static ReimbursementArchiveContents Accepted(
        IReadOnlyList<ReimbursementArchiveXmlEntry> xmlFiles,
        int innerArchives,
        int ignoredEntries,
        int ignoredNestedArchives) =>
        new(null, xmlFiles, innerArchives, ignoredEntries, ignoredNestedArchives);

    /// <summary>A refused archive. Nothing is imported and no job is created.</summary>
    public static ReimbursementArchiveContents Refused(string rejection) =>
        new(rejection, [], 0, 0, 0);
}

/// <summary>
/// Reads an uploaded Nayax reimbursement report archive (issue #1149): the outer ZIP, the archives
/// one level inside it, and the reimbursement XML files in either.
///
/// <para><strong>Nothing is extracted to disk.</strong> Every byte is read in memory, entry names
/// are treated as untrusted labels rather than paths, and no folder on the server is read, written
/// or deleted.</para>
///
/// <para><strong>It is called twice for one upload, on purpose.</strong> The endpoint calls it to
/// decide whether the archive may be accepted at all, and the worker calls it again on the stored
/// bytes to do the importing. The same bytes produce the same walk, which is what lets a resumed job
/// match its recorded outcomes; and validating at upload is what makes a bad archive a <c>400</c>
/// the operator sees immediately rather than a job that fails later.</para>
/// </summary>
public interface IReimbursementArchiveReader
{
    /// <summary>
    /// Walks <paramref name="archive"/> and answers what it contains, or why it was refused. It
    /// never throws for a malformed, empty, oversized or hostile archive: those are refusals.
    /// </summary>
    ReimbursementArchiveContents Read(byte[] archive);
}
