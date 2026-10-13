namespace Inventory.Application.PlatformAdmin;

/// <summary>
/// The Super Admin's directory of every business (issue #544, one slice of #509 under #328): the
/// list they decide from, and the only cross-business read in the application that goes through
/// <c>AppDbContext</c>.
///
/// <para><strong>It reads a fixed set of columns about businesses and nothing else.</strong> Every
/// field is declared on <see cref="PlatformBusinessSummary"/>: the business's name, lifecycle
/// status, creation/application/decision instants, the contact address and ABN its application
/// recorded, how many of its memberships are active, and whether a Nayax connection row exists for
/// it. No sale, stock, cost, purchase, product, machine, site or report value is reachable through
/// this use case, and neither is a member's name, email address or Entra identifier.</para>
///
/// <para><strong>There is no filter, page, sort or search parameter, deliberately.</strong> A
/// caller-supplied predicate over a cross-business read is how such a surface stops being a fixed
/// list and becomes a query engine. The order is the store's - pending applications first, so the
/// queue the Super Admin is here for is at the top - and that is a presentation decision the server
/// keeps.</para>
/// </summary>
public sealed class ListPlatformBusinesses
{
    private readonly IPlatformBusinessDirectoryStore _store;

    public ListPlatformBusinesses(IPlatformBusinessDirectoryStore store)
    {
        _store = store;
    }

    public Task<IReadOnlyList<PlatformBusinessSummary>> Handle(CancellationToken cancellationToken) =>
        _store.ListAsync(cancellationToken);
}

/// <summary>
/// How many business applications are waiting for a decision (issue #544).
///
/// It is its own use case rather than a property of the list because it is the badge a page polls:
/// the answer is one integer about businesses in one status, and asking for it must not read every
/// business's directory row.
/// </summary>
public sealed class CountPendingBusinessApplications
{
    private readonly IPlatformBusinessDirectoryStore _store;

    public CountPendingBusinessApplications(IPlatformBusinessDirectoryStore store)
    {
        _store = store;
    }

    public Task<int> Handle(CancellationToken cancellationToken) => _store.CountPendingAsync(cancellationToken);
}
