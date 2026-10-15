using Microsoft.EntityFrameworkCore;
using Inventory.Application;
using Inventory.Application.Access;
using Inventory.Application.Members;
using Inventory.Application.Nayax;
using Inventory.Application.Onboarding;
using Inventory.Application.Tenancy;
using Inventory.Application.Time;
using Inventory.Infrastructure;
using Inventory.Infrastructure.Documents;
using Inventory.Infrastructure.Imports;
using Inventory.Infrastructure.Nayax;
using Inventory.Application.Businesses;
using InventoryApi.Adapters.Businesses;
using InventoryApi.Adapters.Members;
using InventoryApi.Adapters.Nayax;
using InventoryApi.Adapters.Onboarding;
using InventoryApi.Adapters.PlatformAdmin;
using InventoryApi.Adapters.PlatformDiagnostics;
using Inventory.Application.PlatformAdmin;
using Inventory.Application.SupportSessions;
using InventoryApi.Bootstrap;
using InventoryApi.Auth;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Auth.PlatformAdmin;
using Inventory.Application.PlatformDiagnostics;
using Inventory.Infrastructure.PlatformDiagnostics;
using Microsoft.AspNetCore.Authorization;
using Inventory.Infrastructure.Data;
using InventoryApi.Http;
using InventoryApi.Http.HealthChecks;
using InventoryApi.Observability;
using InventoryApi.Swagger;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

// The business bootstrap is a separate, human-invoked path (issue #64, checkpoint 3). It is
// checked before the web host is built so that starting the API and backfilling ownership can
// never be the same action: a deployment starts the API and does not reach this branch.
if (BusinessBootstrapCommand.Matches(args))
{
    return await BusinessBootstrapCommand.RunAsync(args, CancellationToken.None);
}

// Applying schema migrations is likewise a human-invoked command, not something a deployment
// performs. Normal startup below applies nothing outside Development.
if (DatabaseMigrationCommand.Matches(args))
{
    return await DatabaseMigrationCommand.RunAsync(args, CancellationToken.None);
}

// Copying stored documents to Azure Blob storage is the third human-invoked command (issue #39,
// checkpoint 3). It reads every business's records and writes to a storage account, so it is
// never something the web host does on the way up: reaching this branch means the process was
// started to migrate documents and will exit when it has.
if (DocumentMigrationCommand.Matches(args))
{
    return await DocumentMigrationCommand.RunAsync(args, CancellationToken.None);
}

// Moving the globally configured Nayax operator id and token into the existing business's own
// encrypted record is the fourth human-invoked command (issue #519, a slice of #500). It writes a
// production credential, so it is never something the web host does on the way up: reaching this
// branch means the process was started to migrate that credential and will exit when it has.
if (NayaxConnectionMigrationCommand.Matches(args))
{
    return await NayaxConnectionMigrationCommand.RunAsync(args, CancellationToken.None);
}

// Taking a verified snapshot is likewise a deliberate, human- or scheduler-invoked command, not
// something normal startup performs (issue #331). It is callable manually for one-off
// verification and by the scheduled backup job (issue #333) against the same configured database.
if (BackupDatabaseCommand.Matches(args))
{
    return await BackupDatabaseCommand.RunAsync(args, CancellationToken.None);
}

var builder = WebApplication.CreateBuilder(args);

// Error observability (issue #165). Registered before anything else is built so that startup,
// requests, dependencies, metrics and ILogger logs are all instrumented, and only when
// APPLICATIONINSIGHTS_CONNECTION_STRING is configured - with no connection string nothing is
// registered and the API starts exactly as it did before, which is what Development and the test
// suite rely on. See InventoryApi.Observability.ObservabilityServiceCollectionExtensions and
// README.md § Observability and error diagnostics.
builder.Services.AddInventoryApiTelemetry(builder.Configuration);

// Authentication (issue #38, extended by issue #46). Every environment except the dedicated
// end-to-end testing host registers exactly the real Microsoft Entra JwtBearer scheme this line
// always registered; that host, and only that host, registers the synthetic E2E test scheme
// instead. The decision is made from the hosting environment before any request exists and
// cannot be influenced by request input - see InventoryApi.Auth.E2ETesting.
builder.Services.AddInventoryApiAuthentication(builder.Configuration, builder.Environment);

// Authorization (issues #336 and #525). The default policy is unchanged - every business controller
// keeps [Authorize] plus [RequiredScope("access_as_user")] - and two families of named policy are
// added on top of it.
//
// The platform diagnostics policy (issue #336) is satisfied only by the separately configured Entra
// (tid, oid) pair below, never by a business role, a membership row or anything a request supplies.
// With nothing configured, which is the shipped state, the policy denies everyone.
//
// The capability policies (issue #525) are one per declared Capability, named after the published
// capability name and answered by BusinessCapabilityAuthorizationHandler from the single role →
// capability table of issue #521. They are registered for every capability, including the ones no
// endpoint declares yet, so an endpoint can only ever reference a policy that exists. Neither
// family grants anything the other decides: a capability policy holds no platform-admin
// requirement, and PlatformAdminPolicy grants no business capability.
var configuredPlatformAdmin = ConfiguredPlatformAdmin.From(
    builder.Configuration.GetSection(PlatformAdminOptions.SectionName).Get<PlatformAdminOptions>());

builder.Services.AddSingleton(configuredPlatformAdmin);
builder.Services.AddScoped<IAuthorizationHandler, PlatformAdminAuthorizationHandler>();
builder.Services.AddScoped<IAuthorizationHandler, BusinessCapabilityAuthorizationHandler>();

// Gives the two support-session authorization refusals a body carrying their stable code (issue
// #551), and leaves every other authorization failure exactly as the framework answers it - a bare
// 403 that tells a caller nothing about the role matrix. A singleton, because it holds no
// per-request state and delegates to the framework's own stateless default.
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, SupportSessionAuthorizationResultHandler>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(PlatformAdminPolicy.Name, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.AddRequirements(new PlatformAdminRequirement());
    });

    foreach (var capability in Capabilities.All)
    {
        options.AddPolicy(CapabilityPolicy.Name(capability), policy =>
        {
            policy.RequireAuthenticatedUser();
            policy.AddRequirements(new BusinessCapabilityRequirement(capability));
        });
    }
});

// Required by EntraActorIdentityAccessor, which reads the current request's ClaimsPrincipal.
builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddApplicationServices();
builder.Services.AddInfrastructureServices();

// Liveness/readiness health checks (issue #164). Liveness maps no checks at all - it only proves
// the process can answer HTTP requests. Readiness runs only checks tagged "ready": today that is
// database connectivity, the one dependency without which the API cannot serve normal requests.
// Nayax and Entra ID are deliberately excluded from both - an outage in either external system
// must not make the Inventory API report unhealthy.
builder.Services.AddHealthChecks()
    .AddCheck<AppDbContextHealthCheck>("database", tags: new[] { "ready" });

// Uploaded business documents (issue #39). The composition root is the only place that knows
// the host's content and web roots and reads configuration: the Application layer sees the
// IDocumentStorage port and the Infrastructure adapters see plain paths and settings, so
// neither depends on IWebHostEnvironment or IConfiguration.
//
// DocumentStorage:Provider selects the implementation - FileSystem (the default, and what every
// environment ran before this setting existed) or AzureBlob, which keys documents by the trusted
// current business. An invalid or incomplete AzureBlob configuration fails startup here rather
// than falling back to the local disk. The filesystem implementation stays registered as a
// concrete type under either provider, because documents already written to disk must remain
// readable.
builder.Services.AddDocumentStorage(
    builder.Configuration.GetSection(DocumentStorageOptions.SectionName).Get<DocumentStorageOptions>()
        ?? new DocumentStorageOptions(),
    new FileSystemDocumentStorageOptions
    {
        ContentRootPath = builder.Environment.ContentRootPath,
        WebRootPath = builder.Environment.WebRootPath,
    });

// Pending reimbursement XML files (issue #299). Same arrangement as document storage above: the
// composition root is the only place that knows the host's content and web roots, so the
// Infrastructure adapter sees plain paths and the pending-XML import use case sees only the
// IPendingReimbursementXmlSource port.
builder.Services.AddPendingReimbursementXmlSource(new PendingReimbursementXmlOptions
{
    ContentRootPath = builder.Environment.ContentRootPath,
    WebRootPath = builder.Environment.WebRootPath,
});

// Controlled RFC 7807 responses for Nayax upstream failures, and for the current business's own
// Nayax connection being unusable (issue #520).
builder.Services.AddProblemDetails();
// The two support-session refusals that are raised rather than decided (issue #551): a write that
// reached the persistence boundary or document storage, and a Nayax call whose credentials were
// never resolved. Registered first because it claims only its own two Application-owned types.
builder.Services.AddExceptionHandler<SupportSessionExceptionHandler>();
builder.Services.AddExceptionHandler<NayaxConnectionExceptionHandler>();
builder.Services.AddExceptionHandler<NayaxUpstreamExceptionHandler>();
builder.Services.AddExceptionHandler<DomainExceptionHandler>();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddInventoryApiSwagger();

var databaseConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=inventory.db";

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite(databaseConnectionString);
});

// The platform diagnostics read path (issue #336). The composition root hands the adapter the same
// configured data source AppDbContext uses; the adapter forces the connection open read-only and
// installs the SQLite protections, so no setting here can widen what it may read. The limits are
// the hard maxima - PlatformDiagnosticsQueryLimits.Create can only tighten them - and the audit
// port is satisfied by the ILogger adapter, which is in this layer because the audit event carries
// the request's correlation id.
builder.Services.AddPlatformDiagnostics(new SqliteDiagnosticsOptions
{
    ConnectionString = databaseConnectionString,
});

builder.Services.AddScoped<IPlatformDiagnosticsAudit, LoggingPlatformDiagnosticsAudit>();

// The member management audit trail (issue #533), on the same terms as the diagnostics one above:
// a structured log event at the boundary, which is the only layer that knows the request's
// correlation id, and never a row in the database the change was made in.
builder.Services.AddScoped<IMemberManagementAudit, LoggingMemberManagementAudit>();
// The invite audit trail (issue #531), on exactly the same terms. It is a separate port from the
// member one above because an invite change is a different event with different fields - and
// because neither entry type has a field for the invite token, which is what keeps the secret out
// of the audit trail structurally rather than by a sink's discretion.
builder.Services.AddScoped<IMemberInviteAudit, LoggingMemberInviteAudit>();
// The Owner's business-settings audit trail (issue #548). The port is satisfied in this layer for
// the reason the diagnostics audit's is: the event carries the request's correlation id, which is a
// transport fact. The event names the fields that changed and never a value - see
// LoggingBusinessSettingsAudit.
builder.Services.AddScoped<IBusinessSettingsAudit, LoggingBusinessSettingsAudit>();
// The self-service business application audit trail (issue #541), on the same terms again. It is a
// separate port from the three above because an application is a different event with different
// fields - and because its entry type has no field for a business name, a contact address or an
// ABN, which is what keeps a stranger's submitted personal and tax detail out of the audit trail
// structurally rather than by a sink's discretion.
builder.Services.AddScoped<IBusinessApplicationAudit, LoggingBusinessApplicationAudit>();
// The platform business-decision audit trail (issue #544), on the same terms again, and the first
// one about a platform-admin *write*. A separate port from the four above because a decision is a
// different event with different fields - and because its entry type has no field for a business
// name, a contact address, an applicant identity or the rejection reason's text, which is what keeps
// all four out of the audit trail structurally rather than by a sink's discretion.
builder.Services.AddScoped<IPlatformBusinessDecisionAudit, LoggingPlatformBusinessDecisionAudit>();
// The platform business-lifecycle audit trail (issue #545): deactivating a business, reactivating
// it, and releasing one of its members. A separate port again because a lifecycle action records a
// membership id and an invite count and no rejection reason, and because its entry type has no
// field for a name of any kind - not the business's, and not the released member's.
builder.Services.AddScoped<IPlatformBusinessLifecycleAudit, LoggingPlatformBusinessLifecycleAudit>();
// The support-session audit trail (issue #550): one event when a session starts and one per session
// ended. A separate port again, on the same terms as the three above, because a session is a
// different event with different fields - and because this is the one platform trail that carries the
// administrator's typed reason, which is the fact that makes read access to somebody else's business
// reviewable. Its entry type still has no field for a session id, a business name or anybody else's
// identity, which is what keeps all three out of the trail structurally rather than by a sink's
// discretion.
builder.Services.AddScoped<ISupportSessionAudit, LoggingSupportSessionAudit>();
// What else happens when a business is deactivated (issue #545's hook point): since issue #550, every
// active support session for that business ends. It is the single registration #545 declared this
// hook for, replaced here rather than by editing that transition - the hook runs inside the
// deactivation's own transaction, so a session ended by a deactivation that rolled back never
// happened and a committed deactivation cannot leave one running. A single registration rather than a
// collection, so "what runs here?" has exactly one answer, visible in this file.
builder.Services.AddScoped<IBusinessDeactivationHook, EndSupportSessionsOnBusinessDeactivation>();

// Whether self-service onboarding is switched on at all (issue #541). Read once here, because only
// the composition root reads configuration, and published as the Application layer's narrow flag
// port so the apply endpoint and GET /api/me/account-state cannot disagree about it. Off unless the
// setting says otherwise - an absent, blank or unparseable value leaves it off - and switching it
// on in production is gated on the human-verified release steps in docs/tenant-rollout.md. A
// singleton, because it is one startup decision rather than a per-request one.
builder.Services.AddSingleton<ISelfServiceOnboardingFlag>(
    new ConfiguredSelfServiceOnboardingFlag(builder.Configuration));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngularDevClient", policy =>
    {
        policy.WithOrigins("http://localhost:4200", "https://red-island-0c128c000.7.azurestaticapps.net")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

// Nayax Lynx HTTP client configuration (issue #49): one typed, validated options contract instead
// of the client separately reading IConfiguration. Since issue #520 the only setting it holds is
// BaseUrl - the operator id and the bearer token are each business's own, resolved per call from
// the current business's stored connection - so no credential is read here and there is nothing to
// fall back to. A missing or non-https BaseUrl fails registration here, at startup, rather than the
// first Nayax call; an operator id or token still in configuration is read only by the human-run
// migrate-nayax-connection command. See Inventory.Infrastructure.Nayax.NayaxLynxConfiguration and
// README.md § Configuration and secrets.
//
// The dedicated end-to-end testing host is the one exception (issue #46): it registers no Nayax
// HTTP client at all, so an E2E run has nothing configured that could reach the live operator
// account, and the workflows that legitimately read the fleet see a business with no machines.
if (E2ETestEnvironment.IsEnabled(builder.Environment))
{
    builder.Services.AddScoped<INayaxLynxClient, E2ETestNayaxLynxClient>();
}
else
{
    var nayaxLynxOptions = builder.Configuration.GetSection(NayaxLynxOptions.SectionName).Get<NayaxLynxOptions>()
        ?? new NayaxLynxOptions();
    builder.Services.AddNayaxLynxClient(nayaxLynxOptions);
}

// Encryption of each business's own stored Nayax access token (issue #518). Registered for every
// environment, including the E2E host: the keys are secret configuration this root reads, and with
// the section absent the registration is the fail-closed protector, so the API starts normally and
// only storing or reading a per-business token fails - which, since issue #520, is what every
// Nayax call needs, so an environment that uses Nayax must have a key provisioned. See
// Inventory.Infrastructure.Nayax.NayaxTokenProtectionOptions and README.md § Configuration and
// secrets.
builder.Services.AddNayaxTokenProtection(
    builder.Configuration.GetSection(NayaxTokenProtectionOptions.SectionName).Get<NayaxTokenProtectionOptions>()
        ?? new NayaxTokenProtectionOptions());

// Tenancy (issue #64). Claims parsing stays at this boundary: EntraActorIdentityAccessor is the
// only implementation of the Application's actor port, and the current-business abstraction
// itself (ICurrentBusinessProvider) is registered by AddApplicationServices().
builder.Services.AddScoped<IAuthenticatedActorAccessor, EntraActorIdentityAccessor>();

// The member's display-only name and email address (issue #524). The accessor is the second, and
// deliberately separate, claims adapter at this boundary: it reads the name and email claims only,
// takes the identity half of its answer from the accessor above so there is one implementation of
// "who is calling", and nothing it returns takes part in an access decision.
builder.Services.AddScoped<IActorProfileAccessor, EntraActorProfileAccessor>();

// Whether this request is inside a support session, and which business it reads (issue #551). The
// implementation is in this layer because joining a session is a transport fact - the session id
// arrives in the X-Support-Session header, and whether the caller is the platform administrator is
// this layer's own policy - and it is the only thing in the application that reads that header's
// value. Scoped and memoised, so the authorization handler, the business-scope middleware and the
// use cases all act on one answer.
builder.Services.AddScoped<ISupportSessionAccessor, RequestSupportSessionAccessor>();

// The host-side half of that refresh: one background drain that runs each queued write in its own
// new service scope, after the request that scheduled it has finished and with no business
// resolved. It is the only hosted service this application runs; see
// docs/architecture.md § Member display name and email for why this work is in-process rather than
// a WebJob or a Function.
builder.Services.AddHostedService<MemberProfileRefreshWorker>();

// The per-request current business, published by BusinessScopeMiddleware and read by
// AppDbContext's query filters and SaveChanges enforcement. Registered as the concrete type as
// well, because only the middleware may resolve it; everything else consumes the read-only port.
builder.Services.AddScoped<BusinessScope>();
builder.Services.AddScoped<IBusinessScope>(sp => sp.GetRequiredService<BusinessScope>());

// The per-request business time zone (issue #499), published by the same middleware from the same
// trusted current business and read by the scoped IBusinessCalendar. Registered here, beside the
// business scope, and as the concrete type as well for the same reason: only the middleware may
// publish it, and every consumer reads the read-only port. A request that resolves no business
// resolves no zone either, and the calendar then fails closed instead of guessing one.
builder.Services.AddScoped<BusinessTimeZoneScope>();
builder.Services.AddScoped<IBusinessTimeZoneProvider>(sp => sp.GetRequiredService<BusinessTimeZoneScope>());

// No EF persistence adapter is registered here any more. Issue #307 moved AppDbContext, the EF
// entities and the migrations into Inventory.Infrastructure, issue #308 the ten reporting fact
// providers, and issue #309 the remaining feature stores, so AddInfrastructureServices() above
// registers every Application persistence port - each Scoped, exactly as its registration here
// was - and this file keeps only the provider decision: the AddDbContext/UseSqlite call and the
// connection string a host that calls AddInfrastructureServices() must still make.

var app = builder.Build();

// Schema handling at startup (issue #64, revised by issue #201). Development, Testing, and
// Production apply pending migrations automatically; any other environment applies nothing and
// fails closed unless explicitly opted in. A migration failure always stops startup rather than
// serving requests against a schema its code does not match. See DatabaseSchemaStartup (including
// its concurrency note) and docs/tenant-rollout.md.
//
// Schema migrations never assign tenant ownership or perform the business backfill - that is
// exclusively the human-invoked `bootstrap-business` command. Some of them do rebuild tables and
// copy persisted rows (the NayaxSales re-key); the explicit `migrate-database` command remains
// available to inspect or apply them by hand for diagnostics.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();

    DatabaseSchemaStartup.EnsureSchema(db, app.Environment, app.Configuration, loggerFactory);

    TenantOwnershipReadiness.Report(db, loggerFactory);
}

// Isolated end-to-end test data (issue #46). This does nothing at all unless the process was
// started as the dedicated E2E host, and it writes only its own two synthetic businesses and
// their catalogue into whatever disposable database that host was pointed at. No other
// environment reaches it, and it never performs a backfill or touches an existing row.
await E2ETestFixture.SeedAsync(
    app.Services,
    app.Environment,
    app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InventoryApi.E2ETestFixture"),
    CancellationToken.None);

// The support-session audit trail (issue #551, corrected by issue #672), ahead of everything else
// so that one event records what a request from outside the business asked for and what it was
// actually answered. Both halves of that need this position: a request the authorization middleware
// refuses - a capability the session does not hold, or a write - is refused below here and would
// otherwise never be recorded at all, and a request whose endpoint throws has its status decided by
// the exception handler below here, so anything inside it can only see the 200 the response still
// held. It emits nothing for every other request, and it cannot change a response: see
// SupportSessionAuditMiddleware.
app.UseMiddleware<SupportSessionAuditMiddleware>();

// First in the pipeline so exceptions from controllers, services, and the Nayax
// client are all caught.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Inventory API v1"));
}

// No static-file middleware: the API serves no public assets (the Angular application is a
// separate Azure Static Web App), and static-file middleware does not run controller
// authorization. Uploaded purchase and operating-expense documents are stored outside the
// web root (see FileSystemDocumentStorage) and are only readable through the [Authorize]d
// endpoints, so no business document has an anonymous URL.
app.UseCors("AllowAngularDevClient");
app.UseAuthentication();

// Endpoint authorization: [Authorize], the platform diagnostics policy and the capability policies
// of issue #525. The order with the business-scope middleware below is deliberate and is pinned by
// CapabilityAuthorizationApiTests: authorization runs first, so a caller the capability policy
// refuses never reaches the membership middleware, the endpoint or a use case, and the 403 they get
// is the policy's bare one rather than the middleware's membership sentence.
//
// Ordering is not what makes the capability decision safe, though. The capability handler resolves
// the caller's membership itself, through the same per-request memoised ICurrentBusinessProvider the
// middleware uses, so no capability policy can pass before the membership is resolved however this
// pipeline is later rearranged - the same reason the diagnostics bypass re-evaluates its own policy
// instead of inferring one from where it sits here.
app.UseAuthorization();

// After authentication, so the caller's claims exist, and before the endpoint, so an
// authenticated caller with no business membership is refused before any action reads data. It
// publishes the resolved business and its time zone, which authorization does not: a capability
// policy answers "may this role do that?" and this middleware answers "whose data is this?".
app.UseMiddleware<BusinessScopeMiddleware>();

// Health checks (issue #164). Anonymous by design so Azure App Service and other operators can
// probe them without a bearer token; BusinessScopeMiddleware leaves unauthenticated requests
// alone, so these never trip the 403 "no business membership" path either. /health/live runs no
// checks (Predicate returns false for every registration), so it reports the process is up
// regardless of the database or any other dependency. /health/ready runs only checks tagged
// "ready" and returns 503 when one of them is unhealthy - see AppDbContextHealthCheck.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
}).AllowAnonymous();

app.MapControllers();

app.Run();

return 0;

// Exposed so InventoryApi.Tests can host the API with WebApplicationFactory<Program>.
public partial class Program;
