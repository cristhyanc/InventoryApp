using System;
using System.Threading;
using System.Threading.Tasks;
using Inventory.Application.Reporting.Gst;

namespace InventoryApi.Tests.Application.Reporting.Gst;

/// <summary>
/// In-memory fake of the GST report facts port, so the use case's orchestration can be tested
/// without EF Core or SQLite.
/// </summary>
public sealed class FakeGstReportFactsProvider : IGstReportFactsProvider
{
    private readonly GstReportFacts _facts;

    public FakeGstReportFactsProvider(GstReportFacts facts) => _facts = facts;

    public (DateTime From, DateTime To, long? MachineId)? LastRequest { get; private set; }

    public Task<GstReportFacts> GetFactsAsync(DateTime from, DateTime to, long? machineId, CancellationToken cancellationToken)
    {
        LastRequest = (from, to, machineId);
        return Task.FromResult(_facts);
    }

    public static GstReportFacts Complete(bool importedContainsRows = true, bool importedContainsGstClassification = true) =>
        new(importedContainsRows, importedContainsGstClassification);
}
