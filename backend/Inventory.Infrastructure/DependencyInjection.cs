using Azure.Identity;
using Azure.Storage.Blobs;
using Inventory.Application.Businesses;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Categories;
using Inventory.Application.Commissions;
using Inventory.Application.Costing;
using Inventory.Application.Dashboard;
using Inventory.Application.Documents;
using Inventory.Application.Expenses;
using Inventory.Application.Gst;
using Inventory.Application.Imports;
using Inventory.Application.InventoryCounting;
using Inventory.Application.Machines;
using Inventory.Application.MachineStockSync;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxFeeSettings;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.PickList;
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
using Inventory.Application.SupplierOrders;
using Inventory.Application.Suppliers;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Nayax;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Sites;
using Inventory.Infrastructure.Time;
using Microsoft.Extensions.DependencyInjection;

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
        services.AddScoped<IBusinessProfileStore, EfBusinessProfileStore>();
        services.AddScoped<INayaxFeeRateStore, EfNayaxFeeRateStore>();
        // The per-business Nayax connection (issue #518). Its INayaxTokenProtector dependency comes
        // from AddNayaxTokenProtection below rather than from this method, for the same reason the
        // Nayax HTTP client's options do: only the composition root reads configuration.
        services.AddScoped<INayaxConnectionStore, EfNayaxConnectionStore>();
        services.AddScoped<ISiteCommissionStore, EfSiteCommissionStore>();
        services.AddScoped<ICategoryStore, EfCategoryStore>();
        services.AddScoped<ISupplierStore, EfSupplierStore>();
        services.AddScoped<IOperatingExpenseStore, EfOperatingExpenseStore>();
        services.AddScoped<IProductStore, EfProductStore>();
        services.AddScoped<IProductCatalogStore, EfProductCatalogStore>();
        services.AddScoped<IPurchaseStore, EfPurchaseStore>();
        services.AddScoped<IInventoryMovementStore, EfInventoryMovementStore>();
        services.AddScoped<IInventoryCostLedgerStore, EfInventoryCostLedgerStore>();
        services.AddScoped<ISaleCostingStore, EfSaleCostingStore>();
        services.AddScoped<IInventoryCostTransitionStore, EfInventoryCostTransitionStore>();
        services.AddScoped<IInventoryCostRepairStore, EfInventoryCostRepairStore>();
        services.AddScoped<IHistoricalGstClassificationStore, EfHistoricalGstClassificationStore>();
        services.AddScoped<INayaxSaleTimestampRepairStore, EfNayaxSaleTimestampRepairStore>();
        services.AddScoped<IStockAdjustmentStore, EfStockAdjustmentStore>();
        services.AddScoped<ISupplierOrderStore, EfSupplierOrderStore>();
        services.AddScoped<ISiteFactsStore, EfSiteFactsStore>();
        services.AddScoped<IMachineDashboardFactsStore, EfMachineDashboardFactsStore>();
        services.AddScoped<IProductPurchasePriceHistoryProvider, EfProductPurchasePriceHistoryProvider>();
        services.AddScoped<IProductPurchaseCostFactsProvider, EfProductPurchaseCostFactsProvider>();
        services.AddScoped<ILocalCatalogSnapshotProvider, EfLocalCatalogSnapshotProvider>();
        services.AddScoped<IMachineStockEventStore, EfMachineStockEventStore>();
        services.AddScoped<IOutstandingSupplierOrderQuantityStore, EfOutstandingSupplierOrderQuantityStore>();
        services.AddScoped<IPickListStorageStockStore, EfPickListStorageStockStore>();
        services.AddScoped<ILatestNayaxSalesStore, EfLatestNayaxSalesStore>();
        services.AddScoped<IInventoryCountAdjustmentStore, EfInventoryCountAdjustmentStore>();
        services.AddScoped<IImportedReimbursementStore, EfImportedReimbursementStore>();
        services.AddScoped<INayaxProductCatalogImportStore, EfNayaxProductCatalogImportStore>();
        services.AddScoped<INayaxSalesImportStore, EfNayaxSalesImportStore>();
        services.AddScoped<IDashboardSummarySalesFactsProvider, EfDashboardSummarySalesFactsProvider>();

        return services;
    }

    /// <summary>
    /// Registers the filesystem source of pending reimbursement XML files behind
    /// <see cref="IPendingReimbursementXmlSource"/> (issue #299).
    ///
    /// It is separate from <see cref="AddInfrastructureServices"/>, like
    /// <see cref="AddDocumentStorage"/>, because only the composition root knows the host's
    /// content and web roots; passing them in keeps <c>IWebHostEnvironment</c> out of both
    /// inner layers. Registered as a singleton: the adapter holds only the resolved folder path
    /// and reads nothing per request.
    /// </summary>
    /// <param name="services">The container being built.</param>
    /// <param name="options">The host paths the pending-file folder is resolved from.</param>
    public static IServiceCollection AddPendingReimbursementXmlSource(
        this IServiceCollection services,
        PendingReimbursementXmlOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton(options);
        services.AddSingleton<IPendingReimbursementXmlSource, FileSystemPendingReimbursementXmlSource>();

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
                services.AddSingleton<IDocumentStorage>(sp => sp.GetRequiredService<FileSystemDocumentStorage>());
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
        // which is what decides the tenant prefix every blob name is built from.
        services.AddScoped<IDocumentStorage, AzureBlobDocumentStorage>();
    }

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
    /// </summary>
    /// <exception cref="InvalidOperationException">The configured base URL is missing or invalid.</exception>
    public static IServiceCollection AddNayaxLynxClient(this IServiceCollection services, NayaxLynxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var baseAddress = NayaxLynxConfiguration.BuildBaseAddress(options);

        services.AddTransient<NayaxResilienceHandler>();
        services.AddHttpClient<INayaxLynxClient, NayaxLynxClient>(http => http.BaseAddress = baseAddress)
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
}
