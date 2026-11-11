using Inventory.Domain.ReceiptScanning;

namespace Inventory.Application.ReceiptScanning;

/// <summary>
/// One product of the business, as the reader is told about it (issue #1163): an id and a name, and
/// nothing else.
/// </summary>
public sealed record ReceiptScanProduct(long Id, string Name);

/// <summary>One supplier of the business, on the same terms as <see cref="ReceiptScanProduct"/>.</summary>
public sealed record ReceiptScanSupplier(int Id, string Name);

/// <summary>
/// The file to read. The media type is the one the server derived from the extension, never the one
/// the client claimed (AGENTS.md § Files and attachments).
/// </summary>
/// <param name="MediaType">The derived media type, for example <c>image/jpeg</c>.</param>
/// <param name="Kind">Whether it is an image or a PDF.</param>
/// <param name="Content">The bytes, already read and already size-checked.</param>
/// <param name="PageCount">
/// How many pages a PDF has, for the log line and nothing else; <see langword="null"/> for an image.
/// </param>
public sealed record ReceiptScanFile(
    string MediaType,
    ReceiptScanMediaKind Kind,
    byte[] Content,
    int? PageCount);

/// <summary>
/// Everything the reader is given (issue #1163), and so everything that leaves this application when
/// a receipt is scanned: the file, this business's product names and supplier names, and nothing
/// else.
/// </summary>
/// <param name="File">The receipt.</param>
/// <param name="Products">This business's active products, as ids and names.</param>
/// <param name="Suppliers">This business's suppliers, as ids and names.</param>
public sealed record ReceiptReadRequest(
    ReceiptScanFile File,
    IReadOnlyList<ReceiptScanProduct> Products,
    IReadOnlyList<ReceiptScanSupplier> Suppliers);

/// <summary>
/// What the reader answered: the JSON text it produced, and what the call cost.
/// </summary>
/// <param name="Json">
/// The model's output as text. It is deliberately not parsed by the adapter: validating it against
/// the published schema is <see cref="ReceiptScanPayloadParser"/>'s single responsibility, so a
/// second reader could not come to accept a different shape.
/// </param>
/// <param name="InputTokens">Tokens the call consumed, for the log line, when the reader reports them.</param>
/// <param name="OutputTokens">Tokens the call produced, on the same terms.</param>
public sealed record ReceiptReadResult(string Json, int? InputTokens, int? OutputTokens);

/// <summary>
/// Reads a receipt (issue #1163). The Application-owned port in front of an external model, in the
/// shape every other external integration in this application has (AGENTS.md § Architecture rules):
/// narrow, named after what it does, and with the adapter in <c>Inventory.Infrastructure</c>.
///
/// <para><strong>One call, no retry.</strong> A scan costs money, so a failed read is reported rather
/// than repeated: the operator decides whether to try again. The timeout is configuration.</para>
///
/// <para><strong>It stores nothing.</strong> No implementation may persist the file, the answer or
/// any part of either, and nothing in this contract offers it anywhere to put them.</para>
/// </summary>
public interface IReceiptReader
{
    /// <summary>
    /// Reads <paramref name="request"/> and answers the model's JSON.
    /// </summary>
    /// <exception cref="ReceiptReaderTimeoutException">The reader did not answer in time.</exception>
    /// <exception cref="ReceiptReaderUnavailableException">
    /// The reader refused, failed, or answered something that is not a completion.
    /// </exception>
    Task<ReceiptReadResult> ReadAsync(ReceiptReadRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// The reader did not answer within the configured timeout (issue #1163). Its own exception type
/// because the operator is told something different - the scan may be worth retrying - and because
/// exactly one call having been made is part of the contract.
/// </summary>
public sealed class ReceiptReaderTimeoutException : Exception
{
    public ReceiptReaderTimeoutException()
        : base("The receipt reader did not answer in time.")
    {
    }

    public ReceiptReaderTimeoutException(string message)
        : base(message)
    {
    }

    public ReceiptReaderTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The reader could not be used at all (issue #1163): a transport failure, a refusal, or an answer
/// that is not a completion.
///
/// <para>Its message is this application's own and never the vendor's: an upstream error body can
/// quote a request, and a request here contains a receipt.</para>
/// </summary>
public sealed class ReceiptReaderUnavailableException : Exception
{
    public ReceiptReaderUnavailableException()
        : base("The receipt reader is unavailable.")
    {
    }

    public ReceiptReaderUnavailableException(string message)
        : base(message)
    {
    }

    public ReceiptReaderUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
