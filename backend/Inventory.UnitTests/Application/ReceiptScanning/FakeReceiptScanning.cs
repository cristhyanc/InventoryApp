using Inventory.Application.ReceiptScanning;
using Inventory.Domain.ReceiptScanning;

namespace InventoryApi.Tests.Application.ReceiptScanning;

/// <summary>
/// The receipt reader, substituted (issue #1163). <strong>No test makes a live call</strong> - issue
/// #1163 excludes them explicitly - so every test of the use case and of the endpoint runs against
/// this: it answers a fixed JSON text, counts how many times it was asked, and records the request it
/// was given so a test can assert what would have left the application.
/// </summary>
public sealed class FakeReceiptReader : IReceiptReader
{
    private readonly Func<ReceiptReadRequest, ReceiptReadResult> _answer;

    public FakeReceiptReader(string json, int? inputTokens = 1200, int? outputTokens = 400)
        : this(_ => new ReceiptReadResult(json, inputTokens, outputTokens))
    {
    }

    public FakeReceiptReader(Func<ReceiptReadRequest, ReceiptReadResult> answer) => _answer = answer;

    /// <summary>How many times the reader was asked. Zero is the assertion most of these tests make.</summary>
    public int Calls { get; private set; }

    /// <summary>The last request, so a test can assert exactly what was sent.</summary>
    public ReceiptReadRequest? LastRequest { get; private set; }

    /// <summary>A reader that times out, having been called exactly once.</summary>
    public static FakeReceiptReader TimingOut() =>
        new(_ => throw new ReceiptReaderTimeoutException());

    /// <summary>A reader that cannot be used.</summary>
    public static FakeReceiptReader Unavailable() =>
        new(_ => throw new ReceiptReaderUnavailableException());

    /// <inheritdoc />
    public Task<ReceiptReadResult> ReadAsync(
        ReceiptReadRequest request,
        CancellationToken cancellationToken)
    {
        Calls++;
        LastRequest = request;

        return Task.FromResult(_answer(request));
    }
}

/// <summary>The products and suppliers a scan is read against, in memory.</summary>
public sealed class FakeReceiptScanCatalogueStore : IReceiptScanCatalogueStore
{
    private readonly IReadOnlyList<ReceiptScanProduct> _products;
    private readonly IReadOnlyList<ReceiptScanSupplier> _suppliers;

    public FakeReceiptScanCatalogueStore(
        IReadOnlyList<ReceiptScanProduct>? products = null,
        IReadOnlyList<ReceiptScanSupplier>? suppliers = null)
    {
        _products = products ?? [];
        _suppliers = suppliers ?? [];
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReceiptScanProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_products);

    /// <inheritdoc />
    public Task<IReadOnlyList<ReceiptScanSupplier>> ListSuppliersAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_suppliers);
}

/// <summary>
/// The scanning settings, as a test chooses them. <see cref="SwitchedOff"/> is the shipped state and
/// is what every test host uses unless it is testing a scan.
/// </summary>
public sealed class FakeReceiptScanSettings : IReceiptScanSettings
{
    public bool IsConfigured { get; set; } = true;

    public long MaxImageBytes { get; set; } = 5L * 1024 * 1024;

    public long MaxPdfBytes { get; set; } = 32L * 1024 * 1024;

    public int MaxPdfPages { get; set; } = 100;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>No key configured: the shipped state.</summary>
    public static FakeReceiptScanSettings SwitchedOff() => new() { IsConfigured = false };
}

/// <summary>A PDF page count a test chooses, so no test needs a real PDF to exercise the page limit.</summary>
public sealed class FakePdfPageCounter : IPdfPageCounter
{
    private readonly int? _pages;

    public FakePdfPageCounter(int? pages = 1) => _pages = pages;

    /// <inheritdoc />
    public int? Count(ReadOnlySpan<byte> content) => _pages;
}

/// <summary>
/// The monthly scan counter, in memory (issue #1164): one count per month key, incremented only
/// while the month's allowance is not gone.
///
/// <para>It records <em>every</em> attempt to count, including the ones it refuses, so a test can
/// assert both "the scan was counted" and "nothing was counted" as a list rather than as an
/// inference. The serialisation the real store provides is not modelled here - that is the relational
/// store's own concurrency test - but the comparison and the increment are the same rule.</para>
/// </summary>
public sealed class FakeReceiptScanUsageStore : IReceiptScanUsageStore
{
    private readonly Dictionary<int, int> _used = [];

    public FakeReceiptScanUsageStore(int monthKey = 0, int alreadyUsed = 0)
    {
        if (alreadyUsed > 0)
        {
            _used[monthKey] = alreadyUsed;
        }
    }

    /// <summary>Every month key this store was asked to count a scan against, in order.</summary>
    public List<int> CountAttempts { get; } = [];

    /// <summary>Every month key a scan was actually counted against, in order.</summary>
    public List<int> Counted { get; } = [];

    /// <inheritdoc />
    public Task<int> ReadUsedAsync(int monthKey, CancellationToken cancellationToken = default) =>
        Task.FromResult(_used.GetValueOrDefault(monthKey));

    /// <inheritdoc />
    public Task<int?> TryCountScanAsync(
        int monthKey, int limit, CancellationToken cancellationToken = default)
    {
        CountAttempts.Add(monthKey);

        var used = _used.GetValueOrDefault(monthKey);
        if (!ReceiptScanQuotaPolicy.Fits(used, limit))
        {
            return Task.FromResult<int?>(null);
        }

        _used[monthKey] = used + 1;
        Counted.Add(monthKey);

        return Task.FromResult<int?>(used + 1);
    }
}

/// <summary>
/// The scan log, recorded rather than written. Its one method's parameters are the whole of what a
/// scan may record, so a test that reads them back is asserting the privacy rule itself.
/// </summary>
public sealed class RecordingReceiptScanLog : IReceiptScanLog
{
    /// <summary>Every line recorded, in order.</summary>
    public List<Entry> Entries { get; } = [];

    /// <inheritdoc />
    public void Completed(
        int businessId,
        ScanReceiptOutcome outcome,
        TimeSpan duration,
        int? pageCount,
        int? inputTokens,
        int? outputTokens) =>
        Entries.Add(new Entry(businessId, outcome, duration, pageCount, inputTokens, outputTokens));

    /// <summary>One recorded line.</summary>
    public sealed record Entry(
        int BusinessId,
        ScanReceiptOutcome Outcome,
        TimeSpan Duration,
        int? PageCount,
        int? InputTokens,
        int? OutputTokens);
}
