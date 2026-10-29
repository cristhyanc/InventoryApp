using Inventory.Application.Documents;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Documents.Reconciliation;

/// <summary>
/// The Blob half of <see cref="IAttachmentFileInventory"/> (issue #968).
///
/// <para>Blob storage is laid out per business - <c>tenants/{businessId}/{category}/</c> - so every
/// file it lists is attributed to the business whose prefix it was listed under, and a file no
/// record refers to is an orphan of that business. Both the prefix and the single blob name come
/// from <see cref="BlobDocumentPath"/>, which derives them from a business and a category and
/// nothing else, so a listing can no more cross a business than a read can.</para>
///
/// <para>It only reads. The reconciliation never deletes a blob, and this class offers no way
/// to.</para>
/// </summary>
public sealed class AzureBlobAttachmentFileInventory : IAttachmentFileInventory
{
    private readonly IDocumentBlobContainer _container;

    public AzureBlobAttachmentFileInventory(IDocumentBlobContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);

        _container = container;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<StoredAttachmentFile>> ListAsync(
        DocumentCategory category, IReadOnlyList<int> businessIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(businessIds);

        var files = new List<StoredAttachmentFile>();

        foreach (var businessId in businessIds)
        {
            var prefix = BlobDocumentPath.PrefixFor(BusinessId.From(businessId), category);
            var listings = await _container.ListAsync(prefix, cancellationToken).ConfigureAwait(false);

            files.AddRange(listings.Select(listing => new StoredAttachmentFile(
                category,
                businessId,
                listing.BlobName[prefix.Length..],
                listing.ByteLength)));
        }

        return files;
    }

    /// <inheritdoc />
    public async Task<long?> MeasureAsync(
        DocumentCategory category, int businessId, string storedFileName, CancellationToken cancellationToken)
    {
        var blobName = BlobDocumentPath.For(BusinessId.From(businessId), category, storedFileName);
        if (blobName is null) return null;

        var blob = await _container.OpenReadAsync(blobName, cancellationToken).ConfigureAwait(false);
        if (blob is null) return null;

        await using var content = blob.Content;
        return blob.ByteLength;
    }
}
