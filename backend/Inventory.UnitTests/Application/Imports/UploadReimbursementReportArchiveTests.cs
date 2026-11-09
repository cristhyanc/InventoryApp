using Inventory.Application.Imports;
using Inventory.Application.Tenancy;
using Inventory.Domain.Imports;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;
using Xunit;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// What the upload of issue #1149 accepts and refuses, and what it stores when it accepts.
///
/// <para>The whole point of these cases is that <strong>a refused upload stores nothing</strong>:
/// not the bytes, not a job, not a reimbursement row. A partially accepted settlement archive would
/// leave the operator unable to say which half of their money landed.</para>
/// </summary>
public class UploadReimbursementReportArchiveTests
{
    private static readonly DateTime UploadedAt = new(2026, 11, 9, 4, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_accepted_archive_is_stored_as_a_queued_job_and_answered_with_its_id()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning(
            new ReimbursementArchiveXmlEntry(0, "10-2026/Reimbursments.zip/report.xml",
                ReimbursementImportFakes.File("HASH-A")));

        var result = await Upload(reader, jobs).Handle(
            "Reports_09-11-26.zip", [1, 2, 3, 4], CancellationToken.None);

        Assert.True(result.IsAccepted);
        Assert.Null(result.Rejection);

        var created = Assert.Single(jobs.Created);
        Assert.Equal("Reports_09-11-26.zip", created.FileName);
        Assert.Equal(UploadedAt, created.UploadedAtUtc);
        Assert.Equal([1, 2, 3, 4], created.Archive);
        Assert.Equal(ReimbursementImportFakes.Actor(), created.UploadedBy);
        Assert.Equal(result.JobId, 1);
    }

    /// <summary>
    /// The upload stores the archive and imports nothing: no reimbursement row, no coverage record
    /// and no file outcome exists when the endpoint answers.
    /// </summary>
    [Fact]
    public async Task An_accepted_upload_imports_nothing_itself()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning(
            new ReimbursementArchiveXmlEntry(0, "report.xml", ReimbursementImportFakes.File("HASH-A")));

        await Upload(reader, jobs).Handle("Reports.zip", [1], CancellationToken.None);

        Assert.Empty(jobs.Outcomes);
        Assert.Equal(
            ReimbursementImportJobState.Queued, Assert.Single(jobs.Jobs).Value.State);
    }

    /// <summary>A refused archive stores nothing at all.</summary>
    [Fact]
    public async Task A_refused_archive_stores_no_job()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Refusing(ReimbursementArchivePolicy.TooManyEntriesMessage);

        var result = await Upload(reader, jobs).Handle("Reports.zip", [1], CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.TooManyEntriesMessage, result.Rejection);
        Assert.Empty(jobs.Created);
    }

    /// <summary>An archive with no reimbursement XML is refused: there is nothing to import.</summary>
    [Fact]
    public async Task An_archive_with_no_xml_is_refused()
    {
        var jobs = new FakeReimbursementImportJobStore();

        var result = await Upload(FakeArchiveReader.Empty(), jobs)
            .Handle("Reports.zip", [1], CancellationToken.None);

        Assert.False(result.IsAccepted);
        Assert.Equal(ReimbursementArchivePolicy.NoXmlMessage, result.Rejection);
        Assert.Empty(jobs.Created);
    }

    /// <summary>An empty upload is refused before the archive is even opened.</summary>
    [Fact]
    public async Task An_empty_upload_is_refused_without_reading_anything()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning();

        var result = await Upload(reader, jobs).Handle("Reports.zip", [], CancellationToken.None);

        Assert.Equal(ReimbursementArchivePolicy.EmptyUploadMessage, result.Rejection);
        Assert.Empty(reader.ArchiveLengthsRead);
        Assert.Empty(jobs.Created);
    }

    /// <summary>
    /// The size limit is enforced here as well as by the request pipeline, so a client that lies
    /// about its content length still cannot get a large archive stored.
    /// </summary>
    [Fact]
    public async Task An_upload_over_the_size_limit_is_refused_server_side()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning();

        var result = await Upload(reader, jobs).Handle(
            "Reports.zip",
            new byte[ReimbursementArchivePolicy.MaxUploadBytes + 1],
            CancellationToken.None);

        Assert.Equal(ReimbursementArchivePolicy.TooLargeMessage, result.Rejection);
        Assert.Empty(reader.ArchiveLengthsRead);
        Assert.Empty(jobs.Created);
    }

    /// <summary>
    /// The uploaded name is sanitised before it is stored: a client-supplied path never becomes one.
    /// </summary>
    [Theory]
    [InlineData("../../Reports_09-11-26.zip", "Reports_09-11-26.zip")]
    [InlineData("C:\\Users\\someone\\Reports.zip", "Reports.zip")]
    [InlineData("", "reimbursement-report.zip")]
    [InlineData("..", "reimbursement-report.zip")]
    public async Task The_stored_file_name_is_sanitised(string sent, string expected)
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning(
            new ReimbursementArchiveXmlEntry(0, "report.xml", ReimbursementImportFakes.File("HASH-A")));

        await Upload(reader, jobs).Handle(sent, [1], CancellationToken.None);

        Assert.Equal(expected, Assert.Single(jobs.Created).FileName);
    }

    /// <summary>
    /// An upload with nobody behind it is refused rather than recorded with an empty uploader: who
    /// imported a settlement statement is part of the record.
    /// </summary>
    [Fact]
    public async Task An_unidentifiable_caller_cannot_upload()
    {
        var jobs = new FakeReimbursementImportJobStore();
        var reader = FakeArchiveReader.Returning(
            new ReimbursementArchiveXmlEntry(0, "report.xml", ReimbursementImportFakes.File("HASH-A")));

        await Assert.ThrowsAsync<BusinessAccessDeniedException>(() =>
            new UploadReimbursementReportArchive(
                    reader,
                    jobs,
                    FakeAuthenticatedActorAccessor.Unidentifiable(),
                    new FakeClock(UploadedAt))
                .Handle("Reports.zip", [1], CancellationToken.None));

        Assert.Empty(jobs.Created);
    }

    private static UploadReimbursementReportArchive Upload(
        IReimbursementArchiveReader reader, IReimbursementImportJobStore jobs) =>
        new(
            reader,
            jobs,
            FakeAuthenticatedActorAccessor.Identified(ReimbursementImportFakes.Actor()),
            new FakeClock(UploadedAt));
}
