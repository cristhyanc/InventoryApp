using System.Text;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Domain.Subscriptions;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Imports;
using InventoryApi.Tests.Swagger;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The HTTP surface of the sales history import (issue #1304): the two routes, the <c>202</c> with
/// the job id, the readable <c>400</c> for a file that is refused, and the declarations that make the
/// endpoint the normal sales file upload's equal - the same capability, the same plan requirement and
/// the Domain's own byte limit at the request pipeline.
/// </summary>
public class ImportsControllerNayaxSalesHistoryTests
{
    /// <summary>The two routes the issue names, read back off the endpoints themselves.</summary>
    [Theory]
    [InlineData("POST", "api/imports/nayax-sales-history")]
    [InlineData("GET", "api/imports/nayax-sales-history/jobs")]
    public void The_sales_history_routes_are_published(string method, string route)
    {
        var descriptions = ApiContractTestHost.GetApiDescriptionsFor<ImportsController>();

        Assert.Contains(descriptions, description =>
            string.Equals(description.HttpMethod, method, StringComparison.OrdinalIgnoreCase) &&
            description.RelativePath == route);
    }

    /// <summary>
    /// An accepted upload answers <c>202</c> with the job id and nothing else, and has imported
    /// nothing: the job is queued and the background worker does the importing.
    /// </summary>
    [Fact]
    public async Task An_accepted_upload_answers_202_with_the_job_id()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadNayaxSalesHistory(
            Upload("Sales history.csv", NayaxSalesHistoryImportFixture.Csv(3)),
            CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var body = Assert.IsType<NayaxSalesHistoryUploadAccepted>(accepted.Value);
        Assert.True(body.JobId > 0);

        var job = await fixture.JobRowAsync(body.JobId);
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, job.State);
        Assert.Equal("Sales history.csv", job.FileName);
        Assert.Equal(3, job.TotalRows);
        Assert.Empty(await fixture.SalesAsync());
    }

    /// <summary>A file that is not a CSV is a readable <c>400</c>, and nothing is stored.</summary>
    [Fact]
    public async Task A_spreadsheet_answers_400_with_a_readable_message()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadNayaxSalesHistory(
            Upload("history.xlsx", NayaxSalesHistoryImportFixture.Csv(3)), CancellationToken.None);

        Assert.Equal(
            NayaxSalesHistoryPolicy.UnsupportedFormatMessage,
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);

        await using var verify = fixture.Unrestricted();
        Assert.Empty(verify.NayaxSalesHistoryImportJobs);
    }

    /// <summary>An upload with no file at all is refused before anything else happens.</summary>
    [Fact]
    public async Task An_upload_with_no_file_answers_400()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();
        await using var db = fixture.Context();

        var result = await Controller(fixture, db).UploadNayaxSalesHistory(
            Upload("history.csv", string.Empty), CancellationToken.None);

        Assert.Equal(
            NayaxSalesHistoryPolicy.EmptyUploadMessage,
            Assert.IsType<BadRequestObjectResult>(result.Result).Value);
    }

    /// <summary>
    /// The status read is the caller's own business's jobs, newest first, with the progress the
    /// background job recorded.
    /// </summary>
    [Fact]
    public async Task The_status_read_answers_the_callers_own_jobs_newest_first()
    {
        await using var fixture = await NayaxSalesHistoryImportFixture.CreateAsync();

        var older = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2),
            uploadedAtUtc: NayaxSalesHistoryImportFixture.UploadedAt.AddHours(-1));
        await using (var db = fixture.Context())
            await fixture.Process(db).Handle(older, CancellationToken.None);

        var newer = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(4, firstTransactionId: 8_000_001));

        // Another business's job is not the caller's, and is not in the answer.
        await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(2), NayaxSalesHistoryImportFixture.BusinessB);

        await using var read = fixture.Context();
        var result = await Controller(fixture, read).NayaxSalesHistoryJobs(CancellationToken.None);

        var jobs = Assert.IsAssignableFrom<IReadOnlyList<NayaxSalesHistoryImportJobSummary>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal([newer, older], jobs.Select(job => job.Id));
        Assert.Equal(NayaxSalesHistoryImportJobState.Queued, jobs[0].State);
        Assert.Equal(4, jobs[0].TotalRows);
        Assert.Equal(0, jobs[0].RowsRead);
        Assert.Equal(NayaxSalesHistoryImportJobState.Completed, jobs[1].State);
        Assert.Equal(2, jobs[1].RowsRead);
        Assert.Equal(2, jobs[1].Created);
    }

    /// <summary>
    /// The upload is bounded at the request pipeline as well as in the use case, with the Domain's
    /// own limit - so an oversized body is refused before it is read at all.
    /// </summary>
    [Fact]
    public void The_upload_declares_the_request_size_limit()
    {
        var action = typeof(ImportsController)
            .GetMethod(nameof(ImportsController.UploadNayaxSalesHistory))!;

        var limit = Assert.Single(
            action.GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: false));

        Assert.Equal(
            NayaxSalesHistoryPolicy.MaxUploadBytes,
            ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    /// <summary>
    /// Both routes declare the plan the normal sales file upload declares, which is the issue's own
    /// requirement: it is the same uploaded-file import, done in the background.
    /// </summary>
    [Theory]
    [InlineData(nameof(ImportsController.UploadNayaxSalesHistory))]
    [InlineData(nameof(ImportsController.NayaxSalesHistoryJobs))]
    public void Both_routes_declare_the_sales_file_import_plan(string actionName)
    {
        var action = typeof(ImportsController).GetMethod(actionName)!;

        var plan = Assert.Single(action.GetCustomAttributes(typeof(RequirePlanAttribute), inherit: false));

        Assert.Equal(PlanFeature.SalesFileImport, ((RequirePlanAttribute)plan).Feature);

        // And the capability is the controller's own, as every import's is.
        var capability = Assert.Single(
            typeof(ImportsController).GetCustomAttributes(typeof(RequireCapabilityAttribute), inherit: false));

        Assert.Equal(
            Inventory.Application.Access.Capability.ImportsRun,
            ((RequireCapabilityAttribute)capability).Capability);
    }

    private static IFormFile Upload(string fileName, string content)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return new FormFile(stream, 0, stream.Length, "file", fileName);
    }

    private static ImportsController Controller(
        NayaxSalesHistoryImportFixture fixture, AppDbContext db) =>
        new(
            // The normal upload and the reimbursement routes take no part in these cases.
            new ImportNayaxSales(
                Mock.Of<INayaxSalesWorkbookReader>(),
                Mock.Of<INayaxSalesImportStore>(),
                new FakeNayaxSalesImportTransactions(),
                Mock.Of<Inventory.Application.Costing.ICostSale>(),
                Mock.Of<Inventory.Application.Costing.IRebuildProductCost>(),
                InventoryApi.Tests.Application.Nayax.FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            fixture.Upload(db),
            new ListNayaxSalesHistoryImportJobs(NayaxSalesHistoryImportFixture.Jobs(db)),
            new UploadReimbursementReportArchive(
                Mock.Of<IReimbursementArchiveReader>(),
                Mock.Of<IReimbursementImportJobStore>(),
                InventoryApi.Tests.Application.Tenancy.FakeAuthenticatedActorAccessor.Identified(
                    NayaxSalesHistoryImportFixture.Actor()),
                fixture.Clock),
            new ListReimbursementImportJobs(Mock.Of<IReimbursementImportJobStore>()),
            new GetReimbursementImportJob(Mock.Of<IReimbursementImportJobStore>()));
}
