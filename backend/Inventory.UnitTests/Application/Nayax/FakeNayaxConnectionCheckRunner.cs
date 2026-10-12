using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// A check runner that records the candidate credential it was handed and answers with a fixed
/// report (issue #536).
///
/// It exists so the use case's own decisions - validation, the rate limit, the draft and what the
/// result carries - can be tested without any HTTP at all, and so the test can prove the operator id
/// and token the use case sent to Nayax are exactly the submitted ones. The runner's real behaviour,
/// including the status mapping, is covered against a fake HTTP handler in
/// <c>Inventory.IntegrationTests</c>.
/// </summary>
public sealed class FakeNayaxConnectionCheckRunner : INayaxConnectionCheckRunner
{
    private readonly NayaxConnectionCheckReport _report;

    public FakeNayaxConnectionCheckRunner(NayaxConnectionCheckReport? report = null)
    {
        _report = report ?? AllPassed();
    }

    /// <summary>The credentials each run was asked to test, in order.</summary>
    public List<NayaxConnectionCredential> Candidates { get; } = [];

    /// <summary>A report in which every check passed, against a business with one machine.</summary>
    public static NayaxConnectionCheckReport AllPassed() =>
        new([.. NayaxConnectionCheckPolicy.All.Select(NayaxConnectionCheckLine.Passed)], 1);

    public Task<NayaxConnectionCheckReport> RunAsync(
        NayaxConnectionCredential candidate,
        CancellationToken cancellationToken)
    {
        Candidates.Add(candidate);
        return Task.FromResult(_report);
    }
}
