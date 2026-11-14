using Inventory.Application.Attachments;
using Inventory.Application.Documents;
using Inventory.Application.Purchases;
using Inventory.Application.ReceiptScanning;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Domain.Tenancy;
using InventoryApi.Tests.Application.Attachments;
using InventoryApi.Tests.Application.Gst;
using InventoryApi.Tests.Application.ReceiptScanning;
using InventoryApi.Tests.Application.Tenancy;
using InventoryApi.Tests.Application.Time;

namespace InventoryApi.Tests;

/// <summary>
/// Builds the purchase use cases for tests, following <see cref="TestCostingUseCases"/>.
///
/// <para>It exists because <see cref="UploadPurchase"/> gained the actor port in issue #974: a
/// supplier-order completion must name the operator who decided it, so the use case resolves the
/// actor the same way <c>ApplyInventoryCostRepair</c> does. Every test that only records a purchase
/// gets the identified operator below and is otherwise unaffected.</para>
/// </summary>
internal static class TestPurchaseUseCases
{
    /// <summary>The identified operator a purchase test records as, unless it supplies its own accessor.</summary>
    public static readonly ActorIdentity Operator = Actor("44444444-4444-4444-4444-444444444444");

    public static UploadPurchase Upload(
        IPurchaseStore store,
        IDocumentStorage documents,
        IClock? clock = null,
        IAuthenticatedActorAccessor? actors = null,
        AttachmentQuotaGuard? quota = null,
        IBusinessCalendar? calendar = null)
    {
        var resolvedClock = clock ?? new FakeClock(new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc));
        return new(
            store,
            documents,
            actors ?? FakeAuthenticatedActorAccessor.Identified(Operator),
            resolvedClock,
            // A purchase is never dated after today (issue #1246), so a test that is not about that
            // rule gets a calendar that agrees with its own clock: the business date is the clock's
            // UTC date, which is what keeps every date these tests record a date in the past. A test
            // about the rule, or about a zone the two differ in, supplies its own calendar.
            calendar ?? new FakeBusinessCalendar(resolvedClock.UtcNow),
            quota ?? FakeAttachmentQuota.NotEnforced());
    }

    /// <summary>
    /// Issue #1246's edit use case, which needs the same business calendar: the date an edit would
    /// leave standing - submitted or stored - must be today's or earlier.
    /// </summary>
    public static UpdatePurchase Update(IPurchaseStore store, IBusinessCalendar? calendar = null) =>
        new(store, calendar ?? new FakeBusinessCalendar(DateTime.UtcNow));

    /// <summary>Issue #976's "add a document later" use case, for the controller tests that construct it.</summary>
    public static AttachPurchaseDocument Attach(
        IPurchaseStore store, IDocumentStorage documents, AttachmentQuotaGuard? quota = null) =>
        new(store, documents, quota ?? FakeAttachmentQuota.NotEnforced());

    /// <summary>
    /// Issue #1163's receipt scan, for the controller tests that construct the controller directly.
    /// It defaults to the shipped state - no reader configured - so a test about something else
    /// cannot accidentally exercise a scan, and no test ever reaches a live reader.
    /// </summary>
    public static ScanReceipt Scan(
        IReceiptScanSettings? settings = null,
        IReceiptScanCatalogueStore? catalogue = null,
        IPdfPageCounter? pages = null,
        IReceiptReader? reader = null,
        IReceiptScanLog? log = null,
        ICurrentBusinessProvider? business = null,
        IReceiptScanPurchaseHistoryStore? purchases = null,
        IReceiptScanUsageStore? usage = null,
        IBusinessCalendar? calendar = null) =>
        new(
            settings ?? FakeReceiptScanSettings.SwitchedOff(),
            catalogue ?? new FakeReceiptScanCatalogueStore(),
            purchases ?? new FakeReceiptScanPurchaseHistoryStore(),
            pages ?? new FakePdfPageCounter(),
            reader ?? FakeReceiptReader.Unavailable(),
            log ?? new RecordingReceiptScanLog(),
            business ?? new FakeCurrentBusinessProvider(1),
            // Issue #1164's monthly counter, in memory and starting at zero: a test about something
            // else neither counts a scan nor is refused by a cap it never set.
            usage ?? new FakeReceiptScanUsageStore(),
            calendar ?? new FakeBusinessCalendar(new DateTime(2026, 3, 1)));

    /// <summary>
    /// Issue #1164's "how many scans are left" read, for the controller tests that construct the
    /// controller directly. It counts nothing and makes no outbound call.
    /// </summary>
    public static GetReceiptScanUsage ScanUsage(
        IReceiptScanUsageStore? usage = null,
        IBusinessCalendar? calendar = null) =>
        new(
            usage ?? new FakeReceiptScanUsageStore(),
            calendar ?? new FakeBusinessCalendar(new DateTime(2026, 3, 1)));

    private static ActorIdentity Actor(string objectId)
    {
        ActorIdentity.TryCreate("22222222-2222-2222-2222-222222222222", objectId, out var actor);
        return actor!;
    }
}
