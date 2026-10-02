using Inventory.Domain.FinancialConfiguration;
using InventoryApi.Adapters.Persistence;
using InventoryApi.Data;
using InventoryApi.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Agreement = Inventory.Domain.FinancialConfiguration.CommissionAgreement;
using Payment = Inventory.Domain.FinancialConfiguration.CommissionPayment;

namespace InventoryApi.Tests.Adapters.Persistence;

public sealed class FinancialAdapterTenancyTests : IDisposable
{
    private const long SiteId = 42;
    private const long MachineId = 10;
    private static readonly DateTime Day = new(2026, 9, 1);
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;

    public FinancialAdapterTenancyTests()
    {
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        using var seed = TestAppDbContext.Unrestricted(_options);
        seed.Database.EnsureCreated();
        seed.Businesses.AddRange(
            new Business { Id = 1, Name = "A", CreatedAtUtc = Day },
            new Business { Id = 2, Name = "B", CreatedAtUtc = Day });
        seed.SaveChanges();
        foreach (var businessId in new[] { 1, 2 })
        {
            seed.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = businessId,
                SiteId = SiteId,
                EffectiveFrom = Day,
                EffectiveTo = Day,
                CommissionRate = businessId / 10m,
                Basis = CommissionBasis.GrossSales
            });
            seed.CommissionPayments.Add(new InventoryApi.Models.CommissionPayment
            {
                BusinessId = businessId,
                SiteId = SiteId,
                PeriodStart = Day,
                PeriodEnd = Day,
                PaymentDate = Day,
                Amount = businessId
            });
            seed.ImportedFiles.Add(new ImportedFile
            {
                BusinessId = businessId,
                FileName = "synthetic.xml",
                FileHash = "same-external-hash",
                ImportedAt = Day,
                Reimbursements =
                [
                    new ImportedReimbursement
                    {
                        BusinessId = businessId,
                        ReimbursementStartDate = Day,
                        ReimbursementEndDate = Day,
                        Fees =
                        [
                            new ImportedFee
                            {
                                BusinessId = businessId,
                                FeesTypeId = "processing",
                                TotalSum = businessId,
                                TotalSumWithVat = businessId * 1.1m
                            }
                        ],
                        Devices =
                        [
                            new ImportedReimbursementDevice
                            {
                                BusinessId = businessId,
                                MachineNumber = "10",
                                ProcessingFee = businessId
                            }
                        ]
                    }
                ]
            });
            seed.NayaxSales.AddRange(
                Sale(businessId, 100, 12, businessId),
                Sale(businessId, 101, 55, 99m));
        }
        seed.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static NayaxSales Sale(int businessId, long transactionId, int status, decimal amount) =>
        new()
        {
            BusinessId = businessId,
            TransactionID = transactionId,
            MachineID = MachineId,
            TransactionStatusId = status,
            MachineAuthorizationTime = Day.AddHours(businessId),
            SettlementValue = amount,
            PaymentMethod = "Credit Card"
        };

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fee_facts_keep_reimbursements_fees_devices_and_completed_sales_in_one_business(int businessId)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        var provider = new EfNayaxProcessingFeeFactsProvider(db);
        foreach (var machineId in new long?[] { null, MachineId })
        {
            var facts = await provider.GetFactsAsync(Day, Day, machineId, CancellationToken.None);
            var reimbursement = Assert.Single(facts.Reimbursements);
            Assert.Equal((decimal)businessId, Assert.Single(reimbursement.Fees).TotalSum);
            Assert.Equal((decimal)businessId, Assert.Single(reimbursement.Devices).ProcessingFee);
            Assert.Equal(Day.AddHours(businessId), Assert.Single(facts.CompletedSales).MachineAuthorizationTime);
        }
        var missing = await provider.GetFactsAsync(Day, Day, 999, CancellationToken.None);
        Assert.Empty(missing.CompletedSales);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Commission_reads_and_completed_sale_predicate_exclude_other_business_and_pending_sales(int businessId)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        var store = new EfSiteCommissionStore(db);
        foreach (var siteId in new long?[] { null, SiteId })
        {
            Assert.Equal(businessId / 10m,
                Assert.Single(await store.GetAgreementsAsync(siteId, CancellationToken.None)).CommissionRate);
            Assert.Equal((decimal)businessId,
                Assert.Single(await store.GetPaymentsAsync(siteId, Day, Day, CancellationToken.None)).Amount);
        }
        var sales = await store.GetCompletedSalesAsync([MachineId], Day, Day.AddDays(1), CancellationToken.None);
        Assert.Equal((decimal)businessId, Assert.Single(sales).SettlementValue);
        Assert.True(await store.HasOverlappingAgreementAsync(SiteId, Day, Day, CancellationToken.None));
        Assert.Empty(await store.GetAgreementsAsync(999, CancellationToken.None));
        Assert.Empty(await store.GetPaymentsAsync(SiteId, Day.AddDays(1), Day.AddDays(1), CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task Overlap_lookup_cannot_be_satisfied_by_another_business(int businessId, int otherBusinessId)
    {
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            seed.SiteCommissionAgreements.Add(new SiteCommissionAgreement
            {
                BusinessId = otherBusinessId,
                SiteId = 99,
                EffectiveFrom = Day,
                CommissionRate = .2m,
                Basis = CommissionBasis.GrossSales
            });
            await seed.SaveChangesAsync();
        }
        using var db = TestAppDbContext.For(_options, businessId);
        Assert.False(await new EfSiteCommissionStore(db)
            .HasOverlappingAgreementAsync(99, Day, null, CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task Commission_writes_stamp_the_caller_and_leave_other_business_rows_unchanged(int businessId, int otherBusinessId)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        var store = new EfSiteCommissionStore(db);
        var agreement = await store.AddAgreementAsync(
            new Agreement(0, SiteId, Day.AddDays(1), null, .3m, CommissionFrequency.None,
                CommissionBasis.GrossSales, null, default, default), CancellationToken.None);
        var payment = await store.AddPaymentAsync(
            new Payment(0, SiteId, Day, Day, Day, 3m, "synthetic", default, default), CancellationToken.None);

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(businessId,
            (await verify.SiteCommissionAgreements.SingleAsync(row => row.Id == agreement.Id)).BusinessId);
        Assert.Equal(businessId,
            (await verify.CommissionPayments.SingleAsync(row => row.Id == payment.Id)).BusinessId);
        using var other = TestAppDbContext.For(_options, otherBusinessId);
        var otherStore = new EfSiteCommissionStore(other);
        Assert.Equal(otherBusinessId / 10m,
            Assert.Single(await otherStore.GetAgreementsAsync(SiteId, CancellationToken.None)).CommissionRate);
        Assert.Equal((decimal)otherBusinessId,
            Assert.Single(await otherStore.GetPaymentsAsync(SiteId, Day, Day, CancellationToken.None)).Amount);
    }

    [Fact]
    public async Task Unresolved_business_reads_no_financial_facts_and_cannot_write()
    {
        using (var db = TestAppDbContext.Denied(_options))
        {
            var facts = await new EfNayaxProcessingFeeFactsProvider(db)
                .GetFactsAsync(Day, Day, null, CancellationToken.None);
            Assert.Empty(facts.Reimbursements);
            Assert.Empty(facts.CompletedSales);
            var store = new EfSiteCommissionStore(db);
            Assert.Empty(await store.GetAgreementsAsync(null, CancellationToken.None));
            Assert.Empty(await store.GetPaymentsAsync(null, Day, Day, CancellationToken.None));
            Assert.Empty(await store.GetCompletedSalesAsync([MachineId], Day, Day.AddDays(1), CancellationToken.None));
            Assert.False(await store.HasOverlappingAgreementAsync(SiteId, Day, null, CancellationToken.None));
        }
        using (var db = TestAppDbContext.Denied(_options))
        {
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => new EfSiteCommissionStore(db)
                .AddAgreementAsync(new Agreement(0, SiteId, Day, null, .3m, CommissionFrequency.None,
                    CommissionBasis.GrossSales, null, default, default), CancellationToken.None));
        }
        using (var db = TestAppDbContext.Denied(_options))
        {
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => new EfSiteCommissionStore(db)
                .AddPaymentAsync(new Payment(0, SiteId, Day, Day, Day, 3m, null, default, default), CancellationToken.None));
        }
        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, await verify.SiteCommissionAgreements.CountAsync());
        Assert.Equal(2, await verify.CommissionPayments.CountAsync());
    }
}
