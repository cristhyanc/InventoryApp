using System.Text;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Imports;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Imports;

/// <summary>
/// What the uploaded Nayax ZIP reader of issue #1149 accepts, ignores and refuses.
///
/// <para>These are the security cases of a High-risk change: an archive is attacker-controlled input
/// that arrives as an email attachment somebody forwards, so the cases below are deliberately about
/// hostile shapes as much as about the real one - nesting, entry counts, expansion, compression
/// ratios and entry names holding <c>../</c> or an absolute path. Every fixture figure and account
/// id is made up; see <see cref="ReimbursementArchiveFixtures"/>.</para>
/// </summary>
public class ZipReimbursementArchiveReaderTests
{
    private static readonly DateTime FirstReportDay = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    private static ZipReimbursementArchiveReader Reader() =>
        new(NullLogger<ZipReimbursementArchiveReader>.Instance);

    /// <summary>
    /// The real shape, end to end: the outer archive, the folder, the inner archive, and the XML of
    /// each report day - with the PDF and the XLSX beside them counted as ignored rather than read.
    /// </summary>
    [Fact]
    public void The_real_nested_shape_yields_every_report_xml_and_ignores_the_pdf_and_xlsx()
    {
        var archive = ReimbursementArchiveFixtures.NestedArchive(
            ("10-2026", [FirstReportDay, FirstReportDay.AddDays(7)]),
            ("11-2026", [FirstReportDay.AddDays(28)]));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Null(contents.Rejection);
        Assert.Equal(2, contents.InnerArchives);
        Assert.Equal(3, contents.XmlFiles.Count);
        Assert.All(contents.XmlFiles, entry => Assert.NotNull(entry.Parsed));

        // Six entries ignored: a PDF and an XLSX per report day. Nothing is silently dropped.
        Assert.Equal(6, contents.IgnoredEntries);
        Assert.Equal(0, contents.IgnoredNestedArchives);

        Assert.Equal(
            [
                "10-2026/Reimbursments.zip/report_9900001_20261005.xml",
                "10-2026/Reimbursments.zip/report_9900001_20261012.xml",
                "11-2026/Reimbursments.zip/report_9900001_20261102.xml",
            ],
            contents.XmlFiles.Select(entry => entry.Label));

        // The index is the position among the XML files, which is what a resumed job matches on.
        Assert.Equal([0, 1, 2], contents.XmlFiles.Select(entry => entry.Index));

        // The persisted provenance name is the report's own leaf, never a path.
        Assert.Equal(
            "report_9900001_20261005.xml", contents.XmlFiles[0].Parsed!.FileName);
    }

    /// <summary>
    /// An archive whose XML files are directly inside it - what an operator who unzipped one level
    /// by hand sends - works, with no inner archive at all.
    /// </summary>
    [Fact]
    public void A_flat_archive_of_xml_files_is_imported_without_any_inner_archive()
    {
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            (ReimbursementArchiveFixtures.ReportName(FirstReportDay, ".xml"),
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            (ReimbursementArchiveFixtures.ReportName(FirstReportDay.AddDays(7), ".xml"),
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay.AddDays(7), FirstReportDay.AddDays(13), 412m)));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(0, contents.InnerArchives);
        Assert.Equal(2, contents.XmlFiles.Count);
        Assert.All(contents.XmlFiles, entry => Assert.NotNull(entry.Parsed));
    }

    /// <summary>
    /// Folders are descended into, including the directory entries a ZIP tool writes for them, which
    /// are counted as ignored rather than read.
    /// </summary>
    [Fact]
    public void Subfolders_are_descended_into_and_their_directory_entries_are_ignored()
    {
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
        {
            zip.CreateEntry("10-2026/");
            zip.CreateEntry("10-2026/statements/");
            ReimbursementArchiveFixtures.Write(
                zip,
                "10-2026/statements/report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m));
        });

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(2, contents.IgnoredEntries);
        Assert.Equal(
            ["10-2026/statements/report_9900001_20261005.xml"],
            contents.XmlFiles.Select(entry => entry.Label));
    }

    /// <summary>
    /// An archive inside an inner archive is reported as ignored and never opened - unbounded
    /// recursion over attacker-supplied archives is exactly how a decompression bomb is built.
    /// </summary>
    [Fact]
    public void An_archive_three_levels_deep_is_reported_as_ignored_and_never_opened()
    {
        var third = ReimbursementArchiveFixtures.FlatArchive(
            ("never-opened.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 999m)));

        var second = ReimbursementArchiveFixtures.Zip(zip =>
        {
            ReimbursementArchiveFixtures.Write(
                zip,
                "report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m));
            ReimbursementArchiveFixtures.Write(zip, "deeper.zip", third);
        });

        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.Write(zip, "10-2026/Reimbursments.zip", second));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(1, contents.InnerArchives);
        Assert.Equal(1, contents.IgnoredNestedArchives);

        // The XML inside the third level is not imported, and its name never appears.
        Assert.Equal(
            ["10-2026/Reimbursments.zip/report_9900001_20261005.xml"],
            contents.XmlFiles.Select(entry => entry.Label));
    }

    /// <summary>
    /// The extension match is case-insensitive, for both the XML files and the inner archives: the
    /// real file names are lower case, and a forwarded attachment is not a place to be strict about
    /// it.
    /// </summary>
    [Fact]
    public void The_xml_and_archive_extensions_are_matched_case_insensitively()
    {
        var inner = ReimbursementArchiveFixtures.FlatArchive(
            ("REPORT_9900001_20261005.XML",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));

        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.Write(zip, "10-2026/REIMBURSMENTS.ZIP", inner));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(1, contents.InnerArchives);
        Assert.Single(contents.XmlFiles);
    }

    /// <summary>
    /// One unreadable XML among valid ones is that one entry's own failure - answered as a parsed
    /// <see langword="null"/> - and never a refusal of the archive: the rest still import.
    /// </summary>
    [Fact]
    public void An_unreadable_xml_among_valid_ones_is_that_entrys_own_failure()
    {
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("good.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("broken.xml", "<row unterminated=\"yes\""),
            ("no-rows.xml", "<report><summary total=\"1\" /></report>"));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(3, contents.XmlFiles.Count);
        Assert.NotNull(contents.XmlFiles[0].Parsed);
        Assert.Null(contents.XmlFiles[1].Parsed);
        Assert.Null(contents.XmlFiles[2].Parsed);
    }

    /// <summary>
    /// A traversal or absolute entry name is reduced to a label and is never used as a path. The
    /// recorded label holds no <c>..</c>, no leading separator and no drive letter, and the file
    /// still imports - the name is untrusted input, not a reason to refuse a settlement statement.
    /// </summary>
    [Theory]
    [InlineData("../../../etc/report.xml", "etc/report.xml")]
    [InlineData("..\\..\\windows\\report.xml", "windows/report.xml")]
    [InlineData("/absolute/report.xml", "absolute/report.xml")]
    [InlineData("C:\\Windows\\Temp\\report.xml", "Windows/Temp/report.xml")]
    [InlineData("./report.xml", "report.xml")]
    public void A_traversal_entry_name_becomes_a_label_and_is_never_a_path(
        string entryName, string expectedLabel)
    {
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.Write(
                zip,
                entryName,
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        var entry = Assert.Single(contents.XmlFiles);
        Assert.Equal(expectedLabel, entry.Label);
        Assert.DoesNotContain("..", entry.Label, StringComparison.Ordinal);
        Assert.NotNull(entry.Parsed);
    }

    /// <summary>
    /// An entry name that is nothing but traversal segments becomes a positional label rather than a
    /// guess at what the file was called.
    /// </summary>
    [Fact]
    public void An_entry_name_that_sanitises_away_entirely_gets_a_positional_label()
    {
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.Write(
                zip,
                "../../.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Equal(".xml", Assert.Single(contents.XmlFiles).Label);
    }

    /// <summary>An upload that is not a ZIP at all is refused, not thrown.</summary>
    [Fact]
    public void Something_that_is_not_a_zip_is_refused()
    {
        var contents = Reader().Read(Encoding.UTF8.GetBytes("this is not a ZIP archive at all"));

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.UnreadableArchiveMessage, contents.Rejection);
        Assert.Empty(contents.XmlFiles);
    }

    /// <summary>An empty upload is refused with the sentence the endpoint uses.</summary>
    [Fact]
    public void An_empty_upload_is_refused()
    {
        var contents = Reader().Read([]);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.EmptyUploadMessage, contents.Rejection);
    }

    /// <summary>
    /// An archive holding no XML at all is accepted by the reader and rejected by the use case: the
    /// reader's job is what is in the archive, and "there is nothing to import" is a decision about
    /// the upload.
    /// </summary>
    [Fact]
    public void An_archive_with_no_xml_yields_no_files_for_the_use_case_to_refuse()
    {
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.pdf", "a made-up PDF placeholder"),
            ("report_9900001_20261005.xlsx", "a made-up XLSX placeholder"));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.Empty(contents.XmlFiles);
        Assert.Equal(2, contents.IgnoredEntries);
    }

    /// <summary>An inner entry named <c>.zip</c> that is not one refuses the whole upload.</summary>
    [Fact]
    public void An_inner_archive_that_is_not_readable_refuses_the_whole_upload()
    {
        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("10-2026/Reimbursments.zip", "not actually a ZIP"));

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.UnreadableArchiveMessage, contents.Rejection);
    }

    /// <summary>An upload over the request limit is refused before anything is opened.</summary>
    [Fact]
    public void An_upload_over_the_size_limit_is_refused()
    {
        var oversized = new byte[ReimbursementArchivePolicy.MaxUploadBytes + 1];

        var contents = Reader().Read(oversized);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.TooLargeMessage, contents.Rejection);
    }

    /// <summary>More inner archives than the limit refuses the whole upload.</summary>
    [Fact]
    public void More_inner_archives_than_the_limit_refuses_the_upload()
    {
        var inner = ReimbursementArchiveFixtures.FlatArchive(
            ("report.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));

        var archive = ReimbursementArchiveFixtures.Zip(zip =>
        {
            for (var index = 0; index <= ReimbursementArchivePolicy.MaxInnerArchives; index++)
                ReimbursementArchiveFixtures.Write(zip, $"month-{index}/Reimbursments.zip", inner);
        });

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.TooManyInnerArchivesMessage, contents.Rejection);
        Assert.Empty(contents.XmlFiles);
    }

    /// <summary>More entries than the limit refuses the whole upload, ignored entries included.</summary>
    [Fact]
    public void More_entries_than_the_limit_refuses_the_upload()
    {
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
        {
            for (var index = 0; index <= ReimbursementArchivePolicy.MaxEntries; index++)
                ReimbursementArchiveFixtures.Write(zip, $"notes/{index}.txt", "x");
        });

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.TooManyEntriesMessage, contents.Rejection);
    }

    /// <summary>
    /// One entry that expands past its own limit refuses the whole upload, and the message names the
    /// file so the operator can find it.
    /// </summary>
    [Fact]
    public void One_entry_over_the_expanded_size_limit_refuses_the_upload()
    {
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.WriteStored(
                zip,
                "huge.xml",
                Noise(ReimbursementArchivePolicy.MaxEntryExpandedBytes + 1024)));

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(
            ReimbursementArchivePolicy.EntryTooLargeMessage("huge.xml"), contents.Rejection);
    }

    /// <summary>
    /// Everything expanded adding up past the total limit refuses the whole upload, even while every
    /// single entry is within its own limit and its own ratio.
    /// </summary>
    [Fact]
    public void Everything_expanded_over_the_total_limit_refuses_the_upload()
    {
        // 5 MB of mostly zeros behind 100 KB of noise: inside the per-entry limit, and a ratio of
        // about fifty, so this case is about the total and nothing else.
        var content = new byte[ReimbursementArchivePolicy.MaxEntryExpandedBytes];
        Noise(100 * 1024).CopyTo(content, 0);

        var needed =
            (int)(ReimbursementArchivePolicy.MaxTotalExpandedBytes
                / ReimbursementArchivePolicy.MaxEntryExpandedBytes) + 1;

        var archive = ReimbursementArchiveFixtures.Zip(zip =>
        {
            for (var index = 0; index < needed; index++)
                ReimbursementArchiveFixtures.Write(zip, $"part-{index}.xml", content);
        });

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.TotalTooLargeMessage, contents.Rejection);
    }

    /// <summary>
    /// An entry that expands out of all proportion to its compressed size refuses the whole upload,
    /// even though it is inside every absolute limit. This is the check that makes "a small file
    /// cannot expand without bound" true.
    /// </summary>
    [Fact]
    public void An_entry_that_expands_out_of_all_proportion_refuses_the_upload()
    {
        // A megabyte of zeros compresses to about a kilobyte: a ratio of roughly a thousand, far
        // inside the per-entry and total size limits.
        var archive = ReimbursementArchiveFixtures.Zip(zip =>
            ReimbursementArchiveFixtures.Write(zip, "bomb.xml", new byte[1024 * 1024]));

        var contents = Reader().Read(archive);

        Assert.False(contents.IsAccepted);
        Assert.Equal(
            ReimbursementArchivePolicy.CompressionRatioMessage("bomb.xml"), contents.Rejection);
    }

    /// <summary>
    /// A small, highly compressible report is not judged on its ratio: the check exists to refuse
    /// bombs, and everything below the floor is bounded several times over by the size limits.
    /// </summary>
    [Fact]
    public void A_small_highly_compressible_report_is_not_refused_on_its_ratio()
    {
        var repetitive = string.Concat(Enumerable.Repeat(
            ReimbursementArchiveFixtures.ReportXml(FirstReportDay, FirstReportDay.AddDays(6), 405m),
            8));

        var archive = ReimbursementArchiveFixtures.FlatArchive(("report.xml", repetitive));

        var contents = Reader().Read(archive);

        Assert.True(contents.IsAccepted);
        Assert.NotNull(Assert.Single(contents.XmlFiles).Parsed);
    }

    /// <summary>
    /// Reading the same bytes twice produces the same walk, which is what lets a resumed job match
    /// the outcomes a previous attempt recorded.
    /// </summary>
    [Fact]
    public void The_walk_over_the_same_bytes_is_deterministic()
    {
        var archive = ReimbursementArchiveFixtures.NestedArchive(
            ("10-2026", [FirstReportDay, FirstReportDay.AddDays(7)]),
            ("11-2026", [FirstReportDay.AddDays(28)]));

        var first = Reader().Read(archive);
        var second = Reader().Read(archive);

        Assert.Equal(
            first.XmlFiles.Select(entry => (entry.Index, entry.Label)),
            second.XmlFiles.Select(entry => (entry.Index, entry.Label)));
        Assert.Equal(
            first.XmlFiles.Select(entry => entry.Parsed!.ContentHash),
            second.XmlFiles.Select(entry => entry.Parsed!.ContentHash));
    }

    /// <summary>Deterministic pseudo-random bytes, so a size case is not also a ratio case.</summary>
    private static byte[] Noise(int length)
    {
        var bytes = new byte[length];
        new Random(1149).NextBytes(bytes);
        return bytes;
    }
}
