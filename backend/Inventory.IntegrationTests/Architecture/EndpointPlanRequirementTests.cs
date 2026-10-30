using System.Reflection;
using Inventory.Domain.Subscriptions;
using InventoryApi.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace InventoryApi.Tests.Architecture;

/// <summary>
/// Every controller action is accounted for in one maintained list of plan requirements (issue
/// #967), so a new endpoint fails the build until somebody decides what a business's plan has to
/// include before it may be called.
///
/// <para>This is the plan half of what
/// <see cref="EndpointAuthorizationClassificationTests"/> does for roles, and it is modelled on
/// <see cref="OperatorReadableFinancialFigureTests"/> (issue #700), including its honest limitation:
/// the <see cref="PlanRequirementKind.ViaProviderGate"/> and <see cref="PlanRequirementKind.None"/>
/// entries are an allow-list of <em>decisions</em>, and no reflection rule can prove at build time
/// that an endpoint's Nayax use really does all go through the credential gate. What it does prove
/// is that nobody added an endpoint without that decision being made and reviewed - which is the
/// failure mode this test exists for, because "no plan requirement" used to be the only possible
/// state and will now be a silent one.</para>
///
/// <para>The four kinds are the complete vocabulary:</para>
/// <list type="number">
///   <item><b>A declared feature.</b> <c>[RequirePlan(PlanFeature.X)]</c> on the action or its
///   controller. The plan is evaluated after the role requirement and before the action runs.</item>
///   <item><b>Per report.</b> <c>[RequirePlanPerReport]</c>, which exists for the one export
///   endpoint whose required feature depends on which report it was asked for.</item>
///   <item><b>Via the provider gate.</b> The endpoint declares nothing itself because its
///   sales-provider behaviour is behind
///   <c>Inventory.Application.Nayax.INayaxRequestCredentialProvider</c>'s own plan check (issue
///   #967), so a business whose plan excludes the provider gets this endpoint's existing
///   no-usable-provider behaviour rather than a refusal. Declaring a plan at such an endpoint would
///   be the wrong answer, not a stricter one: it would turn a Pick List that opens manual entry, or
///   a dashboard that withholds one card, into a page that fails.</item>
///   <item><b>None.</b> The endpoint is included in every plan. Free's own features are never
///   locked (AGENTS.md § Business plans), and this is most of the application: inventory,
///   purchasing, suppliers, expenses, machines as local records, manual pick-list entry, the
///   member and platform surfaces, and the reads a client needs to render any of them.</item>
/// </list>
/// </summary>
public class EndpointPlanRequirementTests
{
    /// <summary>How one action's plan requirement is expressed.</summary>
    private enum PlanRequirementKind
    {
        /// <summary><c>[RequirePlan(feature)]</c> on the action or its controller.</summary>
        Declared,

        /// <summary><c>[RequirePlanPerReport]</c>: the report export, and nothing else.</summary>
        PerReport,

        /// <summary>
        /// Nothing declared, because the endpoint's sales-provider behaviour is refused by the
        /// Application layer's own plan check and degrades the way an unusable connection already
        /// does. For every one of them but one that check is
        /// <c>INayaxRequestCredentialProvider</c>'s; the Sync Restock reminder makes no Nayax call
        /// at all, so the credential gate never sees it and its use case asks the same question
        /// itself and reports the same distinct reason.
        /// </summary>
        ViaProviderGate,

        /// <summary>Nothing declared, because every plan includes this endpoint.</summary>
        None,
    }

    /// <summary>
    /// One action's entry. <see cref="Feature"/> is set only for
    /// <see cref="PlanRequirementKind.Declared"/>.
    /// </summary>
    private sealed record PlanRequirementEntry(
        string Endpoint,
        PlanRequirementKind Kind,
        PlanFeature? Feature = null);

    /// <summary>
    /// Every controller action in the API and the plan requirement decided for it. Adding a line is
    /// the decision, and the pull request that adds the endpoint is where it is made.
    ///
    /// <para>The route each key belongs to is in the comment beside it, because the key is the
    /// controller and the action - which is unique even where a route has a JSON and a multipart
    /// overload, and is what a reviewer goes and reads.</para>
    ///
    /// <para>The platform-admin, price-collector, invite-redemption, self-service-onboarding and
    /// <c>api/me</c> endpoints are all <see cref="PlanRequirementKind.None"/> and structurally have
    /// to be: each of them runs with a <strong>denied</strong> business scope or answers a fact
    /// about the caller's own identity, so there is no business whose plan could be evaluated, and a
    /// requirement there would be one nothing could ever satisfy.</para>
    /// </summary>
    private static readonly PlanRequirementEntry[] Inventory =
    [
        // The platform diagnostics API (issue #336): a denied scope, no business, no plan.
        new("AdminDiagnosticsController.Access", PlanRequirementKind.None), // GET api/admin/diagnostics/access
        new("AdminDiagnosticsController.Query", PlanRequirementKind.None), // POST api/admin/diagnostics/query

        // The business shell and the Owner's own business settings: a business's own record, which
        // it can always read and edit whatever it has bought.
        new("BusinessController.Current", PlanRequirementKind.None), // GET api/business/current
        new("BusinessController.SaveSettings", PlanRequirementKind.None), // PUT api/business/settings
        new("BusinessController.Settings", PlanRequirementKind.None), // GET api/business/settings

        // The provider catalogue review. Starting one reads the provider's catalogue, so it is
        // behind the credential gate; applying a stored review and resolving the sales a link
        // explains are local writes over rows this business already holds.
        new("CatalogueReviewController.Apply", PlanRequirementKind.None), // POST api/catalogue-review/apply
        new("CatalogueReviewController.ResolveUnresolvedSales", PlanRequirementKind.None), // POST api/catalogue-review/unresolved-sales/apply
        new("CatalogueReviewController.Start", PlanRequirementKind.ViaProviderGate), // POST api/catalogue-review

        // The catalogue: Free's own feature throughout.
        new("CategoriesController.Create", PlanRequirementKind.None), // POST api/Categories
        new("CategoriesController.Delete", PlanRequirementKind.None), // DELETE api/Categories/{id:long}
        new("CategoriesController.Get", PlanRequirementKind.None), // GET api/Categories/{id:long}
        new("CategoriesController.GetAll", PlanRequirementKind.None), // GET api/Categories
        new("CategoriesController.Update", PlanRequirementKind.None), // PUT api/Categories/{id:long}

        // Supplier price data is issue #977's decision, not this one's: explicitly out of scope
        // here, and unchanged until that task is implemented.
        new("CheapestSuppliersController.Summary", PlanRequirementKind.None), // POST api/cheapest-suppliers/summary

        // The home Dashboard. Its one live fleet read is behind the credential gate, so a business
        // whose plan excludes the provider keeps the whole local summary with the refill card
        // withheld - exactly what it already gets with no usable connection.
        new("DashboardController.Summary", PlanRequirementKind.ViaProviderGate), // GET api/dashboard/summary

        // The catalogue reconciliation reads the provider's catalogue snapshot.
        new("DataQualityController.NayaxCatalogReconciliation", PlanRequirementKind.ViaProviderGate), // GET api/data-quality/nayax-catalog-reconciliation

        // Historical GST classification is a maintenance workflow over this business's own purchase
        // history, which the Bookkeeping and GST reports are calculated from.
        new("HistoricalGstClassificationController.Apply", PlanRequirementKind.None), // POST api/admin/historical-gst-classification/apply
        new("HistoricalGstClassificationController.Preview", PlanRequirementKind.None), // POST api/admin/historical-gst-classification/preview

        // The uploaded Nayax sales export is Pro (decided on #965's review of 29 October 2026); the
        // reimbursement XML import is a settlement statement the business was sent, and its figures
        // feed the two reports a Starter business includes.
        new("ImportsController.ImportNayaxSales", PlanRequirementKind.Declared, PlanFeature.SalesFileImport), // POST api/imports/nayax-sales
        new("ImportsController.ImportPendingXmlFiles", PlanRequirementKind.None), // POST api/imports/pending-xml

        // Costing repairs over this business's own recorded history.
        new("InventoryCostRepairsController.Apply", PlanRequirementKind.None), // POST api/admin/inventory-cost-repair/apply
        new("InventoryCostRepairsController.History", PlanRequirementKind.None), // GET api/admin/inventory-cost-repair/{productId:long}
        new("InventoryCostRepairsController.Preview", PlanRequirementKind.None), // POST api/admin/inventory-cost-repair/preview

        // The inventory-cost transition reads the machines' opening stock from the provider, so all
        // four actions are behind the credential gate and degrade as they already do without one.
        new("InventoryCostTransitionsController.Apply", PlanRequirementKind.ViaProviderGate), // POST api/admin/inventory-cost-transition/apply
        new("InventoryCostTransitionsController.ApplyAll", PlanRequirementKind.ViaProviderGate), // POST api/admin/inventory-cost-transition/apply-all
        new("InventoryCostTransitionsController.Preview", PlanRequirementKind.ViaProviderGate), // POST api/admin/inventory-cost-transition/preview
        new("InventoryCostTransitionsController.PreviewAll", PlanRequirementKind.ViaProviderGate), // POST api/admin/inventory-cost-transition/preview-all

        // Take Inventory: a local count.
        new("InventoryCountController.Apply", PlanRequirementKind.None), // POST api/products/{productId:long}/inventory-count/apply

        // The invite redemption runs with a denied scope and writes the caller's own membership.
        new("InviteRedemptionController.Redeem", PlanRequirementKind.None), // POST api/invites/redeem

        // The business's own machine records, which work with no provider at all.
        new("LocalMachinesController.Create", PlanRequirementKind.None), // POST api/local-machines
        new("LocalMachinesController.Deactivate", PlanRequirementKind.None), // POST api/local-machines/{id:long}/deactivate
        new("LocalMachinesController.Delete", PlanRequirementKind.None), // DELETE api/local-machines/{id:long}
        new("LocalMachinesController.Get", PlanRequirementKind.None), // GET api/local-machines/{id:long}
        new("LocalMachinesController.GetAll", PlanRequirementKind.None), // GET api/local-machines
        new("LocalMachinesController.Reactivate", PlanRequirementKind.None), // POST api/local-machines/{id:long}/reactivate
        new("LocalMachinesController.Rename", PlanRequirementKind.None), // PUT api/local-machines/{id:long}

        // Deciding which machine a provider number means reads the provider's fleet.
        new("MachineLinkReviewController.Apply", PlanRequirementKind.ViaProviderGate), // POST api/machine-link-review/apply
        new("MachineLinkReviewController.Review", PlanRequirementKind.ViaProviderGate), // POST api/machine-link-review

        // The Sync Restock reminder makes no Nayax call, so the credential gate never sees it; it
        // asks the same plan question itself and reports the same distinct reason.
        new("MachineRestockSyncRemindersController.Reminder", PlanRequirementKind.ViaProviderGate), // GET api/machine-restock-sync/reminder

        // The machine reads are the fleet, so they are behind the credential gate and a business
        // without the provider sees its machines as the local records they are. Applying or
        // resolving an already-imported restock event, and recording a count, are local writes.
        new("MachinesController.ApplySyncRestock", PlanRequirementKind.None), // POST api/Machines/{id:long}/sync-restock/apply
        new("MachinesController.GetAll", PlanRequirementKind.ViaProviderGate), // GET api/Machines
        new("MachinesController.GetById", PlanRequirementKind.ViaProviderGate), // GET api/Machines/{id:long}
        new("MachinesController.GetLatestSales", PlanRequirementKind.ViaProviderGate), // GET api/Machines/{id:long}/latest-sales
        new("MachinesController.GetMachineProducts", PlanRequirementKind.ViaProviderGate), // GET api/Machines/{id:long}/products
        new("MachinesController.GetStockLedger", PlanRequirementKind.ViaProviderGate), // GET api/Machines/{id:long}/stock-ledger
        new("MachinesController.RecordStockCount", PlanRequirementKind.None), // POST api/Machines/{id:long}/stock-ledger/counts
        new("MachinesController.ResolveSyncRestockDuplicate", PlanRequirementKind.None), // POST api/Machines/{id:long}/sync-restock/resolve-duplicate
        new("MachinesController.ResolveSyncRestockManually", PlanRequirementKind.None), // POST api/Machines/{id:long}/sync-restock/resolve-manual
        new("MachinesController.SyncRestock", PlanRequirementKind.ViaProviderGate), // POST api/Machines/{id:long}/sync-restock

        // What the caller may do and what account state they are in. A plan is reported here and
        // never enforced: a member who cannot ask what they may do cannot be told anything.
        new("MeController.Access", PlanRequirementKind.None), // GET api/me/access
        new("MeController.AccountState", PlanRequirementKind.None), // GET api/me/account-state

        // Members and invites: who may reach the business, which no plan decides.
        new("MemberInvitesController.Cancel", PlanRequirementKind.None), // POST api/members/invites/{inviteId:int}/cancel
        new("MemberInvitesController.Create", PlanRequirementKind.None), // POST api/members/invites
        new("MemberInvitesController.List", PlanRequirementKind.None), // GET api/members/invites
        new("MembersController.ChangeRole", PlanRequirementKind.None), // PUT api/members/{membershipId:int}/role
        new("MembersController.List", PlanRequirementKind.None), // GET api/members
        new("MembersController.Reinstate", PlanRequirementKind.None), // POST api/members/{membershipId:int}/reinstate
        new("MembersController.Revoke", PlanRequirementKind.None), // POST api/members/{membershipId:int}/revoke

        // The three credential actions call Nayax without the ordinary credential provider, so each
        // declares the feature itself; the status read makes no Nayax call and stays on every plan,
        // which is what tells a downgraded business its saved credentials are still there.
        new("NayaxConnectionController.Retest", PlanRequirementKind.Declared, PlanFeature.SalesProviderIntegration), // POST api/settings/nayax-connection/retest
        new("NayaxConnectionController.Save", PlanRequirementKind.Declared, PlanFeature.SalesProviderIntegration), // POST api/settings/nayax-connection/save
        new("NayaxConnectionController.Status", PlanRequirementKind.None), // GET api/settings/nayax-connection/status
        new("NayaxConnectionController.Test", PlanRequirementKind.Declared, PlanFeature.SalesProviderIntegration), // POST api/settings/nayax-connection/test

        // The timestamp repair's preview reads the provider's own last sales; its apply rewrites
        // stored history and makes no Nayax call.
        new("NayaxSaleTimestampRepairsController.Apply", PlanRequirementKind.None), // POST api/admin/nayax-sale-timestamp-repair/apply
        new("NayaxSaleTimestampRepairsController.Preview", PlanRequirementKind.ViaProviderGate), // POST api/admin/nayax-sale-timestamp-repair/preview

        // The sales sync is the provider read the Dashboard starts with.
        new("NayaxSalesSyncController.SyncLatest", PlanRequirementKind.ViaProviderGate), // POST api/nayax-sales-sync

        // The self-service application runs with a denied scope and creates the business itself.
        new("OnboardingController.Apply", PlanRequirementKind.None), // POST api/onboarding/business-applications

        // Expenses: Free's own feature.
        new("OperatingExpensesController.Create", PlanRequirementKind.None), // POST api/operating-expenses
        new("OperatingExpensesController.CreateWithAttachment", PlanRequirementKind.None), // POST api/operating-expenses
        new("OperatingExpensesController.Delete", PlanRequirementKind.None), // DELETE api/operating-expenses/{id:int}
        new("OperatingExpensesController.Get", PlanRequirementKind.None), // GET api/operating-expenses
        new("OperatingExpensesController.GetAttachment", PlanRequirementKind.None), // GET api/operating-expenses/{id:int}/attachment
        new("OperatingExpensesController.GetById", PlanRequirementKind.None), // GET api/operating-expenses/{id:int}
        new("OperatingExpensesController.Update", PlanRequirementKind.None), // PUT api/operating-expenses/{id:int}
        new("OperatingExpensesController.UpdateWithAttachment", PlanRequirementKind.None), // PUT api/operating-expenses/{id:int}

        // The calculated pick-list matrix reads the fleet, so it is behind the credential gate and a
        // business without the provider is taken to manual entry. Recording a pick - including one
        // run across several machines - is Free's own feature and carries no requirement at all.
        new("PickListController.Get", PlanRequirementKind.ViaProviderGate), // GET api/pick-list
        new("PickListController.RecordMultiMachineRefill", PlanRequirementKind.None), // POST api/pick-list/multi-machine-refills
        new("PickListController.RecordRefill", PlanRequirementKind.None), // POST api/pick-list/refills

        // The price collector is an application with no membership and a denied scope.
        new("PriceCollectorController.Matches", PlanRequirementKind.None), // POST api/price-collector/matches
        new("PriceCollectorController.CompleteRun", PlanRequirementKind.None), // POST api/price-collector/runs/{runId:long}/complete
        new("PriceCollectorController.Results", PlanRequirementKind.None), // POST api/price-collector/results
        new("PriceCollectorController.SearchOutcome", PlanRequirementKind.None), // POST api/price-collector/work-items/{workItemId:long}/outcome
        new("PriceCollectorController.Work", PlanRequirementKind.None), // GET api/price-collector/work

        // Purchase cost history: this business's own records.
        new("ProductPurchaseCostsController.PurchaseCostSummary", PlanRequirementKind.None), // GET api/products/purchase-cost-summary

        // The catalogue, and the two listings that carry the reorder calculation: asked with
        // lowStockOnly, the product listing is the reorder-alert listing, so both are behind the
        // credential gate and answer in storage-only mode without the provider. Everything else
        // here is a local read or write, price tracking included (issue #977 decides that surface).
        new("ProductsController.Create", PlanRequirementKind.None), // POST api/Products
        new("ProductsController.Delete", PlanRequirementKind.None), // DELETE api/Products/{id:long}
        new("ProductsController.Get", PlanRequirementKind.None), // GET api/Products/{id:long}
        new("ProductsController.GetAll", PlanRequirementKind.ViaProviderGate), // GET api/Products
        new("ProductsController.GetGstRule", PlanRequirementKind.None), // GET api/Products/{id:long}/gst-rule
        new("ProductsController.GetPriceTracking", PlanRequirementKind.None), // GET api/Products/{id:long}/price-tracking
        new("ProductsController.InventoryValueSummary", PlanRequirementKind.None), // GET api/Products/inventory-value-summary
        new("ProductsController.LowStock", PlanRequirementKind.ViaProviderGate), // GET api/Products/alerts/low-stock
        new("ProductsController.PriceHistory", PlanRequirementKind.None), // GET api/Products/{id:long}/price-history
        new("ProductsController.PriceTracking", PlanRequirementKind.None), // GET api/Products/price-tracking
        new("ProductsController.SetGstRule", PlanRequirementKind.None), // PUT api/Products/{id:long}/gst-rule
        new("ProductsController.SetPriceTracking", PlanRequirementKind.None), // PUT api/Products/{id:long}/price-tracking
        new("ProductsController.Update", PlanRequirementKind.None), // PUT api/Products/{id:long}

        // Purchasing: Free's own feature, attachments included (the storage quota is issue #968).
        new("PurchasesController.AddFile", PlanRequirementKind.None), // POST api/purchases/{id:int}/file
        new("PurchasesController.Delete", PlanRequirementKind.None), // DELETE api/purchases/{id:int}
        new("PurchasesController.Get", PlanRequirementKind.None), // GET api/purchases/{id:int}
        new("PurchasesController.GetAll", PlanRequirementKind.None), // GET api/purchases
        new("PurchasesController.GetFile", PlanRequirementKind.None), // GET api/purchases/{id:int}/file
        new("PurchasesController.Update", PlanRequirementKind.None), // PUT api/purchases/{id:int}
        new("PurchasesController.Upload", PlanRequirementKind.None), // POST api/purchases

        // The reports. The two that need no provider are Starter; the other six are Pro; the export
        // takes the feature of whichever report it was asked for.
        new("ReportsController.Bookkeeping", PlanRequirementKind.Declared, PlanFeature.BookkeepingAndGstReports), // GET api/reports/bookkeeping
        new("ReportsController.Daily", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/daily
        new("ReportsController.Dashboard", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/dashboard
        new("ReportsController.Export", PlanRequirementKind.PerReport), // GET api/reports/{report}/export
        new("ReportsController.Gst", PlanRequirementKind.Declared, PlanFeature.BookkeepingAndGstReports), // GET api/reports/gst
        new("ReportsController.MachineProfitability", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/machine-profitability
        new("ReportsController.ProductProfitability", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/product-profitability
        new("ReportsController.Reconciliation", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/reconciliation
        new("ReportsController.Transactions", PlanRequirementKind.Declared, PlanFeature.ProviderSalesReports), // GET api/reports/transactions

        // Sale costing rebuilds this business's own stored cost history.
        new("SaleCostingController.ApplyNayaxCostBackfill", PlanRequirementKind.None), // POST api/sale-costing/nayax-cost-backfill/apply
        new("SaleCostingController.Backfill", PlanRequirementKind.None), // POST api/sale-costing/backfill
        new("SaleCostingController.DryRunNayaxCostBackfill", PlanRequirementKind.None), // POST api/sale-costing/nayax-cost-backfill/dry-run

        // The effective-dated processing-fee rates are stored configuration and make no Nayax call.
        new("SettingsController.Get", PlanRequirementKind.None), // GET api/settings/nayax-processing-fee-rates
        new("SettingsController.Save", PlanRequirementKind.None), // POST api/settings/nayax-processing-fee-rates

        // Site commissions: Pro throughout, declared on the controller. Nothing stored is removed
        // when a plan stops including it.
        new("SiteCommissionsController.Agreements", PlanRequirementKind.Declared, PlanFeature.SiteCommissions), // GET api/site-commissions/agreements
        new("SiteCommissionsController.Get", PlanRequirementKind.Declared, PlanFeature.SiteCommissions), // GET api/site-commissions
        new("SiteCommissionsController.RecordPayment", PlanRequirementKind.Declared, PlanFeature.SiteCommissions), // POST api/site-commissions/{siteId:long}/payments
        new("SiteCommissionsController.SaveAgreement", PlanRequirementKind.Declared, PlanFeature.SiteCommissions), // POST api/site-commissions/agreements

        // A site is known only from the provider's fleet read, so all three site reads are behind
        // the credential gate and already have their own no-provider answers.
        new("SitesController.GetAll", PlanRequirementKind.ViaProviderGate), // GET api/Sites
        new("SitesController.GetList", PlanRequirementKind.ViaProviderGate), // GET api/Sites/list
        new("SitesController.GetProducts", PlanRequirementKind.ViaProviderGate), // GET api/Sites/{id:long}/products

        // Storage stock and its history: Free's own feature.
        new("StockController.Adjust", PlanRequirementKind.None), // POST api/products/{productId:long}/stock
        new("StockController.GetRestockCostSuggestion", PlanRequirementKind.None), // GET api/products/{productId:long}/stock/restock-cost-suggestion
        new("StockController.History", PlanRequirementKind.None), // GET api/products/{productId:long}/stock
        new("StockHistoryController.Get", PlanRequirementKind.None), // GET api/stock-history

        // The platform-admin surfaces: a denied scope, and no business of their own to have a plan.
        new("SuperAdminBusinessesController.Approve", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/approve
        new("SuperAdminBusinessesController.Deactivate", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/deactivate
        new("SuperAdminBusinessesController.List", PlanRequirementKind.None), // GET api/super-admin/businesses
        new("SuperAdminBusinessesController.Members", PlanRequirementKind.None), // GET api/super-admin/businesses/{businessId:int}/members
        new("SuperAdminBusinessesController.PendingCount", PlanRequirementKind.None), // GET api/super-admin/businesses/pending-count
        new("SuperAdminBusinessesController.Reactivate", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/reactivate
        new("SuperAdminBusinessesController.Reject", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/reject
        new("SuperAdminBusinessesController.ReleaseMember", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/members/{membershipId:int}/release
        new("SuperAdminOptionVisibilityController.Put", PlanRequirementKind.None), // PUT api/super-admin/option-visibility
        new("SuperAdminPriceCatalogueReviewController.ConfirmAutomaticMatch", PlanRequirementKind.None), // POST api/super-admin/price-catalogue-review/automatic-matches/{suggestionId:long}/confirm
        new("SuperAdminPriceCatalogueReviewController.DecideListing", PlanRequirementKind.None), // POST api/super-admin/price-catalogue-review/listings/{supplierListingId:long}/decision
        new("SuperAdminPriceCatalogueReviewController.DecideMatch", PlanRequirementKind.None), // POST api/super-admin/price-catalogue-review/matches
        new("SuperAdminPriceCatalogueReviewController.Queue", PlanRequirementKind.None), // GET api/super-admin/price-catalogue-review
        new("SuperAdminPriceCatalogueReviewController.UndoAutomaticMatch", PlanRequirementKind.None), // POST api/super-admin/price-catalogue-review/automatic-matches/{suggestionId:long}/undo
        new("SuperAdminPlanGrantsController.End", PlanRequirementKind.None), // POST api/super-admin/businesses/{businessId:int}/plan-grant/end
        new("SuperAdminPlanGrantsController.Get", PlanRequirementKind.None), // GET api/super-admin/businesses/{businessId:int}/plan-grant
        new("SuperAdminPlanGrantsController.Put", PlanRequirementKind.None), // PUT api/super-admin/businesses/{businessId:int}/plan-grant
        new("SuperAdminSupplierPresetsController.Create", PlanRequirementKind.None), // POST api/super-admin/supplier-presets
        new("SuperAdminSupplierPresetsController.List", PlanRequirementKind.None), // GET api/super-admin/supplier-presets
        new("SuperAdminSupplierPresetsController.Update", PlanRequirementKind.None), // PUT api/super-admin/supplier-presets/{id:int}
        new("SuperAdminSupportSessionsController.Active", PlanRequirementKind.None), // GET api/super-admin/support-sessions/active
        new("SuperAdminSupportSessionsController.End", PlanRequirementKind.None), // POST api/super-admin/support-sessions/active/end
        new("SuperAdminSupportSessionsController.Start", PlanRequirementKind.None), // POST api/super-admin/support-sessions/{businessId:int}

        // Supplier orders and suppliers: Free's own feature.
        new("SupplierOrdersController.Cancel", PlanRequirementKind.None), // POST api/SupplierOrders/{id:int}/cancel
        new("SupplierOrdersController.CompletionPreview", PlanRequirementKind.None), // POST api/SupplierOrders/{id:int}/completion-preview
        new("SupplierOrdersController.Create", PlanRequirementKind.None), // POST api/SupplierOrders
        new("SupplierOrdersController.GetActive", PlanRequirementKind.None), // GET api/SupplierOrders
        new("SupplierOrdersController.GetById", PlanRequirementKind.None), // GET api/SupplierOrders/{id:int}
        new("SuppliersController.AddFromPreset", PlanRequirementKind.None), // POST api/Suppliers/available-presets/{presetId:int}
        new("SuppliersController.Create", PlanRequirementKind.None), // POST api/Suppliers
        new("SuppliersController.Delete", PlanRequirementKind.None), // DELETE api/Suppliers/{id:int}
        new("SuppliersController.Get", PlanRequirementKind.None), // GET api/Suppliers/{id:int}
        new("SuppliersController.GetAll", PlanRequirementKind.None), // GET api/Suppliers
        new("SuppliersController.GetAvailablePresets", PlanRequirementKind.None), // GET api/Suppliers/available-presets
        new("SuppliersController.GetGstDefaults", PlanRequirementKind.None), // GET api/Suppliers/{id:int}/gst-defaults
        new("SuppliersController.SetGstDefaults", PlanRequirementKind.None), // PUT api/Suppliers/{id:int}/gst-defaults
        new("SuppliersController.SetNotUsed", PlanRequirementKind.None), // PUT api/Suppliers/{id:int}/not-used
        new("SuppliersController.Update", PlanRequirementKind.None), // PUT api/Suppliers/{id:int}
    ];

    private static readonly Assembly ApiAssembly = typeof(Program).Assembly;

    private const BindingFlags ActionCandidates = BindingFlags.Public | BindingFlags.Instance;

    /// <summary>
    /// The rule itself: every action is in the inventory exactly once, and nothing in the inventory
    /// has stopped being an endpoint.
    /// </summary>
    [Fact]
    public void Every_endpoint_is_accounted_for_in_the_plan_requirement_inventory()
    {
        var actual = Endpoints().Select(endpoint => endpoint.Key).Order(StringComparer.Ordinal).ToArray();
        var declared = Inventory.Select(entry => entry.Endpoint).Order(StringComparer.Ordinal).ToArray();

        var missing = Endpoints()
            .Where(endpoint => !Inventory.Any(entry => entry.Endpoint == endpoint.Key))
            .Select(endpoint =>
                $"            new(\"{endpoint.Key}\", PlanRequirementKind.None), // {endpoint.HttpMethod} {endpoint.Route}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var stale = declared.Except(actual, StringComparer.Ordinal).ToArray();

        Assert.True(
            missing.Length == 0,
            "Every controller action must be accounted for in this file's plan-requirement "
                + "inventory, so a new endpoint cannot default to \"every plan includes it\" with "
                + "nothing in the diff saying so (AGENTS.md § Automated task contract, "
                + "docs/architecture.md § Plan enforcement). Decide what each of these needs and add "
                + $"it:\n{string.Join("\n", missing)}");
        Assert.True(
            stale.Length == 0,
            "The plan-requirement inventory names endpoint(s) that no longer exist. Remove them: "
                + $"{string.Join(", ", stale)}.");
    }

    /// <summary>
    /// The inventory names each endpoint once, so no decision can be recorded twice with two
    /// different answers.
    /// </summary>
    [Fact]
    public void No_endpoint_is_recorded_twice()
    {
        var duplicates = Inventory
            .GroupBy(entry => entry.Endpoint, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            duplicates.Length == 0,
            $"Each endpoint is recorded once. Repeated: {string.Join(", ", duplicates)}.");
    }

    /// <summary>
    /// The four kinds are the complete vocabulary. A fifth would be a new way for a plan to decide
    /// an endpoint, which is a decision to record here and in docs/architecture.md together rather
    /// than one that quietly appears - the rule
    /// <see cref="EndpointAuthorizationClassificationTests.Exactly_five_classes_are_recognised"/>
    /// applies to the role half.
    /// </summary>
    [Fact]
    public void Exactly_four_kinds_are_recognised()
    {
        Assert.Equal(
            [
                PlanRequirementKind.Declared,
                PlanRequirementKind.PerReport,
                PlanRequirementKind.ViaProviderGate,
                PlanRequirementKind.None,
            ],
            Enum.GetValues<PlanRequirementKind>());

        // And each of them is actually in use, so none of the branches below is dead.
        Assert.All(
            Enum.GetValues<PlanRequirementKind>(),
            kind => Assert.Contains(kind, Inventory.Select(entry => entry.Kind)));
    }

    /// <summary>
    /// Only a declared entry names a feature. An entry that recorded one beside "nothing is
    /// declared" would read as a requirement nothing enforces.
    /// </summary>
    [Fact]
    public void Only_a_declared_entry_names_a_feature() =>
        Assert.All(
            Inventory,
            entry => Assert.Equal(
                entry.Kind == PlanRequirementKind.Declared,
                entry.Feature is not null));

    /// <summary>
    /// Each entry matches what the endpoint actually declares, in both directions: a declared
    /// feature is the feature the attribute carries, and an entry that says nothing is declared
    /// carries neither attribute.
    /// </summary>
    [Fact]
    public void Every_entry_matches_what_the_endpoint_declares()
    {
        foreach (var endpoint in Endpoints())
        {
            var entry = Inventory.SingleOrDefault(candidate => candidate.Endpoint == endpoint.Key);
            if (entry is null)
            {
                // Reported by the rule above; this one is about the entries that exist.
                continue;
            }

            var declared = DeclaredFeaturesOf(endpoint);
            var perReport = HasAttribute<RequirePlanPerReportAttribute>(endpoint);

            switch (entry.Kind)
            {
                case PlanRequirementKind.Declared:
                    Assert.Equal([entry.Feature!.Value], declared);
                    Assert.False(perReport, $"{endpoint.Key} declares a feature and [RequirePlanPerReport].");
                    break;
                case PlanRequirementKind.PerReport:
                    Assert.True(perReport, $"{endpoint.Key} is recorded as per-report and declares no marker.");
                    Assert.Empty(declared);
                    break;
                default:
                    Assert.True(
                        declared.Count == 0 && !perReport,
                        $"{endpoint.Key} is recorded as needing no plan requirement and declares one.");
                    break;
            }
        }
    }

    /// <summary>
    /// The export's required feature comes from the same table the report actions' declared features
    /// are compared against, so a report and its own download can never need different plans - the
    /// drift the per-report marker exists to prevent.
    /// </summary>
    [Theory]
    [InlineData("bookkeeping", "ReportsController.Bookkeeping")]
    [InlineData("gst", "ReportsController.Gst")]
    [InlineData("daily", "ReportsController.Daily")]
    [InlineData("reconciliation", "ReportsController.Reconciliation")]
    [InlineData("machine-profitability", "ReportsController.MachineProfitability")]
    [InlineData("product-profitability", "ReportsController.ProductProfitability")]
    [InlineData("dashboard", "ReportsController.Dashboard")]
    [InlineData("transactions", "ReportsController.Transactions")]
    public void A_reports_export_needs_the_same_feature_as_the_report_itself(
        string reportName,
        string reportEndpoint)
    {
        var entry = Inventory.Single(candidate => candidate.Endpoint == reportEndpoint);

        Assert.Equal(PlanRequirementKind.Declared, entry.Kind);
        Assert.Equal(entry.Feature, ReportPlanRequirements.FeatureOf(reportName));
    }

    /// <summary>
    /// The issue's stored-data criterion, read back off the endpoints: the two provider-free reports
    /// are the Starter feature, and the other six reports, Site Commissions and the Nayax sales file
    /// import are Pro features. It is the acceptance criterion in one place, so a later change that
    /// moved one of them fails here and not only in the API tests.
    /// </summary>
    [Theory]
    [InlineData("ReportsController.Bookkeeping", SubscriptionPlan.Starter)]
    [InlineData("ReportsController.Gst", SubscriptionPlan.Starter)]
    [InlineData("ReportsController.Daily", SubscriptionPlan.Pro)]
    [InlineData("ReportsController.Transactions", SubscriptionPlan.Pro)]
    [InlineData("ReportsController.Reconciliation", SubscriptionPlan.Pro)]
    [InlineData("ReportsController.MachineProfitability", SubscriptionPlan.Pro)]
    [InlineData("ReportsController.ProductProfitability", SubscriptionPlan.Pro)]
    [InlineData("ReportsController.Dashboard", SubscriptionPlan.Pro)]
    [InlineData("SiteCommissionsController.Get", SubscriptionPlan.Pro)]
    [InlineData("SiteCommissionsController.Agreements", SubscriptionPlan.Pro)]
    [InlineData("SiteCommissionsController.SaveAgreement", SubscriptionPlan.Pro)]
    [InlineData("SiteCommissionsController.RecordPayment", SubscriptionPlan.Pro)]
    [InlineData("ImportsController.ImportNayaxSales", SubscriptionPlan.Pro)]
    [InlineData("NayaxConnectionController.Test", SubscriptionPlan.Pro)]
    [InlineData("NayaxConnectionController.Save", SubscriptionPlan.Pro)]
    [InlineData("NayaxConnectionController.Retest", SubscriptionPlan.Pro)]
    public void The_listed_endpoints_require_the_plan_the_issue_assigns_them(
        string endpointKey,
        SubscriptionPlan expected)
    {
        var entry = Inventory.Single(candidate => candidate.Endpoint == endpointKey);

        Assert.Equal(PlanRequirementKind.Declared, entry.Kind);
        Assert.Equal(expected, PlanFeaturePolicy.RequiredPlan(entry.Feature!.Value));
    }

    /// <summary>
    /// The criteria that are about an endpoint carrying <em>no</em> plan requirement, which are as
    /// much a part of the decision as the refusals: reading the Nayax connection status stays
    /// available on every plan and makes no Nayax call; the reimbursement XML import is not the
    /// Nayax sales file; manual pick-list entry - including one run across several machines - is
    /// Free's own feature; and so is the storage-only reorder listing.
    /// </summary>
    [Theory]
    [InlineData("NayaxConnectionController.Status")]
    [InlineData("ImportsController.ImportPendingXmlFiles")]
    [InlineData("PickListController.RecordRefill")]
    [InlineData("PickListController.RecordMultiMachineRefill")]
    public void The_listed_endpoints_are_included_in_every_plan(string endpointKey) =>
        Assert.Equal(
            PlanRequirementKind.None,
            Inventory.Single(candidate => candidate.Endpoint == endpointKey).Kind);

    /// <summary>
    /// The per-report marker is on exactly one endpoint. It is a mechanism, so a second use of it
    /// would be a second endpoint deciding its plan from a route value - which is a reviewed
    /// decision and not an implementation detail.
    /// </summary>
    [Fact]
    public void The_per_report_marker_is_on_exactly_the_reports_export()
    {
        var marked = Endpoints()
            .Where(HasAttribute<RequirePlanPerReportAttribute>)
            .Select(endpoint => endpoint.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["ReportsController.Export"], marked);
    }

    /// <summary>
    /// Every declared feature is a declared <see cref="PlanFeature"/>. The attribute refuses an
    /// undeclared value at construction, so this is the standing proof that the refusal is in force
    /// rather than a rule only its own test exercises.
    /// </summary>
    [Fact]
    public void Every_declared_feature_is_part_of_the_published_vocabulary()
    {
        var declared = Endpoints().SelectMany(DeclaredFeaturesOf).Distinct().ToArray();

        Assert.NotEmpty(declared);
        Assert.All(declared, feature => Assert.Contains(feature, PlanFeatures.All));
    }

    /// <summary>
    /// A plan requirement is an addition to an endpoint's authorization, never a replacement for it:
    /// every endpoint that declares one still declares a capability, so a plan cannot become a way
    /// to reach a surface a role does not hold.
    /// </summary>
    [Fact]
    public void Every_endpoint_with_a_plan_requirement_still_declares_a_capability() =>
        Assert.All(
            Endpoints().Where(endpoint =>
                DeclaredFeaturesOf(endpoint).Count > 0
                || HasAttribute<RequirePlanPerReportAttribute>(endpoint)),
            endpoint => Assert.True(
                endpoint.Method.GetCustomAttribute<RequireCapabilityAttribute>() is not null
                || endpoint.Controller.GetCustomAttribute<RequireCapabilityAttribute>(inherit: true) is not null,
                $"{endpoint.Key} declares a plan requirement and no capability."));

    #region Reading the endpoints

    private sealed record ApiEndpoint(Type Controller, MethodInfo Method, string Route, string HttpMethod)
    {
        /// <summary>
        /// The inventory's key: the controller and the action, which is unique even where two
        /// actions share a route and a verb (a JSON and a multipart overload of one operation).
        /// </summary>
        public string Key => $"{Controller.Name}.{Method.Name}";
    }

    private static List<ApiEndpoint> Endpoints() =>
        Controllers()
            .SelectMany(type => ActionCandidatesOf(type)
                .SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()
                    .Select(http => new ApiEndpoint(
                        type,
                        method,
                        RouteOf(type, http),
                        http.HttpMethods.FirstOrDefault() ?? "GET"))))
            .ToList();

    private static IEnumerable<Type> Controllers() =>
        ApiAssembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract);

    private static IEnumerable<MethodInfo> ActionCandidatesOf(Type controller) =>
        controller.GetMethods(ActionCandidates)
            .Where(method => method.DeclaringType is { } declaring
                && declaring.Assembly == ApiAssembly
                && typeof(ControllerBase).IsAssignableFrom(declaring))
            .Where(method => !method.IsSpecialName)
            .Where(method => !method.GetCustomAttributes<NonActionAttribute>().Any());

    private static string RouteOf(Type controller, HttpMethodAttribute http)
    {
        var controllerTemplate = (controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty)
            .Replace(
                "[controller]",
                controller.Name.EndsWith("Controller", StringComparison.Ordinal)
                    ? controller.Name[..^"Controller".Length]
                    : controller.Name,
                StringComparison.Ordinal);

        return string.Join('/', new[] { controllerTemplate, http.Template }
            .Where(segment => !string.IsNullOrEmpty(segment))
            .Select(segment => segment!.Trim('/')));
    }

    /// <summary>
    /// Every feature the endpoint declares: the action's own and its controller's, because filters
    /// accumulate down the endpoint and both are enforced.
    /// </summary>
    private static List<PlanFeature> DeclaredFeaturesOf(ApiEndpoint endpoint) =>
    [
        .. new[]
        {
            endpoint.Method.GetCustomAttribute<RequirePlanAttribute>(),
            endpoint.Controller.GetCustomAttribute<RequirePlanAttribute>(inherit: true),
        }
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Feature)
            .Distinct(),
    ];

    private static bool HasAttribute<TAttribute>(ApiEndpoint endpoint)
        where TAttribute : Attribute =>
        endpoint.Method.GetCustomAttributes<TAttribute>().Any()
        || endpoint.Controller.GetCustomAttributes<TAttribute>(inherit: true).Any();

    #endregion
}
