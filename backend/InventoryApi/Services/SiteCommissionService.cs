using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace InventoryApi.Services;

public sealed class SiteCommissionService : ISiteCommissionService
{
    private const decimal Tolerance = 0.01m;
    private readonly AppDbContext _db;
    private readonly INayaxLynxClient _nayax;

    public SiteCommissionService(AppDbContext db, INayaxLynxClient nayax)
    {
        _db = db;
        _nayax = nayax;
    }

    public async Task<SiteCommissionReportDto> GetReportAsync(DateTime from, DateTime to, long? siteId, CancellationToken cancellationToken = default)
    {
        from = from.Date;
        to = to.Date;
        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(x => x.CustomerID.HasValue && (!siteId.HasValue || x.CustomerID == siteId))
            .ToList();
        var siteMachines = machines.GroupBy(x => x.CustomerID!.Value).ToList();
        var ids = machines.Select(x => x.MachineID).ToList();
        var sales = ids.Count == 0 ? [] : await _db.NayaxSales.AsNoTracking()
            .Where(x => ids.Contains(x.MachineID) && x.MachineAuthorizationTime >= from && x.MachineAuthorizationTime < to.AddDays(1))
            .Where(NayaxTransactionStatusClassifier.CompletedSalePredicate)
            .ToListAsync(cancellationToken);
        var agreements = await _db.SiteCommissionAgreements.AsNoTracking()
            .Where(x => !siteId.HasValue || x.SiteId == siteId)
            .OrderBy(x => x.EffectiveFrom).ToListAsync(cancellationToken);
        var payments = await _db.CommissionPayments.AsNoTracking()
            .Where(x => (!siteId.HasValue || x.SiteId == siteId) && x.PeriodStart == from && x.PeriodEnd == to)
            .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id).ToListAsync(cancellationToken);

        var rows = new List<SiteCommissionRowDto>();
        foreach (var site in siteMachines)
        {
            var siteAgreements = agreements.Where(x => x.SiteId == site.Key).ToList();
            var siteSales = sales.Where(x => site.Any(m => m.MachineID == x.MachineID)).ToList();
            var salesByMachine = siteSales.GroupBy(x => x.MachineID);
            var machineRows = new List<SiteCommissionMachineDto>();
            var dataQuality = new List<string>();
            var effectiveRates = new HashSet<decimal>();
            decimal gross = 0, card = 0, cash = 0, eligible = 0, due = 0;
            foreach (var machineSales in salesByMachine)
            {
                decimal machineGross = 0, machineCard = 0, machineCash = 0, machineEligible = 0, machineDue = 0;
                foreach (var sale in machineSales)
                {
                    SiteCommissionAgreement? agreement;
                    try
                    {
                        agreement = EffectiveFinancialConfiguration.ResolveAgreement(
                            siteAgreements, site.Key, sale.MachineAuthorizationTime);
                    }
                    catch (InvalidOperationException)
                    {
                        dataQuality.Add("Overlapping commission agreements cover one or more sales.");
                        continue;
                    }
                    if (agreement is null)
                    {
                        if (siteAgreements.Count > 0)
                            dataQuality.Add("Commission agreements exist but do not cover one or more sales.");
                        continue;
                    }
                    effectiveRates.Add(agreement.CommissionRate);
                    var paymentType = PaymentMethodClassifier.Classify(sale.PaymentMethod);
                    var saleEligible = SiteCommissionCalculator.EligibleSales(agreement.Basis, sale.SettlementValue, paymentType);
                    machineGross += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Card) machineCard += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Cash) machineCash += sale.SettlementValue;
                    machineEligible += saleEligible;
                    machineDue += SiteCommissionCalculator.CommissionAmount(agreement, sale.SettlementValue, paymentType);
                }
                var machine = site.First(x => x.MachineID == machineSales.Key);
                machineRows.Add(new(machine.MachineID, machine.MachineName ?? $"Machine {machine.MachineID}", machineSales.Count(), machineGross, machineCard, machineCash, machineEligible, machineDue));
                gross += machineGross; card += machineCard; cash += machineCash; eligible += machineEligible; due += machineDue;
            }
            var current = siteAgreements.LastOrDefault(x => x.EffectiveFrom <= to) ?? siteAgreements.LastOrDefault();
            if (effectiveRates.Count > 1)
                dataQuality.Add("Multiple commission rates were used in this period; the displayed rate is representative.");
            var paidRows = payments.Where(x => x.SiteId == site.Key).ToList();
            var paid = paidRows.Sum(x => x.Amount);
            var outstanding = due - paid;
            var productRows = siteSales
                .GroupBy(x => string.IsNullOrWhiteSpace(x.ProductName) ? "Unmapped product" : NayaxProductMatcher.NormalizeName(x.ProductName))
                .Select(x => new SiteCommissionProductDto(x.Key, x.Count(), x.Sum(sale => sale.SettlementValue)))
                .OrderByDescending(x => x.TotalSales)
                .ToList();
            DateTime? dueDate = current?.PaymentDueDaysAfterPeriodEnd is int days ? to.AddDays(days) : null;
            var status = due == 0m ? "—" : paid >= due - Tolerance ? "Paid" : paid > Tolerance ? "Partially Paid" :
                dueDate.HasValue && DateTime.Today > dueDate ? "Overdue" : "Due";
            rows.Add(new(site.Key, SiteNameResolver.FromMachines(site, site.Key),
                from, to, current?.Frequency ?? CommissionFrequency.None, current?.Basis ?? CommissionBasis.GrossSales, gross, card, cash, eligible,
                current?.CommissionRate ?? 0m, due, paid, outstanding, dueDate, status, machineRows, productRows, paidRows,
                dataQuality.Distinct().Any() ? string.Join(" ", dataQuality.Distinct()) : null));
        }
        return new(from, to, rows.OrderBy(x => x.SiteName).ToList());
    }
}
