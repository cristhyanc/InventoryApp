namespace Inventory.Application.Nayax;

/// <summary>
/// Reports the current business's Nayax connection state to any of its members (issue #537, a slice
/// of #506 under #328).
///
/// <para>It exists so the banners of #539 can be shown to a Manager or an Operator - who cannot
/// reach the Owner-only integration settings at all - without any of them learning anything else
/// about the connection. It returns a status and one flag:
/// <see cref="NayaxConnectionStatusSummary"/> carries no operator id, no credential revision, no
/// last-tested instant and no check lines, so the read a whole business can make is the smallest one
/// that answers "is Nayax working here, and is a re-test still needed?".</para>
///
/// <para>A business that has stored no connection has no record to read, which is
/// <see cref="Domain.Nayax.NayaxConnectionStatus.NotConfigured"/> - the same fail-closed answer an
/// unresolved business gets, because the store reports nothing without one.</para>
/// </summary>
public sealed class GetNayaxConnectionStatus
{
    private readonly INayaxConnectionStore _connections;

    public GetNayaxConnectionStatus(INayaxConnectionStore connections)
    {
        _connections = connections;
    }

    /// <param name="cancellationToken">The request's cancellation token.</param>
    public async Task<NayaxConnectionStatusSummary> Handle(CancellationToken cancellationToken = default) =>
        NayaxConnectionStatusSummary.From(await _connections.FindAsync(cancellationToken));
}
