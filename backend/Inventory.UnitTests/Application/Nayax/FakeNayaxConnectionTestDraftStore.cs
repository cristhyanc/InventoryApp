using Inventory.Application.Nayax;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// An in-memory connection-test draft store (issue #536), holding at most one draft, so a use-case
/// test can state exactly what was stored without a database.
///
/// It keeps the whole <see cref="NewNayaxConnectionTestDraft"/> rather than a projection, including
/// the token, so a test can assert that the token reaching the store is the submitted one - and that
/// nothing which comes <em>back</em> out of the port carries it. Encryption is deliberately not
/// modelled: the real adapter owns it, and the integration test over relational SQLite is what proves
/// the ciphertext is what lands in the column.
/// </summary>
public sealed class FakeNayaxConnectionTestDraftStore : INayaxConnectionTestDraftStore
{
    private readonly IClock _clock;

    public FakeNayaxConnectionTestDraftStore(IClock clock)
    {
        _clock = clock;
    }

    /// <summary>The business's one draft, or <c>null</c> when it holds none.</summary>
    public NewNayaxConnectionTestDraft? Current { get; private set; }

    /// <summary>Every draft ever stored, in order, so a replacement can be seen as one.</summary>
    public List<NewNayaxConnectionTestDraft> Stored { get; } = [];

    public Task ReplaceAsync(NewNayaxConnectionTestDraft draft, CancellationToken cancellationToken)
    {
        Current = draft;
        Stored.Add(draft);
        return Task.CompletedTask;
    }

    public Task<StoredNayaxConnectionTestDraft?> FindAsync(
        Guid draftId,
        ActorIdentity actor,
        CancellationToken cancellationToken)
    {
        if (Current is not { } draft
            || draft.Id != draftId
            || draft.CreatedBy != actor
            || draft.ExpiresAtUtc <= _clock.UtcNow)
        {
            return Task.FromResult<StoredNayaxConnectionTestDraft?>(null);
        }

        return Task.FromResult<StoredNayaxConnectionTestDraft?>(new StoredNayaxConnectionTestDraft(
            draft.Id,
            draft.Credential.OperatorId,
            draft.ResultsJson,
            draft.CreatedAtUtc,
            draft.ExpiresAtUtc));
    }
}
