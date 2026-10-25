#nullable enable

using Inventory.Application.Machines;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// In-memory fake of the machine persistence port (issue #740), so Application use-case and
/// controller tests exercise orchestration without depending on EF Core or SQLite.
///
/// <para>The one refusal that depends on stored rows - a machine that is not there - is modelled
/// faithfully here, because every write reports it. Everything the real adapter decides against the
/// relational schema (the database-assigned key, the per-business filtered unique provider machine
/// id, the tenant filters) is proved in
/// <c>InventoryApi.Tests.Adapters.Persistence.EfLocalMachineStoreTests</c>, and
/// <see cref="RefusalToReturn"/> stands in for a refusal a use-case test needs.</para>
/// </summary>
public sealed class FakeLocalMachineStore : ILocalMachineStore
{
    private readonly List<LocalMachineRecord> _machines = [];
    private long _nextId = 1;

    public FakeLocalMachineStore(IEnumerable<LocalMachineRecord>? seed = null)
    {
        if (seed is not null) _machines.AddRange(seed);
        if (_machines.Count > 0) _nextId = _machines.Max(machine => machine.Id) + 1;
    }

    /// <summary>When set, every write is refused with this message.</summary>
    public string? RefusalToReturn { get; set; }

    public LocalMachineWriteFields? LastWritten { get; private set; }

    public long? LastWrittenId { get; private set; }

    public bool? LastActivation { get; private set; }

    public Task<IReadOnlyList<LocalMachineRecord>> ListOrderedByNameAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LocalMachineRecord>>(
            _machines.OrderBy(machine => machine.Name, StringComparer.Ordinal).ToList());

    public Task<LocalMachineRecord?> FindByIdAsync(long id, CancellationToken cancellationToken) =>
        Task.FromResult(_machines.SingleOrDefault(machine => machine.Id == id));

    public Task<LocalMachineWriteAttempt> CreateAsync(
        LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        if (RefusalToReturn is { } refusal) return Task.FromResult(LocalMachineWriteAttempt.Refused(refusal));

        LastWritten = fields;
        var created = new LocalMachineRecord(_nextId++, fields.Name!, IsActive: true);
        _machines.Add(created);
        LastWrittenId = created.Id;
        return Task.FromResult(LocalMachineWriteAttempt.Succeeded(created.Id));
    }

    public Task<LocalMachineWriteAttempt> RenameAsync(
        long id, LocalMachineWriteFields fields, CancellationToken cancellationToken)
    {
        var existing = _machines.SingleOrDefault(machine => machine.Id == id);
        if (existing is null) return Task.FromResult(LocalMachineWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(LocalMachineWriteAttempt.Refused(refusal));

        _machines.Remove(existing);
        _machines.Add(existing with { Name = fields.Name! });
        LastWritten = fields;
        LastWrittenId = id;
        return Task.FromResult(LocalMachineWriteAttempt.Succeeded(id));
    }

    public Task<LocalMachineWriteAttempt> SetActiveAsync(
        long id, bool isActive, CancellationToken cancellationToken)
    {
        var existing = _machines.SingleOrDefault(machine => machine.Id == id);
        if (existing is null) return Task.FromResult(LocalMachineWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(LocalMachineWriteAttempt.Refused(refusal));

        _machines.Remove(existing);
        _machines.Add(existing with { IsActive = isActive });
        LastWrittenId = id;
        LastActivation = isActive;
        return Task.FromResult(LocalMachineWriteAttempt.Succeeded(id));
    }

    /// <summary>
    /// Removes the machine (issue #896). Which machines <em>may</em> be deleted is decided against
    /// the stored rows of four other tables and is proved in
    /// <c>InventoryApi.Tests.Adapters.Persistence.EfLocalMachineStoreDeletionTests</c>;
    /// <see cref="RefusalToReturn"/> stands in for that refusal here.
    /// </summary>
    public Task<LocalMachineWriteAttempt> DeleteAsync(long id, CancellationToken cancellationToken)
    {
        var existing = _machines.SingleOrDefault(machine => machine.Id == id);
        if (existing is null) return Task.FromResult(LocalMachineWriteAttempt.NotFound());
        if (RefusalToReturn is { } refusal) return Task.FromResult(LocalMachineWriteAttempt.Refused(refusal));

        _machines.Remove(existing);
        LastDeletedId = id;
        return Task.FromResult(LocalMachineWriteAttempt.Succeeded(id));
    }

    /// <summary>The machine the last successful delete removed.</summary>
    public long? LastDeletedId { get; private set; }
}
