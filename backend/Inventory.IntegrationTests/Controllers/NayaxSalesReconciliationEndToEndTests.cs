using System.Net;
using System.Text;
using Inventory.Application.Nayax;
using Inventory.Application.NayaxReconciliation;
using Inventory.Domain.Nayax;
using Inventory.Domain.Providers;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Inventory.Infrastructure.Nayax;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// The hourly Nayax sales reconciliation (issue #1104) run the way production runs it: through the
/// real composition root, the real per-business worker scopes, the real
/// <c>CurrentBusinessProvider</c>, the real credential provider and the real typed Nayax client over
/// a stubbed socket - and, above all, with <strong>no signed-in user anywhere</strong>.
///
/// <para>This is the regression test for the production failure where every sweep ended on
/// <see cref="Inventory.Application.Tenancy.BusinessAccessDeniedException"/>: the use case asked
/// <see cref="Inventory.Application.Tenancy.ICurrentBusinessProvider.RequireBusinessIdAsync"/> for its
/// business, and the provider only knew how to answer from a member's membership or a support
/// session - neither of which a background worker has - so "Last reconciliation succeeded" stayed
/// "Never", the business never counted as live via SQS, and every Dashboard visit fell back to a full
/// Nayax <c>lastSales</c> synchronization. The existing reconciliation tests supplied the business
/// through a fake provider and the composition tests never ran a reconciliation, which is how it
/// shipped.</para>
///
/// <para>Two businesses, each with its own credentials and its own enabled sender, so the run is
/// also shown to read each business with its own token and to record each business's success on its
/// own row only.</para>
/// </summary>
public sealed class NayaxSalesReconciliationEndToEndTests
    : IClassFixture<NayaxSalesReconciliationEndToEndTests.ApiFactory>
{
    private readonly ApiFactory _factory;

    public NayaxSalesReconciliationEndToEndTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_sweep_with_no_signed_in_user_reconciles_every_enabled_business_and_records_its_success()
    {
        await _factory.SeedAsync();
        var runner = _factory.Services.GetRequiredService<NayaxSalesReconciliationRunner>();

        var sweep = await runner.SweepOnceAsync(CancellationToken.None);

        Assert.Null(sweep.EligibilityFailure);
        Assert.Equal(2, sweep.EligibleBusinesses);
        Assert.Equal(0, sweep.Failed);
        Assert.Equal(0, sweep.Skipped);
        Assert.Equal(2, sweep.Succeeded);

        // Each business's own row, and only that row, records the success that makes it live.
        await using var db = _factory.Database();
        var senders = await db.BusinessNayaxSqsSenders
            .AsNoTracking()
            .OrderBy(sender => sender.BusinessId)
            .ToListAsync();
        Assert.Equal([ApiFactory.BusinessA, ApiFactory.BusinessB], senders.Select(sender => sender.BusinessId));
        Assert.All(senders, sender => Assert.NotNull(sender.LastReconciliationSucceededAtUtc));

        // Each business was read with its own token: the run for one business never borrowed the
        // other's credentials, and both actually reached the (stubbed) provider.
        Assert.Contains(_factory.Handler.Requests, request =>
            request.Authorization == $"Bearer {ApiFactory.TokenA}"
            && request.Url!.EndsWith($"/machines/{ApiFactory.MachineA}/lastSales", StringComparison.Ordinal));
        Assert.Contains(_factory.Handler.Requests, request =>
            request.Authorization == $"Bearer {ApiFactory.TokenB}"
            && request.Url!.EndsWith($"/machines/{ApiFactory.MachineB}/lastSales", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Handler.Requests, request =>
            request.Authorization == $"Bearer {ApiFactory.TokenA}"
            && request.Url!.Contains($"/machines/{ApiFactory.MachineB}/", StringComparison.Ordinal));
        Assert.DoesNotContain(_factory.Handler.Requests, request =>
            request.Authorization == $"Bearer {ApiFactory.TokenB}"
            && request.Url!.Contains($"/machines/{ApiFactory.MachineA}/", StringComparison.Ordinal));
    }

    public sealed class ApiFactory : WebApplicationFactory<Program>
    {
        public const int BusinessA = 1;
        public const int BusinessB = 2;
        public const long MachineA = 941001;
        public const long MachineB = 941002;

        // Obviously fake credentials and sender ids. No real token, operator or AWS id belongs here.
        public const string TokenA = "fake-reconciliation-token-a";
        public const string TokenB = "fake-reconciliation-token-b";
        private const string OperatorA = "1001001001";
        private const string OperatorB = "2002002002";
        private const string EncryptionKeyId = "2026-11";

        private static readonly DateTime CreatedAt = new(2026, 11, 1, 1, 0, 0, DateTimeKind.Utc);

        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly NayaxTokenProtectionOptions _protection = Protection();
        private readonly SemaphoreSlim _seedGate = new(1, 1);
        private bool _seeded;

        public ApiFactory() => _connection.Open();

        public StubNayaxHandler Handler { get; } = new();

        /// <summary>An unrestricted context for arranging and inspecting state outside a request.</summary>
        public AppDbContext Database()
        {
            // Resolving Services builds and starts the host, which applies the migrations.
            _ = Services;
            return TestAppDbContext.Unrestricted(
                new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        }

        public async Task SeedAsync()
        {
            await _seedGate.WaitAsync();
            try
            {
                if (_seeded)
                {
                    return;
                }

                await using var db = Database();
                db.Businesses.AddRange(BusinessRow(BusinessA, "Vending A"), BusinessRow(BusinessB, "Vending B"));
                db.BusinessNayaxConnections.AddRange(
                    Connection(BusinessA, OperatorA, TokenA),
                    Connection(BusinessB, OperatorB, TokenB));
                db.BusinessNayaxSqsSenders.AddRange(
                    Sender(BusinessA, "AIDATESTSENDERA00001"),
                    Sender(BusinessB, "AIDATESTSENDERB00002"));

                await db.SaveChangesAsync();
                _seeded = true;
            }
            finally
            {
                _seedGate.Release();
            }
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Testing applies pending migrations at startup, so the schema is the migration history.
            builder.UseEnvironment("Testing");

            builder.ConfigureServices(services =>
            {
                var dbContextOptions = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
                if (dbContextOptions is not null)
                {
                    services.Remove(dbContextOptions);
                }

                services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
            });

            builder.ConfigureTestServices(services =>
            {
                // The real AES-GCM protector with a test key, so the seeded tokens are genuinely
                // encrypted at rest rather than refused by the fail-closed unconfigured one.
                services.AddSingleton<INayaxTokenProtector>(new AesGcmNayaxTokenProtector(_protection));

                // The production typed client, wrappers and resilience handler, with only the socket
                // replaced, so no request here can reach a real Nayax endpoint.
                services.AddHttpClient(nameof(INayaxLynxClient))
                    .ConfigurePrimaryHttpMessageHandler(() => Handler);
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _connection.Dispose();
                _seedGate.Dispose();
            }
        }

        private static NayaxTokenProtectionOptions Protection()
        {
            var protection = new NayaxTokenProtectionOptions { ActiveKeyId = EncryptionKeyId };
            protection.Keys[EncryptionKeyId] = Convert.ToBase64String(
                Enumerable.Repeat((byte)11, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());

            return protection;
        }

        private BusinessNayaxConnection Connection(int businessId, string operatorId, string accessToken)
        {
            var encrypted = new AesGcmNayaxTokenProtector(_protection)
                .Protect(accessToken, BusinessId.From(businessId));

            return new BusinessNayaxConnection
            {
                BusinessId = businessId,
                OperatorId = operatorId,
                AccessTokenCiphertext = encrypted.Ciphertext,
                EncryptionKeyId = encrypted.KeyId,
                Status = NayaxConnectionStatus.Ready,
                CredentialRevision = 1,
                LastTestedAtUtc = CreatedAt,
                MachineChecksPending = false,
                UpdatedAtUtc = CreatedAt,
            };
        }

        private static BusinessNayaxSqsSender Sender(int businessId, string awsSenderId) =>
            new()
            {
                BusinessId = businessId,
                AwsSenderId = awsSenderId,
                IsEnabled = true,
                Revision = 1,
                CreatedAtUtc = CreatedAt,
                UpdatedAtUtc = CreatedAt,
            };

        private static Business BusinessRow(int id, string name) =>
            new()
            {
                Id = id,
                Name = name,
                TimeZoneId = "Australia/Sydney",
                Status = BusinessStatus.Active,
                SalesProvider = ExternalProvider.Nayax,
                CreatedAtUtc = CreatedAt,
            };

        /// <summary>
        /// Stands in for the Nayax socket: answers each operator's machine list with that operator's
        /// one machine and every <c>lastSales</c> read with no sales, and records each request's URL
        /// and <c>Authorization</c> header.
        /// </summary>
        public sealed class StubNayaxHandler : HttpMessageHandler
        {
            private readonly object _gate = new();
            private readonly List<(string? Url, string? Authorization)> _requests = [];

            public IReadOnlyList<(string? Url, string? Authorization)> Requests
            {
                get
                {
                    lock (_gate)
                    {
                        return _requests.ToList();
                    }
                }
            }

            /// <summary>
            /// Deliberately inert: <c>IHttpClientFactory</c> disposes a primary handler when its
            /// lifetime expires, and this one is shared by the whole fixture.
            /// </summary>
            protected override void Dispose(bool disposing)
            {
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var url = request.RequestUri?.ToString();
                var authorization = request.Headers.Authorization?.ToString();
                lock (_gate)
                {
                    _requests.Add((url, authorization));
                }

                var machineId = authorization == $"Bearer {TokenA}" ? MachineA : MachineB;
                var body = url is not null && url.EndsWith("/machines", StringComparison.Ordinal)
                    ? $"[{{\"MachineID\":{machineId},\"MachineName\":\"Machine {machineId}\"}}]"
                    : "[]";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    RequestMessage = request,
                });
            }
        }
    }
}
