using System.Text;
using Inventory.Application.Documents;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Infrastructure.Documents;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// The document half of the support-session read-only guarantee (issue #551).
///
/// <para>A document's bytes live outside the database, so <c>BusinessOwnershipEnforcer</c> cannot
/// reach them: a <c>SaveChanges</c> that never happens does not un-write a file, and a deleted
/// receipt is not recoverable by rolling a transaction back. The guard is therefore in front of
/// whichever provider is configured, which is also why it is one decorator and not a check written
/// into both adapters.</para>
///
/// <para>Reads pass through deliberately: a supporter is looking at the business's purchases and
/// expenses, and the attachments are part of what they are looking at.</para>
/// </summary>
public class SupportSessionReadOnlyDocumentStorageTests
{
    [Fact]
    public async Task A_support_session_may_not_save_a_document()
    {
        var inner = new RecordingDocumentStorage();
        var storage = Storage(inner, TestAppDbContext.SupportSessionScopeFor(businessId: 1));

        await Assert.ThrowsAsync<SupportSessionReadOnlyException>(
            () => storage.SaveAsync(DocumentCategory.PurchaseDocument, "stored-name.pdf", Content()));

        Assert.Equal(0, inner.Saves);
    }

    [Fact]
    public async Task A_support_session_may_not_delete_a_document()
    {
        var inner = new RecordingDocumentStorage();
        var storage = Storage(inner, TestAppDbContext.SupportSessionScopeFor(businessId: 1));

        await Assert.ThrowsAsync<SupportSessionReadOnlyException>(
            () => storage.DeleteAsync(DocumentCategory.PurchaseDocument, "stored-name.pdf"));

        Assert.Equal(0, inner.Deletes);
    }

    [Fact]
    public async Task A_support_session_may_read_a_document()
    {
        var inner = new RecordingDocumentStorage();
        var storage = Storage(inner, TestAppDbContext.SupportSessionScopeFor(businessId: 1));

        _ = await storage.OpenReadAsync(DocumentCategory.PurchaseDocument, "stored-name.pdf");

        Assert.Equal(1, inner.Reads);
    }

    /// <summary>
    /// Every other scope passes straight through, including the unscoped one the human-invoked
    /// document migration runs with: this guard is about one state and must not become a second
    /// ownership rule.
    /// </summary>
    [Fact]
    public async Task Every_other_scope_writes_exactly_as_before()
    {
        foreach (var scope in new[]
        {
            TestAppDbContext.ScopeFor(businessId: 1),
            TestAppDbContext.DeniedScope(),
            UnscopedBusinessScope.Instance,
        })
        {
            var inner = new RecordingDocumentStorage();
            var storage = Storage(inner, scope);

            await storage.SaveAsync(DocumentCategory.PurchaseDocument, "stored-name.pdf", Content());
            _ = await storage.DeleteAsync(DocumentCategory.PurchaseDocument, "stored-name.pdf");

            Assert.Equal(1, inner.Saves);
            Assert.Equal(1, inner.Deletes);
        }
    }

    /// <summary>
    /// The guard reports which provider it wraps, so a composition test can state both that the
    /// guard is in place and which implementation was selected behind it.
    /// </summary>
    [Fact]
    public void It_reports_the_storage_it_guards()
    {
        var inner = new RecordingDocumentStorage();

        Assert.Same(inner, Storage(inner, TestAppDbContext.DeniedScope()).Inner);
    }

    private static SupportSessionReadOnlyDocumentStorage Storage(
        IDocumentStorage inner,
        IBusinessScope scope) =>
        new(inner, scope);

    private static Stream Content() => new MemoryStream(Encoding.UTF8.GetBytes("not a real receipt"));

    /// <summary>
    /// Counts what reached the provider, so a refusal can be shown to have happened <em>before</em>
    /// the bytes were touched rather than after.
    /// </summary>
    private sealed class RecordingDocumentStorage : IDocumentStorage
    {
        public int Saves { get; private set; }

        public int Reads { get; private set; }

        public int Deletes { get; private set; }

        public Task SaveAsync(
            DocumentCategory category,
            string storedFileName,
            Stream content,
            CancellationToken cancellationToken = default)
        {
            Saves++;
            return Task.CompletedTask;
        }

        public Task<DocumentContent?> OpenReadAsync(
            DocumentCategory category,
            string? storedFileName,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<DocumentContent?>(null);
        }

        public Task<bool> DeleteAsync(
            DocumentCategory category,
            string? storedFileName,
            CancellationToken cancellationToken = default)
        {
            Deletes++;
            return Task.FromResult(true);
        }
    }
}
