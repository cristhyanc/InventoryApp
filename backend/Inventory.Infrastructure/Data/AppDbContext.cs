using System.Linq.Expressions;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Domain.MachineStock;
using Inventory.Domain.PriceCatalogue;
using Inventory.Domain.Providers;
using Inventory.Domain.Stock;
using Inventory.Domain.Subscriptions;
using Inventory.Domain.Suppliers;
using Inventory.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Inventory.Infrastructure.Models;

namespace Inventory.Infrastructure.Data;

public class AppDbContext : DbContext
{
    private readonly IBusinessScope _businessScope;

    /// <summary>
    /// Constructs a context with no business resolved, which is the fail-closed state: every
    /// tenant-owned read returns nothing and every tenant-owned write is refused.
    ///
    /// Unrestricted, all-business access is never the default. Code that genuinely needs it -
    /// controlled setup, migrations, cross-business maintenance - must say so by passing
    /// <see cref="UnscopedBusinessScope.Instance"/> to the other constructor, so every such
    /// place is greppable and reviewable rather than implied by which overload was convenient.
    /// </summary>
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : this(options, new BusinessScope())
    {
    }

    public AppDbContext(DbContextOptions<AppDbContext> options, IBusinessScope businessScope)
        : base(options)
    {
        _businessScope = businessScope;
    }

    /// <summary>
    /// Read by the global query filters below. It is an instance member rather than a captured
    /// constant so EF re-evaluates it per query instead of baking the first request's business
    /// into the compiled model.
    ///
    /// A denied scope yields <c>null</c>, and the filters compare against it with <c>==</c>, so
    /// an unresolved caller matches no row at all. Failing closed here is deliberate: the
    /// alternative - treating "no business" as "no filter" - would turn a resolution bug into a
    /// silent cross-business data leak.
    /// </summary>
    public int? CurrentBusinessId => _businessScope.BusinessId;

    public bool TenantFilteringEnabled => _businessScope.State != BusinessScopeState.Unscoped;

    /// <summary>
    /// Exposed for the SaveChanges enforcement in <see cref="BusinessOwnershipEnforcer"/>, which
    /// needs the same answer this context filters reads by.
    /// </summary>
    internal IBusinessScope BusinessScope => _businessScope;

    /// <summary>
    /// The business a <see cref="BeginSupplierProvisioning"/> lease is currently open for, and
    /// <c>null</c> - which is every other moment of this context's life - when none is (issue #849).
    ///
    /// <para>Read by <see cref="BusinessOwnershipEnforcer"/>, and only in the branch it takes for a
    /// <em>denied</em> scope, where its answer today is "no tenant-owned write at all". It is not
    /// read by <see cref="CurrentBusinessId"/> or <see cref="TenantFilteringEnabled"/> on purpose: a
    /// lease grants the ownership stamp for new rows of one business and grants no <em>read</em>,
    /// so while one is open every tenant-owned query still matches nothing, exactly as it does for
    /// the rest of the request.</para>
    /// </summary>
    internal int? SupplierProvisioningBusinessId { get; private set; }

    /// <summary>
    /// Opens the one provisioning window in this application: until the returned lease is disposed,
    /// <see cref="BusinessOwnershipEnforcer"/> accepts <em>new <see cref="Supplier"/> rows of
    /// <paramref name="businessId"/> and nothing else</em> on an otherwise denied scope (issue #849,
    /// child C of #835).
    ///
    /// <para><strong>Why a window exists at all.</strong> Approving a business application runs as
    /// the platform administrator, who is a member of no business, so the request carries a denied
    /// <see cref="BusinessScope"/> and the enforcer refuses every tenant-owned write - which is what
    /// makes the platform-admin surface unable to touch a business's own data. The new business's own
    /// copies of the supplier presets that serve it are nevertheless tenant-owned rows that have to be
    /// written in that same transaction, because a business that was approved but did not get them
    /// would be a half-finished tenant. This is the narrow, reviewed exception rather than a
    /// relaxation of the rule: see docs/architecture.md § Supplier presets copied into an approved
    /// business.</para>
    ///
    /// <para><strong>What keeps it narrow.</strong> It is <c>internal</c>, so only this assembly can
    /// take one, and the only caller is <c>EfBusinessSupplierProvisioningStore</c>, which takes it
    /// around one <c>SaveChanges</c>; the business id is the key of the row the approval's own write
    /// transaction read, never a value a request supplied; a lease may be taken only while this
    /// context's own scope is <see cref="BusinessScopeState.Denied"/>, so neither an ordinary
    /// member's request nor a support session can open one; only one may be open at a time; and the
    /// enforcer still refuses every update, every delete and every other entity type while it is
    /// open.</para>
    /// </summary>
    /// <param name="businessId">The business whose new supplier rows may be stamped.</param>
    /// <returns>The lease. Disposing it closes the window; it is never left open across a request.</returns>
    /// <exception cref="InvalidOperationException">
    /// This context has a resolved, support-session or unscoped scope, or a lease is already open.
    /// </exception>
    internal IDisposable BeginSupplierProvisioning(int businessId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(businessId, 0);

        if (_businessScope.State != BusinessScopeState.Denied)
        {
            throw new InvalidOperationException(
                "Supplier provisioning is only available to a request that resolved no business of "
                    + "its own - the platform administrator approving an application. A scope that "
                    + "already names a business must write through the ordinary tenant path.");
        }

        if (SupplierProvisioningBusinessId is not null)
        {
            throw new InvalidOperationException(
                "A supplier provisioning lease is already open on this context.");
        }

        SupplierProvisioningBusinessId = businessId;
        return new SupplierProvisioningLease(this);
    }

    /// <summary>
    /// Closes the provisioning window of <see cref="BeginSupplierProvisioning"/>. A struct would be
    /// copied by the <c>using</c> statement, so this is a sealed class with one field.
    /// </summary>
    private sealed class SupplierProvisioningLease : IDisposable
    {
        private AppDbContext? _context;

        public SupplierProvisioningLease(AppDbContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            if (_context is null)
            {
                return;
            }

            _context.SupplierProvisioningBusinessId = null;
            _context = null;
        }
    }

    /// <summary>
    /// The business a <see cref="BeginPlanGrantWrite"/> lease is currently open for, and <c>null</c> -
    /// which is every other moment of this context's life - when none is (issue #982).
    ///
    /// <para>Read by <see cref="BusinessOwnershipEnforcer"/>, and only in the branch it takes for a
    /// <em>denied</em> scope. Like the provisioning lease above it is deliberately not read by
    /// <see cref="CurrentBusinessId"/> or <see cref="TenantFilteringEnabled"/>: a lease grants a
    /// write and never a read, so while one is open every tenant-owned query still matches nothing
    /// and the one adapter that reads these two tables under a denied scope does so through an
    /// explicit, business-pinned <c>IgnoreQueryFilters</c> statement that a reviewer can see.</para>
    /// </summary>
    internal int? PlanGrantWriteBusinessId { get; private set; }

    /// <summary>
    /// Opens the manual plan grant window: until the returned lease is disposed,
    /// <see cref="BusinessOwnershipEnforcer"/> accepts <em>that one business's
    /// <see cref="BusinessPlanGrant"/> row and the <see cref="BusinessPlanGrantAudit"/> row beside
    /// it, and nothing else</em> on an otherwise denied scope (issue #982, the owner-approved
    /// amendment of 29 October 2026).
    ///
    /// <para><strong>Why a window exists at all.</strong> A manual grant is the Super Admin's
    /// decision about a business they are not a member of, so the request carries a denied
    /// <see cref="BusinessScope"/> and the enforcer refuses every tenant-owned write - which is what
    /// makes the platform-admin surface unable to touch a business's own data. A plan grant is
    /// nevertheless a tenant-owned row of issue #966, and the platform owner has to be able to
    /// grandfather, trial and comp a business without billing it. This is the narrow, reviewed
    /// exception rather than a relaxation of the rule: see docs/architecture.md § Super Admin sets a
    /// business's plan.</para>
    ///
    /// <para><strong>What keeps it narrow.</strong> It is <c>internal</c>, so only this assembly can
    /// take one, and the only caller is <c>EfPlatformPlanGrantStore</c>, which takes it around one
    /// <c>SaveChanges</c>; two entity types and no other, inserts and - for the grant row alone -
    /// updates, never a delete; the business id is the key of the row the decision's own write
    /// transaction read, never a value a request supplied; a lease may be taken only while this
    /// context's own scope is <see cref="BusinessScopeState.Denied"/>, so neither an ordinary
    /// member's request nor a support session can open one; and only one may be open at a time.</para>
    /// </summary>
    /// <param name="businessId">The business whose grant and audit rows may be written.</param>
    /// <returns>The lease. Disposing it closes the window; it is never left open across a request.</returns>
    /// <exception cref="InvalidOperationException">
    /// This context has a resolved, support-session or unscoped scope, or a lease is already open.
    /// </exception>
    internal IDisposable BeginPlanGrantWrite(int businessId)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(businessId, 0);

        if (_businessScope.State != BusinessScopeState.Denied)
        {
            throw new InvalidOperationException(
                "A manual plan grant is only written by a request that resolved no business of its "
                    + "own - the platform administrator deciding it. A scope that already names a "
                    + "business must not write another business's grant at all.");
        }

        if (PlanGrantWriteBusinessId is not null)
        {
            throw new InvalidOperationException("A plan grant write lease is already open on this context.");
        }

        PlanGrantWriteBusinessId = businessId;
        return new PlanGrantWriteLease(this);
    }

    /// <summary>
    /// Closes the window of <see cref="BeginPlanGrantWrite"/>. A sealed class with one field, for the
    /// reason <see cref="SupplierProvisioningLease"/> is one: a struct would be copied by the
    /// <c>using</c> statement.
    /// </summary>
    private sealed class PlanGrantWriteLease : IDisposable
    {
        private AppDbContext? _context;

        public PlanGrantWriteLease(AppDbContext context)
        {
            _context = context;
        }

        public void Dispose()
        {
            if (_context is null)
            {
                return;
            }

            _context.PlanGrantWriteBusinessId = null;
            _context = null;
        }
    }

    public DbSet<Business> Businesses => Set<Business>();

    /// <summary>
    /// Operational audit of the tenancy backfill. Not business data and deliberately not
    /// tenant-filtered; see BusinessBackfillAudit.
    /// </summary>
    public DbSet<BusinessBackfillAudit> BusinessBackfillAudits => Set<BusinessBackfillAudit>();

    public DbSet<BusinessMembership> BusinessMemberships => Set<BusinessMembership>();

    /// <summary>
    /// The single-use invite links an Owner creates for their own business (issue #531). Ordinary
    /// tenant-owned data, unlike the membership table above, and the token is stored only as a
    /// hash; see <see cref="MemberInvite"/>.
    /// </summary>
    public DbSet<MemberInvite> MemberInvites => Set<MemberInvite>();

    /// <summary>
    /// The platform administrator's time-limited, audited read-only support sessions (issue #550).
    /// Structurally non-tenant-owned like the two tables above, and the session id is stored only as
    /// a hash; see <see cref="SupportSession"/>.
    /// </summary>
    public DbSet<SupportSession> SupportSessions => Set<SupportSession>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Product> Products => Set<Product>();

    /// <summary>
    /// The machines this business operates, as rows this application owns (issue #740). Ordinary
    /// tenant-owned data; see <see cref="LocalMachine"/> for why a machine is persisted at all now
    /// and why its provider id sits on the row.
    /// </summary>
    public DbSet<LocalMachine> Machines => Set<LocalMachine>();

    /// <summary>
    /// Each provider's own identifier for a local product, and the same for a category (issue
    /// #732). Ordinary tenant-owned data; see <see cref="ProductExternalId"/> for why a provider id
    /// lives beside the local row instead of being its key.
    /// </summary>
    public DbSet<ProductExternalId> ProductExternalIds => Set<ProductExternalId>();
    public DbSet<CategoryExternalId> CategoryExternalIds => Set<CategoryExternalId>();

    /// <summary>
    /// What each fully successful provider sync or committed import actually accounted for (issue
    /// #730). Append-only, overlapping by design, and the one evidence a report has for telling an
    /// empty period apart from an unread one; see <see cref="ProviderDataCoverage"/>.
    /// </summary>
    public DbSet<ProviderDataCoverage> ProviderDataCoverages => Set<ProviderDataCoverage>();
    public DbSet<StockAdjustment> StockAdjustments => Set<StockAdjustment>();

    /// <summary>
    /// The identity of each batch refill recorded from the pick list (issue #743): which submission
    /// recorded which movements, so a retry is answered with the original result instead of
    /// deducting storage twice. The movements themselves are ordinary
    /// <see cref="StockAdjustment"/> rows; see <see cref="PickListRefill"/>.
    /// </summary>
    public DbSet<PickListRefill> PickListRefills => Set<PickListRefill>();

    public DbSet<PickListRefillLine> PickListRefillLines => Set<PickListRefillLine>();
    public DbSet<Purchase> Receipts => Set<Purchase>();
    public DbSet<NayaxSales> NayaxSales => Set<NayaxSales>();

    /// <summary>
    /// Sales whose two candidate products disagreed when issue #758 resolved every existing sale's
    /// local product. Append-only evidence; a conflicting sale stays unresolved until an audited
    /// human decision names the product - see <see cref="NayaxSaleProductConflict"/>.
    /// </summary>
    public DbSet<NayaxSaleProductConflict> NayaxSaleProductConflicts => Set<NayaxSaleProductConflict>();

    /// <summary>
    /// The audit trail of every sale whose local product was filled in after the fact (issue #758):
    /// the migration's one-off name resolution and each audited operator decision. Append-only; see
    /// <see cref="NayaxSaleProductResolution"/>.
    /// </summary>
    public DbSet<NayaxSaleProductResolution> NayaxSaleProductResolutions => Set<NayaxSaleProductResolution>();
    public DbSet<NayaxMachineStockEvent> NayaxMachineStockEvents => Set<NayaxMachineStockEvent>();

    /// <summary>
    /// Append-only physical counts of what is in a vending machine (issue #56), the baseline the
    /// machine-stock ledger counts forward from. Ordinary tenant-owned data; nothing in the
    /// application updates or deletes a row in this set, and recording one changes nothing else -
    /// see <see cref="MachineStockCount"/>.
    /// </summary>
    public DbSet<MachineStockCount> MachineStockCounts => Set<MachineStockCount>();
    public DbSet<ImportedFile> ImportedFiles => Set<ImportedFile>();
    public DbSet<ImportedReimbursement> ImportedReimbursements => Set<ImportedReimbursement>();
    public DbSet<ImportedReimbursementDevice> ImportedReimbursementDevices => Set<ImportedReimbursementDevice>();
    public DbSet<ImportedDevicePayment> ImportedDevicePayments => Set<ImportedDevicePayment>();
    public DbSet<ImportedFee> ImportedFees => Set<ImportedFee>();
    public DbSet<ImportedPaymentMethod> ImportedPaymentMethods => Set<ImportedPaymentMethod>();
    public DbSet<PurchaseItem> ReceiptItems => Set<PurchaseItem>();
    public DbSet<SupplierOrder> SupplierOrders => Set<SupplierOrder>();
    public DbSet<SupplierOrderLine> SupplierOrderLines => Set<SupplierOrderLine>();
    public DbSet<SupplierOrderReceiptAllocation> SupplierOrderReceiptAllocations => Set<SupplierOrderReceiptAllocation>();
    public DbSet<OperatingExpense> OperatingExpenses => Set<OperatingExpense>();
    public DbSet<NayaxProcessingFeeRate> NayaxProcessingFeeRates => Set<NayaxProcessingFeeRate>();

    /// <summary>
    /// Each business's own Nayax Lynx credentials, with the access token encrypted at rest and the
    /// id of the key that encrypted it (issue #518). Exactly one row per business; see
    /// <see cref="BusinessNayaxConnection"/>.
    /// </summary>
    public DbSet<BusinessNayaxConnection> BusinessNayaxConnections => Set<BusinessNayaxConnection>();

    /// <summary>
    /// Each business's one short-lived, encrypted Nayax connection-test draft (issue #536). Not the
    /// accepted connection above and never read by a Nayax call; see
    /// <see cref="NayaxConnectionTestDraft"/>.
    /// </summary>
    public DbSet<NayaxConnectionTestDraft> NayaxConnectionTestDrafts => Set<NayaxConnectionTestDraft>();
    public DbSet<SiteCommissionAgreement> SiteCommissionAgreements => Set<SiteCommissionAgreement>();
    public DbSet<CommissionPayment> CommissionPayments => Set<CommissionPayment>();
    public DbSet<InventoryCostTransitionBaseline> InventoryCostTransitionBaselines => Set<InventoryCostTransitionBaseline>();
    public DbSet<InventoryCostTransitionMachineStock> InventoryCostTransitionMachineStocks => Set<InventoryCostTransitionMachineStock>();
    public DbSet<InventoryCostTransitionPreviewDraft> InventoryCostTransitionPreviewDrafts => Set<InventoryCostTransitionPreviewDraft>();

    /// <summary>
    /// Append-only costing-only historical repairs (issue #359). Nothing in the application updates
    /// or deletes a row in this set; see <see cref="InventoryCostRepair"/>.
    /// </summary>
    public DbSet<InventoryCostRepair> InventoryCostRepairs => Set<InventoryCostRepair>();

    /// <summary>
    /// Append-only records of stored Nayax sale instants repaired from authoritative source evidence
    /// (issue #472). Nothing in the application updates or deletes a row in this set; see
    /// <see cref="NayaxSaleTimestampRepair"/>.
    /// </summary>
    public DbSet<NayaxSaleTimestampRepair> NayaxSaleTimestampRepairs => Set<NayaxSaleTimestampRepair>();

    /// <summary>The stored, single-use, expiring plans those repairs are confirmed from (issue #472).</summary>
    public DbSet<NayaxSaleTimestampRepairPreviewDraft> NayaxSaleTimestampRepairPreviewDrafts =>
        Set<NayaxSaleTimestampRepairPreviewDraft>();

    /// <summary>
    /// The provider catalogue reviews a link-or-create apply is authorised by (issue #738). At most
    /// one of a business's rows is pending; an applied one is the record of what was decided and is
    /// never rewritten. See <see cref="ConfigureProviderCatalogueReview"/>.
    /// </summary>
    public DbSet<ProviderCatalogueReview> ProviderCatalogueReviews => Set<ProviderCatalogueReview>();

    /// <summary>The provider products and groups inside those reviews (issue #738).</summary>
    public DbSet<ProviderCatalogueReviewItem> ProviderCatalogueReviewItems =>
        Set<ProviderCatalogueReviewItem>();

    /// <summary>
    /// The last provider value each negotiable catalogue field was decided about (issue #738) - the
    /// rows that stop a skipped value being proposed again until the provider changes it.
    /// </summary>
    public DbSet<ProviderCatalogueFieldDecision> ProviderCatalogueFieldDecisions =>
        Set<ProviderCatalogueFieldDecision>();

    /// <summary>
    /// The platform-wide suggested suppliers a Super Admin keeps (issue #848). The one
    /// <em>platform</em> table in this schema: it belongs to no business and carries no business key
    /// at all, which is a different classification from the five structurally non-tenant-owned
    /// tables above; see <see cref="SupplierPreset"/>.
    /// </summary>
    public DbSet<SupplierPreset> SupplierPresets => Set<SupplierPreset>();

    /// <summary>
    /// The single shared row that says whether the one switchable Admin option is offered to anybody
    /// but a Super Admin (issue #873). The second <em>platform</em> table, on the same terms as
    /// <see cref="SupplierPresets"/> above: about no business, so no business key, no index on one
    /// and no query filter. No row at all means every option is offered; see
    /// <see cref="PlatformOptionVisibility"/>.
    /// </summary>
    public DbSet<Models.PlatformOptionVisibility> PlatformOptionVisibility =>
        Set<Models.PlatformOptionVisibility>();

    /// <summary>
    /// The shared catalogue's products (issue #923, parent #817): one row per product that is looked
    /// up at a supplier, however many businesses want its price. The third <em>platform</em> table,
    /// on the terms <see cref="SupplierPresets"/> states - about no business, so no business key, no
    /// index on one and no query filter. See <see cref="MasterProduct"/>.
    /// </summary>
    public DbSet<MasterProduct> MasterProducts => Set<MasterProduct>();

    /// <summary>
    /// One product as one supplier publishes it (issue #923). The fourth platform table; see
    /// <see cref="SupplierListing"/>.
    /// </summary>
    public DbSet<SupplierListing> SupplierListings => Set<SupplierListing>();

    /// <summary>
    /// The suggested matches, per waiting key, that the automatic matcher of #941 produces and the
    /// review of #928 decides (issue #923). The fifth platform table, and the one nothing ever
    /// deletes from; see <see cref="ProductMatchSuggestion"/>.
    /// </summary>
    public DbSet<ProductMatchSuggestion> ProductMatchSuggestions => Set<ProductMatchSuggestion>();

    /// <summary>
    /// Each business's own statement that it wants one of its products priced, and which master
    /// product that product is (issue #923). Ordinary tenant-owned data - stamped, filtered and
    /// enforced centrally like every other owned table - and the only place a business appears in the
    /// shared catalogue at all; see <see cref="ProductPriceTracking"/>.
    /// </summary>
    public DbSet<ProductPriceTracking> ProductPriceTrackings => Set<ProductPriceTracking>();

    /// <summary>
    /// One run of the supplier price collector (issue #925, parent #817): when it started, when it
    /// finished, how it finished and how much it accounted for. The sixth platform table, on the
    /// terms <see cref="SupplierPresets"/> states - a run reads suppliers' shelves for every business
    /// at once, so it is about none of them. See <see cref="PriceCollectorRun"/>.
    /// </summary>
    public DbSet<PriceCollectorRun> PriceCollectorRuns => Set<PriceCollectorRun>();

    /// <summary>
    /// One price one supplier published for one listing at one instant (issue #925). The seventh
    /// platform table, append-only, and read by no purchase, cost, inventory, GST-accounting or
    /// reporting code at all; see <see cref="SupplierPriceObservation"/>.
    /// </summary>
    public DbSet<SupplierPriceObservation> SupplierPriceObservations =>
        Set<SupplierPriceObservation>();

    /// <summary>
    /// One product and one supplier a collector run was asked to search (issue #924, parent #817).
    /// The eighth platform table, on the terms <see cref="SupplierPresets"/> states - a piece of work
    /// is a product description and a supplier's shelf, so it is about no business - and the thing
    /// every result of #937 is attributed to. See <see cref="PriceCollectorWorkItem"/>.
    /// </summary>
    public DbSet<PriceCollectorWorkItem> PriceCollectorWorkItems => Set<PriceCollectorWorkItem>();

    /// <summary>
    /// One result row a collector submitted and what was done with it (issue #937, parent #817). The
    /// ninth platform table, on the terms <see cref="SupplierPresets"/> states, and the store of the
    /// idempotency key the results endpoint answers a repeat from. See
    /// <see cref="PriceCollectorResultRow"/>.
    /// </summary>
    public DbSet<PriceCollectorResultRow> PriceCollectorResultRows => Set<PriceCollectorResultRow>();

    /// <summary>
    /// The latest reading of one listing that was not accepted as its price (issue #937). The tenth
    /// platform table, one row per listing, kept apart from <see cref="SupplierPriceObservations"/>
    /// so every row of <em>that</em> table stays an accepted price. See
    /// <see cref="SupplierListingPendingObservation"/>.
    /// </summary>
    public DbSet<SupplierListingPendingObservation> SupplierListingPendingObservations =>
        Set<SupplierListingPendingObservation>();

    /// <summary>
    /// Each business's paid subscription (issue #966), written only by a verified billing event
    /// (#969). Ordinary tenant-owned data - one row per business, filtered and stamped centrally -
    /// and deliberately a different table from <see cref="BusinessPlanGrants"/>, because neither
    /// record may ever write the other. See <see cref="BusinessBillingSubscription"/>.
    /// </summary>
    public DbSet<BusinessBillingSubscription> BusinessBillingSubscriptions =>
        Set<BusinessBillingSubscription>();

    /// <summary>
    /// The plans granted to a business by a person rather than a payment (issue #966): the
    /// grandfathered Pro grant, and the complimentary grants a Super Admin decides (#982). Ordinary
    /// tenant-owned data, with at most one grandfathered grant per business. See
    /// <see cref="BusinessPlanGrant"/>.
    /// </summary>
    public DbSet<BusinessPlanGrant> BusinessPlanGrants => Set<BusinessPlanGrant>();

    /// <summary>
    /// Every decision a Super Admin has made about a business's manual grant (issue #982): who
    /// granted, changed or ended what, and why. Ordinary tenant-owned data, append-only, and written
    /// in the same <c>SaveChanges</c> as the grant it is about - so the grant and the record of who
    /// gave it commit together or not at all. See <see cref="BusinessPlanGrantAudit"/>.
    /// </summary>
    public DbSet<BusinessPlanGrantAudit> BusinessPlanGrantAudits => Set<BusinessPlanGrantAudit>();

    /// <summary>
    /// The single shared row holding the grandfathering step's fixed cutoff and its completion marker
    /// (issue #966). The eleventh <em>platform</em> table, on the terms
    /// <see cref="SupplierPresets"/> states: about no business, so no business key, no index on one
    /// and no query filter. No row at all means the step has never run, which means nothing is
    /// enforced; see <see cref="SubscriptionGrandfathering"/>.
    /// </summary>
    public DbSet<SubscriptionGrandfathering> SubscriptionGrandfatherings =>
        Set<SubscriptionGrandfathering>();

    /// <summary>
    /// Both save paths funnel through <see cref="BusinessOwnershipEnforcer"/> so tenant
    /// ownership is applied to every write, whichever overload a service happens to call. This
    /// is why services do not, and must not, add their own business filters or stamping.
    ///
    /// <see cref="BusinessTimeZoneEnforcer"/> joins it for the same reason (issue #499): a
    /// business's time zone is what every business date is derived from, so an unresolvable one is
    /// refused on whichever path writes it rather than per write path.
    ///
    /// <see cref="BusinessAbnEnforcer"/> joins them on the same terms (issue #540): the business's
    /// ABN appears on tax invoices and in the GST accounting aid, so a value that cannot be an ABN
    /// is refused centrally rather than per write path.
    ///
    /// <see cref="BusinessStatusEnforcer"/> joins them for the strongest of those reasons (issue
    /// #584): the business's lifecycle status is what decides whether its data may be reached at
    /// all, so a status nobody declared is refused here rather than stored as a business whose
    /// access state nobody chose.
    ///
    /// <see cref="BusinessSalesProviderEnforcer"/> joins them on the same terms (issue #759): the
    /// business's sales provider is the issuer of every external id its products and categories
    /// hold, so a change to it - which would reinterpret those ids as another system's - is refused
    /// centrally rather than trusted not to be written.
    ///
    /// <see cref="BusinessStateEnforcer"/> joins them on the same terms as the ABN (issue #933): the
    /// business's state or territory decides which supplier records it is offered, so a value
    /// outside the eight is refused centrally rather than per write path.
    /// </summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BusinessOwnershipEnforcer.Enforce(this);
        BusinessTimeZoneEnforcer.Enforce(this);
        BusinessAbnEnforcer.Enforce(this);
        BusinessStateEnforcer.Enforce(this);
        BusinessStatusEnforcer.Enforce(this);
        BusinessSalesProviderEnforcer.Enforce(this);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        BusinessOwnershipEnforcer.Enforce(this);
        BusinessTimeZoneEnforcer.Enforce(this);
        BusinessAbnEnforcer.Enforce(this);
        BusinessStateEnforcer.Enforce(this);
        BusinessStatusEnforcer.Enforce(this);
        BusinessSalesProviderEnforcer.Enforce(this);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureTenancy(modelBuilder);
        ConfigureMemberInvites(modelBuilder);
        ConfigureSupportSessions(modelBuilder);
        ConfigureSupplierPresets(modelBuilder);
        ConfigurePlatformOptionVisibility(modelBuilder);
        ConfigureSubscriptions(modelBuilder);
        ConfigurePriceCatalogue(modelBuilder);
        ConfigureProviderLinks(modelBuilder);
        ConfigureProviderIdentityColumns(modelBuilder);
        ConfigureMachines(modelBuilder);
        ConfigureProviderDataCoverage(modelBuilder);
        ConfigureSaleProductAssociation(modelBuilder);
        ConfigureProviderCatalogueReview(modelBuilder);

        modelBuilder.Entity<Product>()
            .Property(p => p.AverageUnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<Product>()
            .Property(p => p.InventoryValue)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .Property(x => x.AverageUnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .Property(x => x.InventoryValue)
            .HasColumnType("decimal(18,6)");
        // One baseline per product, scoped by business. A product cannot span businesses, so
        // this is exactly as strong as the previous product-only constraint - but it states the
        // tenant rule explicitly and lets the index lead with the column every query filters on.
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .HasIndex(x => new { x.BusinessId, x.ProductId })
            .IsUnique();
        modelBuilder.Entity<InventoryCostTransitionBaseline>()
            .HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<InventoryCostTransitionMachineStock>()
            .HasOne(x => x.InventoryCostTransitionBaseline)
            .WithMany(x => x.MachineStocks)
            .HasForeignKey(x => x.InventoryCostTransitionBaselineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Costing repairs (issue #359). Deliberately no unique constraint: a product may need more
        // than one repair, including two at the same instant, and the replay orders them by key.
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.UnitCost)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.TotalValue)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.Reason).IsRequired();
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.CreatedByDirectoryTenantId).IsRequired();
        modelBuilder.Entity<InventoryCostRepair>().Property(x => x.CreatedByObjectId).IsRequired();
        // The replay's load path: one product's repairs, in effective-time order, within a business.
        modelBuilder.Entity<InventoryCostRepair>()
            .HasIndex(x => new { x.BusinessId, x.ProductId, x.EffectiveAt });
        modelBuilder.Entity<InventoryCostRepair>()
            .HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => x.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        // EffectiveAt and CreatedAt are persisted UTC instants, and the SQLite provider does not
        // round-trip DateTimeKind - see the StockAdjustment.CreatedAt comment below for the complete
        // explanation. Marking them UTC on every read keeps the instant the API boundary exposes
        // unambiguous without changing the stored bytes or any comparison semantics, so the replay's
        // ordering against stock movements and sales is unaffected.
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.EffectiveAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<InventoryCostRepair>()
            .Property(x => x.CreatedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // Nayax sale timestamp repairs (issue #472). A transaction may legitimately be repaired more
        // than once - a later, better source can correct an earlier correction - so the audit has no
        // unique constraint, and the index is the read path: one business's repairs, newest applied
        // first, with a transaction lookup leading on the column every query filters on.
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.EvidenceReference).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.AppliedByDirectoryTenantId).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>().Property(x => x.AppliedByObjectId).IsRequired();
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .HasIndex(x => new { x.BusinessId, x.TransactionId, x.AppliedAt });
        // The three instants are persisted UTC and are exposed by the repair API, so each is marked
        // UTC on read - see the StockAdjustment.CreatedAt comment below for the complete explanation
        // of why the SQLite provider makes that necessary. The two business dates are deliberately
        // Kind-free calendar dates and are not converted, so the audit never serialises a date as an
        // instant.
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.PreviousInstantUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.RepairedInstantUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<NayaxSaleTimestampRepair>()
            .Property(x => x.AppliedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // The stored plan is server-internal state, never part of an API response, so it needs no
        // Kind conversion: its two instants are only ever compared against the clock, and a DateTime
        // comparison looks at ticks and not Kind. This matches InventoryCostTransitionPreviewDraft.
        modelBuilder.Entity<NayaxSaleTimestampRepairPreviewDraft>().Property(x => x.PlanJson).IsRequired();

        // One Nayax connection per business (issue #518). The unique index is on the ownership
        // column alone, which is what makes "one per business" a schema guarantee instead of a
        // convention the adapter has to remember: a second row for a business cannot be inserted,
        // so a credential save can only ever create the first one or update the existing one.
        modelBuilder.Entity<BusinessNayaxConnection>()
            .HasIndex(connection => connection.BusinessId)
            .IsUnique();
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.OperatorId).IsRequired();
        // Required, not nullable: the row exists only once credentials have been stored, so an
        // empty ciphertext or a nameless key would be a state nothing can act on. "No credentials"
        // is the absence of the row, reported as NayaxConnectionStatus.NotConfigured.
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.AccessTokenCiphertext).IsRequired();
        modelBuilder.Entity<BusinessNayaxConnection>().Property(x => x.EncryptionKeyId).IsRequired();
        // Both instants are persisted UTC and are exposed through the connection read, so each is
        // marked UTC on the way out - see the StockAdjustment.CreatedAt comment below for why the
        // SQLite provider makes that necessary. The conversion changes no stored byte and no
        // comparison, so the conditional revision check is unaffected.
        modelBuilder.Entity<BusinessNayaxConnection>()
            .Property(x => x.LastTestedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => fromProvider.HasValue
                    ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                    : fromProvider);
        modelBuilder.Entity<BusinessNayaxConnection>()
            .Property(x => x.UpdatedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // One connection-test draft per business (issue #536), for the same reason the connection
        // above has one row per business: "a new test replaces the previous draft" is then a schema
        // guarantee rather than a convention the adapter has to remember, so a second draft - and a
        // second decryptable token - cannot exist for a business however the write path is called.
        modelBuilder.Entity<NayaxConnectionTestDraft>()
            .HasIndex(draft => draft.BusinessId)
            .IsUnique();
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.OperatorId).IsRequired();
        // Required, not nullable, as on the connection: the row exists only once a test has stored
        // credentials, so an empty ciphertext or a nameless key would be a state nothing can act on.
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.AccessTokenCiphertext).IsRequired();
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.EncryptionKeyId).IsRequired();
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.ResultsJson).IsRequired();
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.CreatedByDirectoryTenantId).IsRequired();
        modelBuilder.Entity<NayaxConnectionTestDraft>().Property(x => x.CreatedByObjectId).IsRequired();
        // Both instants are persisted UTC and both are reported, so each is marked UTC on the way
        // out - see the StockAdjustment.CreatedAt comment below for why the SQLite provider makes
        // that necessary. The expiry comparison is a tick comparison either way.
        modelBuilder.Entity<NayaxConnectionTestDraft>()
            .Property(x => x.CreatedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<NayaxConnectionTestDraft>()
            .Property(x => x.ExpiresAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // Purchase/PurchaseItem are the Purchase-language CLR types; explicitly mapped to
        // their legacy "Receipt"/"ReceiptItem" tables so the rename does not change the schema.
        //
        // The check constraint is issue #976's half of "a purchase may have no document": the four
        // document columns are nullable, and they are all present or all absent. It is in the
        // database rather than only in code because a half-recorded document is not recoverable -
        // it would be a row claiming bytes nothing can open, or bytes nothing names - and the
        // "has no document" answer every read and the attach-only-if-empty write depend on would
        // then be ambiguous. Inventory.Domain.Purchases.PurchaseDocumentPolicy.IsMetadataConsistent
        // is the same rule stated once for the application.
        modelBuilder.Entity<Purchase>().ToTable("Receipts", table => table.HasCheckConstraint(
            "CK_Receipts_DocumentMetadataAllOrNothing",
            @"(""StoredFileName"" IS NULL AND ""ContentType"" IS NULL "
                + @"AND ""FileName"" IS NULL AND ""FileSizeBytes"" IS NULL) "
                + @"OR (""StoredFileName"" IS NOT NULL AND ""ContentType"" IS NOT NULL "
                + @"AND ""FileName"" IS NOT NULL AND ""FileSizeBytes"" IS NOT NULL)"));
        modelBuilder.Entity<PurchaseItem>().ToTable("ReceiptItems");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.TotalAmount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.DeliveryCost)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Purchase>()
            .Property(r => r.PackageCost)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<NayaxSales>()
            .ToTable("NayaxSales");

        // The key is ours; TransactionID is Nayax's. See NayaxSales.Id for why they are not
        // the same thing once more than one operator account can exist.
        modelBuilder.Entity<NayaxSales>()
            .HasKey(s => s.Id);

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.Id)
            .ValueGeneratedOnAdd();

        // A Nayax transaction appears at most once per business. Scoping the constraint by
        // business is what lets two operator accounts legitimately report the same remote id.
        modelBuilder.Entity<NayaxSales>()
            .HasIndex(s => new { s.BusinessId, s.TransactionID })
            .IsUnique();

        // MachineAuthorizationTime is a persisted true UTC instant (see docs/architecture.md
        // § Nayax sale timestamps for where that instant is established - the latest-sales
        // synchronization writes it from the Nayax payload's authoritative AuthorizationDateTimeGMT
        // field, not from the identically named machine-local one, issue #380 - and
        // § Timezone and business calendar for what reporting then does with it). This conversion is
        // about Kind metadata only: it has never been, and must not be read as, evidence of what the
        // upstream field means. The Transaction Sales report exposes the instant to the
        // frontend as the transaction's instant. Microsoft's SQLite provider does not round-trip
        // DateTimeKind - see the StockAdjustment.CreatedAt comment below for the complete
        // explanation - so without this the report's transactionDate serialises with no "Z"/offset
        // and the Angular BusinessDateTimePipe parses it as browser-local time (issue #232, the
        // same defect class issue #230 fixed for Stock History). Marking the value as UTC on every
        // read restores the unambiguous instant the API boundary must expose without changing the
        // stored bytes or any comparison semantics (DateTime comparisons and SQL translation only
        // ever look at ticks, never Kind), so sale costing, COGS, fee/commission resolution,
        // reconciliation, and every date-range filter are unaffected. Values derived from this
        // instant as a business-calendar *date* are reduced back to a Kind-free date at the point
        // of derivation, so a date-only field is never serialised as a UTC instant: see
        // EfDailyReportFactsProvider and NayaxProcessingFeeService.
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.MachineAuthorizationTime)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.SettlementValue)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.UnitCostAtSale)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.CostOfGoodsSold)
            .HasColumnType("decimal(18,6)");
        modelBuilder.Entity<NayaxSales>()
            .Property(s => s.NayaxProductCostPrice)
            .HasColumnType("decimal(18,6)");

        // A Nayax machine-stock event (identified by its Nayax EventLogID) appears at most once per
        // business, the same idempotency pattern as NayaxSales/TransactionID above (issue #183).
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasIndex(e => new { e.BusinessId, e.NayaxEventLogId })
            .IsUnique();

        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasIndex(e => new { e.BusinessId, e.MachineId, e.ProcessingStatus });

        // EventDateTimeGmt is normalised to a UTC instant at import (SyncMachineStockFromNayax.AsUtc,
        // issue #217) and the Sync Restock preview exposes it to the frontend as the event's instant.
        // Microsoft's SQLite provider does not round-trip DateTimeKind - see the StockAdjustment
        // .CreatedAt comment below for the complete explanation - so without this the preview's
        // eventDateTimeGmt serialises with no "Z"/offset and the Angular BusinessDateTimePipe parses
        // it as browser-local time (issue #237, the same defect class issue #230/#232 fixed for Stock
        // History/Transaction Sales). Marking the value as UTC on every read restores the unambiguous
        // instant the API boundary must expose without changing the stored bytes or any comparison
        // semantics (DateTime comparisons and SQL translation only ever look at ticks, never Kind),
        // so the 24-hour duplicate heuristic, From-date filtering, reconciliation, idempotency,
        // inventory and costing are unaffected.
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .Property(e => e.EventDateTimeGmt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasOne(e => e.MatchedProduct)
            .WithMany()
            .HasForeignKey(e => e.MatchedProductId)
            .OnDelete(DeleteBehavior.SetNull);

        // The manual refill a duplicate resolution was decided against (issue #196); kept for
        // auditability only, never a driver of any calculation, so it may be orphaned by SetNull.
        modelBuilder.Entity<NayaxMachineStockEvent>()
            .HasOne(e => e.MatchedManualStockAdjustment)
            .WithMany()
            .HasForeignKey(e => e.MatchedManualStockAdjustmentId)
            .OnDelete(DeleteBehavior.SetNull);

        // A machine-stock count is read by machine, then by product, in count order (issue #56), and
        // the tenant column leads the index because every query is filtered by it first.
        modelBuilder.Entity<MachineStockCount>()
            .HasIndex(count => new { count.BusinessId, count.MachineId, count.ProductId, count.CountedAtUtc });

        // CountedAtUtc is written from the IClock port as a UTC instant, and the SQLite provider does
        // not round-trip DateTimeKind - see the StockAdjustment.CreatedAt comment below for the
        // complete explanation - so it is marked UTC on every read. Without this the recorded instant
        // serialises with no "Z" and the frontend would parse the ledger's baseline instant as
        // browser-local time.
        modelBuilder.Entity<MachineStockCount>()
            .Property(count => count.CountedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<MachineStockCount>()
            .Property(count => count.Notes)
            .HasMaxLength(MachineStockCountPolicy.MaxNotesLength);

        modelBuilder.Entity<MachineStockCount>()
            .HasOne(count => count.Product)
            .WithMany()
            .HasForeignKey(count => count.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        // The machine a count was taken in, and the machine a stock movement went to, are local
        // machines (issue #741), with the same composite foreign key the operating expense uses
        // (issue #780, see below): (BusinessId, MachineId) against the (BusinessId, Id) key on
        // Machines, so SQLite itself refuses a reference to another business's machine. Restrict,
        // never cascade: a count and a movement are inventory history, so a machine they refer to
        // is deactivated rather than deleted. Since issue #896 a machine *nothing* refers to can be
        // deleted, and this key is the database's own backstop under that delete's own count - a
        // reference that lands in between makes SQLite refuse the statement, which the store turns
        // into the operator's refusal. No navigation property, for the same reason as the expense.
        modelBuilder.Entity<MachineStockCount>()
            .HasOne<LocalMachine>()
            .WithMany()
            .HasForeignKey(count => new { count.BusinessId, count.MachineId })
            .HasPrincipalKey(machine => new { machine.BusinessId, machine.Id })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<StockAdjustment>()
            .HasOne<LocalMachine>()
            .WithMany()
            .HasForeignKey(adjustment => new { adjustment.BusinessId, adjustment.MachineId })
            .HasPrincipalKey(machine => new { machine.BusinessId, machine.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // The batch refill of issue #743. Its machine is the same local machine reference, with the
        // same composite key and the same Restrict, for the same reasons as above.
        modelBuilder.Entity<PickListRefill>()
            .HasOne<LocalMachine>()
            .WithMany()
            .HasForeignKey(refill => new { refill.BusinessId, refill.MachineId })
            .HasPrincipalKey(machine => new { machine.BusinessId, machine.Id })
            .OnDelete(DeleteBehavior.Restrict);

        // What makes the key idempotent: one refill per key per business, enforced by the database
        // rather than only by the claim the use case makes inside its write transaction. Per
        // business, because two businesses may legitimately choose the same string (AGENTS.md §
        // Tenant ownership and data isolation).
        modelBuilder.Entity<PickListRefill>()
            .HasIndex(refill => new { refill.BusinessId, refill.IdempotencyKey })
            .IsUnique();

        modelBuilder.Entity<PickListRefill>()
            .Property(refill => refill.IdempotencyKey)
            .IsRequired()
            .HasMaxLength(PickListRefillPolicy.MaximumIdempotencyKeyLength);

        // Lowercase hexadecimal SHA-256, so the column is exactly as long as the rendering it holds.
        modelBuilder.Entity<PickListRefill>()
            .Property(refill => refill.RequestFingerprint)
            .IsRequired()
            .HasMaxLength(64);

        modelBuilder.Entity<PickListRefill>()
            .Property(refill => refill.Notes)
            .HasMaxLength(PickListRefillPolicy.MaximumNotesLength);

        // RecordedAtUtc is written from the IClock port as a UTC instant, and the SQLite provider
        // does not round-trip DateTimeKind - see the StockAdjustment.CreatedAt comment below for the
        // complete explanation - so it is marked UTC on every read. Without this a replayed refill's
        // instant serialises with no "Z" and a client would read it as its own local time.
        modelBuilder.Entity<PickListRefill>()
            .Property(refill => refill.RecordedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        modelBuilder.Entity<PickListRefillLine>()
            .HasOne(line => line.Refill)
            .WithMany(refill => refill.Lines)
            .HasForeignKey(line => line.RefillId)
            .OnDelete(DeleteBehavior.Cascade);

        // Read by batch, in the order the operator submitted them, which is the row order a replay
        // has to answer in.
        modelBuilder.Entity<PickListRefillLine>()
            .HasIndex(line => new { line.BusinessId, line.RefillId, line.Id });

        // A multi-machine batch's per-line machine (issue #897). Every column in this application
        // that names a local machine carries the same composite (BusinessId, MachineId) foreign key
        // on the machines' (BusinessId, Id) key, restricted on delete, so a machine cannot be deleted
        // out from under recorded history and a line can never name another business's machine.
        modelBuilder.Entity<PickListRefillLine>()
            .HasOne<LocalMachine>()
            .WithMany()
            .HasForeignKey(line => new { line.BusinessId, line.MachineId })
            .HasPrincipalKey(machine => new { machine.BusinessId, machine.Id })
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Product>()
            .HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.SetNull);

        // A product has no supplier relationship (issue #787, A1 of #786). The supplier
        // relationships that remain are the purchase's, the supplier order's and the operating
        // expense's, below - each one a supplier the business actually transacted with.
        modelBuilder.Entity<StockAdjustment>()
            .HasOne(sa => sa.Product)
            .WithMany(p => p.StockAdjustments)
            .HasForeignKey(sa => sa.ProductId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Purchase>()
            .HasOne(r => r.Supplier)
            .WithMany()
            .HasForeignKey(r => r.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PurchaseItem>()
            .HasOne(i => i.Purchase)
            .WithMany(r => r.Items)
            .HasForeignKey(i => i.ReceiptId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<PurchaseItem>()
            .HasOne(i => i.Product)
            .WithMany()
            .HasForeignKey(i => i.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<StockAdjustment>()
            .HasOne(a => a.ReceiptItem)
            .WithMany()
            .HasForeignKey(a => a.ReceiptItemId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<PurchaseItem>().Property(i => i.Quantity).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<PurchaseItem>().Property(i => i.UnitCost).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.QuantityOrdered).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.QuantityReceived).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderLine>().Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrder>()
            .HasOne(order => order.Supplier)
            .WithMany()
            .HasForeignKey(order => order.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<SupplierOrderLine>()
            .HasOne(line => line.SupplierOrder)
            .WithMany(order => order.Lines)
            .HasForeignKey(line => line.SupplierOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SupplierOrderLine>()
            .HasOne(line => line.Product)
            .WithMany()
            .HasForeignKey(line => line.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().Property(item => item.QuantityApplied).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SupplierOrderReceiptAllocation>()
            .HasOne(allocation => allocation.SupplierOrderLine)
            .WithMany(line => line.ReceiptAllocations)
            .HasForeignKey(allocation => allocation.SupplierOrderLineId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>()
            .HasOne(allocation => allocation.ReceiptItem)
            .WithMany(item => item.SupplierOrderAllocations)
            .HasForeignKey(allocation => allocation.ReceiptItemId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<OperatingExpense>().Property(e => e.AmountExGst).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<OperatingExpense>().Property(e => e.GstAmount).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<OperatingExpense>().Property(e => e.TotalAmount).HasColumnType("decimal(18,2)");
        // StockAdjustment.CreatedAt is always DateTime.UtcNow (see StockAdjustment.cs), but
        // Microsoft's SQLite provider - the only provider this API runs against, see Program.cs -
        // does not round-trip DateTimeKind: a value freshly queried back from the database always
        // materialises as DateTimeKind.Unspecified. System.Text.Json then serialises it without a
        // "Z"/offset, so Stock History's JSON instant is ambiguous and the frontend's
        // BusinessDateTimePipe parses it as browser-local time instead of UTC (issue #230). Marking
        // the value as UTC on every read restores the unambiguous instant the API boundary must
        // expose, without changing the stored bytes or comparison semantics (DateTime comparisons and
        // SQL translation only ever look at ticks, never Kind).
        modelBuilder.Entity<StockAdjustment>()
            .Property(a => a.CreatedAt)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        modelBuilder.Entity<StockAdjustment>().Property(a => a.UnitCost).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.TotalCost).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.AverageUnitCostAfter).HasColumnType("decimal(18,6)");
        modelBuilder.Entity<StockAdjustment>().Property(a => a.InventoryValueAfter).HasColumnType("decimal(18,6)");

        modelBuilder.Entity<Category>().HasIndex(c => c.Name);
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Name);
        // The supplier's optional website (issue #847). Configured here rather than with a
        // [MaxLength] attribute on the entity, which Swashbuckle would publish as a maxLength
        // keyword on the legacy Supplier component the API pins.
        modelBuilder.Entity<Supplier>().Property(s => s.Url).HasMaxLength(SupplierUrlPolicy.MaxLength);
        ConfigureSupplierPresetLink(modelBuilder);
        modelBuilder.Entity<Product>().HasIndex(p => p.Sku);
        modelBuilder.Entity<SupplierOrder>().HasIndex(order => new { order.SupplierId, order.Status, order.OrderDate });
        modelBuilder.Entity<SupplierOrderLine>().HasIndex(line => new { line.ProductId, line.SupplierOrderId });
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().HasIndex(allocation => allocation.ReceiptItemId);
        modelBuilder.Entity<SupplierOrderReceiptAllocation>().HasIndex(allocation => allocation.SupplierOrderLineId);
        // Import de-duplication is per business: two businesses may legitimately import the
        // same file, and one must not be told its own import is a duplicate because another
        // business imported the same bytes first.
        modelBuilder.Entity<ImportedFile>().HasIndex(f => new { f.BusinessId, f.FileHash }).IsUnique();
        modelBuilder.Entity<ImportedReimbursement>()
            .HasOne(r => r.ImportedFile)
            .WithMany(f => f.Reimbursements)
            .HasForeignKey(r => r.ImportedFileId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedReimbursementDevice>()
            .HasOne(d => d.ImportedReimbursement)
            .WithMany(r => r.Devices)
            .HasForeignKey(d => d.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedDevicePayment>()
            .HasOne(p => p.ImportedReimbursement)
            .WithMany(r => r.DevicePayments)
            .HasForeignKey(p => p.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedFee>()
            .HasOne(f => f.ImportedReimbursement)
            .WithMany(r => r.Fees)
            .HasForeignKey(f => f.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedPaymentMethod>()
            .HasOne(p => p.ImportedReimbursement)
            .WithMany(r => r.PaymentMethods)
            .HasForeignKey(p => p.ImportedReimbursementId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<ImportedReimbursement>().Property(r => r.Total).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalBillableTransactionAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalNotBillableTransactionAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.ServiceFee).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.ProcessingFee).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.TotalExtraCharge).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedReimbursementDevice>().Property(d => d.NetAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.TotalSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.ProcessingFees).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedDevicePayment>().Property(p => p.ServiceFees).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalSumWithVat).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.VatPercentage).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.AverageFeeAmount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedFee>().Property(f => f.TotalCount).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<ImportedPaymentMethod>().Property(p => p.TotalSalesSum).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<OperatingExpense>()
            .HasOne(e => e.Supplier)
            .WithMany()
            .HasForeignKey(e => e.SupplierId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.ExpenseDate);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.Category);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.SupplierId);
        modelBuilder.Entity<OperatingExpense>().HasIndex(e => e.MachineId);
        // The machine an expense is attributed to is a local machine (issue #780), and the foreign
        // key is the pair (BusinessId, MachineId) against the (BusinessId, Id) alternate key on
        // Machines, for the reason NayaxSales.ProductId uses one: it makes "an expense names a
        // machine of its own business" something SQLite enforces on every insert and update rather
        // than a rule only BusinessOwnershipEnforcer applies. Restrict, never cascade and never
        // set-null: an expense is financial data, so deleting the machine it belongs to must fail
        // loudly - a machine with expenses is deactivated instead, and the delete of issue #896
        // refuses it before the database has to. No navigation property: an expense carries the id,
        // and a reader that wants the machine reads it through the machine queries.
        modelBuilder.Entity<OperatingExpense>()
            .HasOne<LocalMachine>()
            .WithMany()
            .HasForeignKey(e => new { e.BusinessId, e.MachineId })
            .HasPrincipalKey(machine => new { machine.BusinessId, machine.Id })
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<NayaxProcessingFeeRate>().Property(r => r.FeeExGst).HasColumnType("decimal(18,4)");
        // Each business configures its own effective-dated fee rates.
        modelBuilder.Entity<NayaxProcessingFeeRate>().HasIndex(r => new { r.BusinessId, r.EffectiveFrom }).IsUnique();
        modelBuilder.Entity<SiteCommissionAgreement>().Property(x => x.CommissionRate).HasColumnType("decimal(18,4)");
        // SiteId is a remote Nayax identifier, so the agreement key must be scoped by business
        // as well: the same site id from two operator accounts is two different sites.
        modelBuilder.Entity<SiteCommissionAgreement>().HasIndex(x => new { x.BusinessId, x.SiteId, x.EffectiveFrom }).IsUnique();
        modelBuilder.Entity<CommissionPayment>().Property(x => x.Amount).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CommissionPayment>().HasIndex(x => new { x.SiteId, x.PeriodStart, x.PeriodEnd });

        ConfigureBusinessOwnership(modelBuilder);
    }

    /// <summary>
    /// The one place tenant scoping is applied to reads (issue #64).
    ///
    /// It walks the model rather than naming entities, so every <see cref="IBusinessOwned"/>
    /// type gets the same treatment automatically: a required business key, an index for the
    /// scoped queries, and a global query filter. A new tenant-owned entity is therefore
    /// protected the moment it implements the interface - there is no per-entity list to forget
    /// to update, and no controller-level <c>Where</c> clause anywhere that could be omitted on
    /// one endpoint.
    ///
    /// It deliberately does <em>not</em> add a database foreign key from the business key to
    /// <see cref="Business"/>; see the comment at that point in the loop, and the required
    /// post-backfill integrity step recorded in docs/tenant-rollout.md.
    ///
    /// <see cref="Business"/> and <see cref="BusinessMembership"/> are deliberately excluded.
    /// They are the tenancy tables themselves: membership is what resolves the scope in the
    /// first place, so filtering it by the scope would be circular and would deny every caller.
    /// <see cref="MemberInvite"/> is excluded for a related structural reason (issue #532): the
    /// redemption has to find an invite by the token a caller with no membership presents, under a
    /// denied scope that matches no filtered row. See <see cref="ConfigureMemberInvites"/>.
    /// <see cref="SupportSession"/> is excluded for the same kind of reason (issue #550): every
    /// request that reaches it is a platform-admin request, which resolves no business at all. See
    /// <see cref="ConfigureSupportSessions"/>.
    /// </summary>
    private void ConfigureBusinessOwnership(ModelBuilder modelBuilder)
    {
        // Materialised first: the loop body adds configuration, which mutates the model.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().ToList())
        {
            var clrType = entityType.ClrType;
            if (!typeof(IBusinessOwned).IsAssignableFrom(clrType))
            {
                continue;
            }

            var entity = modelBuilder.Entity(clrType);

            entity.Property(nameof(IBusinessOwned.BusinessId)).IsRequired();

            // Deliberately no database foreign key to Businesses yet - this is a deferral, not a
            // decision that one is unnecessary.
            //
            // It cannot be added while unassigned rows exist: the checkpoint-2 column defaults to
            // 0, which matches no Business, so the constraint would be violated by every
            // pre-existing row and the backfill would have to fight it instead of simply
            // assigning owners. Until then the boundary rests on the query filter and
            // BusinessOwnershipEnforcer, which is sound because BusinessId is never supplied by a
            // caller - it is stamped from resolved membership - so a dangling value cannot be
            // injected through the API. Deleting a Business that still has members is already
            // blocked by BusinessMembership's restricted foreign key.
            //
            // REQUIRED POST-BACKFILL STEP: once the human-controlled bootstrap has run and no row
            // is left unassigned, add these foreign keys in a follow-up migration so referential
            // integrity backs up the application-level enforcement. See docs/tenant-rollout.md.

            // Every tenant-scoped query starts with "BusinessId = @current", so it leads.
            entity.HasIndex(nameof(IBusinessOwned.BusinessId));

            entity.HasQueryFilter(BuildBusinessFilter(clrType));
        }
    }

    /// <summary>
    /// Builds <c>e =&gt; !TenantFilteringEnabled || e.BusinessId == CurrentBusinessId</c> for one
    /// entity type. Both properties are read off this context instance, so the filter tracks the
    /// current request rather than the model-building one.
    /// </summary>
    private LambdaExpression BuildBusinessFilter(Type clrType)
    {
        var entity = Expression.Parameter(clrType, "e");
        var context = Expression.Constant(this);

        var filteringDisabled = Expression.Not(
            Expression.Property(context, nameof(TenantFilteringEnabled)));

        var owned = Expression.Equal(
            Expression.Convert(
                Expression.Property(entity, nameof(IBusinessOwned.BusinessId)),
                typeof(int?)),
            Expression.Property(context, nameof(CurrentBusinessId)));

        return Expression.Lambda(Expression.OrElse(filteringDisabled, owned), entity);
    }

    /// <summary>
    /// Maps the single-use member invite links of issue #531.
    ///
    /// The invite is one of the five structural non-tenant-owned tables (the reviewed decision on
    /// issue #532, see <see cref="MemberInvite"/>), so <see cref="ConfigureBusinessOwnership"/>
    /// does not reach it: its business key, its <c>BusinessId</c> index and the absence of a global
    /// query filter are declared here instead. Everything else this method adds is specific to an
    /// invite: the bounds on its columns, the per-business uniqueness of the token hash, and the
    /// foreign key to the membership a redemption creates.
    /// </summary>
    private static void ConfigureMemberInvites(ModelBuilder modelBuilder)
    {
        var invite = modelBuilder.Entity<MemberInvite>();

        // The owning business, declared explicitly because no central mechanism declares it for
        // this table. Required: an invite with no owner would be an invite into nothing, and the
        // redemption takes the business it writes in from this column. There is deliberately no
        // query filter - the redemption has to find an invite by its token under a denied scope,
        // which a filter would make impossible - so the boundary is this column named in every
        // statement, exactly as it is for BusinessMembership.
        invite.Property(x => x.BusinessId).IsRequired();

        // Every statement in both invite adapters starts with "BusinessId = @business", so it
        // leads. It is the same index ConfigureBusinessOwnership used to add, declared by name here
        // instead, which is what keeps this ownership change free of any schema change.
        invite.HasIndex(x => x.BusinessId);

        // The role the invite grants (issue #521's vocabulary) and what became of the invite. Both
        // are required integer columns holding a declared value, and neither carries a CHECK
        // constraint, for the reason BusinessMembership.Role carries none: SQLite cannot add one to
        // an existing table without rebuilding it, and a value a newer deployment writes has to
        // fail closed in the policy regardless - MemberInvitePolicy reports an undeclared status as
        // unknown and never as Pending.
        invite.Property(x => x.Role).IsRequired();
        invite.Property(x => x.Status).IsRequired();

        // The Owner's note is display-only free text. Bounded in the schema as well as in
        // MemberInvitePolicy, and deliberately not indexed: nothing looks an invite up by it.
        invite.Property(x => x.Note).HasMaxLength(MemberInvitePolicy.MaxNoteLength);

        // The token's digest. Required and fixed-width, because the row exists only once a token
        // has been generated for it: an empty hash would be an invite nothing could ever redeem.
        invite.Property(x => x.TokenHash).IsRequired().HasMaxLength(MemberInviteToken.HashLength);

        // Unique per business, which is the repository-wide rule for a uniqueness constraint over
        // an externally shaped value (docs/architecture.md § Tenant ownership): a globally unique
        // index here would let one business's row deny another's. Global uniqueness is not what
        // makes a token unambiguous anyway - 256 bits of cryptographic randomness is.
        //
        // The redemption's lookup is by TokenHash alone, because the token is the only thing the
        // caller presents, so it is a scan of this index rather than a seek into it. That is a
        // deliberate trade and not a missed index: an index on the hash by itself would be globally
        // unique in effect over a value two businesses may legitimately hold, and the table holds
        // one row per invite ever issued - tens, not millions.
        invite.HasIndex(x => new { x.BusinessId, x.TokenHash }).IsUnique();

        invite.Property(x => x.CreatedByDirectoryTenantId).IsRequired();
        invite.Property(x => x.CreatedByObjectId).IsRequired();

        // The membership a redemption created (#532). Restrict, not Cascade, for the reason
        // BusinessMembership's own foreign key to Businesses is restricted: a membership an invite
        // records as redeemed must not be deletable in a way that silently drops the link between
        // the access and the invite that granted it. There is no navigation property, because
        // nothing in this slice or the next loads the membership through the invite.
        invite
            .HasOne<BusinessMembership>()
            .WithMany()
            .HasForeignKey(x => x.RedeemedByMembershipId)
            .OnDelete(DeleteBehavior.Restrict);

        // All four instants are persisted UTC and all four are reported by the invite API, so each
        // is marked UTC on the way out - see the StockAdjustment.CreatedAt comment below for why
        // the SQLite provider makes that necessary. The conversion changes no stored byte and no
        // comparison, so the expiry condition in the cancellation's WHERE clause is unaffected: a
        // DateTime comparison looks at ticks, not Kind.
        invite.Property(x => x.CreatedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        invite.Property(x => x.ExpiresAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        invite.Property(x => x.UsedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
        invite.Property(x => x.CancelledAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
    }

    /// <summary>
    /// Maps the platform administrator's support sessions of issue #550.
    ///
    /// The session is the fifth structural non-tenant-owned table (see <see cref="SupportSession"/>),
    /// so <see cref="ConfigureBusinessOwnership"/> does not reach it: its business key, its
    /// <c>BusinessId</c> index and the absence of a global query filter are declared here instead.
    /// Everything else this method adds is specific to a session: the bounds on its columns, the
    /// uniqueness of the session-id hash, and the index the per-administrator read seeks on.
    /// </summary>
    private static void ConfigureSupportSessions(ModelBuilder modelBuilder)
    {
        var session = modelBuilder.Entity<SupportSession>();

        // The business being read, declared explicitly because no central mechanism declares it for
        // this table. Required: a session into nothing would be a grant of access to nothing, and
        // every statement over this table names either this column or the actor pair below. There is
        // deliberately no query filter - a platform-admin request resolves no business, so a filter
        // comparing the current business would match none of these rows at all.
        session.Property(x => x.BusinessId).IsRequired();

        // The deactivation hook's statement is "every active session of this business", so the
        // business leads an index of its own.
        session.HasIndex(x => x.BusinessId);

        // The id's digest. Required and fixed-width, because the row exists only once an id has been
        // generated for it: an empty hash would be a session nothing could ever present.
        session.Property(x => x.SessionIdHash).IsRequired().HasMaxLength(SupportSessionId.HashLength);

        // Unique globally rather than per business, which is the opposite of the invite token's
        // constraint - and deliberately so. The repository-wide rule is about uniqueness over values
        // a *business* supplies, where a global constraint would let one business's row deny
        // another's; a session id is generated by this server from 256 bits of cryptographic
        // randomness, is never supplied by anybody, and is looked up (under issue #551) by a request
        // that has no business and therefore cannot narrow the lookup. The index is what makes that
        // lookup a seek, and the uniqueness is what makes "which session is this?" have one answer.
        session.HasIndex(x => x.SessionIdHash).IsUnique();

        // The administrator the session belongs to. Required - a session nobody can be held to is
        // not a session - and indexed as a pair, because "do I already have an active session?",
        // "what is my session?" and "end my session" all start with exactly this comparison.
        session.Property(x => x.ActorDirectoryTenantId).IsRequired();
        session.Property(x => x.ActorObjectId).IsRequired();
        session.HasIndex(x => new { x.ActorDirectoryTenantId, x.ActorObjectId });

        // Why the administrator looked. Required in the schema as well as in SupportSessionPolicy,
        // and bounded there: a session with no recorded reason would be unreviewable access, which is
        // the one thing this record exists to prevent.
        session.Property(x => x.Reason).IsRequired().HasMaxLength(SupportSessionPolicy.MaxReasonLength);

        // All three instants are persisted UTC and all three are reported by this feature's API, so
        // each is marked UTC on the way out - see the StockAdjustment.CreatedAt comment below for why
        // the SQLite provider makes that necessary. The conversion changes no stored byte and no
        // comparison, so the expiry and ended conditions in the stores' WHERE clauses are unaffected:
        // a DateTime comparison looks at ticks, not Kind.
        session.Property(x => x.StartedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        session.Property(x => x.ExpiresAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        session.Property(x => x.EndedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
    }

    /// <summary>
    /// Maps the platform supplier presets of issue #848.
    ///
    /// <para>This is the one <em>platform</em> table in the schema: it is about no business, so it
    /// has no business key, no <c>BusinessId</c> index and no query filter - not as an exemption from
    /// tenant ownership, but because there is no owner for one to compare against. Everything a
    /// business ends up with is its own ordinary tenant-owned <see cref="Supplier"/> row that issue
    /// #835 child C copies; nothing reads back the other way.</para>
    /// </summary>
    private static void ConfigureSupplierPresets(ModelBuilder modelBuilder)
    {
        var preset = modelBuilder.Entity<SupplierPreset>();

        // The displayed name, bounded in the schema as well as in SupplierPresetPolicy.
        preset.Property(x => x.Name).IsRequired().HasMaxLength(SupplierPresetPolicy.MaxNameLength);

        // The comparison key, and the reason it is a column at all: name uniqueness ignoring case has
        // to be the database's rule rather than a read-then-insert, which two concurrent creates can
        // both pass. It is a stored ToUpperInvariant() rather than a NOCASE collation because NOCASE
        // folds ASCII only, so "Café"/"CAFÉ" would be two rows under one and one row under the other.
        preset.Property(x => x.NormalisedName)
            .IsRequired()
            .HasMaxLength(SupplierPresetPolicy.MaxNameLength);
        preset.HasIndex(x => x.NormalisedName).IsUnique();

        // The optional website. Bounded here at the one supplier-website rule's limit - issue #847's
        // Inventory.Application.Suppliers.SupplierUrlPolicy, the same rule and the same column
        // length a business's own Suppliers.Url carries; the shape is that policy's too, which a
        // column constraint cannot express.
        preset.Property(x => x.Url).HasMaxLength(SupplierUrlPolicy.MaxLength);

        // The supplier-master fields of issue #931. The market is two columns because the two claims
        // are different: ServesNationwide is "everywhere", and ServedStates is the closed, ordered,
        // comma-separated code list SupplierPresetMasterPolicy writes - bounded at the longest value
        // that policy's own vocabulary can produce, computed from it rather than guessed, so a ninth
        // code could not leave the column too narrow for it. The list is one column and not a child
        // table because the vocabulary is closed, a market is read and written whole, and nothing
        // joins to a state.
        preset.Property(x => x.ServesNationwide);
        preset.Property(x => x.ServedStates)
            .IsRequired()
            .HasMaxLength(SupplierPresetMasterPolicy.MaxStoredServedStatesLength);

        // Whether the published prices include GST. A stored fact, never inferred, and never a
        // calculated amount: nothing in this mapping or this table holds money.
        preset.Property(x => x.PricesIncludeGst);

        // No HasConversion and no default on the connector, exactly as Business.SalesProvider is
        // mapped (issue #759): a nullable enum maps to a nullable INTEGER, and null - "nothing
        // collects this supplier's prices" - is the value a preset keeps unless somebody chose a
        // route. SupplierPriceConnector is numbered from 1, so no stored number means Varnmill by
        // accident.
        preset.Property(x => x.PriceConnector);
        preset.Property(x => x.PriceConnectorKey)
            .HasMaxLength(SupplierPresetMasterPolicy.MaxConnectorKeyLength);

        // How old an observed price of this supplier may be and still be compared (issue #925). It is
        // stored here, on the supplier master record, precisely so that no code names a supplier: the
        // owner's 30 hours for a daily supplier and 102 for a twice-weekly one are two values of one
        // column, and a supplier whose schedule changes is an edit rather than a release. The column
        // default is the daily limit, because daily is what the collector does unless somebody
        // records otherwise, and the range is checked here as well as in
        // SupplierPriceFreshnessPolicy - a limit of zero would read as "never stale" or "always
        // stale" depending on who resolved it.
        preset.Property(x => x.PriceCheckFreshnessHours)
            .IsRequired()
            .HasDefaultValue(SupplierPriceFreshnessPolicy.DailyCheckLimitHours);
        preset.ToTable(table => table.HasCheckConstraint(
            "CK_SupplierPresets_PriceCheckFreshnessHoursInRange",
            $@"""PriceCheckFreshnessHours"" > 0 "
                + $@"AND ""PriceCheckFreshnessHours"" <= {SupplierPriceFreshnessPolicy.MaxLimitHours}"));

        // Both instants are persisted UTC and both are reported by this feature's API, so each is
        // marked UTC on the way out - see the StockAdjustment.CreatedAt comment below for why the
        // SQLite provider makes that necessary.
        preset.Property(x => x.CreatedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        preset.Property(x => x.UpdatedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
    }

    /// <summary>
    /// Maps the link from a business's supplier to the platform supplier preset it was copied from
    /// (issue #934, second task of #817).
    ///
    /// <para><strong>A tenant-owned table referencing a platform table, which is new here.</strong>
    /// It does not make <see cref="Supplier"/> any less owned or <see cref="SupplierPreset"/> any
    /// more so: the copy is still the business's own row, the master is still about no business, and
    /// the only thing crossing between them is which suggestion this row came from.
    /// <c>BusinessOwnershipCoverageTests</c> accepts this one key by name rather than by exception,
    /// so a second tenant-to-platform key is a decision somebody has to make there.</para>
    ///
    /// <para><strong>No navigation, and <c>Restrict</c>.</strong> Nothing about a business's supplier
    /// reads the master's row, so there is no navigation property in either direction - a copy is
    /// complete in itself, and a navigation would put a platform entity into a tenant-owned entity's
    /// object graph where a careless <c>Include</c> could publish it. The delete behaviour is
    /// <c>Restrict</c> because the alternatives are both wrong: cascading would delete a business's
    /// supplier (and its purchase history's counterparty) when the platform tidied its list, and
    /// setting null would silently unlink a copy and let the business rename it. It costs nothing in
    /// practice - a preset is disabled, never deleted (see <see cref="ConfigureSupplierPresets"/>).
    /// </para>
    ///
    /// <para><strong>One copy per preset per business.</strong> The unique index is filtered to the
    /// rows that carry a link, because "no preset" is the normal case and every unlinked supplier
    /// would otherwise collide with every other. It leads with <c>BusinessId</c>, as every unique
    /// index on tenant-owned data must: two businesses hold a copy of the same preset, and a global
    /// index would let whichever was approved first deny the other.</para>
    /// </summary>
    private static void ConfigureSupplierPresetLink(ModelBuilder modelBuilder)
    {
        var supplier = modelBuilder.Entity<Supplier>();

        supplier
            .HasOne<SupplierPreset>()
            .WithMany()
            .HasForeignKey(s => s.SupplierPresetId)
            .OnDelete(DeleteBehavior.Restrict);

        supplier
            .HasIndex(s => new { s.BusinessId, s.SupplierPresetId })
            .IsUnique()
            .HasFilter(@"""SupplierPresetId"" IS NOT NULL");
    }

    /// <summary>
    /// Maps the single shared option-visibility row (issue #873).
    ///
    /// <para><strong>The key is not generated.</strong> The setting is one answer for the whole
    /// application, so its row key is the constant
    /// <see cref="Models.PlatformOptionVisibility.SingletonId"/> and the database assigns nothing:
    /// two writes that both find no row then collide on the primary key, which the adapter resolves
    /// into the update it should have been, instead of inserting a second row that a later read
    /// would have to choose between.</para>
    ///
    /// <para>The second <em>platform</em> table, on the terms
    /// <see cref="ConfigureSupplierPresets"/> states: about no business, so no business key, no index
    /// on one and no query filter. It is also deliberately not seeded - the absence of the row is the
    /// shipped "everything is offered" state.</para>
    /// </summary>
    private static void ConfigurePlatformOptionVisibility(ModelBuilder modelBuilder)
    {
        var visibility = modelBuilder.Entity<Models.PlatformOptionVisibility>();

        visibility.Property(x => x.Id).ValueGeneratedNever();

        // Persisted UTC, and marked UTC on the way out for the reason the preset instants are: the
        // SQLite provider returns an unspecified kind otherwise.
        visibility.Property(x => x.UpdatedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
    }

    /// <summary>
    /// Maps the business subscription state of issue #966: two tenant-owned tables about one
    /// business's plan, and one platform table about the grandfathering step itself.
    ///
    /// <para><strong>The classification is the design, again.</strong>
    /// <see cref="BusinessBillingSubscription"/> and <see cref="BusinessPlanGrant"/> are facts about
    /// one business, so they are ordinary <see cref="IBusinessOwned"/> rows and
    /// <see cref="ConfigureBusinessOwnership"/> gives each of them the business key, the index, the
    /// query filter and the ownership stamp with nothing added here.
    /// <see cref="SubscriptionGrandfathering"/> is the opposite: one cutoff and one completion marker
    /// for the whole application, so it carries no business key, no index on one and no query filter,
    /// exactly as <see cref="ConfigurePlatformOptionVisibility"/> states for the option-visibility
    /// row.</para>
    ///
    /// <para><strong>Two uniqueness rules, and both are the database's.</strong> A business has at
    /// most one billing subscription, and at most one <em>grandfathered</em> grant - the second
    /// through a filtered unique index, because a business may legitimately hold several
    /// complimentary grants. The grandfathering command's "grant it unless one exists" is written as
    /// one transaction, and this index is what makes that true even if two commands ran at once:
    /// the loser's insert is refused rather than leaving a business with two grandfathered
    /// grants.</para>
    ///
    /// <para>Every instant here is persisted UTC and marked UTC on the way out, for the reason the
    /// support session's are: the SQLite provider returns an unspecified kind otherwise, and the
    /// grant's end instant is reported through <c>GET /api/me/access</c>.</para>
    /// </summary>
    private static void ConfigureSubscriptions(ModelBuilder modelBuilder)
    {
        var subscription = modelBuilder.Entity<BusinessBillingSubscription>();

        // One subscription per business, led by the business key like every other per-business
        // uniqueness rule in this schema (AGENTS.md § Tenant ownership and data isolation).
        subscription.HasIndex(x => x.BusinessId).IsUnique();
        subscription.Property(x => x.CurrentPeriodStartUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        subscription.Property(x => x.CurrentPeriodEndUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
        subscription.Property(x => x.UpdatedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        var grant = modelBuilder.Entity<BusinessPlanGrant>();

        // At most one grandfathered grant per business. Filtered to that one source, because the
        // complimentary grants of #982 are a list rather than a single row. The filter is SQL, so the
        // source's numeric value appears as the literal the index is created with; the model test
        // `The_grandfathered_grant_index_filter_matches_the_declared_source` pins the two together,
        // so renumbering the enum fails a test rather than silently disabling this index.
        grant.HasIndex(x => new { x.BusinessId, x.Source })
            .IsUnique()
            .HasFilter(@"""Source"" = 10");
        grant.Property(x => x.Reason).HasMaxLength(500);
        grant.Property(x => x.StartsAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        grant.Property(x => x.EndsAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
        grant.Property(x => x.GrantedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        // The Super Admin surface's optimistic concurrency counter (issue #982). It defaults to 0,
        // which is what every row written before that surface existed - and the grandfathered grant,
        // which it never writes - holds, and it is an ordinary column rather than a provider row
        // version because the value is compared by the use case, reported to the client and logged.
        grant.Property(x => x.Revision).HasDefaultValue(0);

        // The decisions that produced those grants (issue #982): one append-only row per committed
        // create, change or end, written in the same SaveChanges as the grant it is about. Tenant-
        // owned with nothing added, exactly as the grant is; the reason column matches the grant's
        // length, and the actor's Entra pair is sized like every other stored (tid, oid).
        var grantAudit = modelBuilder.Entity<BusinessPlanGrantAudit>();

        // The one index this table needs: a business's decisions in the order they happened, which is
        // how the trail is read. The business key alone is already indexed centrally.
        grantAudit.HasIndex(x => new { x.BusinessId, x.OccurredAtUtc });
        grantAudit.Property(x => x.Reason).HasMaxLength(ManualPlanGrantPolicy.MaxReasonLength);
        grantAudit.Property(x => x.ActorDirectoryTenantId).HasMaxLength(100);
        grantAudit.Property(x => x.ActorObjectId).HasMaxLength(100);
        grantAudit.Property(x => x.EndsAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
        grantAudit.Property(x => x.OccurredAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

        var grandfathering = modelBuilder.Entity<SubscriptionGrandfathering>();

        // The key is not generated: the step is one answer for the whole application, so two
        // concurrent first runs collide on the primary key instead of recording two cutoffs, and the
        // adapter resolves that collision into reading the cutoff the other run fixed.
        grandfathering.Property(x => x.Id).ValueGeneratedNever();
        grandfathering.Property(x => x.CutoffUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        grandfathering.Property(x => x.CompletedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
    }

    /// <summary>
    /// Maps the shared supplier price catalogue of issue #923 (parent #817): three platform tables
    /// about products, and one tenant-owned table about a business's decision.
    ///
    /// <para><strong>The classification is the design.</strong> <see cref="MasterProduct"/>,
    /// <see cref="SupplierListing"/> and <see cref="ProductMatchSuggestion"/> are about no business -
    /// a brand, a size, a supplier's shelf and a matcher's opinion are facts about products - so each
    /// carries no business key, no index on one and no query filter, exactly as
    /// <see cref="ConfigureSupplierPresets"/> states for the presets. <see cref="ProductPriceTracking"/>
    /// is the opposite: it is a business saying it wants one of its own products priced, which is
    /// ordinary tenant-owned data and gets the ordinary central treatment from
    /// <see cref="ConfigureBusinessOwnership"/> with nothing added here.</para>
    ///
    /// <para><strong>Every key in this catalogue is a stored normalised value under a unique
    /// index</strong>, for the reason <see cref="SupplierPreset.NormalisedName"/> is one: "one
    /// description is one master product" has to be the database's rule rather than a <c>WHERE</c>
    /// clause two concurrent inserts can both pass, and two master products for one product is what
    /// the parent issue exists to prevent.</para>
    ///
    /// <para><strong>Every reference restricts, except the mapping's product.</strong> A supplier
    /// master record, a master product and a proposed master product are all things other rows depend
    /// on, so none of them can be deleted from under those rows. The one cascade is a mapping's own
    /// product: a mapping is a statement <em>about</em> a product and has no meaning without it, and
    /// restricting there would change what deleting a product does - which issue #923's last criterion
    /// forbids.</para>
    /// </summary>
    private static void ConfigurePriceCatalogue(ModelBuilder modelBuilder)
    {
        ConfigureMasterProducts(modelBuilder);
        ConfigureSupplierListings(modelBuilder);
        ConfigureProductMatchSuggestions(modelBuilder);
        ConfigureProductPriceTracking(modelBuilder);
        ConfigurePriceCollectorRuns(modelBuilder);
        ConfigurePriceCollectorWorkItems(modelBuilder);
        ConfigureSupplierPriceObservations(modelBuilder);
        ConfigurePriceCollectorResultRows(modelBuilder);
        ConfigureSupplierListingPendingObservations(modelBuilder);
    }

    /// <summary>
    /// Maps one submitted collector result row (issue #937, parent #817).
    ///
    /// <para>A platform table like the six beside it: no business key, no index on one and no query
    /// filter. Every reference restricts, because a row is the record of a decision and a decision
    /// whose run, work item, listing or observation had been deleted from under it would be
    /// unreadable.</para>
    ///
    /// <para><strong>The unique index is the idempotency key, and it is the database's.</strong>
    /// <c>(run, work item, supplier product id)</c> is what the issue states, and it is an index
    /// rather than a <c>WHERE</c> clause because two concurrent submissions of the same key would
    /// both pass the clause and store one reading twice. The supplier product id is <em>in</em> the
    /// key on purpose: one search legitimately returns a 24-pack and a 30-pack, so accepting one
    /// must not suppress the other. The outcome vocabulary stays the Domain's, for the reason the
    /// work item's does.</para>
    /// </summary>
    private static void ConfigurePriceCollectorResultRows(ModelBuilder modelBuilder)
    {
        var row = modelBuilder.Entity<PriceCollectorResultRow>();

        row.Property(x => x.SupplierProductId)
            .IsRequired()
            .HasMaxLength(SupplierListingPolicy.MaxSupplierProductIdLength);
        row.Property(x => x.Outcome).IsRequired();
        row.Property(x => x.Reason).HasMaxLength(MaxResultReasonLength);

        row
            .HasIndex(x => new
            {
                x.PriceCollectorRunId,
                x.PriceCollectorWorkItemId,
                x.SupplierProductId,
            })
            .IsUnique();

        // "How many distinct supplier product ids has this work item received?" - the count the
        // outcome call of #937 refuses a wrong total against.
        row.HasIndex(x => x.PriceCollectorWorkItemId);

        row
            .HasOne(x => x.PriceCollectorRun)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorRunId)
            .OnDelete(DeleteBehavior.Restrict);

        row
            .HasOne(x => x.PriceCollectorWorkItem)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorWorkItemId)
            .OnDelete(DeleteBehavior.Restrict);

        row
            .HasOne(x => x.SupplierListing)
            .WithMany()
            .HasForeignKey(x => x.SupplierListingId)
            .OnDelete(DeleteBehavior.Restrict);

        row
            .HasOne(x => x.SupplierPriceObservation)
            .WithMany()
            .HasForeignKey(x => x.SupplierPriceObservationId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(row.Property(x => x.SubmittedAtUtc));
    }

    /// <summary>
    /// Maps the latest reading of one listing that was not accepted as its price (issue #937).
    ///
    /// <para><strong>The listing is unique</strong>, which is the "replacing the previous pending
    /// one" rule expressed as an index: one listing holds at most one pending reading, enforced
    /// rather than left to a delete-then-insert nothing checks. The reference to the listing
    /// cascades - deleting a listing deletes the reading that was waiting on it, which has no
    /// meaning without it - and the run and work item restrict, because they are its
    /// provenance.</para>
    ///
    /// <para>The amount, currency, GST-rate and promotion checks are the ones an accepted
    /// observation carries, for the same reasons: a held reading is reviewed and may then become a
    /// price, so storing one this application would refuse as a price would only move the refusal to
    /// the moment somebody approved it.</para>
    /// </summary>
    private static void ConfigureSupplierListingPendingObservations(ModelBuilder modelBuilder)
    {
        var pending = modelBuilder.Entity<SupplierListingPendingObservation>();

        pending.Property(x => x.PackPrice).HasColumnType(MoneyColumnType);
        pending.Property(x => x.RegularPackPrice).HasColumnType(MoneyColumnType);
        pending.Property(x => x.SupplierGstRate).HasColumnType("decimal(5,4)");
        pending.Property(x => x.SizeValue).HasColumnType(SizeColumnType);

        pending.Property(x => x.Currency)
            .IsRequired()
            .HasMaxLength(SupplierPriceObservationPolicy.Currency.Length);
        pending.Property(x => x.PriceBasis).IsRequired();
        pending.Property(x => x.StockStatus).IsRequired();
        pending.Property(x => x.Reason).IsRequired().HasMaxLength(MaxResultReasonLength);
        pending.Property(x => x.SourceReference)
            .HasMaxLength(SupplierPriceObservationPolicy.MaxSourceReferenceLength);
        pending.Property(x => x.SourceUrl)
            .HasMaxLength(SupplierPriceObservationPolicy.MaxSourceUrlLength);
        pending.Property(x => x.Title).HasMaxLength(SupplierListingPolicy.MaxTitleLength);
        pending.Property(x => x.Variant).HasMaxLength(SupplierListingPolicy.MaxVariantLength);
        pending.Property(x => x.Barcode).HasMaxLength(SupplierListingPolicy.MaxBarcodeLength);

        pending.HasIndex(x => x.SupplierListingId).IsUnique();
        pending.HasIndex(x => x.PriceCollectorRunId);
        pending.HasIndex(x => x.PriceCollectorWorkItemId);

        pending.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_PackPricePositive",
                @"""PackPrice"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_RegularPackPricePositive",
                @"""RegularPackPrice"" IS NULL OR ""RegularPackPrice"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_GstRateIsAProportion",
                @"""SupplierGstRate"" IS NULL "
                    + @"OR (""SupplierGstRate"" >= 0 AND ""SupplierGstRate"" <= 1)");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_PromotionEndNeedsPromotion",
                @"""PromotionEndsAtUtc"" IS NULL OR ""IsPromotion"" = 1");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_CurrencyIsAud",
                $@"""Currency"" = '{SupplierPriceObservationPolicy.Currency}'");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_UnitsPerPackPositive",
                @"""UnitsPerPack"" IS NULL OR ""UnitsPerPack"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_SizeAndUnitTogether",
                @"(""SizeValue"" IS NULL AND ""SizeUnit"" IS NULL) "
                    + @"OR (""SizeValue"" IS NOT NULL AND ""SizeUnit"" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_SupplierListingPendingObservations_BarcodeAndScopeTogether",
                @"(""Barcode"" IS NULL AND ""BarcodeScope"" IS NULL) "
                    + @"OR (""Barcode"" IS NOT NULL AND ""BarcodeScope"" IS NOT NULL)");
        });

        pending
            .HasOne(x => x.SupplierListing)
            .WithMany()
            .HasForeignKey(x => x.SupplierListingId)
            .OnDelete(DeleteBehavior.Cascade);

        pending
            .HasOne(x => x.PriceCollectorRun)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorRunId)
            .OnDelete(DeleteBehavior.Restrict);

        pending
            .HasOne(x => x.PriceCollectorWorkItem)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorWorkItemId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(pending.Property(x => x.ObservedAtUtc));
        MarkUtcNullable(pending.Property(x => x.PromotionEndsAtUtc));
        MarkUtc(pending.Property(x => x.RecordedAtUtc));
    }

    /// <summary>
    /// The longest stored refusal or hold reason (issue #937). The reasons are
    /// <c>PriceCollectorResultPolicy</c>'s own sentences rather than caller text, and this bound is
    /// the same one a match suggestion's reason carries, so a reviewer's two explanations are stored
    /// on the same terms.
    /// </summary>
    private const int MaxResultReasonLength = ProductMatchSuggestionPolicy.MaxReasonLength;

    /// <summary>The column type a pack or regular price is stored as, wherever issue #937 declares one.</summary>
    private const string MoneyColumnType = "decimal(18,4)";

    /// <summary>The column type an individual item's size is stored as, wherever issue #937 declares one.</summary>
    private const string SizeColumnType = "decimal(18,3)";

    /// <summary>
    /// Maps one issued piece of collector work (issue #924, parent #817).
    ///
    /// <para>A platform table like the runs beside it: no business key, no index on one and no query
    /// filter, because a product description and a supplier's shelf are about no business. The
    /// exactly-one-product rule is a check constraint as well as a
    /// <c>PriceCollectorWorkItemPolicy</c> rule, for the reason the listing's exclusive-ownership
    /// rule is one: a row naming two products or none is work no result could be attributed to, and a
    /// stored row is not something an application-level check can be trusted to have prevented. The
    /// outcome vocabulary is deliberately the Domain's alone, because expressing it in SQL would
    /// hard-code the enum's numbers into migration history.</para>
    ///
    /// <para>Every reference restricts. A run, a supplier master record and a master product are all
    /// things this row depends on for its meaning, and work issued against something that was then
    /// deleted would be a search nobody could place.</para>
    /// </summary>
    private static void ConfigurePriceCollectorWorkItems(ModelBuilder modelBuilder)
    {
        var workItem = modelBuilder.Entity<PriceCollectorWorkItem>();

        workItem.Property(x => x.WaitingKey).HasMaxLength(PriceCatalogueWaitingKey.MaxLength);

        // The read #937 makes: one run's work. One work item by its own id needs no index, because
        // that id is the primary key.
        workItem.HasIndex(x => x.PriceCollectorRunId);

        // "One work item per product and supplier in a run" is the database's rule rather than a
        // WHERE clause two concurrent issues could both pass - and it is <em>two</em> filtered unique
        // indexes rather than one over all four columns, because SQLite treats NULLs as distinct in a
        // unique index. A single index would have compared (run, supplier, master, NULL) against
        // (run, supplier, master, NULL) and found them different, so the rule it looked like it was
        // enforcing would have been enforced for nothing: a work item names exactly one product, so
        // one of those two columns is always NULL. Each filtered index covers the rows whose own key
        // column is present, where there is no NULL left to be distinct from.
        workItem
            .HasIndex(x => new { x.PriceCollectorRunId, x.SupplierPresetId, x.MasterProductId })
            .IsUnique()
            .HasFilter(@"""MasterProductId"" IS NOT NULL");
        workItem
            .HasIndex(x => new { x.PriceCollectorRunId, x.SupplierPresetId, x.WaitingKey })
            .IsUnique()
            .HasFilter(@"""WaitingKey"" IS NOT NULL");

        workItem.ToTable(table => table.HasCheckConstraint(
            "CK_PriceCollectorWorkItems_OneProduct",
            @"(""MasterProductId"" IS NOT NULL AND ""WaitingKey"" IS NULL) "
                + @"OR (""MasterProductId"" IS NULL AND ""WaitingKey"" IS NOT NULL)"));

        // The outcome and the instant it was recorded at travel together, each being the evidence
        // about the other - the same shape the listing's price state uses. An issued work item has
        // neither; a reported one has both.
        workItem.ToTable(table => table.HasCheckConstraint(
            "CK_PriceCollectorWorkItems_OutcomeAndInstantTogether",
            @"(""Outcome"" IS NULL AND ""OutcomeAtUtc"" IS NULL) "
                + @"OR (""Outcome"" IS NOT NULL AND ""OutcomeAtUtc"" IS NOT NULL)"));

        workItem
            .HasOne(x => x.PriceCollectorRun)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorRunId)
            .OnDelete(DeleteBehavior.Restrict);

        workItem
            .HasOne(x => x.SupplierPreset)
            .WithMany()
            .HasForeignKey(x => x.SupplierPresetId)
            .OnDelete(DeleteBehavior.Restrict);

        workItem
            .HasOne(x => x.MasterProduct)
            .WithMany()
            .HasForeignKey(x => x.MasterProductId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(workItem.Property(x => x.IssuedAtUtc));
        MarkUtcNullable(workItem.Property(x => x.OutcomeAtUtc));
    }

    /// <summary>
    /// Maps one run of the supplier price collector (issue #925, parent #817).
    ///
    /// <para>A platform table like the three above: no business key, no index on one and no query
    /// filter, because a run reads suppliers' shelves for every business at once. The counts are
    /// checked non-negative in the database as well as in <c>PriceCollectorRunPolicy</c>, because a
    /// stored row is not something an application-level check can be trusted to have prevented. The
    /// status-against-end-instant rule is deliberately the Domain's alone, for the reason the
    /// mapping's status rule is: expressing it in SQL would hard-code the status enum's numbers into
    /// migration history.</para>
    /// </summary>
    private static void ConfigurePriceCollectorRuns(ModelBuilder modelBuilder)
    {
        var run = modelBuilder.Entity<PriceCollectorRun>();

        run.Property(x => x.Status).IsRequired();

        run.ToTable(table => table.HasCheckConstraint(
            "CK_PriceCollectorRuns_CountsNotNegative",
            @"""ListingsCheckedCount"" >= 0 AND ""ObservationsRecordedCount"" >= 0 "
                + @"AND ""FailureCount"" >= 0"));

        // The per-outcome counts of issue #937, checked on exactly the terms the three above are:
        // a stored row is not something an application-level check can be trusted to have
        // prevented, and a negative count of searches is not a smaller number of searches.
        run.ToTable(table => table.HasCheckConstraint(
            "CK_PriceCollectorRuns_SearchCountsNotNegative",
            @"""CompleteSearchCount"" >= 0 AND ""TruncatedSearchCount"" >= 0 "
                + @"AND ""NoMatchesSearchCount"" >= 0 AND ""FailedSearchCount"" >= 0 "
                + @"AND ""NotSearchedCount"" >= 0"));

        // The one read a collector and the summary of #930 both make: the most recent runs.
        run.HasIndex(x => x.StartedAtUtc);

        // And the one issuing a work list makes (issue #924): the runs that are still open, which a
        // new run expires. Indexed on the status alone because "open" is one value of it and the
        // table holds one row per run rather than per listing.
        run.HasIndex(x => x.Status);

        MarkUtc(run.Property(x => x.StartedAtUtc));
        MarkUtcNullable(run.Property(x => x.CompletedAtUtc));
        MarkUtcNullable(run.Property(x => x.ExpiresAtUtc));
    }

    /// <summary>
    /// Maps one observed supplier price (issue #925, parent #817).
    ///
    /// <para><strong>A platform table, and an append-only one.</strong> What a supplier charges for a
    /// pack is a fact about that supplier's shelf and about no business, so there is no business key,
    /// no index on one and no query filter. There is no updated instant either: a correction is a
    /// later observation, never an edit of a stored one, which is what keeps the raw facts separate
    /// from the derived comparison (AGENTS.md § Nayax integration states the same rule for imported
    /// facts).</para>
    ///
    /// <para><strong>Both references restrict.</strong> An observation whose listing or whose
    /// collector run had been deleted from under it would be a price nobody could place or
    /// trust.</para>
    ///
    /// <para><strong>The check constraints are the facts the comparison depends on.</strong> A pack
    /// price of zero divides into a unit price of zero and compares as cheaper than every real offer;
    /// a GST rate that is not a proportion - 10 where 0.10 belongs - converts a price to eleven times
    /// itself; a promotion end date on a price that is not a promotion silently withdraws a real
    /// offer; and a currency that is not AUD would be compared as though it were dollars. Each is
    /// also a <c>SupplierPriceObservationPolicy</c> rule.</para>
    /// </summary>
    private static void ConfigureSupplierPriceObservations(ModelBuilder modelBuilder)
    {
        var observation = modelBuilder.Entity<SupplierPriceObservation>();

        // Published amounts at the scale the imported financial facts in this schema are stored at.
        // Nothing here is divided or rounded: the per-unit figure is derived by
        // CheapestUnitOfferPolicy at full precision and rounded only where it is displayed.
        observation.Property(x => x.PackPrice).HasColumnType("decimal(18,4)");
        observation.Property(x => x.RegularPackPrice).HasColumnType("decimal(18,4)");

        // A proportion, like a match suggestion's confidence beside it.
        observation.Property(x => x.SupplierGstRate).HasColumnType("decimal(5,4)");

        observation.Property(x => x.Currency)
            .IsRequired()
            .HasMaxLength(SupplierPriceObservationPolicy.Currency.Length);
        observation.Property(x => x.PriceBasis).IsRequired();
        observation.Property(x => x.StockStatus).IsRequired();
        observation.Property(x => x.SourceReference)
            .HasMaxLength(SupplierPriceObservationPolicy.MaxSourceReferenceLength);
        observation.Property(x => x.SourceUrl)
            .HasMaxLength(SupplierPriceObservationPolicy.MaxSourceUrlLength);

        // The two reads this table exists for: one listing's prices newest-first - which is how the
        // latest observation of each listing is found - and one run's own results.
        observation.HasIndex(x => new { x.SupplierListingId, x.ObservedAtUtc });
        observation.HasIndex(x => x.PriceCollectorRunId);

        observation.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_SupplierPriceObservations_PackPricePositive",
                @"""PackPrice"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierPriceObservations_RegularPackPricePositive",
                @"""RegularPackPrice"" IS NULL OR ""RegularPackPrice"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierPriceObservations_GstRateIsAProportion",
                @"""SupplierGstRate"" IS NULL "
                    + @"OR (""SupplierGstRate"" >= 0 AND ""SupplierGstRate"" <= 1)");
            table.HasCheckConstraint(
                "CK_SupplierPriceObservations_PromotionEndNeedsPromotion",
                @"""PromotionEndsAtUtc"" IS NULL OR ""IsPromotion"" = 1");
            table.HasCheckConstraint(
                "CK_SupplierPriceObservations_CurrencyIsAud",
                $@"""Currency"" = '{SupplierPriceObservationPolicy.Currency}'");
        });

        observation
            .HasOne(x => x.SupplierListing)
            .WithMany()
            .HasForeignKey(x => x.SupplierListingId)
            .OnDelete(DeleteBehavior.Restrict);

        observation
            .HasOne(x => x.PriceCollectorRun)
            .WithMany()
            .HasForeignKey(x => x.PriceCollectorRunId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(observation.Property(x => x.ObservedAtUtc));
        MarkUtcNullable(observation.Property(x => x.PromotionEndsAtUtc));
        MarkUtc(observation.Property(x => x.CreatedAtUtc));
    }

    private static void ConfigureMasterProducts(ModelBuilder modelBuilder)
    {
        var master = modelBuilder.Entity<MasterProduct>();

        master.Property(x => x.Brand).IsRequired().HasMaxLength(MasterProductPolicy.MaxBrandLength);
        master.Property(x => x.Name).IsRequired().HasMaxLength(MasterProductPolicy.MaxNameLength);

        // The size at the scale the Domain accepts and no finer, which is why MasterProductPolicy
        // refuses a finer value rather than rounding one: a per-unit price divides by this number.
        master.Property(x => x.SizeValue).HasColumnType("decimal(18,3)");
        master.Property(x => x.SizeUnit).IsRequired();

        // A size of zero is not a smaller product: it is a denominator that makes a per-unit price
        // infinite and compares as cheaper than every real product. The database refuses it as well
        // as MasterProductPolicy, because a stored row is not something an application-level check
        // can be trusted to have prevented.
        master.ToTable(table => table.HasCheckConstraint(
            "CK_MasterProducts_SizeValuePositive",
            @"""SizeValue"" > 0"));

        // The comparison key, bounded at the longest value the policy's own vocabulary can produce -
        // computed from it rather than guessed, so widening a part cannot leave this column too
        // narrow for the keys it must hold.
        master.Property(x => x.NormalisedKey)
            .IsRequired()
            .HasMaxLength(MasterProductPolicy.MaxNormalisedKeyLength);
        master.HasIndex(x => x.NormalisedKey).IsUnique();

        MarkUtc(master.Property(x => x.CreatedAtUtc));
        MarkUtc(master.Property(x => x.UpdatedAtUtc));
    }

    private static void ConfigureSupplierListings(ModelBuilder modelBuilder)
    {
        var listing = modelBuilder.Entity<SupplierListing>();

        listing.Property(x => x.SupplierProductId)
            .IsRequired()
            .HasMaxLength(SupplierListingPolicy.MaxSupplierProductIdLength);
        listing.Property(x => x.Url).HasMaxLength(SupplierListingPolicy.MaxUrlLength);
        listing.Property(x => x.Title).HasMaxLength(SupplierListingPolicy.MaxTitleLength);

        // The listing's own identity reading (issue #937), at the one size scale this catalogue
        // uses: a listing's size is compared against a master product's, so a second scale would be
        // a second answer to "is this the same size?".
        listing.Property(x => x.Variant).HasMaxLength(SupplierListingPolicy.MaxVariantLength);
        listing.Property(x => x.SizeValue).HasColumnType(SizeColumnType);

        // Text, and never a number: 0012345678905 and 12345678905 are different barcodes, and the
        // leading zeros are not recoverable once a column has parsed them away.
        listing.Property(x => x.Barcode).HasMaxLength(SupplierListingPolicy.MaxBarcodeLength);

        listing.Property(x => x.WaitingKey).HasMaxLength(PriceCatalogueWaitingKey.MaxLength);
        listing.Property(x => x.OriginWaitingKey).HasMaxLength(PriceCatalogueWaitingKey.MaxLength);
        listing.Property(x => x.MatchStatus).IsRequired();

        // One supplier lists one product once, which is what the collector work list of #924
        // deduplicates by. It is not scoped by business because there is no business here: the
        // supplier master record is platform configuration and so is its shelf.
        listing.HasIndex(x => new { x.SupplierPresetId, x.SupplierProductId }).IsUnique();

        // The two lookups the catalogue reads make: a master product's listings, and the listings a
        // search is still holding under its waiting key.
        listing.HasIndex(x => x.MasterProductId);
        listing.HasIndex(x => x.WaitingKey);

        // The exclusive-ownership rule of issue #923, in the database and not only in
        // SupplierListingPolicy: a listing references exactly one of a master product and an active
        // waiting key. Both at once is a listing claiming to be two things; neither is a listing
        // nothing can ever find again. OriginWaitingKey is deliberately outside this constraint -
        // that is the whole reason it is a second column - so a matched listing can still say which
        // search discovered it.
        //
        // The pack and barcode rules are here for the same reason, and each is one of issue #923's
        // criteria: a pack size of zero divides, and a carton barcode with no scope prices one can
        // at the price of thirty. The mapping's own status/master/source consistency is deliberately
        // left to ProductPriceTrackingPolicy instead, because expressing it in SQL would hard-code
        // the status enum's numbers into migration history.
        listing.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_SupplierListings_OneOwner",
                @"(""MasterProductId"" IS NOT NULL AND ""WaitingKey"" IS NULL) "
                    + @"OR (""MasterProductId"" IS NULL AND ""WaitingKey"" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_SupplierListings_UnitsPerPackPositive",
                @"""UnitsPerPack"" IS NULL OR ""UnitsPerPack"" > 0");
            table.HasCheckConstraint(
                "CK_SupplierListings_BarcodeAndScopeTogether",
                @"(""Barcode"" IS NULL AND ""BarcodeScope"" IS NULL) "
                    + @"OR (""Barcode"" IS NOT NULL AND ""BarcodeScope"" IS NOT NULL)");

            // Issue #937's own pair, on the barcode rule's terms: a number with no unit cannot be
            // compared with anything, and a unit with no number describes nothing. A size of zero
            // is refused for the reason a master product's is - it is a denominator.
            table.HasCheckConstraint(
                "CK_SupplierListings_SizeAndUnitTogether",
                @"(""SizeValue"" IS NULL AND ""SizeUnit"" IS NULL) "
                    + @"OR (""SizeValue"" IS NOT NULL AND ""SizeUnit"" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_SupplierListings_SizeValuePositive",
                @"""SizeValue"" IS NULL OR ""SizeValue"" > 0");
        });

        listing
            .HasOne(x => x.SupplierPreset)
            .WithMany()
            .HasForeignKey(x => x.SupplierPresetId)
            .OnDelete(DeleteBehavior.Restrict);

        listing
            .HasOne(x => x.MasterProduct)
            .WithMany()
            .HasForeignKey(x => x.MasterProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // The listing's own price state (issue #925): which observation is current, when it was last
        // checked successfully, and when its price last changed. Three facts rather than one, because
        // "how old is this price?" and "when did this supplier last move it?" are different
        // questions, and a daily re-read that finds the same price answers only the first.
        //
        // The pointer has no navigation property in either direction, like the supplier's link to its
        // preset: it is a cursor into an append-only table, not a parent-child graph, and no Include
        // may pull a listing's whole price history with it. Restrict, because the observation a
        // listing points at is not something to delete from under it.
        listing
            .HasOne<SupplierPriceObservation>()
            .WithMany()
            .HasForeignKey(x => x.LatestObservationId)
            .OnDelete(DeleteBehavior.Restrict);

        // SupplierListingPriceStatePolicy's rule, in the database too: the observation and the check
        // instant are recorded together - each is the evidence about the other - and a price that was
        // never observed cannot have changed. Nothing here clears any of the three: a failed or
        // missing check leaves the last good price exactly where it was, which is the owner's
        // constraint of 27 October 2026.
        listing.ToTable(table => table.HasCheckConstraint(
            "CK_SupplierListings_PriceStateConsistent",
            @"((""LatestObservationId"" IS NULL AND ""LastCheckedAtUtc"" IS NULL "
                + @"AND ""LastPriceChangeAtUtc"" IS NULL) "
                + @"OR (""LatestObservationId"" IS NOT NULL AND ""LastCheckedAtUtc"" IS NOT NULL))"));

        MarkUtc(listing.Property(x => x.CreatedAtUtc));
        MarkUtc(listing.Property(x => x.UpdatedAtUtc));
        MarkUtcNullable(listing.Property(x => x.LastCheckedAtUtc));
        MarkUtcNullable(listing.Property(x => x.LastPriceChangeAtUtc));
        MarkUtcNullable(listing.Property(x => x.LastNotFoundAtUtc));
    }

    private static void ConfigureProductMatchSuggestions(ModelBuilder modelBuilder)
    {
        var suggestion = modelBuilder.Entity<ProductMatchSuggestion>();

        suggestion.Property(x => x.WaitingKey)
            .IsRequired()
            .HasMaxLength(PriceCatalogueWaitingKey.MaxLength);
        suggestion.Property(x => x.SuggestedBrand).HasMaxLength(MasterProductPolicy.MaxBrandLength);
        suggestion.Property(x => x.SuggestedName).HasMaxLength(MasterProductPolicy.MaxNameLength);
        suggestion.Property(x => x.SuggestedSizeValue).HasColumnType("decimal(18,3)");

        // A proportion, at the scale ProductMatchSuggestionPolicy accepts. It is money-shaped and is
        // not money: nothing in this catalogue holds an amount.
        suggestion.Property(x => x.Confidence).HasColumnType("decimal(5,4)");

        suggestion.Property(x => x.Reason)
            .IsRequired()
            .HasMaxLength(ProductMatchSuggestionPolicy.MaxReasonLength);
        suggestion.Property(x => x.CollectorRun)
            .IsRequired()
            .HasMaxLength(ProductMatchSuggestionPolicy.MaxCollectorRunLength);
        suggestion.Property(x => x.Status).IsRequired();

        // The two reads this table exists for: what has been suggested for a waiting key, and whether
        // a (waiting key, master product) pair was already refused. Neither is unique - a pair that
        // was rejected and later proposed again is two rows, and keeping both is the point.
        suggestion.HasIndex(x => new { x.WaitingKey, x.Status });
        suggestion.HasIndex(x => new { x.MasterProductId, x.WaitingKey });

        // One proposal per row: an existing master product, or a complete description of a new one,
        // never both and never neither. Those are two different things to do about one waiting key,
        // and a row holding both would leave the reviewer of #928 unable to tell which was meant. The
        // suggested size and the confidence are checked on the same terms as a master product's own
        // size: a stored value nothing could act on is worse than a refused write.
        suggestion.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_ProductMatchSuggestions_OneProposal",
                @"(""MasterProductId"" IS NOT NULL "
                    + @"AND ""SuggestedBrand"" IS NULL AND ""SuggestedName"" IS NULL "
                    + @"AND ""SuggestedSizeValue"" IS NULL AND ""SuggestedSizeUnit"" IS NULL) "
                    + @"OR (""MasterProductId"" IS NULL "
                    + @"AND ""SuggestedBrand"" IS NOT NULL AND ""SuggestedName"" IS NOT NULL "
                    + @"AND ""SuggestedSizeValue"" IS NOT NULL AND ""SuggestedSizeUnit"" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_ProductMatchSuggestions_SuggestedSizePositive",
                @"""SuggestedSizeValue"" IS NULL OR ""SuggestedSizeValue"" > 0");
            table.HasCheckConstraint(
                "CK_ProductMatchSuggestions_ConfidenceIsAProportion",
                @"""Confidence"" >= 0 AND ""Confidence"" <= 1");
        });

        suggestion
            .HasOne(x => x.MasterProduct)
            .WithMany()
            .HasForeignKey(x => x.MasterProductId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(suggestion.Property(x => x.CreatedAtUtc));
        MarkUtc(suggestion.Property(x => x.UpdatedAtUtc));
    }

    private static void ConfigureProductPriceTracking(ModelBuilder modelBuilder)
    {
        var tracking = modelBuilder.Entity<ProductPriceTracking>();

        tracking.Property(x => x.WaitingKey)
            .IsRequired()
            .HasMaxLength(PriceCatalogueWaitingKey.MaxLength);
        tracking.Property(x => x.SearchName)
            .IsRequired()
            .HasMaxLength(ProductPriceTrackingPolicy.MaxSearchNameLength);

        // Required and possibly empty, which is deliberate: Product.Unit is nullable, and "no unit
        // label" has to have exactly one stored spelling or one product becomes two searches.
        tracking.Property(x => x.UnitLabel)
            .IsRequired()
            .HasMaxLength(ProductPriceTrackingPolicy.MaxUnitLabelLength);
        tracking.Property(x => x.Status).IsRequired();

        // One mapping per product, scoped by business for the reason every uniqueness rule over
        // tenant-owned data is: two businesses may legitimately track the same product, and a global
        // constraint would let whichever tracked it first deny the other.
        tracking.HasIndex(x => new { x.BusinessId, x.ProductId }).IsUnique();

        // The master products a business is tracking through, and the waiting keys a work list groups
        // by. The index on BusinessId alone is ConfigureBusinessOwnership's, added centrally.
        tracking.HasIndex(x => new { x.BusinessId, x.MasterProductId });
        tracking.HasIndex(x => x.WaitingKey);

        // On the products' (BusinessId, Id) business key, so a mapping cannot name another business's
        // product even in principle - the shape every tenant-owned reference to a product and a
        // machine takes in this schema. Cascade because a mapping is a statement about a product and
        // has no meaning without it, and because restricting would change what deleting a product
        // does, which issue #923's last criterion forbids.
        tracking
            .HasOne(x => x.Product)
            .WithMany()
            .HasForeignKey(x => new { x.BusinessId, x.ProductId })
            .HasPrincipalKey(product => new { product.BusinessId, product.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict, unlike the product above: a master product businesses are tracking through is not
        // something to remove from under them, and nulling a mapping would turn a settled match back
        // into a search without anybody deciding to.
        tracking
            .HasOne(x => x.MasterProduct)
            .WithMany()
            .HasForeignKey(x => x.MasterProductId)
            .OnDelete(DeleteBehavior.Restrict);

        MarkUtc(tracking.Property(x => x.CreatedAtUtc));
        MarkUtc(tracking.Property(x => x.UpdatedAtUtc));
    }

    /// <summary>
    /// Marks a persisted UTC instant as UTC on the way out. The SQLite provider returns an
    /// unspecified kind otherwise; see the <c>StockAdjustment.CreatedAt</c> comment below for the
    /// longer version of why that matters.
    /// </summary>
    private static void MarkUtc(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTime> instant) =>
        instant.HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));

    /// <summary>
    /// The same for an instant that may be absent, where "absent" stays absent rather than becoming
    /// a UTC-marked default - the shape <c>SupportSession.EndedAtUtc</c> uses.
    /// </summary>
    private static void MarkUtcNullable(
        Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder<DateTime?> instant) =>
        instant.HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);

    /// <summary>
    /// Maps the provider identity of a business, a product and a category (issue #759, parent #329
    /// decision P1 as revised on 18 October 2026).
    ///
    /// <para><strong>One provider per business, so the provider id is a column.</strong>
    /// <c>Business.SalesProvider</c> names the one external system a business's sales come from, or
    /// nothing at all; <c>Product.ExternalProductId</c> and <c>Category.ExternalCategoryId</c> hold
    /// that provider's id for the row. The provider is therefore a fact about the business rather
    /// than a column of every mapping, which is what replaced the link tables of issue #732
    /// (<see cref="ConfigureProviderLinks"/>, kept unread until #760 drops them).</para>
    ///
    /// <para><strong>Opaque text with the same bound as a link's.</strong> The column is
    /// <see cref="ExternalId.MaxLength"/> characters of text, stored as written and compared
    /// exactly - a provider that issues a code or a GUID needs no schema change, and
    /// <c>Inventory.Domain.Providers.NayaxExternalId</c> stays the one conversion for Nayax's
    /// numeric ids.</para>
    ///
    /// <para><strong>Unique per business, among the rows that have one.</strong> The index is
    /// filtered on <c>IS NOT NULL</c>, because "this row has no provider id" is a real state that
    /// every row of a business with no provider is in, and a plain unique index would allow exactly
    /// one of them. It is scoped by business for the reason every constraint over an externally
    /// supplied value is (docs/architecture.md § Tenant ownership): two operators' catalogues may
    /// legitimately both contain product 100, and a global index would let whichever imported first
    /// deny the other its own id. There is deliberately no uniqueness the other way round - a local
    /// row holds at most one provider id because the column is single-valued, which is the
    /// simplification this model exists for.</para>
    ///
    /// <para>No foreign key and no cascade are needed any more: the id is on the row it belongs to,
    /// so it cannot name another business's row and cannot outlive a deleted one. The
    /// <c>(BusinessId, Id)</c> alternate keys on <c>Products</c> and <c>Categories</c> stay as the
    /// link tables' principal key until #760.</para>
    /// </summary>
    private static void ConfigureProviderIdentityColumns(ModelBuilder modelBuilder)
    {
        // No HasConversion and no default: a nullable enum maps to a nullable INTEGER, and null -
        // "no sales provider" - is the value an existing business keeps unless the migration found
        // evidence of one. ExternalProvider is numbered from 1, so no stored integer this
        // application writes means Nayax by accident.
        modelBuilder.Entity<Business>().Property(business => business.SalesProvider);

        var product = modelBuilder.Entity<Product>();
        product.Property(x => x.ExternalProductId).HasMaxLength(ExternalId.MaxLength);
        product
            .HasIndex(x => new { x.BusinessId, x.ExternalProductId })
            .IsUnique()
            .HasFilter(@"""ExternalProductId"" IS NOT NULL");

        var category = modelBuilder.Entity<Category>();
        category.Property(x => x.ExternalCategoryId).HasMaxLength(ExternalId.MaxLength);
        category
            .HasIndex(x => new { x.BusinessId, x.ExternalCategoryId })
            .IsUnique()
            .HasFilter(@"""ExternalCategoryId"" IS NOT NULL");
    }

    /// <summary>
    /// Maps the machines a business operates (issue #740, parent #329 child G1).
    ///
    /// <para><strong>The table is <c>Machines</c>, and the entity is
    /// <see cref="LocalMachine"/></strong>: the type name <c>Machine</c> is still occupied by the
    /// retired response shape the machine wire-contract test compares against, and removing that
    /// belongs to issue #153. The table name is declared explicitly so it does not depend on which
    /// type happens to own it.</para>
    ///
    /// <para><strong>The provider id is the same column shape as a product's</strong>
    /// (<see cref="ConfigureProviderIdentityColumns"/>, issue #759): opaque text bounded by
    /// <see cref="ExternalId.MaxLength"/>, unique per business among the rows that have one, with the
    /// index filtered on <c>IS NOT NULL</c> because "this machine has no provider id" is the normal
    /// state of every machine of a business with no provider and a plain unique index would allow
    /// exactly one of them. Scoped by business for the reason every constraint over an externally
    /// supplied value is: two operators' fleets may legitimately both contain machine 7.</para>
    ///
    /// <para>A machine is ordinary tenant-owned data, so <see cref="ConfigureBusinessOwnership"/>
    /// gives it its required business key, its <c>BusinessId</c> index and its global query filter;
    /// what is declared here is everything specific to a machine.</para>
    ///
    /// <para><strong>The <c>(BusinessId, Id)</c> alternate key is the principal side of every
    /// reference to a machine</strong> (issue #780). It is declared here rather than left for
    /// <c>HasPrincipalKey</c> to infer, for the reason the product and category ones are
    /// (<see cref="ConfigureProviderLinks"/>): "(business, local id) identifies one machine" is a
    /// constraint on <c>Machines</c> in its own right, and the migration that added it rebuilt the
    /// table to state it.</para>
    /// </summary>
    private static void ConfigureMachines(ModelBuilder modelBuilder)
    {
        var machine = modelBuilder.Entity<LocalMachine>();

        machine.ToTable("Machines");
        machine.Property(x => x.Name).IsRequired();

        // The machine lists are ordered by name, exactly as the category list is.
        machine.HasIndex(x => x.Name);

        machine.Property(x => x.ExternalMachineId).HasMaxLength(ExternalId.MaxLength);
        machine
            .HasIndex(x => new { x.BusinessId, x.ExternalMachineId })
            .IsUnique()
            .HasFilter(@"""ExternalMachineId"" IS NOT NULL");

        machine.HasAlternateKey(x => new { x.BusinessId, x.Id });

        // The Sync Restock coverage of issue #891. The SQLite provider does not round-trip
        // DateTimeKind - see the StockAdjustment.CreatedAt comment for the complete explanation -
        // and this instant is published by the Dashboard reminder, so it is marked UTC on every
        // read. Without it the reminder's lastSuccessfulSyncAtUtc serialises with no "Z" and the
        // frontend's BusinessDateTimePipe would read it as browser-local time, which is exactly how
        // "synced 3 days ago" becomes the wrong number for an operator in another zone.
        machine
            .Property(x => x.LastSuccessfulRestockSyncStartedAtUtc)
            .HasConversion(
                toProvider => toProvider,
                fromProvider => fromProvider.HasValue
                    ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                    : fromProvider);
    }

    /// <summary>
    /// Maps the provider identity link tables of issue #732 (parent #329 decision P1).
    ///
    /// <para><strong>Nothing reads or writes them since issue #759</strong>, which moved a provider
    /// id onto the local row (<see cref="ConfigureProviderIdentityColumns"/>). They are still mapped
    /// and still in the schema only because Production applies migrations at startup and the code
    /// already running there still reads them; #760 drops both tables once this release is in
    /// production. The mapping below is therefore history, kept accurate rather than changed.</para>
    ///
    /// Both are ordinary tenant-owned entities, so <see cref="ConfigureBusinessOwnership"/> gives
    /// each its required business key, its <c>BusinessId</c> index and its global query filter;
    /// what is declared here is everything specific to a link.
    ///
    /// Two uniqueness rules, and each is a different statement. <c>(BusinessId, Provider,
    /// ExternalId)</c> says one provider id names at most one local row - per business, because an
    /// external id is a value two businesses may legitimately hold (docs/architecture.md § Tenant
    /// ownership), and a global index over it would let whichever business imported first deny the
    /// other its own catalogue. <c>(BusinessId, Provider, ProductId)</c>/<c>CategoryId</c> says a
    /// local row carries at most one id per provider, which is what keeps "which Nayax product is
    /// this?" a question with one answer; a second provider gets its own row rather than replacing
    /// that one.
    ///
    /// The foreign key is the pair <c>(BusinessId, local id)</c> against an alternate key on the
    /// principal, not the local id alone. That is the point of the whole mapping: it makes "a link
    /// belongs to the same business as the row it points at" a constraint SQLite enforces on every
    /// insert and update, rather than a rule only <see cref="BusinessOwnershipEnforcer"/> applies -
    /// AGENTS.md requires the database-level guarantee here because the link is the thing that will
    /// later resolve a remote identifier to a local product, and a link that crossed a business
    /// would resolve one business's sale onto another's product. Cascade, because a link is part of
    /// the row it names: deleting a product or category takes its provider ids with it rather than
    /// leaving a mapping to nothing.
    /// </summary>
    private static void ConfigureProviderLinks(ModelBuilder modelBuilder)
    {
        // The principal side of both foreign keys below, declared explicitly rather than left for
        // HasPrincipalKey to infer: it is a constraint on Products and Categories in its own right -
        // "(business, local id) identifies one row" - and the migration that added it rebuilt those
        // two tables to state it, which is not something to leave implied by a relationship.
        modelBuilder.Entity<Product>().HasAlternateKey(product => new { product.BusinessId, product.Id });
        modelBuilder.Entity<Category>().HasAlternateKey(category => new { category.BusinessId, category.Id });

        var productLink = modelBuilder.Entity<ProductExternalId>();

        productLink.Property(x => x.Provider).IsRequired();
        productLink.Property(x => x.ExternalId).IsRequired().HasMaxLength(ExternalId.MaxLength);

        productLink.HasIndex(x => new { x.BusinessId, x.Provider, x.ExternalId }).IsUnique();
        productLink.HasIndex(x => new { x.BusinessId, x.Provider, x.ProductId }).IsUnique();

        productLink
            .HasOne(link => link.Product)
            .WithMany()
            .HasForeignKey(link => new { link.BusinessId, link.ProductId })
            .HasPrincipalKey(product => new { product.BusinessId, product.Id })
            .OnDelete(DeleteBehavior.Cascade);

        var categoryLink = modelBuilder.Entity<CategoryExternalId>();

        categoryLink.Property(x => x.Provider).IsRequired();
        categoryLink.Property(x => x.ExternalId).IsRequired().HasMaxLength(ExternalId.MaxLength);

        categoryLink.HasIndex(x => new { x.BusinessId, x.Provider, x.ExternalId }).IsUnique();
        categoryLink.HasIndex(x => new { x.BusinessId, x.Provider, x.CategoryId }).IsUnique();

        categoryLink
            .HasOne(link => link.Category)
            .WithMany()
            .HasForeignKey(link => new { link.BusinessId, link.CategoryId })
            .HasPrincipalKey(category => new { category.BusinessId, category.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Maps the association between a stored sale and the local product it belongs to (issue #758,
    /// parent #329 decision A2c), and the two append-only tables that record how an existing sale's
    /// association was decided.
    ///
    /// <para><strong>The association is a foreign key on the business key, and it restricts.</strong>
    /// <c>NayaxSales.ProductId</c> is nullable - unresolved is a real state - and the foreign key is
    /// the pair <c>(BusinessId, ProductId)</c> against the <c>(BusinessId, Id)</c> alternate key on
    /// <c>Products</c>, for the same reason the provider links used one: it makes "a sale names a
    /// product of its own business" something SQLite enforces on every insert and update rather than
    /// a rule only <see cref="BusinessOwnershipEnforcer"/> applies, and a sale attributed to another
    /// business's product would put one business's revenue against another's inventory. The delete
    /// behaviour is <see cref="DeleteBehavior.Restrict"/>, never cascade and never set-null: a sale
    /// is historical financial data, and deleting the product it sold must fail loudly rather than
    /// delete the sale or quietly detach its COGS from any product.</para>
    ///
    /// <para><strong>The provider is required.</strong> <c>Provider</c> records which external
    /// system the row's identifiers came from, which a provider switch (#761) needs and which the
    /// table's name only implies. The entity defaults it to <c>Nayax</c> and both writers set it
    /// explicitly; <c>ExternalProvider</c> is numbered from 1, so a stored zero is not a provider.</para>
    ///
    /// <para><strong>The provenance flag is the column that keeps the old values honest.</strong>
    /// <c>ProviderProductIdIsRaw</c> is false on every row that predates #758, whose
    /// <c>NayaxProductId</c> may hold either a raw provider id or the local product id the old
    /// synchronization wrote, and true on every row written since. Nothing reads a
    /// provenance-unknown value as a key of anything.</para>
    ///
    /// <para>The index is <c>(BusinessId, ProductId)</c>: every costing, replay and report read is
    /// "this business's sales of this product", and the tenant query filter supplies the business
    /// predicate exactly as it does on every other owned table.</para>
    /// </summary>
    private static void ConfigureSaleProductAssociation(ModelBuilder modelBuilder)
    {
        var sale = modelBuilder.Entity<NayaxSales>();

        sale.Property(x => x.Provider).IsRequired();
        sale.Property(x => x.ProviderProductIdIsRaw).IsRequired();

        sale.HasIndex(x => new { x.BusinessId, x.ProductId });

        // No navigation property: a sale reads its product through the ordinary product queries, and
        // adding one here would invite a reader to load a catalogue row where it wants a stored
        // association. The relationship exists for the constraint.
        sale
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => new { x.BusinessId, x.ProductId })
            .HasPrincipalKey(product => new { product.BusinessId, product.Id })
            .OnDelete(DeleteBehavior.Restrict);

        var conflict = modelBuilder.Entity<NayaxSaleProductConflict>();
        conflict.HasIndex(x => new { x.BusinessId, x.NayaxSaleId }).IsUnique();

        var resolution = modelBuilder.Entity<NayaxSaleProductResolution>();
        resolution.Property(x => x.Rule).IsRequired();
        resolution.Property(x => x.CandidateProductIds).HasMaxLength(64);
        resolution.Property(x => x.DecidedBy).HasMaxLength(256);
        resolution.HasIndex(x => new { x.BusinessId, x.NayaxSaleId });
    }

    /// <summary>
    /// Maps the provider catalogue review and the last-decided provider field values (issue #738,
    /// parent #329 decision C1).
    ///
    /// <para><strong>Three additive tables, and nothing existing changes.</strong> A business with a
    /// usable provider connection that never starts a review has three empty tables and identical
    /// behaviour, which is what lets this ship on its own while Production applies migrations at
    /// startup.</para>
    ///
    /// <para><strong>The link columns are foreign keys on the business key, and they cascade.</strong>
    /// A review item's <c>LinkedProductId</c>/<c>LinkedCategoryId</c> and a field decision's
    /// <c>ProductId</c> are pairs with <c>BusinessId</c> against the <c>(BusinessId, Id)</c> alternate
    /// keys on <c>Products</c>/<c>Categories</c>, for the reason <c>NayaxSales.ProductId</c> is
    /// (<see cref="ConfigureSaleProductAssociation"/>): "this draft names a row of its own business"
    /// becomes something SQLite enforces on every insert rather than a rule only
    /// <see cref="BusinessOwnershipEnforcer"/> applies. The delete behaviour is
    /// <see cref="DeleteBehavior.Cascade"/> rather than <c>Restrict</c>, which is the one place this
    /// differs from a sale: a review is a draft about the catalogue and a field decision is a note
    /// about what was last asked, so neither is historical financial data and neither may be a reason
    /// a product cannot be deleted.</para>
    ///
    /// <para><strong>At most one pending review per business, enforced by the database.</strong> The
    /// filtered unique index over <c>(BusinessId)</c> where <c>AppliedAtUtc IS NULL</c> is what makes
    /// "the business's pending review" a single row rather than whichever one a query happened to
    /// order first. It is filtered for the same reason the external id indexes are: an applied review
    /// is a normal, repeatable state, so a plain unique index would allow exactly one of those.</para>
    ///
    /// <para><strong>One decision per product and field.</strong> That unique index is what makes the
    /// upsert an upsert: the answer to "what did we last ask about this field?" is single-valued, so a
    /// second row for the same pair would make the skip rule depend on which one was read.</para>
    /// </summary>
    private static void ConfigureProviderCatalogueReview(ModelBuilder modelBuilder)
    {
        var review = modelBuilder.Entity<ProviderCatalogueReview>();

        review.Property(x => x.Provider).IsRequired();
        review.Property(x => x.AppliedBy).HasMaxLength(256);

        // Both instants are persisted UTC and both are reported by the review API, so each is marked
        // UTC on the way out - the SQLite provider otherwise reads them back as Unspecified. The
        // conversion changes no stored byte and no comparison.
        review.Property(x => x.StartedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        review.Property(x => x.AppliedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => fromProvider.HasValue
                ? DateTime.SpecifyKind(fromProvider.Value, DateTimeKind.Utc)
                : fromProvider);
        // Declared with a name of its own so it is a *second* index over BusinessId rather than a
        // reconfiguration of the tenant index ConfigureBusinessOwnership adds to every owned entity:
        // that one is what every scoped read uses, and this one is a constraint. The filter cannot be
        // moved into the key either - SQLite treats NULLs as distinct in a unique index, so
        // UNIQUE(BusinessId, AppliedAtUtc) would permit exactly the two pending reviews it is meant
        // to refuse.
        review
            .HasIndex(x => x.BusinessId, "IX_ProviderCatalogueReviews_BusinessId_Pending")
            .IsUnique()
            .HasFilter(@"""AppliedAtUtc"" IS NULL");

        var item = modelBuilder.Entity<ProviderCatalogueReviewItem>();

        item.Property(x => x.Kind).IsRequired();
        item.Property(x => x.ExternalId).IsRequired().HasMaxLength(ExternalId.MaxLength);
        item.Property(x => x.ProviderName).IsRequired().HasMaxLength(512);
        item.Property(x => x.ProviderGroupExternalId).HasMaxLength(ExternalId.MaxLength);
        item.Property(x => x.ProviderUnitPrice).HasMaxLength(64);
        item.Property(x => x.ProviderUnitVolume).HasMaxLength(64);
        item.HasIndex(x => new { x.BusinessId, x.ReviewId, x.Kind });

        item
            .HasOne(x => x.Review)
            .WithMany(x => x.Items)
            .HasForeignKey(x => x.ReviewId)
            .OnDelete(DeleteBehavior.Cascade);

        item
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => new { x.BusinessId, x.LinkedProductId })
            .HasPrincipalKey(product => new { product.BusinessId, product.Id })
            .OnDelete(DeleteBehavior.Cascade);

        item
            .HasOne<Category>()
            .WithMany()
            .HasForeignKey(x => new { x.BusinessId, x.LinkedCategoryId })
            .HasPrincipalKey(category => new { category.BusinessId, category.Id })
            .OnDelete(DeleteBehavior.Cascade);

        var decision = modelBuilder.Entity<ProviderCatalogueFieldDecision>();

        decision.Property(x => x.Field).IsRequired();
        decision.Property(x => x.Decision).IsRequired();
        decision.Property(x => x.ProviderValue).IsRequired().HasMaxLength(64);
        decision.Property(x => x.DecidedAtUtc).HasConversion(
            toProvider => toProvider,
            fromProvider => DateTime.SpecifyKind(fromProvider, DateTimeKind.Utc));
        decision.HasIndex(x => new { x.BusinessId, x.ProductId, x.Field }).IsUnique();

        decision
            .HasOne<Product>()
            .WithMany()
            .HasForeignKey(x => new { x.BusinessId, x.ProductId })
            .HasPrincipalKey(product => new { product.BusinessId, product.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }

    /// <summary>
    /// Maps the provider data coverage record (issue #730, parent #329 decision P9).
    ///
    /// <para>No uniqueness constraint, deliberately. Two successful runs legitimately claim
    /// overlapping - even identical - ranges, because each one read a rolling window that reaches
    /// back past the previous run; a unique index would reject the second and silently turn a
    /// successful run into no evidence at all. Duplication is harmless here because the availability
    /// policy merges ranges rather than counting them.</para>
    ///
    /// <para>The index is the shape every read has: this business's rows of one provider and source,
    /// ordered by where their range starts. The tenant query filter supplies the business predicate,
    /// so <c>BusinessId</c> leads the index for the same reason it does on every other owned table.</para>
    /// </summary>
    private static void ConfigureProviderDataCoverage(ModelBuilder modelBuilder)
    {
        var coverage = modelBuilder.Entity<ProviderDataCoverage>();

        coverage.Property(x => x.Provider).IsRequired();
        coverage.Property(x => x.Source).IsRequired();
        coverage.HasIndex(x => new { x.BusinessId, x.Provider, x.Source, x.FromUtc });
    }

    /// <summary>
    /// Maps the application-owned tenancy tables introduced for issue #64: the Business that owns
    /// data, and the BusinessMembership rows that approve an authenticated Entra actor for it.
    ///
    /// This step only establishes the ownership model. Adding a TenantId to the business entities
    /// and backfilling existing records are separate, later changes.
    /// </summary>
    private static void ConfigureTenancy(ModelBuilder modelBuilder)
    {
        // Name is a display name, not an identifier: nothing resolves a business by it, so it
        // carries no uniqueness constraint and no index. Two businesses may legitimately trade
        // under the same name; ownership is decided by the key and the membership rows alone.
        modelBuilder.Entity<Business>().Property(b => b.Name).IsRequired();

        // The business's IANA time zone (issue #499). Required, because a business with no zone
        // has no derivable business dates at all; the value itself is validated against the host's
        // time-zone database by BusinessTimeZoneEnforcer, which a schema constraint cannot do.
        modelBuilder.Entity<Business>().Property(b => b.TimeZoneId).IsRequired();

        // The lifecycle status (issue #540). Required, because "no status" is not a state a
        // business can be in and an unfilled column would deny access without saying why; the
        // vocabulary itself is enforced where it has to be anyway - an undeclared stored value is
        // not Active, so BusinessMembershipResolutionPolicy denies it. No CHECK constraint, for
        // the reason BusinessMembership.Role carries none: SQLite cannot add one without
        // rebuilding the table, and a value a newer deployment writes has to fail closed in the
        // policy regardless.
        //
        // The profile columns the business application collects are all nullable, so the existing
        // business migrates without a single invented value, and none of them is validated by a
        // schema constraint: the ABN's check digits are enforced by BusinessAbnEnforcer on
        // SaveChanges, which a constraint cannot express, and the rest are facts an application
        // form records rather than values anything derives from.
        modelBuilder.Entity<Business>().Property(b => b.Status).IsRequired();

        // The state or territory the business trades in (issue #933). Nullable, because no existing
        // business has one and nothing backfills them, and bounded at the length of the longest
        // abbreviation. The vocabulary itself is not a schema constraint, for the reason the status
        // and the member's role carry none: SQLite cannot add a CHECK constraint to an existing
        // table without rebuilding it, so an undeclared value is refused where it has to be anyway -
        // BusinessStateEnforcer on SaveChanges, over every write path to a business alike.
        modelBuilder.Entity<Business>().Property(b => b.State).HasMaxLength(AustralianStates.MaxLength);

        // The backfill audit is keyed only by its own id: it records what happened to a table,
        // including runs that assigned rows to the wrong business, so it must stay queryable
        // independently of the tenancy state it is evidence about.
        modelBuilder.Entity<BusinessBackfillAudit>().Property(a => a.TableName).IsRequired();
        modelBuilder.Entity<BusinessBackfillAudit>().HasIndex(a => a.RunId);

        modelBuilder.Entity<BusinessMembership>()
            .HasOne(m => m.Business)
            .WithMany(b => b.Memberships)
            .HasForeignKey(m => m.BusinessId)
            // Restrict, not Cascade: a business that still has approved actors must not be
            // deletable in a way that silently drops its authorization rows.
            .OnDelete(DeleteBehavior.Restrict);

        // Entra tid/oid are case-insensitive GUID strings. ActorIdentity already normalizes them
        // before a lookup; NOCASE additionally means a hand-seeded bootstrap row that used a
        // different casing still matches, and still collides with the unique index below rather
        // than creating a second, ambiguity-causing membership.
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.DirectoryTenantId)
            .IsRequired()
            .UseCollation("NOCASE");
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.ObjectId)
            .IsRequired()
            .UseCollation("NOCASE");

        // The member's role (issue #521). Stored as the constrained BusinessRole vocabulary - an
        // integer column holding one of the declared values - not a free-text name, so a typo
        // cannot become a role. The schema does not additionally forbid an undeclared integer:
        // SQLite cannot add a CHECK constraint to an existing table without rebuilding it, and the
        // migration that added this column is deliberately additive on the one table that decides
        // who may sign in. An undeclared stored value is therefore handled where it has to be
        // handled anyway, for a value a newer deployment wrote as much as for a hand-edited one:
        // BusinessMembershipResolutionPolicy denies access for it.
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.Role)
            .IsRequired();

        // When this membership's state last changed (issue #522). Required: every row records an
        // instant, and the additive migration filled the pre-existing ones from CreatedAtUtc.
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.StatusChangedAtUtc)
            .IsRequired();

        // The member's display-only name and email address (issue #524). Both are nullable, because
        // a membership exists before its member has ever signed in and a validated token need not
        // carry either claim, and both are bounded: MemberProfile refuses a longer claim value
        // outright, and the declared length states that bound in the schema as well.
        //
        // Deliberately not indexed, not unique and not collated for lookup, unlike the identity
        // columns above. These two are display data: nothing resolves a membership, a business or a
        // person by them, and an index would be the first step towards something doing so.
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.DisplayName)
            .HasMaxLength(MemberProfile.MaxLength);
        modelBuilder.Entity<BusinessMembership>()
            .Property(m => m.Email)
            .HasMaxLength(MemberProfile.MaxLength);

        // One membership row per (actor, business), active or revoked, so duplicates cannot
        // quietly accumulate for one person in one business.
        modelBuilder.Entity<BusinessMembership>()
            .HasIndex(m => new { m.DirectoryTenantId, m.ObjectId, m.BusinessId })
            .IsUnique();

        // The resolution lookup path: every membership for one authenticated actor.
        modelBuilder.Entity<BusinessMembership>()
            .HasIndex(m => new { m.DirectoryTenantId, m.ObjectId });

        // One *active* membership per identity, across every business (issue #522). A person
        // belongs to one business at a time, and until this slice that was only a resolution rule:
        // BusinessMembershipResolutionPolicy denied an actor with two active memberships every
        // business, which is the right fail-closed answer but arrives at sign-in, after the fact.
        //
        // The index is filtered on the active rows, which is what makes it a rule about state
        // rather than about how the state was reached: an INSERT of a second active membership and
        // an UPDATE that reactivates a revoked one are refused alike, while any number of revoked
        // rows may stay for the membership history. It reads no business state, so a Pending or
        // Deactivated business's membership still occupies the identity's one active membership.
        //
        // Both columns are NOCASE (above), so a case-shifted hand-entered row collides here rather
        // than becoming a second active membership. It is named explicitly, and declared with the
        // named HasIndex overload, because it covers the same two columns as the unfiltered lookup
        // index above: EF identifies an index by its property list, so an unnamed declaration here
        // would reconfigure that index instead of adding this one - and the lookup index has to
        // stay, since a filtered index cannot serve a lookup that must also see revoked rows.
        modelBuilder.Entity<BusinessMembership>()
            .HasIndex(
                m => new { m.DirectoryTenantId, m.ObjectId },
                "IX_BusinessMemberships_DirectoryTenantId_ObjectId_Active")
            .HasFilter("\"IsActive\" = 1")
            .IsUnique();
    }
}
