#nullable enable
using InventoryApi.Controllers;
using InventoryApi.Swagger;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.Swagger;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace InventoryApi.Tests.Swagger;

/// <summary>
/// Builds the real MVC API-explorer model and the real Swagger document from
/// <see cref="InventoryApi.Swagger.SwaggerServiceCollectionExtensions.AddInventoryApiSwagger(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>, so the contract
/// regression tests assert against what the application actually publishes rather than against
/// a hand-written copy of it.
/// </summary>
internal static class ApiContractTestHost
{
    private static readonly Lazy<ServiceProvider> LazyProvider = new(Build, isThreadSafe: true);

    /// <summary>Every action the API explorer exposes, with its effective route template.</summary>
    internal static IReadOnlyList<ApiDescription> GetApiDescriptions() =>
        LazyProvider.Value
            .GetRequiredService<IApiDescriptionGroupCollectionProvider>()
            .ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .ToList();

    internal static IReadOnlyList<ApiDescription> GetApiDescriptionsFor<TController>()
        where TController : class =>
        GetApiDescriptions()
            .Where(description => description.ActionDescriptor is ControllerActionDescriptor controller &&
                controller.ControllerTypeInfo.AsType() == typeof(TController))
            .ToList();

    internal static OpenApiDocument GetSwaggerDocument() =>
        LazyProvider.Value.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The controllers live in the InventoryApi assembly, which is not this test run's entry
        // assembly, so the application part is added explicitly. Registering IHostEnvironment
        // only afterwards keeps MVC from also scanning the test assembly for controllers.
        services.AddControllers().AddApplicationPart(typeof(PurchasesController).Assembly);
        var environment = new ApiContractHostEnvironment();
        services.AddSingleton<IHostEnvironment>(environment);
        services.AddSingleton<IWebHostEnvironment>(environment);
        services.AddInventoryApiSwagger();

        // OperatingExpensesController declares two [HttpPost] actions on "api/operating-expenses",
        // which makes Swashbuckle refuse to generate any document. That conflict predates the
        // Purchase rename and is unrelated to it, so it is resolved here (in the test host only,
        // never in the application's own configuration) to keep these tests focused on the
        // purchase contract rather than silently changing an unrelated published operation.
        services.Configure<SwaggerGenOptions>(options =>
            options.ResolveConflictingActions(descriptions => descriptions.First()));

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The minimum the API explorer and Swashbuckle need to resolve; nothing here reads the
    /// filesystem, and it is registered after AddControllers so MVC does not also scan the test
    /// assembly for controllers.
    /// </summary>
    private sealed class ApiContractHostEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = typeof(PurchasesController).Assembly.GetName().Name!;
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
