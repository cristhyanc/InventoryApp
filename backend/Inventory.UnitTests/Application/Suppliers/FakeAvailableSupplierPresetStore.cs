using Inventory.Application.Suppliers;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;

namespace InventoryApi.Tests.Application.Suppliers;

/// <summary>
/// An in-memory <see cref="IAvailableSupplierPresetStore"/> (issue #935): a platform list, one
/// business's state, and the copies that business holds, with the real state rule applied over them.
///
/// <para>It records every call and the business id each was given, because "the business is the
/// caller's own and never a value a request supplied" is a property a use-case test has to be able
/// to state. <see cref="RefuseNextCopy"/> stages the one race the pre-check cannot win - the unique
/// index refusing a copy created between the check and the insert - so the use case's translation of
/// that violation is covered without two threads interleaving.</para>
/// </summary>
public sealed class FakeAvailableSupplierPresetStore : IAvailableSupplierPresetStore
{
    private readonly List<(int Id, string Name, string? Url, bool IsEnabled, bool Nationwide, string States)> _presets = [];
    private readonly List<int> _copied = [];
    private int _nextSupplierId = 500;

    /// <summary>The state the business trades in, or <see langword="null"/> for a legacy one.</summary>
    public string? BusinessState { get; set; }

    /// <summary>Every business id this store was called with, in order.</summary>
    public List<int> BusinessIds { get; } = [];

    /// <summary>
    /// How many times the available list was read, which is what the state change's top-up starts
    /// with: it is how a test states that a save which changed no state asked for nothing.
    /// </summary>
    public int ListCalls { get; private set; }

    /// <summary>
    /// Makes the next <see cref="CopyAsync"/> fail the way the filtered unique index does, which is
    /// what the losing request of two simultaneous adds sees.
    /// </summary>
    public bool RefuseNextCopy { get; set; }

    /// <summary>The marker exception <see cref="IsDuplicateCopy"/> recognises.</summary>
    private sealed class DuplicateCopyException : Exception
    {
        public DuplicateCopyException()
            : base("UNIQUE constraint failed: Suppliers.BusinessId, Suppliers.SupplierPresetId")
        {
        }
    }

    public FakeAvailableSupplierPresetStore WithPreset(
        int id,
        string name,
        string? url = null,
        bool isEnabled = true,
        bool servesNationwide = true,
        string servedStates = "")
    {
        _presets.Add((id, name, url, isEnabled, servesNationwide, servedStates));
        return this;
    }

    /// <summary>Records that the business already holds a copy of that record.</summary>
    public FakeAvailableSupplierPresetStore WithCopyOf(int presetId)
    {
        _copied.Add(presetId);
        return this;
    }

    /// <summary>The preset ids this business holds a copy of, in the order they were created.</summary>
    public IReadOnlyList<int> Copies => _copied;

    public Task<IReadOnlyList<AvailableSupplierPreset>> ListAvailableAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        BusinessIds.Add(businessId.Value);
        ListCalls++;

        return Task.FromResult<IReadOnlyList<AvailableSupplierPreset>>(
            Offered().Select(preset => new AvailableSupplierPreset(preset.Id, preset.Name, preset.Url)).ToList());
    }

    public Task<SupplierPresetAvailability> FindAvailableAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken)
    {
        BusinessIds.Add(businessId.Value);

        var preset = _presets.Find(candidate => candidate.Id == presetId);
        if (preset.Id == 0 || !Serves(preset))
        {
            return Task.FromResult(SupplierPresetAvailability.NotOffered);
        }

        return Task.FromResult(_copied.Contains(presetId)
            ? SupplierPresetAvailability.AlreadyCopied
            : SupplierPresetAvailability.Available);
    }

    public Task<SupplierRecord> CopyAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken)
    {
        BusinessIds.Add(businessId.Value);

        if (RefuseNextCopy)
        {
            RefuseNextCopy = false;
            // As the index does: the copy exists, written by whoever won the race.
            _copied.Add(presetId);
            throw new DuplicateCopyException();
        }

        var preset = _presets.Find(candidate => candidate.Id == presetId);
        _copied.Add(presetId);

        return Task.FromResult(new SupplierRecord(
            _nextSupplierId++,
            preset.Name,
            null,
            null,
            null,
            null,
            preset.Url,
            presetId,
            IsHidden: false));
    }

    public bool IsDuplicateCopy(Exception exception) => exception is DuplicateCopyException;

    private IEnumerable<(int Id, string Name, string? Url, bool IsEnabled, bool Nationwide, string States)> Offered() =>
        _presets
            .Where(preset => !_copied.Contains(preset.Id))
            .Where(Serves)
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The real rule, so this double cannot disagree with the adapter about a market.</summary>
    private bool Serves((int Id, string Name, string? Url, bool IsEnabled, bool Nationwide, string States) preset) =>
        SupplierPresetMarketPolicy.IsOfferedTo(
            preset.IsEnabled,
            preset.Nationwide,
            preset.States,
            BusinessState);
}
