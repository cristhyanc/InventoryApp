namespace Inventory.Application.Documents;

/// <summary>
/// A document opened for reading. The caller owns the stream and must dispose it; nothing here
/// reveals where the bytes came from, so a caller cannot turn a read into a physical path or a
/// storage URL.
/// </summary>
public sealed class DocumentContent : IAsyncDisposable, IDisposable
{
    /// <summary>Creates a readable document over <paramref name="content"/>.</summary>
    public DocumentContent(Stream content, long byteLength, DateTimeOffset? lastModified = null)
    {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        ByteLength = byteLength;
        LastModified = lastModified;
    }

    /// <summary>The document bytes, positioned at the start.</summary>
    public Stream Content { get; }

    /// <summary>Length of the stored document in bytes.</summary>
    public long ByteLength { get; }

    /// <summary>
    /// When the stored document was last written, as the storage itself reports it, or
    /// <c>null</c> when the storage cannot say. It is a plain instant rather than anything
    /// storage-specific - a filesystem write time, a blob's last-modified - so the API boundary
    /// can turn it into a <c>Last-Modified</c> response header without knowing which storage
    /// produced it.
    /// </summary>
    public DateTimeOffset? LastModified { get; }

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
