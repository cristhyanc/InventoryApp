using Azure.Identity;
using Azure.Storage.Blobs;
using Inventory.Application.CatalogReconciliation;
using Inventory.Application.Documents;
using Inventory.Application.Imports;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxProcessingFees;
using Inventory.Application.Reporting.Bookkeeping;
using Inventory.Application.Reporting.Dashboard;
using Inventory.Application.Reporting.Daily;
using Inventory.Application.Reporting.Export;
using Inventory.Application.Reporting.Gst;
using Inventory.Application.Reporting.MachineProfitability;
using Inventory.Application.Reporting.ProductProfitability;
using Inventory.Application.Reporting.Reconciliation;
using Inventory.Application.Reporting.Transactions;
using Inventory.Application.Sites;
using Inventory.Application.Time;
using Inventory.Infrastructure.Clock;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Nayax;
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
        services.AddSingleton<IBusinessCalendar, SydneyBusinessCalendar>();

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
    /// Validates <paramref name="options"/> (<see cref="NayaxLynxConfiguration.ValidateNonSecretFields"/>)
    /// and registers the Nayax Lynx HTTP client behind <see cref="INayaxLynxClient"/> (issue #49),
    /// with bounded timeout/retry/circuit-breaker resilience (<see cref="NayaxResilienceHandler"/>,
    /// issue #48) applied to every call it makes.
    /// <paramref name="options"/> should already have its <see cref="NayaxLynxOptions.AccessToken"/>
    /// resolved (<see cref="NayaxLynxConfiguration.ResolveAccessToken"/>), since that is a secret
    /// this method does not read configuration for. Validation happens here, eagerly, so a
    /// missing base URL or operator ID fails registration at startup rather than the first Nayax
    /// call.
    /// </summary>
    /// <exception cref="InvalidOperationException">A required non-secret field is missing or invalid.</exception>
    public static IServiceCollection AddNayaxLynxClient(this IServiceCollection services, NayaxLynxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        NayaxLynxConfiguration.ValidateNonSecretFields(options);

        services.AddSingleton(options);
        services.AddTransient<NayaxResilienceHandler>();
        services.AddHttpClient<INayaxLynxClient, NayaxLynxClient>()
            .AddHttpMessageHandler<NayaxResilienceHandler>();

        return services;
    }
}
