using Inventory.Infrastructure.Documents;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// An in-memory stand-in for the Azure Blob container, with the semantics the real one gives us:
/// create-if-absent, a missing blob reading as nothing, and a failed create storing nothing.
///
/// It exists so the document migration can be tested for what it actually decides - which key a
/// document goes to, when it copies, when it refuses - without an Azure account, credentials or
/// a network. It records every call, so a test can assert that a dry run wrote nothing at all
/// rather than merely that nothing came back.
/// </summary>
internal sealed class InMemoryDocumentBlobContainer : IDocumentBlobContainer
{
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);

    /// <summary>Attempted creates, whether or not the key was free.</summary>
    public int CreateAttempts { get; private set; }

    public int DeleteAttempts { get; private set; }

    /// <summary>Thrown by every read, for the failures that must not look like a missing blob.</summary>
    public Exception? FailReadsWith { get; set; }

    /// <summary>Thrown by every create, for a storage failure part way through a migration.</summary>
    public Exception? FailCreatesWith { get; set; }

    public IReadOnlyCollection<string> BlobNames => _blobs.Keys;

    public byte[] this[string blobName] => _blobs[blobName];

    public bool Contains(string blobName) => _blobs.ContainsKey(blobName);

    /// <summary>Places a blob directly, for arranging a destination that is already occupied.</summary>
    public void Put(string blobName, byte[] content) => _blobs[blobName] = content;

    public async Task<bool> CreateAsync(string blobName, Stream content, CancellationToken cancellationToken)
    {
        CreateAttempts++;
        if (_blobs.ContainsKey(blobName)) return false;
        if (FailCreatesWith is not null) throw FailCreatesWith;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _blobs[blobName] = buffer.ToArray();
        return true;
    }

    public Task<BlobDocument?> OpenReadAsync(string blobName, CancellationToken cancellationToken)
    {
        if (FailReadsWith is not null) throw FailReadsWith;

        return Task.FromResult(_blobs.TryGetValue(blobName, out var content)
            ? new BlobDocument(new MemoryStream(content), content.Length, DateTimeOffset.UnixEpoch)
            : null);
    }

    public Task<bool> DeleteAsync(string blobName, CancellationToken cancellationToken)
    {
        DeleteAttempts++;
        return Task.FromResult(_blobs.Remove(blobName));
    }
}
