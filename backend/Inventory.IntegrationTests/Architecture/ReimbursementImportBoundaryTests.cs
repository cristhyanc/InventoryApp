using Inventory.Application.Imports;
using Inventory.Infrastructure.Imports;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Two boundaries of the uploaded reimbursement report import that are easier to keep by
/// construction than by review (issue #1149): what its log <em>can</em> carry, and that it has
/// nothing to do with the server's pending import folder.
/// </summary>
public class ReimbursementImportBoundaryTests
{
    /// <summary>
    /// <strong>No method of the log port accepts anything that could be XML content, a figure or a
    /// payment detail.</strong> The acceptance criterion is about what a log line carries, and the
    /// way to keep it is to make the unwanted value unexpressible: every parameter is an identifier,
    /// a failure type name or the run's own outcome record, so nobody can log a settlement figure by
    /// forgetting to withhold it. A file name may appear, which is the one thing about an import that
    /// may.
    /// </summary>
    [Fact]
    public void The_worker_log_port_cannot_be_handed_xml_content_or_a_figure()
    {
        var permitted = new[]
        {
            typeof(int),
            typeof(string),
            typeof(ReimbursementImportJobRunResult),
        };

        var parameters = typeof(IReimbursementImportJobLog)
            .GetMethods()
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .Distinct()
            .ToArray();

        Assert.All(parameters, type => Assert.Contains(type, permitted));

        // And the one record that is permitted carries counts and an outcome - nothing a file said.
        Assert.Equal(
            [
                typeof(ReimbursementImportJobRunOutcome),
                typeof(Inventory.Domain.Imports.ReimbursementImportFileCounts),
                typeof(int),
            ],
            typeof(ReimbursementImportJobRunResult)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }

    /// <summary>
    /// <strong>The uploaded-archive import never touches the shared pending folder.</strong> Nothing
    /// on its path is constructed with the pending-file source or with the host paths that folder is
    /// resolved from, so there is no way for it to read, write or delete a file there - and the
    /// archive reader takes nothing but a logger, because every byte it reads is in memory.
    /// </summary>
    [Fact]
    public void Nothing_on_the_uploaded_archive_path_can_reach_the_pending_folder()
    {
        Type[] forbidden =
        [
            typeof(IPendingReimbursementXmlSource),
            typeof(FileSystemPendingReimbursementXmlSource),
            typeof(PendingReimbursementXmlOptions),
        ];

        Type[] onThePath =
        [
            typeof(UploadReimbursementReportArchive),
            typeof(ProcessReimbursementImportJob),
            typeof(ReimbursementImportJobRunner),
            typeof(ZipReimbursementArchiveReader),
        ];

        foreach (var type in onThePath)
        {
            var dependencies = type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.All(dependencies, dependency => Assert.DoesNotContain(dependency, forbidden));
        }

        // The reader's whole dependency list, written out: a logger and nothing else.
        Assert.Equal(
            [typeof(Microsoft.Extensions.Logging.ILogger<ZipReimbursementArchiveReader>)],
            typeof(ZipReimbursementArchiveReader)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Select(parameter => parameter.ParameterType));
    }
}
