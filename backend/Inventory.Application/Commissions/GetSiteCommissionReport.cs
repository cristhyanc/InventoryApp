using Inventory.Application.Nayax;
using Inventory.Application.Sites;
using Inventory.Application.Time;
using Inventory.Domain.FinancialConfiguration;
using Inventory.Domain.Reporting.ProductMatching;

namespace Inventory.Application.Commissions;

public sealed class GetSiteCommissionReport : IGetSiteCommissionReport
{
    private readonly INayaxLynxClient _nayax;
    private readonly ISiteCommissionStore _store;
    private readonly ISiteNameResolver _siteNameResolver;
    private readonly IBusinessCalendar _businessCalendar;

    public GetSiteCommissionReport(
        INayaxLynxClient nayax,
        ISiteCommissionStore store,
        ISiteNameResolver siteNameResolver,
        IBusinessCalendar businessCalendar)
    {
        _nayax = nayax;
        _store = store;
        _siteNameResolver = siteNameResolver;
        _businessCalendar = businessCalendar;
    }

    public async Task<SiteCommissionReport> Handle(
        DateTime from,
        DateTime to,
        long? siteId,
        CancellationToken cancellationToken)
    {
        from = from.Date;
        to = to.Date;
        var machines = (await _nayax.GetMachinesAsync(cancellationToken))
            .Where(machine => machine.CustomerID.HasValue && (!siteId.HasValue || machine.CustomerID == siteId))
            .ToList();
        var siteMachines = machines.GroupBy(machine => machine.CustomerID!.Value).ToList();
        var ids = machines.Select(machine => machine.MachineID).ToList();
        var sales = ids.Count == 0
            ? []
            : await _store.GetCompletedSalesAsync(ids, from, to.AddDays(1), cancellationToken);
        var agreements = await _store.GetAgreementsAsync(siteId, cancellationToken);
        var payments = await _store.GetPaymentsAsync(siteId, from, to, cancellationToken);

        var rows = new List<SiteCommissionReportRow>();
        foreach (var site in siteMachines)
        {
            var siteAgreements = agreements.Where(agreement => agreement.SiteId == site.Key)
                .OrderBy(agreement => agreement.EffectiveFrom).ToList();
            var siteSales = sales.Where(sale => site.Any(machine => machine.MachineID == sale.MachineId)).ToList();
            var salesByMachine = siteSales.GroupBy(sale => sale.MachineId);
            var machineRows = new List<SiteCommissionMachineReportRow>();
            var dataQuality = new List<string>();
            var effectiveRates = new HashSet<decimal>();
            var hasConfigurationGap = false;
            var hasOverlap = false;
            decimal gross = 0, card = 0, cash = 0, eligible = 0, due = 0;
            foreach (var machineSales in salesByMachine)
            {
                decimal machineGross = 0, machineCard = 0, machineCash = 0, machineEligible = 0, machineDue = 0;
                var machineHasConfigurationGap = false;
                var machineHasOverlap = false;
                foreach (var sale in machineSales)
                {
                    var paymentType = PaymentMethodClassifier.Classify(sale.PaymentMethod);
                    machineGross += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Card) machineCard += sale.SettlementValue;
                    if (paymentType == NayaxPaymentType.Cash) machineCash += sale.SettlementValue;

                    CommissionAgreement? agreement;
                    try
                    {
                        agreement = EffectiveFinancialConfiguration.ResolveAgreement(
                            siteAgreements, site.Key, sale.MachineAuthorizationTime);
                    }
                    catch (InvalidOperationException)
                    {
                        dataQuality.Add("Overlapping commission agreements cover one or more sales.");
                        hasOverlap = true;
                        machineHasOverlap = true;
                        continue;
                    }
                    if (agreement is null)
                    {
                        if (siteAgreements.Count > 0)
                        {
                            dataQuality.Add("Commission agreements exist but do not cover one or more sales.");
                            hasConfigurationGap = true;
                            machineHasConfigurationGap = true;
                        }
                        continue;
                    }
                    effectiveRates.Add(agreement.CommissionRate);
                    var saleEligible = SiteCommissionCalculator.EligibleSales(
                        agreement.Basis, sale.SettlementValue, paymentType);
                    machineEligible += saleEligible;
                    machineDue += SiteCommissionCalculator.CommissionAmount(
                        agreement, sale.SettlementValue, paymentType);
                }
                var machine = site.First(item => item.MachineID == machineSales.Key);
                machineRows.Add(new(machine.MachineID, machine.MachineName ?? $"Machine {machine.MachineID}",
                    machineSales.Count(), machineGross, machineCard, machineCash, machineEligible, machineDue,
                    !machineHasConfigurationGap && !machineHasOverlap, machineHasConfigurationGap, machineHasOverlap));
                gross += machineGross;
                card += machineCard;
                cash += machineCash;
                eligible += machineEligible;
                due += machineDue;
            }

            var current = siteAgreements.LastOrDefault(agreement => agreement.EffectiveFrom <= to)
                ?? siteAgreements.LastOrDefault();
            if (effectiveRates.Count > 1)
                dataQuality.Add("Multiple commission rates were used in this period; the displayed rate is representative.");
            var paidRows = payments.Where(payment => payment.SiteId == site.Key).ToList();
            var paid = paidRows.Sum(payment => payment.Amount);
            var outstanding = due - paid;
            var productRows = siteSales
                .GroupBy(sale => string.IsNullOrWhiteSpace(sale.ProductName)
                    ? "Unmapped product"
                    : ProductMatcher.NormalizeName(sale.ProductName))
                .Select(group => new SiteCommissionProductReportRow(
                    group.Key, group.Count(), group.Sum(sale => sale.SettlementValue)))
                .OrderByDescending(product => product.TotalSales)
                .ToList();
            DateTime? dueDate = current?.PaymentDueDaysAfterPeriodEnd is int days ? to.AddDays(days) : null;
            var status = due == 0m ? "—" : paid >= due - 0.01m ? "Paid" : paid > 0.01m ? "Partially Paid" :
                dueDate.HasValue && _businessCalendar.Today > dueDate ? "Overdue" : "Due";
            var row = new SiteCommissionReportRow(
                site.Key,
                _siteNameResolver.Resolve(site.ToList(), site.Key),
                from,
                to,
                current?.Frequency ?? CommissionFrequency.None,
                current?.Basis ?? CommissionBasis.GrossSales,
                gross,
                card,
                cash,
                eligible,
                current?.CommissionRate ?? 0m,
                due,
                paid,
                outstanding,
                dueDate,
                status,
                machineRows,
                productRows,
                paidRows,
                dataQuality.Distinct().Any() ? string.Join(" ", dataQuality.Distinct()) : null)
            {
                HasConfigurationGap = hasConfigurationGap,
                HasOverlap = hasOverlap,
                UsesMultipleRates = effectiveRates.Count > 1
            };
            rows.Add(row);
        }

        return new SiteCommissionReport(from, to, rows.OrderBy(row => row.SiteName).ToList());
    }
}
