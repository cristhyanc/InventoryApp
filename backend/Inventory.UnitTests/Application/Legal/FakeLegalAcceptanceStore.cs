using Inventory.Application.Legal;

namespace InventoryApi.Tests.Application.Legal;

/// <summary>
/// An in-memory <see cref="ILegalAcceptanceStore"/> (issue #1259): it keeps every acceptance it was
/// handed, in order, so a test can assert that exactly one was recorded, what it says, and - through
/// <see cref="Observer"/> - where in the surrounding transaction it happened.
///
/// <para>It records nothing on its own and refuses nothing: whether an acceptance may be written at
/// all is the use case's decision, and the relational half - the tenant-owned insert through the
/// narrow write window, and the rollback of a refused operation - is proved against a real SQLite
/// file by the adapter and API tests.</para>
/// </summary>
public sealed class FakeLegalAcceptanceStore : ILegalAcceptanceStore
{
    private readonly List<LegalAcceptance> _recorded = [];

    /// <summary>Every acceptance recorded, in the order the use case recorded them.</summary>
    public IReadOnlyList<LegalAcceptance> Recorded => _recorded;

    /// <summary>
    /// Reports each call onto a shared ordered log, so "the acceptance was written inside the write
    /// transaction" is assertable rather than assumed.
    /// </summary>
    public Action<string>? Observer { get; set; }

    /// <summary>The operation name <see cref="Observer"/> is called with.</summary>
    public const string Record = "legal-acceptance-recorded";

    /// <inheritdoc />
    public Task RecordAsync(LegalAcceptance acceptance, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(acceptance);

        Observer?.Invoke(Record);
        _recorded.Add(acceptance);
        return Task.CompletedTask;
    }
}
