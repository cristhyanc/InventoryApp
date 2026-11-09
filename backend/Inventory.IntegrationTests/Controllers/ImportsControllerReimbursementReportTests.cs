using Inventory.Application.Imports;
using Inventory.Application.Time;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Data;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Imports;
using InventoryApi.Tests.Application.Providers;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Infrastructure.Imports;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The HTTP surface of the Reimbursement Report import (issue #1149): the three routes, the
/// <c>202</c> with the job id, the readable <c>400</c> for a bad archive, and the <c>404</c> for a
/// job of another business.
/// </summary>
public class ImportsControllerReimbursementReportTests
{
    private static readonly DateTime FirstReportDay = new(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The three routes the issue names, read back off the endpoints themselves.</summary>
    [Theory]
    [InlineData("POST", "api/imports/reimbursement-report")]
    [InlineData("GET", "api/imports/reimbursement-report/jobs")]
    [InlineData("GET", "api/imports/reimbursement-report/jobs/{jobId}")]
    public void The_reimbursement_report_routes_are_published(string method, string route)
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, method, StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == route);
    }

    /// <summary>
    /// An accepted upload answers <c>202</c> with the job id and nothing else, and has imported
    /// nothing: the job is queued.
    /// </summary>
    [Fact]
    public async Task An_accepted_upload_answers_202_with_the_job_id()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadReimbursementReport(
            Upload("Reports_09-11-26.zip", Archive()), CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var body = Assert.IsType<ReimbursementReportUploadAccepted>(accepted.Value);
        Assert.True(body.JobId > 0);

        var job = await fixture.JobRowAsync(body.JobId);
        Assert.Equal(ReimbursementImportJobState.Queued, job.State);
        Assert.Equal("Reports_09-11-26.zip", job.FileName);
        Assert.Empty(await fixture.FileRowsAsync(body.JobId));
    }

    /// <summary>A bad archive is a readable <c>400</c>, and nothing is stored.</summary>
    [Fact]
    public async Task A_bad_archive_answers_400_with_a_readable_message()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadReimbursementReport(
            Upload("Reports.zip", [1, 2, 3, 4]), CancellationToken.None);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Equal(ReimbursementArchivePolicy.UnreadableArchiveMessage, bad.Value);

        await using var verify = fixture.Unrestricted();
        Assert.Empty(verify.ReimbursementImportJobs);
    }

    /// <summary>An archive with no reimbursement XML is refused, with the reason why.</summary>
    [Fact]
    public async Task An_archive_with_no_xml_answers_400()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        await using var db = fixture.Context();

        var archive = ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.pdf", "a made-up PDF placeholder"));

        var result = await Controller(fixture, db).UploadReimbursementReport(
            Upload("Reports.zip", archive), CancellationToken.None);

        Assert.Equal(
            ReimbursementArchivePolicy.NoXmlMessage,
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>An upload with no file at all is refused before anything else happens.</summary>
    [Fact]
    public async Task An_upload_with_no_file_answers_400()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadReimbursementReport(
            Upload("Reports.zip", []), CancellationToken.None);

        Assert.Equal(
            ReimbursementArchivePolicy.EmptyUploadMessage,
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// The status list is the caller's own business's jobs, newest first, with the counts from the
    /// outcome rows.
    /// </summary>
    [Fact]
    public async Task The_job_list_is_the_callers_own_business_newest_first()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();

        var older = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessA,
            ReimbursementImportJobFixture.UploadedAt);
        var newer = await fixture.QueueJobAsync(
            Archive(),
            ReimbursementImportJobFixture.BusinessA,
            ReimbursementImportJobFixture.UploadedAt.AddMinutes(5));
        await fixture.QueueJobAsync(Archive(), ReimbursementImportJobFixture.BusinessB);

        await using var db = fixture.Context();
        var result = await Controller(fixture, db)
            .ReimbursementReportJobs(CancellationToken.None);

        var jobs = Assert.IsAssignableFrom<IReadOnlyList<ReimbursementImportJobSummary>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal([newer, older], jobs.Select(job => job.Id));
    }

    /// <summary>One job's detail carries every file outcome, in the archive's own order.</summary>
    [Fact]
    public async Task One_jobs_detail_carries_its_file_outcomes()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobId = await fixture.QueueJobAsync(ReimbursementArchiveFixtures.FlatArchive(
            ("a.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)),
            ("b.xml", "not xml at all")));

        await using (var worker = fixture.Context())
        {
            await fixture.Process(worker).Handle(jobId, CancellationToken.None);
        }

        await using var db = fixture.Context();
        var result = await Controller(fixture, db)
            .ReimbursementReportJob(jobId, CancellationToken.None);

        var detail = Assert.IsType<ReimbursementImportJobDetail>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(ReimbursementImportJobState.CompletedWithFailures, detail.Job.State);
        Assert.Equal(1, detail.Job.ImportedFiles);
        Assert.Equal(1, detail.Job.FailedFiles);
        Assert.Equal(["a.xml", "b.xml"], detail.Files.Select(file => file.File));
        Assert.Equal(
            ProcessReimbursementImportJob.UnreadableFileReason, detail.Files[1].Reason);
    }

    /// <summary>A job of another business answers <c>404</c>.</summary>
    [Fact]
    public async Task A_job_of_another_business_answers_404()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();
        var jobOfB = await fixture.QueueJobAsync(
            Archive(), ReimbursementImportJobFixture.BusinessB);

        await using var db = fixture.Context(ReimbursementImportJobFixture.BusinessA);
        var result = await Controller(fixture, db)
            .ReimbursementReportJob(jobOfB, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>A job key nobody uploaded answers <c>404</c> too.</summary>
    [Fact]
    public async Task An_unknown_job_answers_404()
    {
        await using var fixture = await ReimbursementImportJobFixture.CreateAsync();

        await using var db = fixture.Context();
        var result = await Controller(fixture, db)
            .ReimbursementReportJob(4242, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    /// <summary>
    /// The upload is bounded at the request pipeline as well as in the use case, with the Domain's
    /// own limit - so an oversized body is refused before it is read at all.
    /// </summary>
    [Fact]
    public void The_upload_declares_the_request_size_limit()
    {
        var action = typeof(ImportsController)
            .GetMethod(nameof(ImportsController.UploadReimbursementReport))!;

        var limit = Assert.Single(
            action.GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: false));

        Assert.Equal(
            ReimbursementArchivePolicy.MaxUploadBytes,
            ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    private static ImportsController Controller(
        ReimbursementImportJobFixture fixture, AppDbContext db)
    {
        var jobs = ReimbursementImportJobFixture.Jobs(db);

        return new ImportsController(
            new ImportNayaxSales(
                Mock.Of<INayaxSalesWorkbookReader>(),
                Mock.Of<INayaxSalesImportStore>(),
                Mock.Of<Inventory.Application.Costing.ICostSale>(),
                Mock.Of<Inventory.Application.Costing.IRebuildProductCost>(),
                InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            new ImportPendingReimbursementXmlFiles(
                Mock.Of<IPendingReimbursementXmlSource>(),
                new ImportReimbursementXmlFile(
                    Mock.Of<IImportedReimbursementStore>(),
                    Mock.Of<IClock>(),
                    new FakeProviderDataCoverageStore())),
            new UploadReimbursementReportArchive(
                new Inventory.Infrastructure.Imports.ZipReimbursementArchiveReader(
                    NullLogger<Inventory.Infrastructure.Imports.ZipReimbursementArchiveReader>.Instance),
                jobs,
                FakeAuthenticatedActorAccessor.Identified(ReimbursementImportJobFixture.Actor()),
                fixture.Clock),
            new ListReimbursementImportJobs(jobs),
            new GetReimbursementImportJob(jobs));
    }

    private static IFormFile Upload(string fileName, byte[] content)
    {
        var stream = new MemoryStream(content);

        return new FormFile(stream, 0, stream.Length, "file", fileName);
    }

    private static byte[] Archive() =>
        ReimbursementArchiveFixtures.FlatArchive(
            ("report_9900001_20261005.xml",
                ReimbursementArchiveFixtures.ReportXml(
                    FirstReportDay, FirstReportDay.AddDays(6), 405m)));
}
