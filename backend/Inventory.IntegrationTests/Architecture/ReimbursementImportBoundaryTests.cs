using Inventory.Application.Imports;
using Inventory.Infrastructure.Imports;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Two boundaries of the uploaded reimbursement report import that are easier to keep by
/// construction than by review (issue #1149): what its log <em>can</em> carry, and that no
/// reimbursement import reads from a folder on the server any more (issue #1237).
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
    /// <strong>No reimbursement import reaches a folder on the server.</strong> Issue #1237 retired
    /// the import that read whatever XML somebody had left in <c>{WebRootPath}/ImportedFiles</c>, so
    /// its port, its adapter and the options record carrying the host's paths are gone from both
    /// inner layers - and nothing on the upload's path takes a dependency that carries a host path,
    /// which is how the retired import reached the folder in the first place. The archive reader
    /// takes nothing but a logger, because every byte it reads is in memory.
    /// </summary>
    [Fact]
    public void No_reimbursement_import_can_reach_a_folder_on_the_server()
    {
        string[] retired =
        [
            "Inventory.Application.Imports.IPendingReimbursementXmlSource",
            "Inventory.Application.Imports.ImportPendingReimbursementXmlFiles",
            "Inventory.Application.Imports.ImportedFileImportResult",
            "Inventory.Infrastructure.Imports.FileSystemPendingReimbursementXmlSource",
            "Inventory.Infrastructure.Imports.PendingReimbursementXmlOptions",
        ];

        Type[] onThePath =
        [
            typeof(UploadReimbursementReportArchive),
            typeof(ProcessReimbursementImportJob),
            typeof(ReimbursementImportJobRunner),
            typeof(ZipReimbursementArchiveReader),
        ];

        var layers = new[]
        {
            typeof(UploadReimbursementReportArchive).Assembly,
            typeof(ZipReimbursementArchiveReader).Assembly,
        };

        Assert.All(retired, name => Assert.All(layers, layer => Assert.Null(layer.GetType(name))));

        // A host path is how a folder is reached at all, so no dependency on the path may carry one.
        string[] hostPaths = ["ContentRootPath", "WebRootPath"];

        foreach (var type in onThePath)
        {
            var dependencies = type.GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType)
                .ToArray();

            Assert.All(dependencies, dependency => Assert.DoesNotContain(
                dependency.GetProperties().Select(property => property.Name),
                name => hostPaths.Contains(name, StringComparer.Ordinal)));
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
