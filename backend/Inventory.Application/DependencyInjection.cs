using Inventory.Application.Access;
using Inventory.Application.Businesses;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.CatalogueReview;
using Inventory.Application.Categories;
using Inventory.Application.Commissions;
using Inventory.Application.Costing;
using Inventory.Application.Dashboard;
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
using Inventory.Application.Onboarding;
using Inventory.Application.PickList;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.PlatformDiagnostics;
using Inventory.Application.Products;
using Inventory.Application.Purchases;
using Inventory.Application.Reorder;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.SalesSync;
using Inventory.Application.SaleTimestampRepair;
using Inventory.Application.Sites;
using Inventory.Application.Stock;
using Inventory.Application.Suppliers;
using Inventory.Application.SupplierOrders;
using Inventory.Application.SupportSessions;
using Inventory.Application.Tenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Inventory.Application;

public static class ApplicationServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        // Current-business resolution is per request: it must never be cached across callers.
        services.AddScoped<ICurrentBusinessProvider, CurrentBusinessProvider>();

        // The current business's own name and time zone (issue #499). Per request like everything
        // tenancy-scoped: the answer belongs to one caller's membership.
        services.AddScoped<GetCurrentBusiness>();

        // The Owner's own business settings (issue #548). Scoped for the same reason: both read and
        // write act on the business one caller's membership resolved, and a saved time zone has to
        // take effect on the next request rather than being held in a cache.
        services.AddScoped<GetBusinessSettings>();
        services.AddScoped<UpdateBusinessSettings>();

        // What the current request may do (issue #551). Scoped because both of its sources are
        // per-request facts: the caller's membership role, and the support session they presented.
        // Registered before its consumers below for no reason other than reading order.
        services.AddScoped<CurrentCapabilities>();

        // The signed-in member's role and capabilities (issue #521). Per request for the same
        // reason, and so a role change applies on the next request rather than being cached.
        services.AddScoped<GetCurrentAccess>();

        // The signed-in person's own account state (issue #523). Scoped like the rest of tenancy:
        // the answer belongs to one caller's identity, and a revoked or granted membership must
        // show up on their next request rather than be cached.
        services.AddScoped<GetAccountState>();

        // The shared write path every membership change runs through (issue #522). Scoped, because
        // the transaction it opens is the request's own AppDbContext transaction, which the
        // operation's write has to save inside.
        services.AddScoped<MembershipWriteGuard>();

        // The Owner's member management (issue #533). Scoped like the rest of tenancy: each answer
        // belongs to one caller's resolved business, and the writer shares the request's own
        // AppDbContext with the write guard whose transaction its writes have to run inside.
        services.AddScoped<MemberChangeWriter>();
        services.AddScoped<ListMembers>();
        services.AddScoped<ChangeMemberRole>();
        services.AddScoped<RevokeMember>();
        services.AddScoped<ReinstateMember>();

        // The Owner's invite links (issue #531). Scoped for the same reason: each answer belongs to
        // one caller's resolved business, and the cancellation's conditional write runs on that
        // request's own AppDbContext - which is also what lets #509's deactivation call the store's
        // cancel-all inside its own transaction.
        services.AddScoped<CreateMemberInvite>();
        services.AddScoped<ListMemberInvites>();
        services.AddScoped<CancelMemberInvite>();

        // Redeeming an invite (issue #532). Scoped like every other use case, and it has to be: the
        // membership it creates and the invite it closes are written through the request's own
        // AppDbContext, inside the write transaction MembershipWriteGuard opens on that same
        // context. It is the one use case here reached by a caller with no resolved business, so it
        // takes no ICurrentBusinessProvider at all.
        services.AddScoped<RedeemMemberInvite>();

        // The self-service business application (issue #541). Scoped for the same reasons the
        // redemption is: the business and the membership it creates are written through the
        // request's own AppDbContext, inside MembershipWriteGuard's transaction on that same
        // context, and it is the second use case here reached by a caller with no resolved business
        // - so it takes no ICurrentBusinessProvider either. The rate limiter is a singleton because
        // the window it remembers is state about the process rather than about one request: "this
        // identity has applied five times in the last ten minutes" must be the same answer for
        // every request, exactly as the Nayax test limiter's window is.
        services.AddSingleton<BusinessApplicationRateLimiter>();
        services.AddScoped<ApplyForBusiness>();

        // The Super Admin's business directory and the two decisions on an application (issue
        // #544). Scoped for the reason the two paths above are: the decision's authoritative read
        // and its write run on the request's own AppDbContext, inside MembershipWriteGuard's
        // transaction on that same context. They take no ICurrentBusinessProvider at all - a
        // platform administrator has no membership, so there is no business to resolve - and the
        // business they act on is named by the route, which is a decision's subject rather than a
        // tenancy scope (docs/architecture.md § Super Admin businesses API).
        services.AddScoped<ListPlatformBusinesses>();
        services.AddScoped<CountPendingBusinessApplications>();
        services.AddScoped<BusinessApplicationDecisionWriter>();
        services.AddScoped<ApproveBusinessApplication>();
        services.AddScoped<RejectBusinessApplication>();

        // Switching a business off and on again, and releasing its members while it is off (issue
        // #545). Scoped for the same reasons: the authoritative read, the status write, the invite
        // cancellation and the membership release all run on the request's own AppDbContext, inside
        // MembershipWriteGuard's transaction on that same context, and the business they act on is
        // named by the route - a decision's subject rather than a tenancy scope.
        services.AddScoped<BusinessLifecycleWriter>();
        services.AddScoped<DeactivateBusiness>();
        services.AddScoped<ReactivateBusiness>();
        services.AddScoped<ListDeactivatedBusinessMembers>();
        services.AddScoped<ReleaseBusinessMember>();

        // The Super Admin's read-only support sessions (issue #550). Scoped for the same reasons
        // again: the business read, the session write and the rule re-checks run on the request's own
        // AppDbContext, inside MembershipWriteGuard's transaction on that same context, and the
        // business a session is started for is named by the route - the subject of the session rather
        // than a tenancy scope. The deactivation hook that ends a business's sessions is registered by
        // the composition root, where issue #545's single hook registration lives.
        services.AddScoped<StartSupportSession>();
        services.AddScoped<GetActiveSupportSession>();
        services.AddScoped<EndSupportSession>();

        // The member's display-only name and email address, refreshed from their sign-in token
        // (issue #524). The hourly throttle and the hand-off queue are singletons because both are
        // state about the process rather than about one request: "this membership was refreshed
        // within the hour" and "these refreshes are waiting" must be the same answer for every
        // request. The scheduler the middleware calls is a singleton over them for the same reason,
        // and holds nothing per request itself.
        services.AddSingleton<MemberProfileRefreshThrottle>();
        services.AddSingleton<MemberProfileRefreshQueue>();
        services.AddSingleton<ScheduleMemberProfileRefresh>();

        // Scoped, because it wraps the request-scoped IActorProfileAccessor: this is the one
        // dependency the business-scope middleware injects for the whole feature.
        services.AddScoped<MemberProfileRefreshRequestScheduler>();

        // The write itself is Scoped like every other use case, and is resolved from the drain's own
        // new scope - never the request's - so it runs with a denied business scope and its own
        // AppDbContext (docs/architecture.md § Member display name and email).
        services.AddScoped<RefreshMemberProfile>();

        // The Nayax credentials every ordinary Nayax call is made with (issue #520), resolved per
        // call from the trusted current business over #518's store. Scoped for the same reason the
        // current business is: a credential belongs to one caller's business and must never be
        // cached across callers. The gate-free re-test read (#506) is a separate registration of a
        // separate port, so nothing that injects the ordinary provider can reach it.
        services.AddScoped<INayaxRequestCredentialProvider, NayaxRequestCredentialProvider>();
        services.AddScoped<INayaxConnectionRetestCredentialProvider, NayaxConnectionRetestCredentialProvider>();

        // The Owner-only connection test (issue #536). The rate limiter is a singleton because the
        // window it remembers is state about the process rather than about one request: "this
        // business has run five tests in the last ten minutes" must be the same answer for every
        // request, exactly as MemberProfileRefreshThrottle's hour is.
        services.AddSingleton<NayaxConnectionTestRateLimiter>();
        services.AddScoped<TestNayaxConnection>();

        // Saving a tested draft, re-testing the stored token and reporting the connection's state
        // (issue #537). The re-test shares the singleton limiter above on purpose: a re-test is the
        // same eight calls against the same upstream, so test and re-test runs count together.
        services.AddScoped<SaveNayaxConnection>();
        services.AddScoped<RetestNayaxConnection>();
        services.AddScoped<GetNayaxConnectionStatus>();

        services.AddScoped<ListNayaxFeeRates>();
        services.AddScoped<SaveNayaxFeeRate>();
        services.AddScoped<GetNayaxProcessingFees>();
        services.AddScoped<IGetNayaxProcessingFees>(sp => sp.GetRequiredService<GetNayaxProcessingFees>());
        services.AddScoped<GetSiteCommissionReport>();
        services.AddScoped<IGetSiteCommissionReport>(sp => sp.GetRequiredService<GetSiteCommissionReport>());
        services.AddScoped<GetSiteCommissionAgreements>();
        services.AddScoped<SaveSiteCommissionAgreement>();
        services.AddScoped<RecordSiteCommissionPayment>();
        services.AddScoped<ListCategories>();
        services.AddScoped<GetCategory>();
        services.AddScoped<CreateCategory>();
        services.AddScoped<UpdateCategory>();
        services.AddScoped<DeleteCategory>();
        services.AddScoped<ListSuppliers>();
        services.AddScoped<GetSupplier>();
        services.AddScoped<CreateSupplier>();
        services.AddScoped<UpdateSupplier>();
        services.AddScoped<DeleteSupplier>();
        services.AddScoped<GetSupplierGstDefaults>();
        services.AddScoped<SetSupplierGstDefaults>();
        services.AddScoped<ListOperatingExpenses>();
        services.AddScoped<GetOperatingExpense>();
        services.AddScoped<GetOperatingExpenseAttachment>();
        services.AddScoped<CreateOperatingExpense>();
        services.AddScoped<UpdateOperatingExpense>();
        services.AddScoped<DeleteOperatingExpense>();
        services.AddScoped<GetNayaxCatalogReconciliation>();
        services.AddScoped<GetSiteSummaries>();
        services.AddScoped<GetSiteProducts>();
        // The business's own machine records (issue #740), which work with no provider at all. They
        // are deliberately separate from the live-fleet use cases below.
        services.AddScoped<ListLocalMachines>();
        services.AddScoped<GetLocalMachine>();
        services.AddScoped<CreateLocalMachine>();
        services.AddScoped<RenameLocalMachine>();
        services.AddScoped<SetLocalMachineActivation>();
        // The route id of every api/machines/{id} endpoint is a local machine (issue #741); this is
        // the one place it becomes the pair of ids the features behind those routes need.
        services.AddScoped<ResolveMachineIdentity>();
        services.AddScoped<ListMachineDashboard>();
        services.AddScoped<GetMachineDashboard>();
        services.AddScoped<ListMachineProducts>();
        services.AddScoped<GetMachineLatestSales>();
        services.AddScoped<SyncMachineStockFromNayax>();
        services.AddScoped<ApplyMachineStockSync>();
        services.AddScoped<ResolveMachineStockDuplicate>();
        services.AddScoped<ResolveMachineStockEventsAsAlreadyRecorded>();

        // The machine-stock ledger (issue #56). Scoped like every other use case: the reconciliation
        // reads one caller's resolved business through the request's own AppDbContext, and a recorded
        // count is written through that same context.
        services.AddScoped<GetMachineStockReconciliation>();
        services.AddScoped<RecordMachineStockCount>();
        // The latest-sales synchronization (issue #187) and its per-business run limit (issue #681).
        // The limiter is a singleton for the reason the connection test's is: the window it
        // remembers is state about the process rather than about one request, so "this business has
        // synchronized ten times in the last two minutes" must be the same answer for every request.
        services.AddSingleton<NayaxSalesSyncRateLimiter>();
        services.AddScoped<SyncLatestNayaxSales>();
        services.AddScoped<ComputePurchaseTotalValidation>();
        services.AddScoped<ComputePurchaseGstSummary>();
        services.AddScoped<ListPurchases>();
        services.AddScoped<GetPurchase>();
        services.AddScoped<GetPurchaseFile>();
        services.AddScoped<UploadPurchase>();
        services.AddScoped<UpdatePurchase>();
        services.AddScoped<DeletePurchase>();
        services.AddScoped<ListActiveSupplierOrders>();
        services.AddScoped<GetSupplierOrder>();
        services.AddScoped<CreateSupplierOrder>();
        services.AddScoped<CancelSupplierOrder>();
        services.AddScoped<CalculateReorderNeeds>();
        services.AddScoped<ListProducts>();
        services.AddScoped<GetProduct>();
        services.AddScoped<ListLowStockProducts>();
        services.AddScoped<CreateProduct>();
        services.AddScoped<UpdateProduct>();
        services.AddScoped<DeleteProduct>();
        services.AddScoped<GetProductGstRule>();
        services.AddScoped<SetProductGstRule>();
        services.AddScoped<ResolveMachineProductPricing>();
        services.AddScoped<GetPickList>();
        // The pick list's batch refill (issue #743): one request, one write-locked transaction, all
        // rows or none.
        services.AddScoped<RecordPickListRefill>();
        services.AddScoped<RecordInventoryMovement>();
        services.AddScoped<IRecordInventoryMovement>(sp => sp.GetRequiredService<RecordInventoryMovement>());
        services.AddScoped<RebuildProductCost>();
        services.AddScoped<IRebuildProductCost>(sp => sp.GetRequiredService<RebuildProductCost>());
        services.AddScoped<CostSale>();
        services.AddScoped<ICostSale>(sp => sp.GetRequiredService<CostSale>());
        services.AddScoped<CostPendingSales>();
        services.AddScoped<BackfillSaleCosts>();
        services.AddScoped<BackfillNayaxHistoricalSaleCosts>();
        services.AddScoped<PreviewInventoryCostTransition>();
        services.AddScoped<ApplyInventoryCostTransition>();
        services.AddScoped<PreviewAllInventoryCostTransitions>();
        services.AddScoped<ApplyAllInventoryCostTransitions>();
        services.AddScoped<PreviewInventoryCostRepair>();
        services.AddScoped<ApplyInventoryCostRepair>();
        services.AddScoped<GetInventoryCostRepairHistory>();
        services.AddScoped<PreviewNayaxSaleTimestampRepair>();
        services.AddScoped<ApplyNayaxSaleTimestampRepair>();
        services.AddScoped<PreviewHistoricalGstClassification>();
        services.AddScoped<ApplyHistoricalGstClassification>();
        services.AddScoped<ApplyInventoryCount>();
        services.AddScoped<ImportNayaxProductCatalog>();
        // The provider catalogue review (issue #738). The direct import above is unchanged and keeps
        // its own path; these are the deliberate link-or-create alternative to it.
        services.AddScoped<StartCatalogueReview>();
        services.AddScoped<ApplyCatalogueReview>();
        services.AddScoped<ResolveLinkedProviderSales>();
        services.AddScoped<ImportNayaxSales>();
        services.AddScoped<ImportPendingReimbursementXmlFiles>();
        services.AddScoped<GetStockHistory>();
        services.AddScoped<ListStockHistory>();
        services.AddScoped<GetRestockCostSuggestion>();
        services.AddScoped<IGetRestockCostSuggestion>(sp => sp.GetRequiredService<GetRestockCostSuggestion>());
        services.AddScoped<AdjustStock>();
        services.AddScoped<GetProductPriceComparison>();
        services.AddScoped<GetBookkeepingReport>();
        services.AddScoped<IGetBookkeepingReport>(sp => sp.GetRequiredService<GetBookkeepingReport>());
        services.AddScoped<GetDailyReport>();
        services.AddScoped<GetReconciliationReport>();
        services.AddScoped<GetMachineProfitabilityReport>();
        services.AddScoped<GetProductProfitabilityReport>();
        services.AddScoped<IGetProductProfitabilityReport>(sp => sp.GetRequiredService<GetProductProfitabilityReport>());
        services.AddScoped<GetGstAccountingAid>();
        services.AddScoped<GetDashboardReport>();
        services.AddScoped<GetInventoryValuationSummary>();
        services.AddScoped<GetDashboardSummary>();
        services.AddScoped<GetTransactionSalesReport>();
        services.AddScoped<GetReportExportRows>();

        // The platform-admin diagnostics read (issue #336). The use case is registered here with
        // every other use case; its IDiagnosticsQueryExecutor port is satisfied by
        // AddPlatformDiagnostics() in Inventory.Infrastructure, and its IPlatformDiagnosticsAudit
        // port by the ILogger adapter in InventoryApi, because the audit event carries the
        // request's correlation id.
        services.AddScoped<RunDiagnosticsQuery>();

        return services;
    }
}
