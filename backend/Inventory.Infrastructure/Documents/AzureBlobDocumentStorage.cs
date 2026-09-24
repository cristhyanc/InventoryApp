using Inventory.Application.Documents;
using Inventory.Application.Tenancy;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Stores uploaded business documents in a private Azure Blob container, keyed by the business
/// that owns them (issue #39, checkpoint 2).
///
/// Every operation starts by demanding a resolved current business from
/// <see cref="IBusinessScope"/> - the trusted scope issue #64 publishes once per request from
/// the authenticated actor's membership - and builds its blob name from that. No business
/// identifier reaches this class from a route, query, form, header, JSON body or file name, and
/// <see cref="IDocumentStorage"/> deliberately has no parameter that could carry one. The
/// consequence is that "which business" is not a decision any caller gets to make, correctly or
/// otherwise: business A's request can only ever address <c>tenants/A/...</c>, so knowing
/// another business's record id or stored file name buys nothing.
///
/// The container is private and no SAS or public URL is ever generated; documents leave only
/// through the authenticated API endpoints that call this adapter.
/// </summary>
public sealed class AzureBlobDocumentStorage : IDocumentStorage
{
    private readonly IDocumentBlobContainer _container;
    private readonly IBusinessScope _businessScope;

    /// <summary>Creates the adapter over a container and the current request's business scope.</summary>
    public AzureBlobDocumentStorage(IDocumentBlobContainer container, IBusinessScope businessScope)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(businessScope);

        _container = container;
        _businessScope = businessScope;
    }

    /// <inheritdoc />
    public async Task SaveAsync(
        DocumentCategory category,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storedFileName);
        ArgumentNullException.ThrowIfNull(content);

        var blobName = BlobNameFor(category, storedFileName)
            ?? throw new ArgumentException(
                "Stored document name does not reduce to a usable file name.", nameof(storedFileName));

        // A blob is only readable once its upload has been committed, so a failed upload leaves
        // no partial document to clean up. Deleting on failure would be worse than doing
        // nothing: the one blob that could exist at this name is a document some other record
        // already owns, and this call must not be able to destroy it.
        var created = await _container.CreateAsync(blobName, content, cancellationToken);
        if (!created)
        {
            throw new IOException(
                $"A document is already stored for this business under '{storedFileName}'. Stored names are server-generated and are never overwritten.");
        }
    }

    /// <inheritdoc />
    public async Task<DocumentContent?> OpenReadAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default)
    {
        var blobName = BlobNameFor(category, storedFileName);
        if (blobName is null) return null;

        var blob = await _container.OpenReadAsync(blobName, cancellationToken);
        return blob is null ? null : new DocumentContent(blob.Content, blob.ByteLength, blob.LastModified);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default)
    {
        var blobName = BlobNameFor(category, storedFileName);
        if (blobName is null) return false;

        return await _container.DeleteAsync(blobName, cancellationToken);
    }

    /// <summary>
    /// The blob name for the current business, or <c>null</c> when the stored name is unusable.
    /// Resolving the business here, rather than taking it as a parameter, is what makes the
    /// tenant prefix impossible to influence from outside.
    /// </summary>
    private string? BlobNameFor(DocumentCategory category, string? storedFileName) =>
        BlobDocumentPath.For(RequireCurrentBusiness(), category, storedFileName);

    /// <summary>
    /// The trusted current business, or a refusal. Both non-resolved states fail closed:
    /// <see cref="BusinessScopeState.Denied"/> is a caller with no membership, and
    /// <see cref="BusinessScopeState.Unscoped"/> is the deliberate all-business opt-out, which
    /// has no meaning here - there is no such thing as "every business's copy" of one document,
    /// and silently picking a prefix would be a cross-business write waiting to happen.
    /// Cross-business access for maintenance work needs its own explicit path rather than a
    /// weakening of this one.
    /// </summary>
    private BusinessId RequireCurrentBusiness()
    {
        if (_businessScope.State == BusinessScopeState.Unscoped)
        {
            throw new InvalidOperationException(
                "Document storage cannot run with an unscoped business scope: a document belongs to exactly one business, and its blob name is derived from that business. Cross-business access requires a separate, explicit path.");
        }

        if (_businessScope.State != BusinessScopeState.Resolved ||
            _businessScope.BusinessId is not { } businessId ||
            !BusinessId.TryFrom(businessId, out var trusted))
        {
            throw new BusinessAccessDeniedException(BusinessAccessDenialReason.MembershipMissing);
        }

        return trusted;
    }
}
