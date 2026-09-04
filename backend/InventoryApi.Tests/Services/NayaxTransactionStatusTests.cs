using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using InventoryApi.Data;
using InventoryApi.Integrations.Nayax;
using InventoryApi.Models;
using InventoryApi.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Services;

public class NayaxTransactionStatusTests
{
    [Theory]
    [InlineData(12, NayaxTransactionStatus.Completed)]
    [InlineData(55, NayaxTransactionStatus.Pending)]
    [InlineData(80, NayaxTransactionStatus.Pending)]
    [InlineData(62, NayaxTransactionStatus.Refunded)]
    [InlineData(null, NayaxTransactionStatus.Unknown)]
    [InlineData(21, NayaxTransactionStatus.Unknown)]
    public void Classifier_maps_raw_status_ids(int? statusId, NayaxTransactionStatus expected)
    {
        Assert.Equal(expected, NayaxTransactionStatusClassifier.Classify(statusId));
    }

    [Fact]
    public async Task Pending_completed_reimport_updates_existing_transaction()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var db = new AppDbContext(options);
        var client = new Mock<INayaxLynxClient>().Object;
        var service = new MachineService(db, client);

        await service.ImportNayaxSalesFromExcelAsync(File("TransactionID,TransactionStatusId,MachineID,SettlementValue,Quantity,MachineAuthorizationTime\n1,55,10,5,1,2/9/2026 2:30:00 PM"));
        await service.ImportNayaxSalesFromExcelAsync(File("TransactionID,TransactionStatusId,MachineID,SettlementValue,Quantity,MachineAuthorizationTime\n1,12,10,5,1,2/9/2026 2:30:00 PM"));

        var sale = Assert.Single(db.NayaxSales);
        Assert.Equal(12, sale.TransactionStatusId);
    }

    private static IFormFile File(string csv)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        return new FormFile(stream, 0, stream.Length, "file", "sales.csv");
    }
}
