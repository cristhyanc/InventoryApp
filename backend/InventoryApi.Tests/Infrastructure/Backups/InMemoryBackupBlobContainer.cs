using Inventory.Infrastructure.Backups;

namespace InventoryApi.Tests.Infrastructure.Backups;

/// <summary>
/// An in-memory stand-in for the backup container, with the semantics the real one gives us
/// (issue #332): a conditional create that refuses an occupied name without touching what is
/// already there, metadata stored alongside the bytes, and a missing blob reading as nothing.
///
/// It exists so the uploader can be tested for what it actually decides - which object a
/// snapshot goes to, when a monthly recovery point is created, when an upload counts as
/// verified - without an Azure account, credentials or a network. It records every attempted
/// create, so a test can assert that nothing was even tried rather than merely that nothing
/// came back.
/// </summary>
internal sealed class InMemoryBackupBlobContainer : IBackupBlobContainer
{
    private readonly Dictionary<string, StoredBlob> _blobs = new(StringComparer.Ordinal);

    /// <summary>Attempted creates, whether or not the name was free.</summary>
    public List<string> CreateAttempts { get; } = [];

    /// <summary>Thrown by every create, for a storage failure part way through an upload.</summary>
    public Exception? FailCreatesWith { get; set; }

    /// <summary>
    /// Rewrites what a create actually stores, for the case the uploader has to catch: the write
    /// is accepted and what the service holds afterwards is not what was sent. Only reading the
    /// object back can detect that, which is why the uploader verifies completion.
    /// </summary>
    public Func<StoredBlob, StoredBlob>? CorruptCreatedBlobWith { get; set; }

    public IReadOnlyCollection<string> BlobNames => _blobs.Keys;

    public StoredBlob this[string blobName] => _blobs[blobName];

    public bool Contains(string blobName) => _blobs.ContainsKey(blobName);

    /// <summary>Places a blob directly, for arranging a month that already has a recovery point.</summary>
    public void Put(string blobName, byte[] content, IReadOnlyDictionary<string, string>? metadata = null) =>
        _blobs[blobName] = new StoredBlob(
            content,
            metadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    public async Task<bool> CreateIfAbsentAsync(
        string blobName,
        Stream content,
        IReadOnlyDictionary<string, string> metadata,
        CancellationToken cancellationToken)
    {
        CreateAttempts.Add(blobName);

        // The real container sends If-None-Match: *, so the service itself refuses the write and
        // the existing object is never read, rewritten or deleted.
        if (_blobs.ContainsKey(blobName)) return false;
        if (FailCreatesWith is not null) throw FailCreatesWith;

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        var stored = new StoredBlob(
            buffer.ToArray(),
            new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase));

        _blobs[blobName] = CorruptCreatedBlobWith is null ? stored : CorruptCreatedBlobWith(stored);
        return true;
    }

    public Task<BackupBlobProperties?> GetPropertiesAsync(string blobName, CancellationToken cancellationToken) =>
        Task.FromResult(_blobs.TryGetValue(blobName, out var blob)
            ? new BackupBlobProperties(blob.Content.Length, blob.Metadata)
            : null);

    /// <summary>A stored object: its bytes and the metadata the create carried.</summary>
    internal sealed record StoredBlob(byte[] Content, IReadOnlyDictionary<string, string> Metadata);
}
