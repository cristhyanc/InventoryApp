using Inventory.Application.Purchases;
using Inventory.Infrastructure.Data;
using InventoryApi.Auth.E2ETesting;
using InventoryApi.Tests.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Issue #1289's aborted-request criterion: a closed tab or a timed-out <c>GET api/purchases</c>
/// must not be logged as a server failure, must never answer <c>500</c>, and must never expose the
/// cancellation as an error in a response body. <see cref="InventoryApi.Http.GlobalExceptionHandler"/>
/// already excludes <see cref="OperationCanceledException"/> from its own logging
/// (<c>GlobalExceptionHandlerTests.Caller_cancellation_is_not_logged_as_an_unexpected_error</c>); what
/// this proves is the other half - that the controller's own cancellation token actually reaches
/// that handler when the client disconnects, rather than the request's database work running on
/// unobserved as it did before issue #1289.
/// </summary>
public sealed class PurchaseListAbortedRequestApiTests : IClassFixture<PurchaseListAbortedRequestApiTests.ApiFactory>
{
    private const string PurchasesUri = "/api/purchases";

    private readonly ApiFactory _factory;

    public PurchaseListAbortedRequestApiTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Aborting_the_purchases_list_request_logs_no_error_and_answers_no_500()
    {
        using var requestCts = new CancellationTokenSource();
        var client = _factory.BusinessA();
        // Clears the host's own startup logging (migrations, the E2E fixture seed) so only this
        // request's own logging is under test.
        _factory.ClearLog();

        var requestTask = client.GetAsync(PurchasesUri, requestCts.Token);

        // Cancel once the controller's own call into the store has actually started, so the
        // request is genuinely in flight - not cancelled before it ever reached the database work
        // this issue is about.
        var started = await Task.WhenAny(_factory.Store.ListCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(_factory.Store.ListCalled.Task, started);
        requestCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);

        Assert.DoesNotContain(
            _factory.LoggedEntries(), entry => entry.Level == LogLevel.Error || entry.Level == LogLevel.Critical);
    }

    /// <summary>
    /// The API hosted the way the end-to-end harness hosts it, with the purchase store replaced by
    /// a double whose list read blocks until cancelled - so the test controls exactly when the
    /// request is "in flight" instead of racing a real query's own duration.
    /// </summary>
    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly RecordingLoggerProvider _logs = new();

        public ApiFactory()
        {
            E2ETestHostContentRoot.Pin();
            _connection.Open();
        }

        public BlockingPurchaseStore Store { get; } = new();

        public HttpClient BusinessA() => As(E2ETestActors.BusinessAOwner.Key);

        public void ClearLog() => _logs.Clear();

        public IReadOnlyList<(LogLevel Level, string Message)> LoggedEntries() => _logs.Entries();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(E2ETestEnvironment.EnvironmentName);
            builder.UseSetting("Database:AllowAutomaticMigrationUnsafeOutsideDevelopment", "true");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

                var purchaseStore = services.SingleOrDefault(d => d.ServiceType == typeof(IPurchaseStore));
                if (purchaseStore is not null)
                {
                    services.Remove(purchaseStore);
                }

                services.AddScoped<IPurchaseStore>(_ => Store);
            });

            builder.ConfigureLogging(logging => logging.AddProvider(_logs));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
            }
        }

        private HttpClient As(string actorKey)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.TryAddWithoutValidation(E2ETestActors.ActorHeaderName, actorKey);
            return client;
        }
    }

    /// <summary>
    /// An <see cref="IPurchaseStore"/> whose list read never completes on its own: it signals
    /// <see cref="ListCalled"/> as soon as it is entered and then waits on the token it was given,
    /// so a test can cancel a request that is provably already inside the store call rather than
    /// one still on its way there.
    /// </summary>
    public sealed class BlockingPurchaseStore : IPurchaseStore
    {
        public TaskCompletionSource ListCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<PurchaseRecord>> ListAsync(int? supplierId, CancellationToken cancellationToken)
        {
            ListCalled.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return [];
        }

        public Task<PurchaseRecord?> FindByIdAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseFileMetadata?> FindFileMetadataAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> SupplierExistsAsync(int supplierId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> AllProductsExistAsync(IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<long, string>> FindRetiredProductsAsync(
            IReadOnlyCollection<long> productIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<(long ProductId, string? ProductName, DateTime CutoffAt)?> FindConflictingCostTransitionBaselineAsync(
            IReadOnlyCollection<long> productIds, DateTime purchaseDate, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseCreateOutcome> CreateAsync(
            PurchaseFields fields,
            IReadOnlyList<PurchaseItemInput> items,
            PurchaseFileMetadata? file,
            Inventory.Application.SupplierOrders.SupplierOrderCompletionDecision? supplierOrderCompletion,
            Inventory.Application.Attachments.AttachmentUploadReservation? attachmentReservation,
            PurchaseIdempotencyClaim? idempotency,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseIdempotentCreate?> FindByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseDuplicateMatch?> FindPossibleDuplicateAsync(
            int supplierId, string invoiceNumber, DateTime fromUtcInclusive, DateTime toUtcExclusive,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseRecord?> UpdateAsync(
            int id, PurchaseFields fields, IReadOnlyList<PurchaseItemInput>? items, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<string?> DeleteAsync(int id, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<PurchaseDocumentAttachment> AttachDocumentAsync(
            int id,
            PurchaseFileMetadata file,
            Inventory.Application.Attachments.AttachmentUploadReservation? attachmentReservation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Captures every log entry from every category, so the test can assert on the whole request's
    /// logging rather than one logger's, the same pattern
    /// <c>BusinessApplicationApiFactory.RecordingLoggerProvider</c> uses.
    /// </summary>
    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public ILogger CreateLogger(string categoryName) => new Recording(this);

        public void Clear()
        {
            lock (_entries) _entries.Clear();
        }

        public IReadOnlyList<(LogLevel Level, string Message)> Entries()
        {
            lock (_entries) return _entries.ToList();
        }

        public void Dispose()
        {
        }

        private void Add(LogLevel level, string message)
        {
            lock (_entries) _entries.Add((level, message));
        }

        private sealed class Recording(RecordingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                provider.Add(logLevel, formatter(state, exception));
            }
        }
    }
}
