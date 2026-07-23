using InventoryApi.Data;
using InventoryApi.DTOs;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;

namespace InventoryApi.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MachinesController(AppDbContext db, INayaxLynxClient nayaxLynxClient) : ControllerBase
    {
        [HttpGet("{id:long}")]
        public async Task<ActionResult<Machine>> GetById(long id)
        {
            var nayaxMachine = await nayaxLynxClient.GetMachineAsync(id);
            if (nayaxMachine == null)
            {
                return NotFound();
            }
            var machine = await GetMachineSalesAsync(nayaxMachine, await db.Products.ToListAsync());
            return Ok(machine);
        }

        [HttpGet]
        public async Task<ActionResult<List<Machine>>> GetAll()
        {
            var nayaxMachines = await nayaxLynxClient.GetMachinesAsync();
            var products = await db.Products.ToListAsync();
            var machines = await Task.WhenAll(nayaxMachines.Select(x => GetMachineSalesAsync(x, products)));
            return Ok(machines);
        }

        [HttpGet("{id:long}/products")]
        public async Task<ActionResult<List<Product>>> GetMachineProducts(long id)
        {
            var nayaxMachineProducts = await nayaxLynxClient.GetMachineProductsAsync(id);
            var product = await db.Products.Include(x => x.Category).ToListAsync();
            var products = nayaxMachineProducts.Select(mp =>
            {
                var result = product.First(p => p.Id == mp.NayaxProductID);
                result.MachinePrice = mp.RetailPrice ?? 0;
                result.CommissionValue = mp.CommissionValue ?? 0;
                result.SuggestedNetValue = result.MachinePrice - (result.MachinePrice * result.CommissionValue / 100) - result.UnitPrice - (decimal)0.18;
                result.MdbCode = mp.MDBCode;
                return result;
            }).ToList().OrderBy(x=>x.MdbCode);
            return Ok(products);
        }


        private async Task<Machine> GetMachineSalesAsync(NayaxMachine machine, List<Product> products)
        {
            try
            {
                var today = DateTime.Today;
                var currentWeek = GetWeekRange(today, 0);
                var lastWeek = GetWeekRange(today, -1);
                var twoWeeksAgo = GetWeekRange(today, -2);

                var lastSalesTask = nayaxLynxClient.GetMachineLastSalesAsync(machine.MachineID);
                
                var machineProductsTask = nayaxLynxClient.GetMachineProductsAsync(machine.MachineID);


                await Task.WhenAll(lastSalesTask, machineProductsTask);

                var lastSales = await lastSalesTask;
                var machineProducts = await machineProductsTask;

                var results = new Machine
                {
                    ActorID = machine.ActorID,
                    MachineID = machine.MachineID,
                    MachineName = machine.MachineName,
                    MachineNumber = machine.MachineNumber
                };


                var todaySales = lastSales.Where(s => s.MachineAuthorizationTime >= today).ToList();
                var currentWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= currentWeek.Start && s.MachineAuthorizationTime <= currentWeek.End).ToList();
                var lastWeekSales = lastSales.Where(s => s.MachineAuthorizationTime >= lastWeek.Start && s.MachineAuthorizationTime <= lastWeek.End).ToList();
                var twoWeeksAgoSales = lastSales.Where(s => s.MachineAuthorizationTime >= twoWeeksAgo.Start && s.MachineAuthorizationTime <= twoWeeksAgo.End).ToList();

                results.CurrentWeekNetRevenue = CalculateRevenue(currentWeekSales, machineProducts, products);
                results.LastWeekNetRevenue = CalculateRevenue(lastWeekSales, machineProducts, products);
                results.TodayNetRevenue = CalculateRevenue(todaySales, machineProducts, products);
                results.TwoWeeksAgoGrossRevenue = CalculateRevenue(twoWeeksAgoSales, machineProducts, products);

                results.TodayGrossRevenue = todaySales.Sum(s => s.SettlementValue);
                results.CurrentWeekGrossRevenue = currentWeekSales.Sum(s => s.SettlementValue);
                results.LastWeekGrossRevenue = lastWeekSales.Sum(s => s.SettlementValue);
                results.TwoWeeksAgoGrossRevenue = twoWeeksAgoSales.Sum(s => s.SettlementValue);

                return results;
            }
            catch (Exception ex)
            {

                throw;
            }
           

        }

        private static decimal CalculateRevenue(List<NayaxLastSalesReport> sales, List<NayaxMachineProduct> machineProducts, List<Product> products)
        {
            decimal totalRevenue = 0;
            foreach (var sale in sales)
            {
                var machineProduct = machineProducts.SingleOrDefault(p => p.ProductName == sale.ProductName);
                if (machineProduct != null)
                {
                    var product = products.SingleOrDefault(p => p.Id == machineProduct.NayaxProductID);
                    decimal productCost = product?.UnitPrice ?? 0;
                    decimal commission = sale.SettlementValue * machineProduct.CommissionValue??0;
                    decimal paymentFee = (decimal)(sale.PaymentMethod == "Cash" ? 0 : 0.18);
                    totalRevenue += sale.SettlementValue - (productCost * sale.Quantity) - commission - paymentFee;
                }
            }
            return totalRevenue;
        }

        public static (DateTime Start, DateTime End) GetWeekRange(DateTime referenceDate, int weeksOffset = 0)
        {
            // Normalize to date only (no time component)
            DateTime date = referenceDate.Date.AddDays(weeksOffset * 7);

            // Calculate offset to Monday (start of week)
            int diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
            DateTime startOfWeek = date.AddDays(-diff);
            DateTime endOfWeek = startOfWeek.AddDays(7).AddMilliseconds(-1);

            return (startOfWeek, endOfWeek);
        }


    }
}
