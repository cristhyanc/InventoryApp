using Inventory.Application.Reporting.Gst;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Gst;

namespace InventoryApi.Tests.Application.Reporting.Gst;

/// <summary>
/// In-memory fake of the one-column GST registration read (issue #1318), so the BAS summary's gate
/// can be tested without EF Core or SQLite. <see cref="LastBusinessId"/> is what shows the business
/// came from resolved membership rather than from the filter.
/// </summary>
public sealed class FakeGstRegistrationStore : IGstRegistrationStore
{
    private readonly bool? _registered;

    public FakeGstRegistrationStore(bool? registered) => _registered = registered;

    public BusinessId? LastBusinessId { get; private set; }

    public Task<bool?> FindGstRegisteredAsync(BusinessId businessId, CancellationToken cancellationToken)
    {
        LastBusinessId = businessId;
        return Task.FromResult(_registered);
    }

    /// <summary>
    /// The BAS summary use case over <paramref name="aid"/> for a GST-registered business - what a
    /// test about something other than the registration gate needs, and what the reports controller
    /// and export tests construct so their bookkeeping export carries the summary a real one does.
    /// </summary>
    public static GetGstBasSummary BasSummary(IGetGstAccountingAid aid, bool? registered = true) =>
        new(aid, new FakeCurrentBusinessProvider(1), new FakeGstRegistrationStore(registered));
}
