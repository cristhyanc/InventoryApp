using Inventory.Application.ReceiptScanning;

namespace Inventory.Infrastructure.ReceiptScanning;

/// <summary>
/// The single typed configuration contract for receipt scanning (issue #1163), bound once in the
/// composition root instead of the reader reaching into <c>IConfiguration</c>.
///
/// <para><strong>In production every value comes from App Service application settings or a Key Vault
/// reference, and none of them is ever in the repository.</strong> The names are
/// <c>ReceiptScanning__ApiKey</c>, <c>ReceiptScanning__Model</c>,
/// <c>ReceiptScanning__TimeoutSeconds</c>, <c>ReceiptScanning__MaxImageBytes</c>,
/// <c>ReceiptScanning__MaxPdfBytes</c> and <c>ReceiptScanning__MaxPdfPages</c>; a human sets them
/// (README.md § Receipt scanning).</para>
///
/// <para><strong>With no <see cref="ApiKey"/> the feature is off.</strong> That is the shipped state
/// and the state of every environment and test host: the endpoint answers "not available", no
/// outbound call is made, and deploying without the key is safe. <strong>The production key is set
/// only after issue #1164's monthly cap is deployed and plan enforcement is on</strong> - the launch
/// gate recorded on #1163.</para>
/// </summary>
public sealed class ReceiptScanningOptions
{
    public const string SectionName = "ReceiptScanning";

    /// <summary>
    /// The reader's API key. A secret: never logged, never in a response, never in a validation
    /// message and never in the repository. Absent means "no reader": the feature is off.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// The model the reader asks. Decided on 10 November 2026 after a test on 8 real receipts;
    /// changing it is a decision with its own issue, which is why it is a setting with that decision
    /// as its default rather than a constant.
    /// </summary>
    public string Model { get; set; } = ReceiptScanningConfiguration.DefaultModel;

    /// <summary>The reader's base address. Overridable so a test host can point the typed client at itself.</summary>
    public string BaseUrl { get; set; } = ReceiptScanningConfiguration.DefaultBaseUrl;

    /// <summary>How long one read may take before it is abandoned. No retry follows.</summary>
    public int TimeoutSeconds { get; set; } = ReceiptScanningConfiguration.DefaultTimeoutSeconds;

    /// <summary>The largest image the reader accepts, in bytes.</summary>
    public long MaxImageBytes { get; set; } = ReceiptScanningConfiguration.DefaultMaxImageBytes;

    /// <summary>The largest PDF the reader accepts, in bytes.</summary>
    public long MaxPdfBytes { get; set; } = ReceiptScanningConfiguration.DefaultMaxPdfBytes;

    /// <summary>The most pages the reader accepts in one PDF.</summary>
    public int MaxPdfPages { get; set; } = ReceiptScanningConfiguration.DefaultMaxPdfPages;
}

/// <summary>
/// The validated form of <see cref="ReceiptScanningOptions"/>, and the implementation of
/// <see cref="IReceiptScanSettings"/> the use case sees (issue #1163).
///
/// <para><strong>Two outcomes, and both are deliberate.</strong> With no API key the feature is off:
/// the endpoint answers "not available", nothing leaves the process, and every environment that has
/// not been given a key behaves exactly as it did before this feature existed. With a key and a
/// broken setting beside it - a blank model, a timeout of zero, a limit of zero, a non-https base
/// URL - registration fails here, at startup, with the setting named rather than at the first scan.
/// It is <c>NayaxSqsConfiguration</c>'s shape, for <c>NayaxSqsConfiguration</c>'s reason.</para>
///
/// <para><strong>The key is held here and published nowhere.</strong> There is no property on
/// <see cref="IReceiptScanSettings"/> that could return it, and nothing in this type's
/// <c>ToString</c>, logs or exceptions names it.</para>
/// </summary>
public sealed class ReceiptScanningConfiguration : IReceiptScanSettings
{
    /// <summary>
    /// The model decided on 10 November 2026 (issue #1163's known constraints): all 8 test receipts
    /// right on money and quantities. Haiku was rejected - it marked unprinted carton sizes as single
    /// items and double-counted an already-included levy.
    /// </summary>
    public const string DefaultModel = "claude-sonnet-5-5";

    /// <summary>The reader's documented base address.</summary>
    public const string DefaultBaseUrl = "https://api.anthropic.com/";

    /// <summary>The timeout issue #1163 specifies.</summary>
    public const int DefaultTimeoutSeconds = 60;

    /// <summary>
    /// The default image size limit: 5 MB, the reader's documented per-image ceiling at the time this
    /// was written. A setting rather than a constant precisely because the vendor's limits change -
    /// raising or lowering it is an App Service setting, not a deployment of new code.
    /// </summary>
    public const long DefaultMaxImageBytes = 5L * 1024 * 1024;

    /// <summary>
    /// The default PDF size limit: 32 MB, the reader's documented per-request ceiling. In practice
    /// the scan endpoint's own upload cap of eight million bytes stops a request first, and the
    /// purchase document rules cap a receipt at 10 MB after it
    /// (<c>Inventory.Domain.Purchases.PurchaseDocumentPolicy</c>), so this is the loosest of the
    /// three and exists so they cannot silently disagree if either ceiling ever rises.
    /// </summary>
    public const long DefaultMaxPdfBytes = 32L * 1024 * 1024;

    /// <summary>The default page limit: 100 pages, the reader's documented per-document ceiling.</summary>
    public const int DefaultMaxPdfPages = 100;

    private ReceiptScanningConfiguration(
        string? apiKey,
        string model,
        Uri baseAddress,
        TimeSpan timeout,
        long maxImageBytes,
        long maxPdfBytes,
        int maxPdfPages)
    {
        ApiKey = apiKey;
        Model = model;
        BaseAddress = baseAddress;
        Timeout = timeout;
        MaxImageBytes = maxImageBytes;
        MaxPdfBytes = maxPdfBytes;
        MaxPdfPages = maxPdfPages;
    }

    /// <summary>
    /// The reader's key, or <see langword="null"/> when none is configured. Internal: the adapter is
    /// the only thing that may see it, and <see cref="IReceiptScanSettings"/> deliberately cannot.
    /// </summary>
    internal string? ApiKey { get; }

    /// <summary>The model to ask.</summary>
    public string Model { get; }

    /// <summary>The reader's base address.</summary>
    public Uri BaseAddress { get; }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    /// <inheritdoc />
    public TimeSpan Timeout { get; }

    /// <inheritdoc />
    public long MaxImageBytes { get; }

    /// <inheritdoc />
    public long MaxPdfBytes { get; }

    /// <inheritdoc />
    public int MaxPdfPages { get; }

    /// <summary>
    /// Validates <paramref name="options"/> and resolves them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A key is configured together with a setting that cannot be used.
    /// </exception>
    public static ReceiptScanningConfiguration From(ReceiptScanningOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var apiKey = string.IsNullOrWhiteSpace(options.ApiKey) ? null : options.ApiKey.Trim();
        var baseAddress = BaseAddressOf(options, apiKey);

        if (apiKey is not null)
        {
            Require(!string.IsNullOrWhiteSpace(options.Model), $"{SettingName(nameof(options.Model))} must be set.");
            Require(options.TimeoutSeconds > 0, $"{SettingName(nameof(options.TimeoutSeconds))} must be above zero.");
            Require(options.MaxImageBytes > 0, $"{SettingName(nameof(options.MaxImageBytes))} must be above zero.");
            Require(options.MaxPdfBytes > 0, $"{SettingName(nameof(options.MaxPdfBytes))} must be above zero.");
            Require(options.MaxPdfPages > 0, $"{SettingName(nameof(options.MaxPdfPages))} must be above zero.");
        }

        return new ReceiptScanningConfiguration(
            apiKey,
            string.IsNullOrWhiteSpace(options.Model) ? DefaultModel : options.Model.Trim(),
            baseAddress,
            TimeSpan.FromSeconds(options.TimeoutSeconds > 0 ? options.TimeoutSeconds : DefaultTimeoutSeconds),
            options.MaxImageBytes > 0 ? options.MaxImageBytes : DefaultMaxImageBytes,
            options.MaxPdfBytes > 0 ? options.MaxPdfBytes : DefaultMaxPdfBytes,
            options.MaxPdfPages > 0 ? options.MaxPdfPages : DefaultMaxPdfPages);
    }

    /// <summary>
    /// The base address, which must be absolute, and must be <c>https</c> whenever a key is
    /// configured: a key travels on every request, and a key on a plaintext connection is a
    /// disclosed key. A test host with no key may point at its own loopback address.
    /// </summary>
    private static Uri BaseAddressOf(ReceiptScanningOptions options, string? apiKey)
    {
        var configured = string.IsNullOrWhiteSpace(options.BaseUrl) ? DefaultBaseUrl : options.BaseUrl.Trim();

        Require(
            Uri.TryCreate(configured, UriKind.Absolute, out var baseAddress),
            $"{SettingName(nameof(options.BaseUrl))} must be an absolute URL.");
        Require(
            apiKey is null || baseAddress!.Scheme == Uri.UriSchemeHttps,
            $"{SettingName(nameof(options.BaseUrl))} must use https when an API key is configured.");

        return baseAddress!;
    }

    private static string SettingName(string property) => $"{ReceiptScanningOptions.SectionName}:{property}";

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
