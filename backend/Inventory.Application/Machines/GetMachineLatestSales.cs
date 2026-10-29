using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;

namespace Inventory.Application.Machines;

/// <summary>
/// The machine latest-sales read use case (issue #768): the sales Nayax currently reports for one
/// machine, newest first, for the machine-details page's Sales tab.
///
/// <para><strong>It is a read, and nothing else.</strong> The only port it holds is
/// <see cref="INayaxLynxClient"/>, so there is structurally no way for it to import or persist a
/// sale, deduct storage, recost inventory or apply a stock event - the things
/// <c>SyncLatestNayaxSales</c> does with the very same adapter call. A refresh on this screen
/// therefore changes nothing a report, a cost or a quantity is derived from, however often it is
/// pressed.</para>
///
/// <para><strong>What Nayax returns is a rolling window, not a history.</strong> The endpoint is
/// documented as "the most recent sales transactions for a specific machine" with no date range, no
/// row count and no paging, so this use case promises neither: it reports the rows it was given, in
/// instant order, and the presentation says that is what they are.</para>
///
/// <para><strong>An unusable provider is an availability answer, not an error</strong> - the shape
/// <c>CalculateReorderNeeds</c> (issue #726) and <c>GetSiteCommissionReport</c> (issue #731)
/// established, with one state added. Three refusals become an answer: no sales provider configured
/// at all (issue #759 - <c>NayaxConnectionStatus.NotConfigured</c>, which is exactly "nothing is
/// stored for this business", and storing credentials is the one thing that claims
/// <c>Business.SalesProvider</c>), a stored connection the status gate refuses, and a Nayax
/// permission refusal. Everything else propagates: a transport failure, a cancellation and the
/// support-session refusal (<see cref="SupportSessions.SupportSessionNayaxUnavailableException"/>,
/// issue #551) are outages or refusals, not a machine that has sold nothing, and this read never
/// writes a connection status of its own.</para>
///
/// <para>The machine is identified by its provider number. Since issue #741 the route takes the
/// local machine id and resolves it to that number first, so this read and <c>GET
/// api/machines/{id}</c> always read the same machine; a machine with no provider number never
/// reaches it.</para>
/// </summary>
public sealed class GetMachineLatestSales
{
    private readonly INayaxLynxClient _nayax;

    public GetMachineLatestSales(INayaxLynxClient nayax) => _nayax = nayax;

    public async Task<MachineLatestSales> Handle(long machineId, CancellationToken cancellationToken)
    {
        List<NayaxLastSalesReport> reported;
        try
        {
            reported = await _nayax.GetMachineLastSalesAsync(machineId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NayaxNotConnectedException exception)
        {
            return MachineLatestSales.Unavailable(ClassifyUnavailableReason(exception));
        }
        catch (NayaxPermissionNotGrantedException)
        {
            return MachineLatestSales.Unavailable(
                MachineLatestSalesUnavailableReason.PermissionNotGranted);
        }

        return new MachineLatestSales(
        [
            .. reported
                .Select(Project)
                // Newest first, and a sale whose instant the payload did not carry goes last rather
                // than to the top or the bottom of an epoch-ordered list. The transaction id breaks
                // the remaining ties so one window always renders in the same order.
                .OrderByDescending(sale => sale.SaleAtUtc.HasValue)
                .ThenByDescending(sale => sale.SaleAtUtc)
                .ThenByDescending(sale => sale.TransactionId),
        ]);
    }

    /// <summary>
    /// The plan is asked about first, because a plan refusal reads no connection at all and so
    /// carries no status to classify (issue #967): a business that has not bought the provider is
    /// not a business with none configured.
    ///
    /// <para>Otherwise, NotConfigured is the state of a business with nothing stored, so it is the
    /// state of a business with no sales provider (issue #759). Any other refused status - today
    /// NeedsAttention, including the one a 401 has just written - is a provider that is configured
    /// and currently unusable, which is a different sentence for the operator.</para>
    /// </summary>
    private static MachineLatestSalesUnavailableReason ClassifyUnavailableReason(
        NayaxNotConnectedException exception)
    {
        if (exception.RequiredPlan is not null)
        {
            return MachineLatestSalesUnavailableReason.PlanRequired;
        }

        return exception.Status == NayaxConnectionStatus.NotConfigured
            ? MachineLatestSalesUnavailableReason.NoSalesProvider
            : MachineLatestSalesUnavailableReason.NayaxNotConnected;
    }

    /// <summary>
    /// One reported row, narrowed to the fields the page displays. Absent text stays
    /// <see langword="null"/> rather than becoming an empty cell that reads as a reported value, and
    /// the amount is copied unchanged: no cost, profit, margin or local product is derived here.
    /// </summary>
    private static MachineLatestSale Project(NayaxLastSalesReport reported) => new(
        reported.TransactionID,
        reported.AuthorizationInstantUtc,
        Reported(reported.ProductName),
        Reported(reported.PaymentMethod),
        reported.SettlementValue,
        Reported(reported.CurrencyCode));

    /// <summary>
    /// The provider's own text, or <see langword="null"/> when it reported nothing usable. The live
    /// endpoint renders a product name with trailing whitespace, so the value is trimmed for display
    /// and a value that is only whitespace is "not reported" rather than a blank name.
    /// </summary>
    private static string? Reported(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
