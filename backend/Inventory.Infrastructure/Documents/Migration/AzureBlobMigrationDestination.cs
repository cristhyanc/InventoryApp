using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Documents.Migration;

/// <summary>
/// The Azure Blob destination for the document migration.
///
/// It builds keys with <see cref="BlobDocumentPath"/> - the same rules
/// <see cref="AzureBlobDocumentStorage"/> uses for live requests - so a migrated document lands
/// exactly where the running application will later look for it. Reimplementing the naming here
/// would be the classic migration failure: documents copied successfully to somewhere nothing
/// reads from.
///
/// It goes through <see cref="IDocumentBlobContainer"/> rather than the SDK directly, which
/// keeps the checkpoint 2 error semantics: a blob that is not there reads as nothing, while a
/// missing container, a refused authorization or an unreachable account propagates instead of
/// being reported as an empty destination the migration would then happily fill.
/// </summary>
public sealed class AzureBlobMigrationDestination : IDocumentMigrationDestination
{
    private readonly IDocumentBlobContainer _container;

    /// <summary>Creates the destination over a container.</summary>
    public AzureBlobMigrationDestination(IDocumentBlobContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public string KeyFor(BusinessId businessId, DocumentCategory category, string storedFileName) =>
        BlobDocumentPath.For(businessId, category, storedFileName)
            ?? throw new ArgumentException(
                "Stored document name does not reduce to a usable file name.", nameof(storedFileName));

    /// <inheritdoc />
    public async Task<DocumentFingerprint?> InspectAsync(
        BusinessId businessId,
        DocumentCategory category,
        string storedFileName,
        CancellationToken cancellationToken)
    {
        var blob = await _container.OpenReadAsync(
            KeyFor(businessId, category, storedFileName), cancellationToken);
        if (blob is null) return null;

        await using (blob.Content)
        {
            return await DocumentContentHash.ComputeAsync(blob.Content, cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<bool> UploadIfAbsentAsync(
        BusinessId businessId,
        DocumentCategory category,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        return _container.CreateAsync(
            KeyFor(businessId, category, storedFileName), content, cancellationToken);
    }
}
