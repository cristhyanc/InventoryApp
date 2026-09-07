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
            .Where(x => (!siteId.HasValue || x.SiteId == siteId) && x.EffectiveFrom <= to && (x.EffectiveTo == null || x.EffectiveTo >= from))
            .OrderBy(x => x.EffectiveFrom).ToListAsync(cancellationToken);
        var payments = await _db.CommissionPayments.AsNoTracking()
            .Where(x => (!siteId.HasValue || x.SiteId == siteId) && x.PeriodStart == from && x.PeriodEnd == to)
            .OrderBy(x => x.PaymentDate).ThenBy(x => x.Id).ToListAsync(cancellationToken);

        var rows = new List<SiteCommissionRowDto>();
        foreach (var site in siteMachines)
        {
            var siteAgreements = agreements.Where(x => x.SiteId == site.Key).ToList();
            var salesByMachine = sales.Where(x => site.Any(m => m.MachineID == x.MachineID)).GroupBy(x => x.MachineID);
            var machineRows = new List<SiteCommissionMachineDto>();
            var dataQuality = new List<string>();
            decimal gross = 0, card = 0, cash = 0, eligible = 0, due = 0;
            foreach (var machineSales in salesByMachine)
            {
                decimal machineGross = 0, machineCard = 0, machineCash = 0, machineEligible = 0, machineDue = 0;
                foreach (var sale in machineSales)
                {
                    var matching = siteAgreements.Where(a => a.EffectiveFrom.Date <= sale.MachineAuthorizationTime.Date &&
                        (!a.EffectiveTo.HasValue || a.EffectiveTo.Value.Date >= sale.MachineAuthorizationTime.Date)).ToList();
                    if (matching.Count != 1)
                    {
                        dataQuality.Add(matching.Count == 0 ? "No commission agreement covers one or more sales." : "Overlapping commission agreements cover one or more sales.");
                        continue;
                    }
                    var agreement = matching[0];
                    var paymentType = PaymentMethodClassifier.Classify(sale.PaymentMethod);
                    var saleEligible = agreement.Basis switch
                    {
                        CommissionBasis.CardSales => paymentType == NayaxPaymentType.Card ? sale.SettlementValue : 0m,
                        CommissionBasis.SalesExGst => sale.SettlementValue - ReportingCalculations.GstFromInclusive(sale.SettlementValue),
                        _ => sale.SettlementValue
                    };
                    machineGross += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Card) machineCard += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Cash) machineCash += sale.SettlementValue;
                    machineEligible += saleEligible;
                    machineDue += saleEligible * agreement.CommissionRate;
                }
                var machine = site.First(x => x.MachineID == machineSales.Key);
                machineRows.Add(new(machine.MachineID, machine.MachineName ?? $"Machine {machine.MachineID}", machineSales.Count(), machineGross, machineCard, machineCash, machineEligible, machineDue));
                gross += machineGross; card += machineCard; cash += machineCash; eligible += machineEligible; due += machineDue;
            }
            var current = siteAgreements.LastOrDefault(x => x.EffectiveFrom <= to) ?? siteAgreements.LastOrDefault();
            if (current is null) dataQuality.Add("Site has no commission configuration.");
            var paidRows = payments.Where(x => x.SiteId == site.Key).ToList();
            var paid = paidRows.Sum(x => x.Amount);
            var outstanding = due - paid;
            DateTime? dueDate = current?.PaymentDueDaysAfterPeriodEnd is int days ? to.AddDays(days) : null;
            var status = due == 0m ? "—" : paid >= due - Tolerance ? "Paid" : paid > Tolerance ? "Partially Paid" :
                dueDate.HasValue && DateTime.Today > dueDate ? "Overdue" : "Due";
            rows.Add(new(site.Key, site.First().MachineName?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"Site {site.Key}",
                from, to, current?.Frequency ?? CommissionFrequency.None, current?.Basis ?? CommissionBasis.GrossSales, gross, card, cash, eligible,
                current?.CommissionRate ?? 0m, due, paid, outstanding, dueDate, status, machineRows, paidRows,
                dataQuality.Distinct().Any() ? string.Join(" ", dataQuality.Distinct()) : null));
        }
        return new(from, to, rows.OrderBy(x => x.SiteName).ToList());
    }
}
