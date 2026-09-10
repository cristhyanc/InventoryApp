using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class ReportingServiceTests
{
    private static AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new AppDbContext(options);
    }

    private static ReportingService Reporting(AppDbContext db, INayaxLynxClient nayaxLynxClient = null,
        ISiteCommissionService siteCommissionService = null)
    {
        var commissions = siteCommissionService is null ? new Mock<ISiteCommissionService>() : null;
        commissions?.Setup(x => x.GetReportAsync(
                It.IsAny<DateTime>(), It.IsAny<DateTime>(), It.IsAny<long?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((DateTime from, DateTime to, long? _, CancellationToken _) =>
                new SiteCommissionReportDto(from, to, []));
        return new ReportingService(db, new NayaxProcessingFeeService(db),
            siteCommissionService ?? commissions!.Object, nayaxLynxClient);
    }

    [Fact]
    public void Reporting_service_resolves_with_required_financial_dependencies()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddScoped<INayaxLynxClient>(_ => new TransactionTestNayaxClient());
        services.AddScoped<INayaxProcessingFeeService, NayaxProcessingFeeService>();
        services.AddScoped<ISiteCommissionService, SiteCommissionService>();
        services.AddScoped<IReportingService, ReportingService>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.IsType<ReportingService>(scope.ServiceProvider.GetRequiredService<IReportingService>());
    }

    [Fact]
    public async Task GetDailyAsync_returns_empty_report_for_blank_period()
    {
        using var db = CreateDbContext();
        var report = await Reporting(db).GetDailyAsync(new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 1)));

        Assert.NotNull(report);
        Assert.Empty(report.Rows);
        Assert.NotNull(report.Totals);
    }

    [Fact]
    public async Task GetReconciliationAsync_handles_missing_imported_reimbursements_without_throwing()
    {
        using var db = CreateDbContext();
        db.NayaxSales.Add(new NayaxSales
        {
            TransactionID = 2, MachineID = 10, SettlementValue = 50m,
            MachineAuthorizationTime = new DateTime(2025, 8, 10),
            PaymentMethod = "Card", TransactionStatusId = NayaxTransactionStatusIds.Completed
        });
        await db.SaveChangesAsync();

        var report = await Reporting(db).GetReconciliationAsync(
            new ReportingFilterDto(new DateTime(2025, 8, 1), new DateTime(2025, 8, 31)));

        Assert.NotNull(report);
        Assert.Equal(0m, report.ImportedReimbursement);
        Assert.True(report.DataQuality.Notes?.Any(note => note.Contains("No imported reimbursement row matched", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private sealed class TransactionTestNayaxClient : INayaxLynxClient
    {
        public Task<List<NayaxDevice>> GetDevicesAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxDevice>());
        public Task<List<NayaxMachine>> GetMachinesAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxMachine>());
        public Task<List<NayaxMachineProduct>> GetMachineProductsAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new List<NayaxMachineProduct>());
        public Task<List<NayaxMachineProduct>> CreateMachineProductsAsync(long machineId, List<NayaxMachineProduct> products, CancellationToken ct = default) => Task.FromResult(products);
        public Task<List<NayaxProduct>> GetProductsAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxProduct>());
        public Task<List<NayaxProductGroup>> GetProductGroupssAsync(CancellationToken ct = default) => Task.FromResult(new List<NayaxProductGroup>());
        public Task<List<NayaxLastSalesReport>> GetMachineLastSalesAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new List<NayaxLastSalesReport>());
        public Task<NayaxMachine> GetMachineAsync(long machineId, CancellationToken ct = default) => Task.FromResult(new NayaxMachine { MachineID = machineId });
    }
}
