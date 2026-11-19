using System.Diagnostics;
using System.Globalization;
using Inventory.Application.Imports;
using Inventory.Domain.Imports;
using Inventory.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace InventoryApi.Tests.Application.Imports;

/// <summary>
/// The <strong>opt-in</strong> full-size measurement issue #1304 asks for: one synthetic history at
/// the row ceiling, imported end to end by the production wiring, with the write-lock hold of every
/// batch and the total duration reported.
///
/// <para><strong>Why it is opt-in rather than an ordinary test.</strong> This repository stops any
/// test that runs past 90 seconds (AGENTS.md § Commands), and a quarter of a million rows of real
/// historical costing takes tens of minutes - the cost the batching exists to spread out. So the
/// case is skipped unless it is asked for, which keeps <c>scripts/validate.sh</c> exactly as fast as
/// it was, and <see cref="NayaxSalesHistoryWriteLockHoldTests"/> remains the per-batch assertion that
/// runs on every validation. This one is how a human, or a scheduled run, obtains the full-size
/// figures the issue wants reported.</para>
///
/// <para><strong>How to run it.</strong> From the repository root:</para>
/// <code>
/// INVENTORY_NAYAX_HISTORY_BENCHMARK=1 dotnet test \
///   backend/Inventory.IntegrationTests/Inventory.IntegrationTests.csproj --configuration Release \
///   --filter "FullyQualifiedName~NayaxSalesHistoryFullSizeBenchmark" --logger "console;verbosity=detailed"
/// </code>
/// <para><c>INVENTORY_NAYAX_HISTORY_BENCHMARK_ROWS</c> and
/// <c>INVENTORY_NAYAX_HISTORY_BENCHMARK_PRODUCTS</c> override the size; their defaults are the
/// policy's row ceiling and a 200-product catalogue, which is the distribution a real machine fleet
/// of that history length has.</para>
///
/// <para><strong>The product distribution is the thing to state with any figure this produces.</strong>
/// Each row's historical cost replays its own product's ledger up to the sale's instant, so what a
/// batch costs grows with the sales <em>per product</em> already stored - rows divided by the
/// catalogue - and not with the length of the file. 250,000 rows over 200 products ends at 1,250
/// sales a product; the same history over 20 products ends at 12,500, which is ten times the replay
/// depth for the same batch size. A run therefore reports both numbers, and a budget met at one
/// distribution says nothing about a much smaller catalogue.</para>
///
/// <para><strong>What it has measured, on this repository's runners (19 November 2026).</strong></para>
/// <code>
/// rows     products  sales/product  longest batch  whole import
///   2,000       200             10          1.1 s           4 s
///  25,000        20          1,250          4.6 s       1.9 min
///  20,000         4          5,000         12.6 s       4.2 min   (10 of 40 batches over budget)
/// </code>
/// <para>The middle line is the depth a 250,000-row history over a 200-product catalogue ends at, and
/// at that depth every batch stayed inside the budget (1.0 s rising to 4.6 s over fifty batches).
/// The last line is the small-catalogue case: from about three thousand sales a product the holds
/// passed <see cref="NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds"/> - 10.3 s to 12.6 s for its
/// last ten batches - so the 10-second budget is <em>not</em> met by a history that concentrates a
/// quarter of a million rows on a handful of products, and a smaller batch size only moves that
/// depth rather than bounding it. Bounding it needs the incremental sale-time ledger read that is
/// recorded as follow-up work, which is a costing-internals change issue #1304 excludes.</para>
///
/// <para><strong>A full 250,000-row run has not completed here.</strong> The attempt exceeded the
/// 30-minute ceiling of the commands this environment allows, so the total duration at the row
/// ceiling is known only as more than half an hour. The per-batch hold - which is what the budget is
/// about, and what a batch of a fixed size costs at a given depth - is therefore measured at the
/// depth a full-size history ends at rather than projected from a shorter file.</para>
/// </summary>
public class NayaxSalesHistoryFullSizeBenchmark
{
    /// <summary>The variable that asks for the run. Anything but <c>1</c> or <c>true</c> skips it.</summary>
    private const string EnabledVariable = "INVENTORY_NAYAX_HISTORY_BENCHMARK";

    private const string RowsVariable = "INVENTORY_NAYAX_HISTORY_BENCHMARK_ROWS";
    private const string ProductsVariable = "INVENTORY_NAYAX_HISTORY_BENCHMARK_PRODUCTS";

    /// <summary>The catalogue a 250,000-row history of a real machine fleet is spread over.</summary>
    private const int DefaultProducts = 200;

    private readonly ITestOutputHelper _output;

    public NayaxSalesHistoryFullSizeBenchmark(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task A_full_size_history_holds_the_write_lock_inside_its_budget()
    {
        if (!Requested())
        {
            _output.WriteLine(
                $"Skipped: set {EnabledVariable}=1 to run the full-size measurement. It imports "
                    + $"{Size(RowsVariable, NayaxSalesHistoryPolicy.MaxRows):N0} rows end to end and "
                    + "takes tens of minutes, which is why it is not part of repository validation.");
            return;
        }

        var rows = Size(RowsVariable, NayaxSalesHistoryPolicy.MaxRows);
        var products = Size(ProductsVariable, DefaultProducts);

        await using var fixture = await NayaxSalesHistoryImportFixture.OnDiskAsync(products);

        var written = Stopwatch.StartNew();
        var jobId = await fixture.QueueJobAsync(
            NayaxSalesHistoryImportFixture.Csv(rows, products: products));
        written.Stop();

        _output.WriteLine(
            $"{rows:N0} rows over {products:N0} products ({rows / products:N0} sales a product) "
                + $"uploaded and stored in {written.Elapsed.TotalSeconds:F1}s.");

        var whole = Stopwatch.StartNew();
        NayaxSalesHistoryImportJobRunResult result;
        ObservedSalesHistoryTransactions observed;
        await using (var db = fixture.Context())
        {
            observed = new ObservedSalesHistoryTransactions(new EfNayaxSalesImportTransactions(db));
            result = await fixture.Process(db, observed).Handle(jobId, CancellationToken.None);
        }

        whole.Stop();

        Report(rows, products, observed.Holds, whole.Elapsed);

        Assert.Equal(NayaxSalesHistoryImportJobRunOutcome.Finished, result.Outcome);
        Assert.Equal(rows, result.Progress.RowsRead);
        Assert.Equal(rows, result.Progress.Created);
        Assert.Equal(
            (rows + NayaxSalesHistoryPolicy.BatchRows - 1) / NayaxSalesHistoryPolicy.BatchRows,
            observed.Holds.Count);

        // The measurement, not a projection of one: every batch of a full-length history stayed
        // inside the budget, or this fails and says which ones did not.
        Assert.All(
            observed.Holds,
            hold => Assert.InRange(
                hold.TotalSeconds, 0, NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds));
    }

    /// <summary>
    /// The figures a pull request quotes: the whole import, and the hold of the batches that matter -
    /// the last ones, because the hold grows with the sales already stored.
    /// </summary>
    private void Report(int rows, int products, List<TimeSpan> holds, TimeSpan whole)
    {
        var seconds = holds.Select(hold => hold.TotalSeconds).ToList();
        var lastTen = seconds.Skip(Math.Max(seconds.Count - 10, 0)).ToList();
        var overBudget = seconds.Count(hold => hold > NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds);

        _output.WriteLine(
            $"{rows:N0} rows in {holds.Count:N0} batches of {NayaxSalesHistoryPolicy.BatchRows}, over "
                + $"{products:N0} products: whole import {whole.TotalMinutes:F1} min "
                + $"({whole.TotalSeconds:F0}s), {rows / Math.Max(whole.TotalSeconds, 0.001):F0} rows/s.");
        _output.WriteLine(
            $"Write lock per batch against a budget of {NayaxSalesHistoryPolicy.MaxWriteLockHoldSeconds}s: "
                + $"longest {seconds.Max():F1}s, average {seconds.Average():F1}s, first "
                + $"{seconds[0]:F1}s, last {seconds[^1]:F1}s, last ten "
                + $"{string.Join("s, ", lastTen.Select(hold => hold.ToString("F1", CultureInfo.InvariantCulture)))}s.");
        _output.WriteLine(
            $"Batches over budget: {overBudget:N0} of {holds.Count:N0}. Every tenth batch: "
                + string.Join(
                    ", ",
                    seconds
                        .Select((hold, index) => (hold, index))
                        .Where(batch => batch.index % 10 == 0)
                        .Select(batch =>
                            $"#{batch.index + 1} {batch.hold.ToString("F1", CultureInfo.InvariantCulture)}s")));
    }

    private static bool Requested() =>
        Environment.GetEnvironmentVariable(EnabledVariable) is "1" or "true" or "TRUE";

    private static int Size(string variable, int fallback) =>
        int.TryParse(
            Environment.GetEnvironmentVariable(variable),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value) && value > 0
            ? value
            : fallback;
}
