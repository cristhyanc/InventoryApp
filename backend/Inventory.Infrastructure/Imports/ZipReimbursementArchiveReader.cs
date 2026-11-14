using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure.Imports;

/// <summary>
/// Reads the ZIP Nayax emails (issue #1149), behind the Application's
/// <see cref="IReimbursementArchiveReader"/> port.
///
/// <para><strong>The real shape it is written for.</strong>
/// <c>Reports_DD-MM-YY.zip</c> holds <c>MM-YYYY/Reimbursments.zip</c> (Nayax's own spelling), which
/// holds a PDF, an XLSX and an XML per report day. So: descend through the folders of the uploaded
/// archive, open the archives found <em>in</em> it, and import the <c>.xml</c> entries of either.
/// An uploaded archive whose XML files are directly inside it works too, which is what an operator
/// who unzipped one level by hand will send.</para>
///
/// <para><strong>One level of nesting, and not one more.</strong> An archive inside an inner archive
/// is counted as ignored rather than opened. Recursing into attacker-supplied archives is how a
/// decompression bomb is built, and nothing Nayax sends needs it.</para>
///
/// <para><strong>Nothing is ever extracted to a path.</strong> Entry names are untrusted input: a
/// name may hold <c>../</c>, an absolute path, a drive letter or control characters, and this
/// adapter never passes one to the filesystem at all. Every byte is read into memory, and a name is
/// reduced by <see cref="ReimbursementArchivePolicy.SanitiseEntryName"/> to a label that is only
/// recorded and displayed. No folder on the server is read, written or deleted at all.</para>
///
/// <para><strong>Every limit is enforced here and defined in the Domain.</strong>
/// <see cref="ReimbursementArchivePolicy"/> holds the numbers and the refusal sentences; this
/// adapter counts entries, counts inner archives, and - crucially - reads each entry through a
/// bounded copy rather than trusting its declared length, because a ZIP's declared sizes are part of
/// the attacker-supplied input. A broken limit refuses the whole archive: nothing partial is ever
/// returned.</para>
///
/// <para><strong>Archive and XML failures are translated here and never cross the port.</strong> A
/// file that is not a readable ZIP is a refusal; a single entry that is not a readable reimbursement
/// report is answered as a parsed-as-<see langword="null"/> entry, which the job records as that
/// one file's own failure. Nothing about either is thrown at the Application layer.</para>
/// </summary>
public sealed class ZipReimbursementArchiveReader : IReimbursementArchiveReader
{
    /// <summary>
    /// The one category an entry that could not be parsed is logged under. It is a fixed word rather
    /// than anything derived from the file or from the parser's exception, because the exception's
    /// own message is assembled out of the uploaded bytes - an attribute name, an element name, a
    /// position - and a log sink is retained telemetry, not a place to publish a piece of a
    /// settlement statement.
    /// </summary>
    public const string UnparsableXmlFailure = "unparsable-reimbursement-xml";

    private readonly ILogger<ZipReimbursementArchiveReader> _logger;

    public ZipReimbursementArchiveReader(ILogger<ZipReimbursementArchiveReader> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public ReimbursementArchiveContents Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);

        if (archive.Length == 0)
            return ReimbursementArchiveContents.Refused(ReimbursementArchivePolicy.EmptyUploadMessage);

        if (archive.Length > ReimbursementArchivePolicy.MaxUploadBytes)
            return ReimbursementArchiveContents.Refused(ReimbursementArchivePolicy.TooLargeMessage);

        var walk = new Walk();

        try
        {
            using var stream = new MemoryStream(archive, writable: false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var rejection = WalkArchive(zip, prefix: string.Empty, depth: 0, walk);
            if (rejection is not null) return ReimbursementArchiveContents.Refused(rejection);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            // A damaged, truncated or simply-not-a-ZIP upload. The refusal names no server path and
            // no internal detail; the log line carries the exception for an operator, and the
            // archive's own bytes are never written anywhere.
            _logger.LogWarning(exception, "An uploaded reimbursement report archive could not be read.");
            return ReimbursementArchiveContents.Refused(
                ReimbursementArchivePolicy.UnreadableArchiveMessage);
        }

        return ReimbursementArchiveContents.Accepted(
            walk.XmlFiles, walk.InnerArchives, walk.IgnoredEntries, walk.IgnoredNestedArchives);
    }

    /// <summary>
    /// Walks one archive's entries, answering a refusal sentence or <see langword="null"/>.
    ///
    /// <para><paramref name="depth"/> is <c>0</c> for the uploaded archive and <c>1</c> for an
    /// archive inside it. There is no depth <c>2</c>: at depth <c>1</c> an archive entry is counted
    /// as ignored instead of opened.</para>
    /// </summary>
    private string? WalkArchive(ZipArchive zip, string prefix, int depth, Walk walk)
    {
        foreach (var entry in zip.Entries)
        {
            walk.Entries++;
            if (walk.Entries > ReimbursementArchivePolicy.MaxEntries)
                return ReimbursementArchivePolicy.TooManyEntriesMessage;

            var label = Label(prefix, entry.FullName, walk.Entries);

            // A ZIP has no folders, only entry names that contain them; a directory entry is one
            // whose name ends at the separator. Descending into folders therefore needs nothing -
            // the XML in MM-YYYY/ is simply an entry whose name says so.
            if (string.IsNullOrEmpty(entry.Name))
            {
                walk.IgnoredEntries++;
                continue;
            }

            if (ReimbursementArchivePolicy.IsArchiveEntry(entry.Name))
            {
                var archiveRejection = WalkArchiveEntry(entry, label, depth, walk);
                if (archiveRejection is not null) return archiveRejection;
                continue;
            }

            if (!ReimbursementArchivePolicy.IsXmlEntry(entry.Name))
            {
                // The PDF and the XLSX of each report day, and anything else. Counted, never read:
                // an entry this import does not expand cannot expand into anything.
                walk.IgnoredEntries++;
                continue;
            }

            var xmlRejection = WalkXmlEntry(entry, label, walk);
            if (xmlRejection is not null) return xmlRejection;
        }

        return null;
    }

    /// <summary>
    /// Handles one archive-entry: ignores it at depth &gt; 0, otherwise expands and recurses into it.
    /// Split out of <see cref="WalkArchive"/> to keep that method's branching readable.
    /// </summary>
    private string? WalkArchiveEntry(ZipArchiveEntry entry, string label, int depth, Walk walk)
    {
        if (depth > 0)
        {
            // An archive inside an inner archive. Reported as ignored, never opened.
            walk.IgnoredNestedArchives++;
            return null;
        }

        walk.InnerArchives++;
        if (walk.InnerArchives > ReimbursementArchivePolicy.MaxInnerArchives)
            return ReimbursementArchivePolicy.TooManyInnerArchivesMessage;

        var inner = Expand(entry, label, walk, out var innerRejection);
        if (innerRejection is not null) return innerRejection;

        try
        {
            using var innerStream = new MemoryStream(inner!, writable: false);
            using var innerZip = new ZipArchive(innerStream, ZipArchiveMode.Read);

            return WalkArchive(innerZip, label, depth + 1, walk);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            // An inner entry named .zip that is not one refuses the whole upload rather than
            // being silently ignored: the operator uploaded something this application
            // cannot account for, and half an import is worse than none.
            _logger.LogWarning(
                exception,
                "An archive inside an uploaded reimbursement report archive could not be read. Entry={ArchiveEntry}",
                label);
            return ReimbursementArchivePolicy.UnreadableArchiveMessage;
        }
    }

    /// <summary>
    /// Expands and parses one XML entry, recording it on <paramref name="walk"/>. Split out of
    /// <see cref="WalkArchive"/> to keep that method's branching readable.
    /// </summary>
    private string? WalkXmlEntry(ZipArchiveEntry entry, string label, Walk walk)
    {
        var bytes = Expand(entry, label, walk, out var rejection);
        if (rejection is not null) return rejection;

        walk.XmlFiles.Add(new ReimbursementArchiveXmlEntry(
            walk.XmlFiles.Count, label, Parse(label, bytes!)));

        return null;
    }

    /// <summary>
    /// Reads one entry into memory under every size and ratio limit, or answers a refusal.
    ///
    /// <para>The entry's own <c>Length</c> is deliberately not trusted as the bound: it is a value
    /// from the archive's central directory, which is attacker-supplied, so the copy itself is what
    /// stops - one byte past the limit is enough to know the limit is broken. The declared length is
    /// only used to refuse an obviously oversized entry before reading anything.</para>
    /// </summary>
    private static byte[]? Expand(ZipArchiveEntry entry, string label, Walk walk, out string? rejection)
    {
        rejection = null;

        if (entry.Length > ReimbursementArchivePolicy.MaxEntryExpandedBytes)
        {
            rejection = ReimbursementArchivePolicy.EntryTooLargeMessage(label);
            return null;
        }

        var remainingTotal = ReimbursementArchivePolicy.MaxTotalExpandedBytes - walk.ExpandedBytes;
        if (remainingTotal <= 0)
        {
            rejection = ReimbursementArchivePolicy.TotalTooLargeMessage;
            return null;
        }

        // One byte of headroom, so "read more than the limit" is distinguishable from "read exactly
        // the limit" without a second pass.
        var entryCeiling = (long)ReimbursementArchivePolicy.MaxEntryExpandedBytes;
        var ceiling = Math.Min(entryCeiling, remainingTotal);

        using var expanded = new MemoryStream();
        using var source = entry.Open();

        var buffer = new byte[81920];
        long written = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;

            if (written > ceiling)
            {
                rejection = written > entryCeiling
                    ? ReimbursementArchivePolicy.EntryTooLargeMessage(label)
                    : ReimbursementArchivePolicy.TotalTooLargeMessage;
                return null;
            }

            expanded.Write(buffer, 0, read);
        }

        // The ratio check is what makes "a small file cannot expand without bound" true even while
        // each absolute limit holds. A stored (uncompressed) entry has a ratio of one and passes,
        // and an entry too small to exhaust anything is not judged on its ratio at all.
        if (entry.CompressedLength > 0
            && written >= ReimbursementArchivePolicy.CompressionRatioFloorBytes
            && written > entry.CompressedLength * (long)ReimbursementArchivePolicy.MaxCompressionRatio)
        {
            rejection = ReimbursementArchivePolicy.CompressionRatioMessage(label);
            return null;
        }

        walk.ExpandedBytes += written;

        return expanded.ToArray();
    }

    /// <summary>
    /// Parses one XML entry, or answers <see langword="null"/> when it is not a readable
    /// reimbursement report: a file this application cannot read is one file's recorded failure, not
    /// an exception the import loop has to understand.
    /// </summary>
    private PendingReimbursementXmlFile? Parse(string label, byte[] bytes)
    {
        try
        {
            // The label's leaf is what is persisted for provenance, so a stored file name is the
            // report's own name rather than a path a client chose.
            var leaf = label[(label.LastIndexOf('/') + 1)..];

            return ReimbursementXmlParser.Parse(leaf, bytes);
        }
        catch (Exception exception) when (exception is InvalidDataException
            or FormatException
            or JsonException
            or XmlException)
        {
            // The entry label and one fixed category, and nothing else: a log line is not a place to
            // publish settlement figures, and the parser's own exception is a piece of the uploaded
            // file by another route - its message quotes the attribute name, the element name or the
            // position in the bytes that it failed on. So the exception is deliberately not attached,
            // the same withholding NayaxUpstreamExceptionHandler makes for the same reason
            // (docs/architecture.md § Exception ownership table). What an operator needs from this
            // line is which entry failed, which the label says.
            _logger.LogWarning(
                "An entry of an uploaded reimbursement report archive could not be parsed. "
                    + "Entry={ArchiveEntry} Failure={FailureCategory}",
                label,
                UnparsableXmlFailure);

            return null;
        }
    }

    /// <summary>
    /// The label an entry is recorded under: its sanitised name inside the archive it came from,
    /// prefixed by the archive that held it. A name that sanitises away entirely - one made of
    /// nothing but traversal segments - becomes a positional label rather than a guess.
    /// </summary>
    private static string Label(string prefix, string fullName, int position)
    {
        var sanitised = ReimbursementArchivePolicy.SanitiseEntryName(fullName);
        var name = sanitised.Length > 0 ? sanitised : $"entry-{position}";
        var label = prefix.Length > 0 ? $"{prefix}/{name}" : name;

        return label.Length <= ReimbursementArchivePolicy.MaxEntryLabelLength
            ? label
            : label[^ReimbursementArchivePolicy.MaxEntryLabelLength..];
    }

    /// <summary>The running totals one <see cref="Read"/> call enforces its limits against.</summary>
    private sealed class Walk
    {
        public List<ReimbursementArchiveXmlEntry> XmlFiles { get; } = [];

        public int Entries { get; set; }

        public int InnerArchives { get; set; }

        public int IgnoredEntries { get; set; }

        public int IgnoredNestedArchives { get; set; }

        public long ExpandedBytes { get; set; }
    }
}
