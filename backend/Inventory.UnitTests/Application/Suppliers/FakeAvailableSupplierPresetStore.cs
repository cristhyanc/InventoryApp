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
/// to state. <see cref="CopyAsync"/> applies the same rule the adapter applies inside its write
/// lock, so this double cannot make a use case look correct by being more permissive than the real
/// one: a record that stopped being offered is <c>NotAvailable</c> here too.</para>
///
/// <para><see cref="AfterListing"/> stages the window the real adapter closes - something committing
/// between the available list and a copy - so a test can disable a record, re-point it or move the
/// business and then assert the top-up skipped it. <see cref="RefuseNextCopy"/> stages the one thing
/// the in-lock read cannot see: the unique index refusing a copy a writer outside this operation
/// created, which the use cases translate rather than surfacing as a server error.</para>
/// </summary>
public sealed class FakeAvailableSupplierPresetStore : IAvailableSupplierPresetStore
{
    private readonly List<StoredPreset> _presets = [];
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
    /// what a copy written by a writer this operation was not serialised with produces.
    /// </summary>
    public bool RefuseNextCopy { get; set; }

    /// <summary>
    /// Runs immediately after <see cref="ListAvailableAsync"/> has answered, which is the window the
    /// real adapter's write lock closes: it is how a test commits a disable, a re-point or a state
    /// change between the candidate list and the authoritative copy.
    /// </summary>
    public Action? AfterListing { get; set; }

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
        _presets.Add(new StoredPreset(id, name, url, isEnabled, servesNationwide, servedStates));
        return this;
    }

    /// <summary>Records that the business already holds a copy of that record.</summary>
    public FakeAvailableSupplierPresetStore WithCopyOf(int presetId)
    {
        _copied.Add(presetId);
        return this;
    }

    /// <summary>Stops suggesting that record, as a Super Admin disabling it does.</summary>
    public void Disable(int presetId) => Replace(presetId, preset => preset with { IsEnabled = false });

    /// <summary>Points that record at other states only, as a Super Admin editing its market does.</summary>
    public void ServeOnly(int presetId, string servedStates) =>
        Replace(presetId, preset => preset with { Nationwide = false, States = servedStates });

    /// <summary>The preset ids this business holds a copy of, in the order they were created.</summary>
    public IReadOnlyList<int> Copies => _copied;

    public Task<IReadOnlyList<AvailableSupplierPreset>> ListAvailableAsync(
        BusinessId businessId,
        CancellationToken cancellationToken)
    {
        BusinessIds.Add(businessId.Value);
        ListCalls++;

        var offered = Offered()
            .Select(preset => new AvailableSupplierPreset(preset.Id, preset.Name, preset.Url))
            .ToList();

        AfterListing?.Invoke();

        return Task.FromResult<IReadOnlyList<AvailableSupplierPreset>>(offered);
    }

    public Task<SupplierPresetAddition> CopyAsync(
        BusinessId businessId,
        int presetId,
        CancellationToken cancellationToken)
    {
        BusinessIds.Add(businessId.Value);

        if (RefuseNextCopy)
        {
            RefuseNextCopy = false;
            // As the index does: the copy exists, written by whoever this operation did not see.
            _copied.Add(presetId);
            throw new DuplicateCopyException();
        }

        // The same decision the adapter makes inside its write lock, and in the same order.
        var preset = _presets.Find(candidate => candidate.Id == presetId);
        if (preset is null || !Serves(preset))
        {
            return Task.FromResult(SupplierPresetAddition.NotAvailable());
        }

        if (_copied.Contains(presetId))
        {
            return Task.FromResult(SupplierPresetAddition.AlreadyAdded());
        }

        _copied.Add(presetId);

        return Task.FromResult(SupplierPresetAddition.Added(new SupplierRecord(
            _nextSupplierId++,
            preset.Name,
            null,
            null,
            null,
            null,
            preset.Url,
            presetId,
            IsHidden: false)));
    }

    public bool IsDuplicateCopy(Exception exception) => exception is DuplicateCopyException;

    private void Replace(int presetId, Func<StoredPreset, StoredPreset> change)
    {
        var index = _presets.FindIndex(candidate => candidate.Id == presetId);
        _presets[index] = change(_presets[index]);
    }

    private IEnumerable<StoredPreset> Offered() =>
        _presets
            .Where(preset => !_copied.Contains(preset.Id))
            .Where(Serves)
            .OrderBy(preset => preset.Name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The real rule, so this double cannot disagree with the adapter about a market.</summary>
    private bool Serves(StoredPreset preset) =>
        SupplierPresetMarketPolicy.IsOfferedTo(
            preset.IsEnabled,
            preset.Nationwide,
            preset.States,
            BusinessState);

    /// <summary>One platform record as this double holds it.</summary>
    private sealed record StoredPreset(
        int Id,
        string Name,
        string? Url,
        bool IsEnabled,
        bool Nationwide,
        string States);
}
