using Azure.Identity;
using Azure.Storage.Blobs;
using Inventory.Application.Attachments;
using Inventory.Application.Businesses;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.CatalogueReview;
using Inventory.Application.Categories;
using Inventory.Application.Commissions;
using Inventory.Application.ConnectedMachines;
using Inventory.Application.Costing;
using Inventory.Application.Dashboard;
using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Application.Gst;
using Inventory.Application.Imports;
using Inventory.Application.InventoryCounting;
using Inventory.Application.MachineStock;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Machines;
using Inventory.Application.Members;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.NayaxReconciliation;
using Inventory.Application.NayaxSaleReview;
using Inventory.Application.NayaxSqs;
using Inventory.Application.Onboarding;
using Inventory.Application.PickList;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.PlatformNayaxSqs;
using Inventory.Application.PlatformOptions;
using Inventory.Application.PlatformPlans;
using Inventory.Application.PlatformReceiptScans;
using Inventory.Application.PriceCatalogue;
using Inventory.Application.Products;
using Inventory.Application.Providers;
using Inventory.Application.Purchases;
using Inventory.Application.ReceiptScanning;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Application.SalesSync;
using Inventory.Application.Sites;
using Inventory.Application.Stock;
using Inventory.Application.Subscriptions;
using Inventory.Application.SupplierOrders;
using Inventory.Application.Suppliers;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.NayaxSqs;
using Inventory.Infrastructure.ReceiptScanning;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Reporting;
using Inventory.Infrastructure.Sites;
using Inventory.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Inventory.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();

        // The business calendar is per request, not a singleton (issue #499): it derives its dates
        // in the time zone configured on the business this request resolved, which it reads from
        // the IBusinessTimeZoneProvider the API boundary publishes once per request (registered in
        // the composition root beside IBusinessScope, for the same reason).
        //
        // A host that resolves this outside a request - the human-invoked bootstrap-business,
        // migrate-database and backup commands - has no current business, so it has no business
        // calendar either: every member of the port fails closed with
        // BusinessTimeZoneUnavailableException rather than silently answering in Australia/Sydney
        // or the host's own zone. None of those commands needs a business date; one that ever does
        // must construct a ZonedBusinessCalendar with the zone it means, explicitly.
        services.AddScoped<IBusinessCalendar, CurrentBusinessCalendar>();

        // The platform collection calendar (issue #1108), which is a different port from the one
        // above and not a fallback for it: it dates no business and no financial record, it reads the
        // one declared zone a supplier search schedule's days mean
        // (SupplierSearchSchedulePolicy.TimeZoneId), and it needs no current business - which is why
        // a supplier preset, which belongs to no business, has a collection day at all. Scoped so one
        // issued work list decides every supplier's occurrence against one calendar.
        services.AddScoped<ISupplierSearchScheduleCalendar, SupplierSearchScheduleCalendar>();

        // The uploaded Nayax sales export reader (issue #301). Unlike the pending-XML source below
        // it needs no host path and no configuration - the caller hands it the uploaded bytes - so
        // it is registered here rather than through its own extension, and is a singleton because
        // it holds no state between reads.
        services.AddSingleton<INayaxSalesWorkbookReader, ClosedXmlNayaxSalesWorkbookReader>();

        // The non-EF adapters that issue #306 moved out of InventoryApi. All three need no
        // AppDbContext, no host path and no configuration, so they belong here rather than in the
        // API composition root or behind their own extension method.

        // Report CSV/XLSX byte encoding (issue #306). Singleton for the same reason as the workbook
        // reader: the caller hands it a finished ReportExportTable and it keeps nothing.
        services.AddSingleton<IReportExportFileWriter, ReportExportFileWriter>();

        // The site display name derived from a site's Nayax machine names (issue #306). Scoped, as
        // the SiteNameResolverAdapter registration in Program.cs that it replaces was: the type is
        // stateless, so the lifetime is not observable, and keeping it means this relocation
        // changes no registration a consumer could notice.
        services.AddScoped<ISiteNameResolver, SiteNameResolver>();

        // The remote half of the Nayax catalog reconciliation (issues #55/#306). Scoped, as the
        // Program.cs registration it replaces was. It is the one adapter registered here whose own
        // dependency, INayaxLynxClient, comes from AddNayaxLynxClient below rather than from this
        // method, because that registration needs the validated options only the composition root
        // can read; a host that calls this method must call that one too.
        services.AddScoped<INayaxCatalogSnapshotProvider, NayaxCatalogSnapshotProvider>();

        // The reporting EF fact providers that issue #308 moved out of InventoryApi
        // (Inventory.Infrastructure.Reporting.Persistence). Unlike everything above they do need an
        // AppDbContext, which the composition root still registers together with the provider and
        // the connection string it chooses (see AddDbContext/UseSqlite in Program.cs): a host that
        // calls this method must register the context too, the same obligation
        // NayaxCatalogSnapshotProvider already creates for AddNayaxLynxClient.
        //
        // Every one is Scoped, exactly as the Program.cs registration it replaces was, because the
        // AppDbContext it reads is: relocating these adapters must not change a lifetime a consumer
        // could notice. EfTransactionSalesReportFactsProvider additionally takes the
        // INayaxLynxClient from AddNayaxLynxClient for its live site-name lookup.
        services.AddScoped<IBookkeepingReportFactsProvider, EfBookkeepingReportFactsProvider>();
        services.AddScoped<IDailyReportFactsProvider, EfDailyReportFactsProvider>();
        services.AddScoped<IReconciliationReportFactsProvider, EfReconciliationReportFactsProvider>();
        services.AddScoped<IMachineProfitabilityReportFactsProvider, EfMachineProfitabilityReportFactsProvider>();
        services.AddScoped<IProductProfitabilityReportFactsProvider, EfProductProfitabilityReportFactsProvider>();
        services.AddScoped<IGstReportFactsProvider, EfGstReportFactsProvider>();
        services.AddScoped<IDashboardReportFactsProvider, EfDashboardReportFactsProvider>();
        services.AddScoped<IInventoryValuationFactsProvider, EfInventoryValuationFactsProvider>();
        services.AddScoped<ITransactionSalesReportFactsProvider, EfTransactionSalesReportFactsProvider>();

        // The processing-fee facts the fee-bearing reports above are built from, moved with them.
        services.AddScoped<INayaxProcessingFeeFactsProvider, EfNayaxProcessingFeeFactsProvider>();

        // The remaining EF adapters, which issue #309 moved out of InventoryApi/Adapters/Persistence
        // (Inventory.Infrastructure.Persistence) and which complete #153's persistence move: the
        // composition root now owns no persistence implementation at all. Like the reporting
        // providers above they need the AppDbContext a host that calls this method must register
        // together with its provider and connection string.
        //
        // Every one is Scoped, exactly as the Program.cs registration it replaces was. That is not
        // cosmetic here: a use case that composes several of these adapters relies on them sharing
        // one AppDbContext, and therefore one change tracker and one transaction, for the whole
        // request (docs/architecture.md § Concurrency inside one request). The transaction
        // boundaries, the AppDbContext tenant query filters and the BusinessOwnershipEnforcer stamp
        // on SaveChanges are unchanged by the relocation; no adapter scopes a read or a write
        // itself.
        //
        // Grouped by the Application feature whose port each satisfies, in the order Program.cs
        // registered them.
        services.AddScoped<IBusinessMembershipStore, EfBusinessMembershipStore>();
        // The write side of the membership table (issue #522): the BEGIN IMMEDIATE transaction and
        // the rows the one-active-membership and last-Owner rules are decided from.
        services.AddScoped<IBusinessMembershipWriteStore, EfBusinessMembershipWriteStore>();
        // The display-only member profile (issue #524): its own narrow write path, deliberately
        // separate from the membership write store above because it changes nothing a membership
        // means and takes none of that path's write lock.
        services.AddScoped<IMemberProfileWriteStore, EfMemberProfileWriteStore>();
        // The Owner-facing member roster and the three targeted member updates (issue #533). It
        // reads and writes the same non-tenant-owned table as the two stores above, and takes the
        // business id explicitly for that reason.
        services.AddScoped<IBusinessMemberStore, EfBusinessMemberStore>();
        // The Owner's invite links (issue #531). Like the membership stores above it names the business
        // in every statement, because the invite table is structurally non-tenant-owned too (the
        // reviewed ownership decision on issue #532): the redemption below has to find an invite by
        // its token under a denied scope, which no central query filter can match.
        services.AddScoped<IMemberInviteStore, EfMemberInviteStore>();
        // Redeeming an invite (issue #532) - the one persistence surface a caller with no business
        // membership can reach. A separate, deliberately tiny adapter from the Owner-facing store
        // above: its lookup takes only the presented token's hash, and every statement after that
        // names the business id the invite row reported, never one the request supplied.
        services.AddScoped<IMemberInviteRedemptionStore, EfMemberInviteRedemptionStore>();
        // The self-service business application (issue #541) - the second persistence surface a
        // caller with no business membership can reach, and the only one that creates a business.
        // A separate, deliberately tiny adapter again: it reads one business by the id the
        // applicant's own membership row supplied, and the business it creates is its own owner, so
        // no statement here names an id that came from a request.
        services.AddScoped<IBusinessApplicationStore, EfBusinessApplicationStore>();
        // The Super Admin's business directory and the two decisions on an application (issue
        // #544) - the first platform-admin surface that writes, and the only request path that
        // reads across every business through AppDbContext. A separate, deliberately tiny adapter
        // again, and for a stronger reason than the two above: the whole of what a platform
        // administrator can reach has to be one reviewable file. It is scoped like every other
        // adapter here, so it runs on the request's own denied-scope context and inside
        // MembershipWriteGuard's transaction on it; nothing hands it an unscoped context.
        services.AddScoped<IPlatformBusinessDirectoryStore, EfPlatformBusinessDirectoryStore>();
        // The platform administrator's read-only support sessions (issue #550). A separate,
        // deliberately tiny adapter again, over one table and no other: it names the authenticated
        // actor or the business its caller's transaction read in every statement, because the session
        // table is structurally non-tenant-owned too - a platform-admin request resolves no business,
        // so no central query filter can match these rows. Scoped like every adapter here, so the
        // start, the end and the deactivation hook all run on the request's own denied-scope context
        // and inside MembershipWriteGuard's transaction on it.
        services.AddScoped<ISupportSessionStore, EfSupportSessionStore>();
        // The platform supplier presets (issue #848): one more tiny adapter over one table, and the
        // one table in this schema that names no business in any statement because it is about none.
        // Scoped like every adapter here, so it runs on the request's own denied-scope context.
        services.AddScoped<ISupplierPresetStore, EfSupplierPresetStore>();
        // The shared switchable menu visibility (issue #1148, replacing issue #873's single-row
        // adapter over PlatformOptionVisibility, which nothing writes any more): a table that names
        // no business, for the same reason the presets do not - the setting is one answer for the
        // whole application. Scoped like every adapter here, so the Super Admin's write runs on the
        // request's own denied-scope context and a member's read on their own resolved one; neither
        // scope selects anything, because there is no owner to compare.
        services.AddScoped<IMenuVisibilityStore, EfMenuVisibilityStore>();
        // The business subscription state of issue #966: three deliberately tiny adapters over three
        // tables, kept apart because the records they hold must never write each other. The billing
        // subscription and the manual grants are ordinary tenant-owned reads and writes through the
        // central filters and the ownership stamp - the business comes from the caller's resolved
        // scope, never from a parameter - and the grandfathering record is the third table in this
        // schema that names no business, for the same reason the option visibility is: the cutoff and
        // the completion marker are one answer for the whole application. All three are scoped like
        // every adapter here, so the per-request entitlement read runs on the request's own context
        // and the human-run grandfathering command on the one it opened per business.
        services.AddScoped<IBillingSubscriptionStore, EfBillingSubscriptionStore>();
        services.AddScoped<IManualPlanGrantStore, EfManualPlanGrantStore>();
        services.AddScoped<ISubscriptionGrandfatheringStore, EfSubscriptionGrandfatheringStore>();
        // The connected-machine snapshots of issue #978: ordinary tenant-owned, append-only rows
        // recorded through one insert-if-absent claim, with the uniqueness rule on (business, UTC
        // date, account series) belonging to the database. Scoped like every adapter here, so a page
        // load records on the request's own context and the daily job on the one it opened for that
        // business.
        services.AddScoped<IConnectedMachineSnapshotStore, EfConnectedMachineSnapshotStore>();

        // The Super Admin's manual plan grant (issue #982). A separate adapter rather than a method
        // on either store above, because it answers a different kind of question: the business is the
        // subject of a platform decision rather than the caller's own resolved scope, so this is the
        // one path that reads and writes a business's grant under a denied scope - through one
        // business-pinned IgnoreQueryFilters read and the narrow write lease of
        // AppDbContext.BeginPlanGrantWrite. Scoped like every adapter here, so it runs on the
        // request's own context and inside the decision's transaction on it, which is also the only
        // way that lease can be taken at all.
        services.AddScoped<IPlatformPlanGrantStore, EfPlatformPlanGrantStore>();

        // The Super Admin's Nayax SQS sender identity (issue #1101). A separate adapter again, and
        // for the grant adapter's reason: the business is the subject of a platform decision rather
        // than the caller's own resolved scope, so this is the one path that reads and writes a
        // business's sender row under a denied scope - through business-pinned IgnoreQueryFilters
        // reads and the narrow write lease of AppDbContext.BeginNayaxSqsSenderWrite. Scoped like
        // every adapter here, so it runs on the request's own context and inside the decision's
        // transaction on it, which is also the only way that lease can be taken at all.
        services.AddScoped<IPlatformNayaxSqsSenderStore, EfPlatformNayaxSqsSenderStore>();

        // The Super Admin's view of one business's monthly receipt-scan count (issue #1164). A
        // separate adapter again, for the two adapters above's reason - the business is the subject
        // of a platform read rather than the caller's own resolved scope - and the narrowest of the
        // three: one business-pinned IgnoreQueryFilters read of one integer, no write of any kind and
        // therefore no lease at all. Scoped like every adapter here, so it runs on the request's own
        // context.
        services.AddScoped<IPlatformReceiptScanUsageStore, EfPlatformReceiptScanUsageStore>();

        // The attachment storage accounting of issue #968: the business's own usage, and the
        // reservation an upload takes before it writes a byte. Scoped like every adapter here, so
        // it runs on the request's own context and inside its resolved business.
        services.AddScoped<IAttachmentQuotaStore, EfAttachmentQuotaStore>();
        // The one provisioning write of issue #849: the copies of those presets a business receives
        // when its application is approved. A separate one-method adapter rather than a method on
        // the directory store above, because this is the only platform-admin path that writes a
        // tenant-owned row and that is exactly what makes it worth reading on its own. Scoped like
        // every adapter here, so it runs on the request's own denied-scope context and inside the
        // approval's transaction on it - which is also the only way its narrow provisioning lease
        // can be taken at all.
        services.AddScoped<IBusinessSupplierProvisioningStore, EfBusinessSupplierProvisioningStore>();
        // The shared supplier price catalogue (issue #923, parent #817). Two adapters because they
        // answer two different kinds of question: one business's own mappings, which are ordinary
        // tenant-owned data read and written through the central filters and the ownership stamp; and
        // the deduplicated work list the collector of #924 is built from, which reads the platform
        // catalogue tables and carries no business identity out. Both are scoped on the request's own
        // AppDbContext like every adapter here, and neither is given an unscoped context. The work
        // list store is where issue #924's reviewed cross-business read lives: two
        // IgnoreQueryFilters statements over the mappings' business-free text columns, reachable only
        // from the collector endpoint - and, since issue #977, the plan read that decides which
        // businesses those two statements may reach at all, which is the one place a business id
        // exists on this path and never leaves it. See EfPriceCatalogueWorkListStore and
        // docs/architecture.md § Collector work list.
        services.AddScoped<IProductPriceTrackingStore, EfProductPriceTrackingStore>();
        services.AddScoped<IPriceCatalogueWorkListStore, EfPriceCatalogueWorkListStore>();
        // The collector work list's one write (issue #924): a run and its work items, both platform
        // tables, in one transaction on the request's own denied-scope context.
        services.AddScoped<IPriceCollectorRunStore, EfPriceCollectorRunStore>();
        // And the collector's results (issue #937): the same platform tables plus the listings, the
        // observations, the held readings and the recorded outcome of every submitted row - each
        // method one transaction on that same denied-scope context, so the authoritative read, the
        // comparison and the write are one operation. See EfPriceCollectorResultStore.
        services.AddScoped<IPriceCollectorResultStore, EfPriceCollectorResultStore>();
        // And the collector's match proposals (issue #941): the match suggestions, the listings a
        // matched key had already collected, and the four match columns of the mappings that carry
        // that key - the one tenant-owned write a denied scope may make here, inside the narrow
        // matching window AppDbContext grants and BusinessOwnershipEnforcer bounds. Scoped on the
        // request's own context like every adapter above, so nothing can hand it an unscoped one.
        // See EfPriceCollectorMatchStore and docs/architecture.md § Collector product matching.
        services.AddScoped<IPriceCollectorMatchStore, EfPriceCollectorMatchStore>();
        // And the Super Admin's review of all of it (issue #928): the queue and the four decisions.
        // It is one of the two writers of MasterProducts in the application (issue #1024's create and
        // edit is the other), the only path that confirms a supplier listing or a pack size, and the
        // second path that writes a mapping's match columns
        // under a denied scope - through the narrow review window AppDbContext grants and
        // BusinessOwnershipEnforcer bounds, and through one IgnoreQueryFilters read in one place.
        // Scoped on the request's own context like every adapter above, so nothing can hand it an
        // unscoped one. See EfPriceCatalogueReviewStore and docs/architecture.md § Super Admin review
        // of the supplier price catalogue.
        services.AddScoped<IPriceCatalogueReviewStore, EfPriceCatalogueReviewStore>();
        // And the Super Admin's read-only view of the master products themselves (issue #1023): one
        // bounded, searchable page of the shared catalogue and one product with its supplier listings.
        // It writes nothing, takes no lease and makes no IgnoreQueryFilters statement, because every
        // table it reads is a platform table that carries no business key at all - so a denied scope
        // reads the whole catalogue and none of anybody's own rows. Scoped on the request's own
        // context like every adapter above, so nothing can hand it an unscoped one. See
        // EfMasterProductCatalogueStore and docs/architecture.md § Super Admin master products.
        services.AddScoped<IMasterProductCatalogueStore, EfMasterProductCatalogueStore>();
        // And managing that catalogue (issue #1024): the second writer of MasterProducts, and the
        // last one - it adds a master product and corrects an existing one's brand, name or size. It
        // writes that one platform table and reads one more, SupplierListings, for the single
        // question the size safeguard turns on: how many of the product's listings are confirmed.
        // Nothing tenant-owned is read or written, so it takes no lease and makes no
        // IgnoreQueryFilters statement either. Scoped on the request's own context like every adapter
        // above. See EfMasterProductCatalogueWriteStore and docs/architecture.md § Super Admin master
        // products.
        services.AddScoped<IMasterProductCatalogueWriteStore, EfMasterProductCatalogueWriteStore>();
        // And the business lookup of the same master products (issue #1049): the one read a member
        // makes of them, behind the ordinary business endpoint authorization. One statement over one
        // platform table and five columns of it - no listing, no price, no mapping, no product and no
        // business - so it writes nothing, takes no lease and makes no IgnoreQueryFilters statement,
        // and it answers the same catalogue under a business scope as under a denied one because a
        // platform table has no filter to apply. Scoped on the request's own context like every
        // adapter above. See EfMasterProductSuggestionStore and docs/architecture.md § Tenant
        // ownership.
        services.AddScoped<IMasterProductSuggestionStore, EfMasterProductSuggestionStore>();
        // The cheapest-supplier summary (issue #930): the read behind the Cheapest supplier column.
        // It reaches the platform catalogue only through the caller's own matched mappings and the
        // supplier records the caller holds a copy of, on the request's own scoped context like the
        // adapters above.
        services.AddScoped<ICheapestSupplierSummaryStore, EfCheapestSupplierSummaryStore>();
        services.AddScoped<IBusinessProfileStore, EfBusinessProfileStore>();
        // The Owner-only settings read and write (issue #548), deliberately a separate adapter from
        // the profile read above: that one answers every request, this one is a rare write that has
        // to go through SaveChanges so the central business enforcers validate it.
        services.AddScoped<IBusinessSettingsStore, EfBusinessSettingsStore>();
        services.AddScoped<INayaxFeeRateStore, EfNayaxFeeRateStore>();
        // The per-business Nayax connection (issue #518). Its INayaxTokenProtector dependency comes
        // from AddNayaxTokenProtection below rather than from this method, for the same reason the
        // Nayax HTTP client's options do: only the composition root reads configuration.
        services.AddScoped<INayaxConnectionStore, EfNayaxConnectionStore>();
        // The short-lived, encrypted connection-test draft (issue #536). A separate store from the
        // connection above, over a separate table, because a draft is credentials nothing has
        // accepted: no Nayax call may resolve one, and nothing that reads the connection may reach
        // one by accident.
        services.AddScoped<INayaxConnectionTestDraftStore, EfNayaxConnectionTestDraftStore>();
        // The transaction a save-from-draft (issue #537) claims the draft and writes the credential
        // in. It is registered over the request's own AppDbContext, which is the scoped unit of work
        // both stores above are constructed with, so the two writes join one transaction.
        services.AddScoped<INayaxConnectionSaveTransactions, EfNayaxConnectionSaveTransactions>();
        services.AddScoped<ISiteCommissionStore, EfSiteCommissionStore>();
        services.AddScoped<ICategoryStore, EfCategoryStore>();
        services.AddScoped<ILocalMachineStore, EfLocalMachineStore>();
        // Provider machine linking (issue #742): the one adapter that writes
        // LocalMachine.ExternalMachineId, and the only reason the port above never does. Scoped like
        // every other adapter here: its authoritative read and its write are one transaction on the
        // request's own AppDbContext.
        services.AddScoped<IProviderMachineLinkStore, EfProviderMachineLinkStore>();
        services.AddScoped<ISupplierStore, EfSupplierStore>();

        // The "Available to add" suppliers of issue #935. Scoped on the request's own AppDbContext
        // like every other adapter here: its reads and its insert are the ordinary tenant-scoped
        // ones, and the state change's top-up joins the settings save's own write transaction.
        services.AddScoped<IAvailableSupplierPresetStore, EfAvailableSupplierPresetStore>();
        services.AddScoped<IOperatingExpenseStore, EfOperatingExpenseStore>();
        services.AddScoped<IProductStore, EfProductStore>();
        // "Set GST rule in bulk" (issue #1076). Its own adapter rather than two more methods on
        // EfProductStore: it is the one write in this folder that touches every eligible product of
        // a business at once, and its BEGIN IMMEDIATE count-compare-write belongs where it can be
        // read and reviewed as the one thing it is.
        services.AddScoped<IBulkProductGstRuleStore, EfBulkProductGstRuleStore>();
        services.AddScoped<IProductCatalogStore, EfProductCatalogStore>();
        services.AddScoped<IPurchaseStore, EfPurchaseStore>();
        // The one read receipt scanning makes of the business's own data (issue #1163): two columns
        // each of its products and suppliers, through the ordinary tenant query filters.
        services.AddScoped<IReceiptScanCatalogueStore, EfReceiptScanCatalogueStore>();
        // And the one read behind a suggested pack size (issue #1165): the latest recorded purchase
        // line for a few of this business's products from one supplier, through the same filters.
        services.AddScoped<IReceiptScanPurchaseHistoryStore, EfReceiptScanPurchaseHistoryStore>();
        // And the business's own monthly scan counter (issue #1164): one row per business per
        // calendar month of its own zone, counted inside one write-locked transaction so two
        // simultaneous scans cannot both take the last one.
        services.AddScoped<IReceiptScanUsageStore, EfReceiptScanUsageStore>();
        services.AddScoped<IInventoryMovementStore, EfInventoryMovementStore>();
        services.AddScoped<IInventoryCostLedgerStore, EfInventoryCostLedgerStore>();
        services.AddScoped<ISaleCostingStore, EfSaleCostingStore>();
        services.AddScoped<IInventoryCostTransitionStore, EfInventoryCostTransitionStore>();
        services.AddScoped<IInventoryCostRepairStore, EfInventoryCostRepairStore>();
        services.AddScoped<IHistoricalGstClassificationStore, EfHistoricalGstClassificationStore>();
        services.AddScoped<INayaxSaleTimestampRepairStore, EfNayaxSaleTimestampRepairStore>();
        services.AddScoped<IStockAdjustmentStore, EfStockAdjustmentStore>();
        services.AddScoped<ISupplierOrderStore, EfSupplierOrderStore>();
        services.AddScoped<ISupplierOrderCompletionStore, EfSupplierOrderCompletionStore>();
        services.AddScoped<ISiteFactsStore, EfSiteFactsStore>();
        services.AddScoped<IMachineDashboardFactsStore, EfMachineDashboardFactsStore>();
        services.AddScoped<IProductPurchasePriceHistoryProvider, EfProductPurchasePriceHistoryProvider>();
        services.AddScoped<IProductPurchaseCostFactsProvider, EfProductPurchaseCostFactsProvider>();
        services.AddScoped<ILocalCatalogSnapshotProvider, EfLocalCatalogSnapshotProvider>();
        services.AddScoped<IMachineStockEventStore, EfMachineStockEventStore>();
        // The Sync Restock workflow's own successful-fetch coverage and the unresolved-event counts
        // the Dashboard reminder is built from (issue #891).
        services.AddScoped<IMachineRestockSyncCoverageStore, EfMachineRestockSyncCoverageStore>();
        services.AddScoped<IMachineStockLedgerStore, EfMachineStockLedgerStore>();
        // What the machine itself reports it is holding (issue #56). Scoped because reading it spends
        // the current business's own Nayax credentials, which are resolved per request.
        services.AddScoped<IMachineStockObservationSource, NayaxMachineStockObservationSource>();
        // How a Nayax product id becomes a local product id (issue #733): Product.ExternalProductId
        // (issue #759), read by every feature that matches live Nayax machine data to the catalogue.
        services.AddScoped<INayaxProductLinkStore, EfNayaxProductLinkStore>();
        // And how a Nayax machine number becomes a local machine id (issue #780):
        // LocalMachine.ExternalMachineId, the machine half of the same lookup.
        services.AddScoped<INayaxMachineLinkStore, EfNayaxMachineLinkStore>();
        services.AddScoped<IOutstandingSupplierOrderQuantityStore, EfOutstandingSupplierOrderQuantityStore>();
        services.AddScoped<IPickListStorageStockStore, EfPickListStorageStockStore>();
        // The batch refill's own write path (issue #743). Scoped like every other adapter here: the
        // BEGIN IMMEDIATE transaction it opens is on the request's own AppDbContext, which is what
        // puts the key claim, the storage check and the deductions inside one write lock.
        services.AddScoped<IPickListRefillStore, EfPickListRefillStore>();
        // The multi-machine batch's write path (issue #897), over the same two tables and the same
        // shared BEGIN IMMEDIATE write lock, so the two refill endpoints serialise against each other.
        services.AddScoped<IMultiMachinePickListRefillStore, EfMultiMachinePickListRefillStore>();
        services.AddScoped<ILatestNayaxSalesStore, EfLatestNayaxSalesStore>();
        // The queue reader's two sender adapters (issue #1103), and they are deliberately two. The
        // directory answers "whose sale is this?" before any business exists, which is the one
        // reviewed IgnoreQueryFilters statement outside a request and is read-only; the activity
        // store stamps the last-message instant on that business's own row inside its resolved scope,
        // as an ordinary tenant-owned update with nothing added. Both are scoped like every adapter
        // here, so each runs on the context of the scope the reader opened for it.
        services.AddScoped<INayaxSqsSenderDirectory, EfNayaxSqsSenderDirectory>();
        services.AddScoped<INayaxSqsSenderActivityStore, EfNayaxSqsSenderActivityStore>();
        // The Dashboard's read of the same row (issue #1106), scoped for the same reason: it runs on
        // the request's own context, in the caller's own business scope, and it is read-only and
        // projects no sender id.
        services.AddScoped<INayaxSqsLiveStatusStore, EfNayaxSqsLiveStatusStore>();
        // The hourly reconciliation's two adapters (issue #1104), scoped for the same reason: both run
        // on the context of the per-business scope the runner opened. The evidence store's writes each
        // take SQLite's write lock before their own reads, which is what makes the sale's state and
        // the evidence for it one operation - and what keeps issue #1105's Owner decisions from
        // interleaving with a run. The success store is an ordinary tenant-owned update of one column.
        services.AddScoped<INayaxSaleProviderEvidenceStore, EfNayaxSaleProviderEvidenceStore>();

        // The Nayax sales review of issue #1105: the Owner's decisions and the Dashboard summary.
        services.AddScoped<INayaxSaleReviewStore, EfNayaxSaleReviewStore>();
        services.AddScoped<IProductCostAttentionReader, EfProductCostAttentionReader>();
        services.AddScoped<INayaxReconciliationSuccessStore, EfNayaxReconciliationSuccessStore>();
        services.AddScoped<IInventoryCountAdjustmentStore, EfInventoryCountAdjustmentStore>();
        services.AddScoped<IImportedReimbursementStore, EfImportedReimbursementStore>();
        // The uploaded reimbursement report import of issue #1149. All three are scoped because each
        // runs on one context: the job store on the caller's own request scope for the upload and
        // the status reads, and on the worker's per-business scope for the import; the per-file
        // transaction on that same worker scope's context, which is what makes a file's rows, its
        // coverage and its outcome one commit; and the queue on the deliberately business-less scope
        // the worker opens for its one reviewed cross-business statement.
        services.AddScoped<IReimbursementImportJobStore, EfReimbursementImportJobStore>();
        services.AddScoped<IReimbursementImportFileTransactions, EfReimbursementImportFileTransactions>();
        services.AddScoped<IReimbursementImportJobQueue, EfReimbursementImportJobQueue>();
        // Reading the uploaded ZIP (issue #1149). A singleton because it holds nothing per request:
        // it is given bytes and answers what they contain, touches no database and no filesystem,
        // and its limits are the Domain's constants.
        services.AddSingleton<IReimbursementArchiveReader, ZipReimbursementArchiveReader>();
        // The provider catalogue review (issue #738), the catalogue import's one persistence
        // adapter since issue #739. Scoped like every other adapter here: both
        // operations read and write one caller's resolved business through the request's own
        // AppDbContext, and the review's link-or-create apply is decided inside its transaction.
        services.AddScoped<ICatalogueReviewStore, EfCatalogueReviewStore>();
        services.AddScoped<ILinkedProviderSaleResolutionStore, EfLinkedProviderSaleResolutionStore>();
        services.AddScoped<INayaxSalesImportStore, EfNayaxSalesImportStore>();
        services.AddScoped<IProviderDataCoverageStore, EfProviderDataCoverageStore>();
        services.AddScoped<IDashboardSummarySalesFactsProvider, EfDashboardSummarySalesFactsProvider>();

        return services;
    }

    /// <summary>
    /// Registers document storage: the configured provider behind
    /// <see cref="IDocumentStorage"/>, and the filesystem implementation as a concrete type
    /// whatever the provider is.
    ///
    /// It is separate from <see cref="AddInfrastructureServices"/> because only the composition
    /// root knows the host's content and web roots and can read configuration; passing both in
    /// keeps <c>IWebHostEnvironment</c> and <c>IConfiguration</c> out of the Application and
    /// Infrastructure layers.
    ///
    /// An invalid or incomplete Azure configuration throws here, at startup. There is
    /// deliberately no fallback from AzureBlob to the filesystem: an application that could not
    /// reach its configured storage would otherwise start writing business documents to a local
    /// disk nobody is watching.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The raw <c>DocumentStorage</c> configuration section.</param>
    /// <param name="fileSystemOptions">The host paths the filesystem implementation uses.</param>
    /// <exception cref="InvalidOperationException">The document-storage configuration is unsupported or incomplete.</exception>
    public static IServiceCollection AddDocumentStorage(
        this IServiceCollection services,
        DocumentStorageOptions options,
        FileSystemDocumentStorageOptions fileSystemOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fileSystemOptions);

        var configuration = DocumentStorageConfiguration.Resolve(options);
        services.AddSingleton(configuration);

        // Registered whichever provider is selected. The documents already on disk do not move
        // when the provider changes, so reading them stays possible - which the filesystem-to-Blob
        // migration will need, and which keeps switching the provider back a configuration
        // change rather than a code change.
        services.AddSingleton(fileSystemOptions);
        services.AddSingleton<FileSystemDocumentStorage>();

        switch (configuration.Provider)
        {
            case DocumentStorageProvider.FileSystem:
                // Scoped rather than singleton since issue #551, because the read-only guard reads
                // the request's own business scope. The filesystem adapter it wraps is still the one
                // singleton it always was.
                services.AddScoped<IDocumentStorage>(sp => ReadOnlyInSupportSession(
                    sp, sp.GetRequiredService<FileSystemDocumentStorage>()));
                break;

            case DocumentStorageProvider.AzureBlob:
                AddAzureBlobDocumentStorage(services, configuration);
                break;

            default:
                throw new InvalidOperationException(
                    $"Document-storage provider '{configuration.Provider}' has no registration.");
        }

        return services;
    }

    private static void AddAzureBlobDocumentStorage(
        IServiceCollection services,
        ResolvedDocumentStorageConfiguration configuration)
    {
        // DefaultAzureCredential and BlobServiceClient are both thread-safe and cache their
        // tokens and connections, so they are built once. The credential chain is what keeps
        // secrets out of configuration entirely: the App Service authenticates with its
        // system-assigned managed identity, and a developer with the same container role
        // authenticates with their own Azure CLI / Visual Studio / VS Code sign-in. No account
        // key, SAS token, client secret or connection string is read anywhere.
        services.AddSingleton<BlobServiceClient>(_ =>
            new BlobServiceClient(configuration.BlobServiceUri, new DefaultAzureCredential()));

        services.AddSingleton<IDocumentBlobContainer>(sp => new AzureBlobContainer(
            sp.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(configuration.ContainerName)));

        // Scoped, unlike the filesystem adapter: it reads the current request's business scope,
        // which is what decides the tenant prefix every blob name is built from. Registered as the
        // concrete type and reached through the read-only guard of issue #551, exactly as the
        // filesystem provider is.
        services.AddScoped<AzureBlobDocumentStorage>();
        services.AddScoped<IDocumentStorage>(sp => ReadOnlyInSupportSession(
            sp, sp.GetRequiredService<AzureBlobDocumentStorage>()));
    }

    /// <summary>
    /// Wraps whichever document-storage provider was selected in the support-session read-only
    /// guard (issue #551), so the rule is registered once rather than repeated per provider - and so
    /// a provider added later inherits it by being registered through here.
    /// </summary>
    private static IDocumentStorage ReadOnlyInSupportSession(
        IServiceProvider serviceProvider,
        IDocumentStorage storage) =>
        new SupportSessionReadOnlyDocumentStorage(
            storage,
            serviceProvider.GetRequiredService<IBusinessScope>());

    /// <summary>
    /// Registers the Nayax Lynx HTTP client behind <see cref="INayaxLynxClient"/> (issue #49), with
    /// bounded timeout/retry/circuit-breaker resilience (<see cref="NayaxResilienceHandler"/>,
    /// issue #48) applied to every call it makes.
    ///
    /// <strong>Only the base URL is global (issue #520.)</strong> The operator id and the bearer
    /// token are each business's own: <see cref="NayaxLynxClient"/> resolves them per call from
    /// <c>INayaxRequestCredentialProvider</c> over the current business's stored connection, so
    /// nothing credential-shaped is registered here, baked into the shared <c>HttpClient</c>, or
    /// available to fall back to. <paramref name="options"/> is read for its
    /// <see cref="NayaxLynxOptions.BaseUrl"/> and is deliberately not registered in the container;
    /// an operator id or token that is still in configuration is read only by the human-run
    /// <c>migrate-nayax-connection</c> command.
    ///
    /// Validation and the base address happen here, eagerly, so a missing or non-https base URL
    /// fails registration at startup rather than the first Nayax call.
    ///
    /// <strong>The port is the caching decorator, not the HTTP client (issues #840 and #842.)</strong>
    /// <see cref="CachingNayaxLynxClient"/> is what every consumer resolves, and it wraps the typed
    /// <see cref="NayaxLynxClient"/> registered under its own type; the machine lists it keeps live in
    /// the one singleton <see cref="NayaxMachineListCache"/> and each machine's selections in the one
    /// singleton <see cref="NayaxMachineProductsCache"/>, because a cache that was per request would
    /// cache nothing a second page load - or a simultaneous one - could use. The <c>HttpClient</c>
    /// itself is still named after the port, so its resilience pipeline and base address are
    /// unchanged.
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured base URL is missing or invalid.</exception>
    public static IServiceCollection AddNayaxLynxClient(this IServiceCollection services, NayaxLynxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseAddress = NayaxLynxConfiguration.BuildBaseAddress(options);

        // The resilience handler reads the current instant through the application's clock port to
        // interpret an HTTP-date Retry-After on a 429 (issue #844). TryAdd because
        // AddInfrastructureServices registers the same singleton, and either call may come first:
        // attaching the handler must never depend on the order the two registrations ran in.
        services.TryAddSingleton<IClock, SystemClock>();
        services.AddTransient<NayaxResilienceHandler>();
        services.AddHttpClient<NayaxLynxClient>(nameof(INayaxLynxClient), http => http.BaseAddress = baseAddress)
            .AddHttpMessageHandler<NayaxResilienceHandler>();

        // Singletons: the whole point of issues #840 and #842 is that a list read on one request is
        // still good on the next one, and on the two that started beside it. Neither holds a
        // credential, a token or a connection status - only machine ids, names and site ids, and the
        // machines' own selections - under keys that name the business they belong to.
        services.AddSingleton<NayaxMachineListCache>();
        services.AddSingleton<NayaxMachineProductsCache>();

        // The decorator takes the HTTP client as INayaxLynxCachedReadClient - the port plus the reads
        // and the machine-product write that are sent with a credential the decorator has already
        // resolved and gated, so each cache key names the credentials the request really carried - and
        // resolves the credential provider and the business scope per request like every other
        // consumer. A test can still put its own double behind it. Transient, as the typed client it
        // wraps is.
        //
        // Outside it sits the connected-machine counter of issue #978, which records the day's
        // snapshot from a complete fleet read the application was performing anyway - so the count
        // costs no extra Nayax call on a page load - and delegates everything else untouched. It is
        // outermost because it must see the fresh reads every consumer makes, and it is the only
        // place the refresh path records a snapshot. It takes the caching decorator as
        // INayaxFleetProvenanceClient, because the fleet read has to come back with the account
        // series of the credential snapshot that authenticated it: that is what the snapshot is
        // stamped with, and reading the connection again afterwards would describe whichever account
        // is stored by then rather than the one the fleet was read from.
        services.AddTransient<INayaxLynxClient>(serviceProvider =>
            new ConnectedMachineCountingNayaxLynxClient(
                new CachingNayaxLynxClient(
                    serviceProvider.GetRequiredService<NayaxLynxClient>(),
                    serviceProvider.GetRequiredService<INayaxRequestCredentialProvider>(),
                    serviceProvider.GetRequiredService<IBusinessScope>(),
                    serviceProvider.GetRequiredService<NayaxMachineListCache>(),
                    serviceProvider.GetRequiredService<NayaxMachineProductsCache>()),
                serviceProvider.GetRequiredService<IConnectedMachineSnapshotRecorder>(),
                serviceProvider
                    .GetRequiredService<ILogger<ConnectedMachineCountingNayaxLynxClient>>()));

        // The daily job's own fleet read (issue #978) is given the inner typed client explicitly,
        // never the port registered above: the port is the decorator, so resolving it here would make
        // the job's read record the snapshot twice over - once through the decorator and once through
        // the job - and would be a resolution cycle besides. Every refusal is unchanged, because the
        // status gate and the support-session rule belong to the credential provider the typed client
        // already uses. The typed client is also an INayaxFleetProvenanceClient, so the job's read
        // reports the account series it was authenticated as and the snapshot is stamped with that
        // rather than with the series the job read before the call.
        services.AddScoped<IConnectedMachineFleetReader>(serviceProvider =>
            new NayaxConnectedMachineFleetReader(serviceProvider.GetRequiredService<NayaxLynxClient>()));

        // The connection-test check runner (issue #536) gets its own HttpClient from the same base
        // address and the same resilience handler, and registers no credential here either: it
        // authenticates each check with the pair the Owner submitted, which is why it cannot share
        // the typed client above. Same base URL and same timeout/retry/circuit-breaker policy on
        // purpose - "Nayax is unavailable" has to mean the same thing on a test as on a sync.
        services.AddHttpClient<INayaxConnectionCheckRunner, NayaxConnectionCheckRunner>(
                http => http.BaseAddress = baseAddress)
            .AddHttpMessageHandler<NayaxResilienceHandler>();

        return services;
    }

    /// <summary>
    /// Registers the encryption of a business's stored Nayax access token behind
    /// <see cref="INayaxTokenProtector"/> (issue #518).
    ///
    /// Separate from <see cref="AddInfrastructureServices"/>, like
    /// <see cref="AddNayaxLynxClient"/> and <see cref="AddDocumentStorage"/>, because only the
    /// composition root reads configuration - which keeps Key Vault and app secrets out of
    /// Inventory.Application and Inventory.Domain entirely.
    ///
    /// Two outcomes, and both are deliberate. Configured key material is validated here, eagerly,
    /// so a half-configured section fails at startup with the setting named rather than the first
    /// time an operator saves a token. No key material at all is a legitimate state - provisioning
    /// the key is a human step, and an environment that uses no Nayax integration needs none - so
    /// it registers the fail-closed <see cref="UnconfiguredNayaxTokenProtector"/> instead of
    /// refusing to start: the API runs normally and only storing or reading a per-business token
    /// fails, which since issue #520 is what every Nayax call does and nothing else does.
    ///
    /// Singleton: the decoded keys are immutable and are read on every save and every credential
    /// read.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The configured encryption keys, bound from configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// Key material is configured but incomplete, malformed, or names an active key that is absent.
    /// </exception>
    public static IServiceCollection AddNayaxTokenProtection(
        this IServiceCollection services,
        NayaxTokenProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsConfigured)
        {
            services.AddSingleton<INayaxTokenProtector>(new UnconfiguredNayaxTokenProtector());

            return services;
        }

        // Constructed now rather than lazily: validation of the key section belongs at startup.
        var protector = new AesGcmNayaxTokenProtector(options);
        services.AddSingleton<INayaxTokenProtector>(protector);

        return services;
    }

    /// <summary>
    /// Registers everything receipt scanning needs behind its Application ports (issue #1163): the
    /// settings, the PDF page counter, the log sink and the typed <c>HttpClient</c> that talks to the
    /// reader.
    ///
    /// Separate from <see cref="AddInfrastructureServices"/>, like <see cref="AddNayaxLynxClient"/>
    /// and <see cref="AddNayaxTokenProtection"/>, because only the composition root reads
    /// configuration - which keeps the API key out of Inventory.Application and Inventory.Domain
    /// entirely. The key is held by <see cref="ReceiptScanningConfiguration"/> and is deliberately not
    /// reachable through <see cref="IReceiptScanSettings"/>, which is what the use case sees.
    ///
    /// <para><strong>Two outcomes, and both are deliberate.</strong> With no
    /// <c>ReceiptScanning:ApiKey</c> the feature is off: <see cref="IReceiptScanSettings.IsConfigured"/>
    /// is <see langword="false"/>, the endpoint answers "not available" before anything is read, and
    /// nothing leaves the process - which is the shipped state and the state of every test host. With
    /// a key and a broken setting beside it, registration fails here, at startup, with the setting
    /// named (see <see cref="ReceiptScanningConfiguration.From"/>).</para>
    ///
    /// <para>The typed client is registered either way, with the configured base address and the
    /// configured timeout, because an unconfigured reader refuses on its own
    /// (<see cref="ClaudeReceiptReader"/> makes no unauthenticated call) and a container that composes
    /// identically in both states is one fewer thing that can differ between environments. The
    /// timeout is the <c>HttpClient</c>'s, which is what makes "no automatic retry" structural: there
    /// is no resilience handler here, deliberately, unlike the Nayax client - a retried scan is a
    /// second payment.</para>
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The receipt scanning settings, bound from configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// An API key is configured together with a setting that cannot be used.
    /// </exception>
    public static IServiceCollection AddReceiptScanning(
        this IServiceCollection services,
        ReceiptScanningOptions options)
    {
        // Validated now rather than lazily, for the reason the token protection section is: a
        // half-configured section belongs at startup, not at the first scan.
        var configuration = ReceiptScanningConfiguration.From(options);

        services.AddSingleton(configuration);
        services.AddSingleton<IReceiptScanSettings>(configuration);
        services.AddSingleton<IPdfPageCounter, PdfPageCounter>();
        services.AddSingleton<IReceiptScanLog, LoggingReceiptScanLog>();

        services.AddHttpClient<IReceiptReader, ClaudeReceiptReader>(http =>
        {
            http.BaseAddress = configuration.BaseAddress;
            http.Timeout = configuration.Timeout;
        });

        return services;
    }

    /// <summary>
    /// Registers everything the Nayax SQS sales reader needs behind its Application ports (issue
    /// #1103): the queue client, the pacing, the log sink, the process-wide health record and the
    /// reader itself.
    ///
    /// Separate from <see cref="AddInfrastructureServices"/>, like <see cref="AddNayaxLynxClient"/>
    /// and <see cref="AddNayaxTokenProtection"/>, because only the composition root reads
    /// configuration - which keeps the queue URLs and the AWS credential pair out of
    /// Inventory.Application and Inventory.Domain entirely.
    ///
    /// <para><strong>Two outcomes, and both are deliberate.</strong> With no
    /// <c>NayaxSqs:QueueUrl</c> the reader is registered and does not start: it logs one information
    /// line and returns, and the <see cref="UnconfiguredNayaxSqsQueueClient"/> it holds refuses every
    /// operation, so an environment with no queue - every test host, the hosted testing environment,
    /// and the shipped state - starts exactly as it did before and cannot reach a queue by accident.
    /// With a queue URL and a missing region or credential, registration fails here, at startup, with
    /// the setting named rather than with an API that quietly imports nothing (see
    /// <see cref="NayaxSqsConfiguration.From"/>).</para>
    ///
    /// <para>Singletons throughout. One <see cref="AwsNayaxSqsQueueClient"/> because the AWS SDK's
    /// client is thread-safe and owns the HTTP connection pool - one per poll would renegotiate TLS
    /// every twenty seconds - and one <see cref="NayaxSqsReaderHealth"/> because there is one reader
    /// in one process and its state is a fact about that process. The per-business work the reader
    /// does is scoped: it opens a service scope per message through
    /// <c>INayaxSqsReaderScopes</c>, which the composition root satisfies.</para>
    ///
    /// <para>The hosted service that starts the loop is <strong>not</strong> registered here: that is
    /// the composition root's own line, so "what background work does this application run?" stays
    /// answerable from <c>Program.cs</c>.</para>
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The queue settings, bound from configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// A queue URL is configured without the region and credential pair the reader needs.
    /// </exception>
    public static IServiceCollection AddNayaxSqsReader(
        this IServiceCollection services,
        NayaxSqsOptions options)
    {
        // Validated now rather than lazily, for the reason the token protection section is: a
        // half-configured queue belongs at startup, not at the first poll.
        var configuration = NayaxSqsConfiguration.From(options);

        services.AddSingleton(configuration);
        services.AddSingleton<INayaxSqsReaderSettings>(configuration);

        if (configuration.IsConfigured)
        {
            services.AddSingleton<INayaxSqsQueueClient>(
                _ => new AwsNayaxSqsQueueClient(configuration));
        }
        else
        {
            services.AddSingleton<INayaxSqsQueueClient>(new UnconfiguredNayaxSqsQueueClient());
        }

        services.TryAddSingleton<IClock, SystemClock>();
        services.AddSingleton<INayaxSqsReaderPacing, SystemNayaxSqsReaderPacing>();
        services.AddSingleton<INayaxSqsReaderLog, LoggingNayaxSqsReaderLog>();
        services.AddSingleton<NayaxSqsReaderHealth>();
        services.AddSingleton<NayaxSqsSalesReader>();

        return services;
    }
}
