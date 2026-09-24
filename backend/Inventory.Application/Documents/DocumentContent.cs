namespace Inventory.Application.Documents;

/// <summary>
/// A document opened for reading. The caller owns the stream and must dispose it; nothing here
/// reveals where the bytes came from, so a caller cannot turn a read into a physical path or a
/// storage URL.
/// </summary>
public sealed class DocumentContent : IAsyncDisposable, IDisposable
{
    /// <summary>Creates a readable document over <paramref name="content"/>.</summary>
    public DocumentContent(Stream content, long byteLength)
    {
        ArgumentNullException.ThrowIfNull(content);

        Content = content;
        ByteLength = byteLength;
    }

    /// <summary>The document bytes, positioned at the start.</summary>
    public Stream Content { get; }

    /// <summary>Length of the stored document in bytes.</summary>
    public long ByteLength { get; }

    /// <inheritdoc />
    public void Dispose() => Content.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}
