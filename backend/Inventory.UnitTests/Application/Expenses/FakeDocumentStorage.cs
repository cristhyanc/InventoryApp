using Inventory.Application.Documents;

namespace InventoryApi.Tests.Application.Expenses;

/// <summary>In-memory fake of the document-storage port, for Application use-case tests.</summary>
public sealed class FakeDocumentStorage : IDocumentStorage
{
    private readonly Dictionary<(DocumentCategory Category, string StoredFileName), byte[]> _documents = [];

    public List<string> SavedFileNames { get; } = [];
    public List<string?> DeletedFileNames { get; } = [];
    public bool ThrowOnSave { get; set; }

    public async Task SaveAsync(DocumentCategory category, string storedFileName, Stream content, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSave) throw new InvalidOperationException("storage failure");

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        _documents[(category, storedFileName)] = buffer.ToArray();
        SavedFileNames.Add(storedFileName);
    }

    public Task<DocumentContent?> OpenReadAsync(DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default)
    {
        if (storedFileName is null || !_documents.TryGetValue((category, storedFileName), out var bytes))
            return Task.FromResult<DocumentContent?>(null);

        return Task.FromResult<DocumentContent?>(new DocumentContent(new MemoryStream(bytes), bytes.Length));
    }

    public Task<bool> DeleteAsync(DocumentCategory category, string? storedFileName, CancellationToken cancellationToken = default)
    {
        DeletedFileNames.Add(storedFileName);
        return Task.FromResult(storedFileName is not null && _documents.Remove((category, storedFileName)));
    }

    public bool Contains(DocumentCategory category, string storedFileName) => _documents.ContainsKey((category, storedFileName));
}
