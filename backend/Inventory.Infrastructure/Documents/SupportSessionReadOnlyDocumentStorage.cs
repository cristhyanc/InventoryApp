using Inventory.Application.Documents;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;

namespace Inventory.Infrastructure.Documents;

/// <summary>
/// Refuses every document write while the request is running in a support session (issue #551), and
/// is transparent otherwise.
///
/// <para>It is the document half of the persistence read-only guarantee.
/// <c>BusinessOwnershipEnforcer</c> covers the database, but a document's bytes live outside it, so
/// a <c>SaveChanges</c> that never happens would not by itself stop a file being written or deleted
/// - and a deleted receipt is not recoverable by rolling a transaction back.</para>
///
/// <para>It wraps whichever provider is configured rather than being written into both adapters,
/// so the rule is in one place and a third provider inherits it by being registered behind this.
/// Reads pass straight through: a supporter is looking at the business's purchases and expenses, and
/// their attachments are part of what they are looking at.</para>
///
/// <para>It decides nothing about ownership. Which business a document belongs to is still the inner
/// adapter's answer, from the same business scope - this only asks whether writing is allowed at
/// all.</para>
/// </summary>
public sealed class SupportSessionReadOnlyDocumentStorage : IDocumentStorage
{
    private readonly IDocumentStorage _inner;
    private readonly IBusinessScope _businessScope;

    public SupportSessionReadOnlyDocumentStorage(IDocumentStorage inner, IBusinessScope businessScope)
    {
        _inner = inner;
        _businessScope = businessScope;
    }

    /// <summary>
    /// The storage this guards, so a composition test can assert which provider was selected
    /// without the guard hiding it.
    /// </summary>
    public IDocumentStorage Inner => _inner;

    /// <inheritdoc />
    public Task SaveAsync(
        DocumentCategory category,
        string storedFileName,
        Stream content,
        CancellationToken cancellationToken = default)
    {
        RefuseInSupportSession();

        return _inner.SaveAsync(category, storedFileName, content, cancellationToken);
    }

    /// <inheritdoc />
    public Task<DocumentContent?> OpenReadAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default) =>
        _inner.OpenReadAsync(category, storedFileName, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeleteAsync(
        DocumentCategory category,
        string? storedFileName,
        CancellationToken cancellationToken = default)
    {
        RefuseInSupportSession();

        return _inner.DeleteAsync(category, storedFileName, cancellationToken);
    }

    private void RefuseInSupportSession()
    {
        if (_businessScope.State == BusinessScopeState.SupportReadOnly)
        {
            throw new SupportSessionReadOnlyException();
        }
    }
}
