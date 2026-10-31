using Inventory.Application.MachineStockSync;

namespace InventoryApi.Tests.Application.MachineStockSync;

/// <summary>
/// An in-memory <see cref="IMachineRestockSyncCoverageStore"/> (issue #891).
///
/// <para><see cref="TryRecordSuccessfulSyncAsync"/> reproduces the real adapter's monotonic
/// condition - coverage advances only for a strictly greater <c>(revision, started instant)</c> pair
/// - because that ordering is what the reminder's freshness depends on. The relational proof that the
/// real <c>UPDATE ... WHERE</c> behaves identically lives in the integration tests; this keeps the
/// unit tests honest about which pair they offer.</para>
///
/// <para>It deliberately does <strong>not</strong> reproduce the adapter's credential fence - the
/// condition that the business's stored connection still holds the offered revision - because there
/// is no stored connection here to fence on, and a fake that answered that question from a field of
/// its own would be proving its own arithmetic. That part of the contract is established only
/// relationally, against the real connection row and the real conditional statement, in
/// <c>MachineRestockSyncCoverageTests</c>; nothing that passes against this double may be read as
/// evidence about it.</para>
///
/// <para>It does not reproduce the "nothing left to sync" condition (issue #1018) either: that is
/// a query over the imported events, proven relationally in <c>MachineRestockSyncCoverageTests</c>.</para>
///
/// <para>It records every attempt in <see cref="Attempts"/>, including the refused ones, because
/// several of the properties under test are about a write that must <em>not</em> happen: a failed
/// fetch records nothing at all, and an operation that completed under replaced credentials must not
/// advance anything.</para>
/// </summary>
internal sealed class FakeMachineRestockSyncCoverageStore : IMachineRestockSyncCoverageStore
{
    private readonly Dictionary<long, (DateTime StartedAtUtc, int CredentialRevision)> _coverage = new();
    private readonly List<MachineRestockSyncCoverageRecord> _machines = new();

    /// <summary>Every write attempt, in order, accepted or not.</summary>
    public List<(long LocalMachineId, DateTime StartedAtUtc, int CredentialRevision, bool Accepted)> Attempts { get; } =
        new();

    /// <summary>The exception the next write should throw, if any.</summary>
    public Exception? WriteFailure { get; set; }

    /// <summary>The exception the next aggregate read should throw, if any.</summary>
    public Exception? ReadFailure { get; set; }

    /// <summary>How many times the aggregate read was made.</summary>
    public int Reads { get; private set; }

    /// <summary>The pending window start the last aggregate read was asked for (issue #948).</summary>
    public DateTime? LastPendingFromUtc { get; private set; }

    /// <summary>Declares what the aggregate read returns, replacing anything declared before.</summary>
    public FakeMachineRestockSyncCoverageStore WithMachines(params MachineRestockSyncCoverageRecord[] machines)
    {
        _machines.Clear();
        _machines.AddRange(machines);
        return this;
    }

    /// <summary>The coverage currently stored for a machine, or <c>null</c> when none is.</summary>
    public (DateTime StartedAtUtc, int CredentialRevision)? CoverageOf(long localMachineId) =>
        _coverage.TryGetValue(localMachineId, out var stored) ? stored : null;

    public Task<bool> TryRecordSuccessfulSyncAsync(
        long localMachineId,
        DateTime startedAtUtc,
        int credentialRevision,
        long nayaxMachineId,
        DateTime pendingFromUtc,
        CancellationToken cancellationToken)
    {
        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        var stored = CoverageOf(localMachineId);
        var accepted = stored is not { } existing
            || existing.CredentialRevision < credentialRevision
            || (existing.CredentialRevision == credentialRevision && existing.StartedAtUtc < startedAtUtc);

        if (accepted)
        {
            _coverage[localMachineId] = (startedAtUtc, credentialRevision);
        }

        Attempts.Add((localMachineId, startedAtUtc, credentialRevision, accepted));
        return Task.FromResult(accepted);
    }

    public Task<IReadOnlyList<MachineRestockSyncCoverageRecord>> GetProviderMachineCoverageAsync(
        DateTime pendingFromUtc, CancellationToken cancellationToken)
    {
        Reads++;
        LastPendingFromUtc = pendingFromUtc;

        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Task.FromResult<IReadOnlyList<MachineRestockSyncCoverageRecord>>(_machines.ToList());
    }
}
