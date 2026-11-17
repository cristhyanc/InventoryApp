using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Tenancy;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Reporting.Persistence;
using Inventory.Infrastructure.Data;
using Inventory.Infrastructure.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Agreement = Inventory.Domain.FinancialConfiguration.CommissionAgreement;

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
            new Business { Id = 1, Name = "A", Status = BusinessStatus.Active, CreatedAtUtc = Day },
            new Business { Id = 2, Name = "B", Status = BusinessStatus.Active, CreatedAtUtc = Day });
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
        }
        var sales = await store.GetCompletedSalesAsync([MachineId], Day, Day.AddDays(1), CancellationToken.None);
        Assert.Equal((decimal)businessId, Assert.Single(sales).SettlementValue);
        Assert.True(await store.HasOverlappingAgreementAsync(SiteId, Day, Day, CancellationToken.None));
        Assert.Empty(await store.GetAgreementsAsync(999, CancellationToken.None));
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

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(businessId,
            (await verify.SiteCommissionAgreements.SingleAsync(row => row.Id == agreement.Id)).BusinessId);
        using var other = TestAppDbContext.For(_options, otherBusinessId);
        var otherStore = new EfSiteCommissionStore(other);
        Assert.Equal(otherBusinessId / 10m,
            Assert.Single(await otherStore.GetAgreementsAsync(SiteId, CancellationToken.None)).CommissionRate);
    }

    /// <summary>
    /// Issue #1254: the agreement an end date is written to is found only through the central tenant
    /// filter, so the route value selects an agreement of the caller's own business and never a
    /// tenant. Each business holds an agreement here, so the other one's key is a real key that
    /// simply is not theirs - which is the case a filter has to answer, rather than a missing row.
    /// </summary>
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task Another_businesss_agreement_can_neither_be_found_nor_ended(int businessId, int otherBusinessId)
    {
        int otherAgreementId;
        using (var seed = TestAppDbContext.Unrestricted(_options))
        {
            otherAgreementId = (await seed.SiteCommissionAgreements
                .SingleAsync(row => row.BusinessId == otherBusinessId)).Id;
        }

        using var db = TestAppDbContext.For(_options, businessId);
        var store = new EfSiteCommissionStore(db);

        Assert.Null(await store.FindAgreementAsync(otherAgreementId, CancellationToken.None));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => store.SetAgreementEndDateAsync(
            otherAgreementId, Day.AddDays(5), Day, CancellationToken.None));

        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(
            Day,
            (await verify.SiteCommissionAgreements.SingleAsync(row => row.Id == otherAgreementId)).EffectiveTo);
    }

    /// <summary>
    /// The caller's own agreement is found by its key and ends on the written date, with the
    /// <c>UpdatedAt</c> stamp the use case sampled inside the write lock and nothing else changed.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task The_callers_own_agreement_is_found_and_ended_on_the_written_date(int businessId)
    {
        using var db = TestAppDbContext.For(_options, businessId);
        var store = new EfSiteCommissionStore(db);
        var own = await store.AddAgreementAsync(
            new Agreement(0, 77, Day.AddDays(1), null, .25m, CommissionFrequency.Monthly,
                CommissionBasis.GrossSales, 14, default, default),
            CancellationToken.None);
        var stamped = new DateTime(2026, 11, 16, 3, 0, 0, DateTimeKind.Utc);

        Assert.Null((await store.FindAgreementAsync(own.Id, CancellationToken.None))!.EffectiveTo);
        var ended = await store.SetAgreementEndDateAsync(
            own.Id, Day.AddDays(30), stamped, CancellationToken.None);

        Assert.Equal(Day.AddDays(30), ended.EffectiveTo);
        Assert.Equal(stamped, ended.UpdatedAt);
        Assert.Equal(.25m, ended.CommissionRate);
        Assert.Equal(Day.AddDays(1), ended.EffectiveFrom);
        Assert.Equal(14, ended.PaymentDueDaysAfterPeriodEnd);
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
            Assert.Empty(await store.GetCompletedSalesAsync([MachineId], Day, Day.AddDays(1), CancellationToken.None));
            Assert.False(await store.HasOverlappingAgreementAsync(SiteId, Day, null, CancellationToken.None));
        }
        using (var db = TestAppDbContext.Denied(_options))
        {
            await Assert.ThrowsAsync<CrossBusinessAccessException>(() => new EfSiteCommissionStore(db)
                .AddAgreementAsync(new Agreement(0, SiteId, Day, null, .3m, CommissionFrequency.None,
                    CommissionBasis.GrossSales, null, default, default), CancellationToken.None));
        }
        using var verify = TestAppDbContext.Unrestricted(_options);
        Assert.Equal(2, await verify.SiteCommissionAgreements.CountAsync());
    }
}
