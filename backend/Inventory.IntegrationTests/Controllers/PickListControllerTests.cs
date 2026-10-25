using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.PickList;
using Inventory.Domain.Exceptions;
using Inventory.Domain.Stock;
using InventoryApi.Adapters.Mapping;
using InventoryApi.Controllers;
using InventoryApi.DTOs;
using InventoryApi.Tests.Application.Machines;
using InventoryApi.Tests.Application.Nayax;
using InventoryApi.Tests.Application.PickList;
using InventoryApi.Tests.Application.Time;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Controllers;

public class PickListControllerTests
{
    private static readonly DateTime RecordedAt = new(2026, 10, 20, 2, 30, 0, DateTimeKind.Utc);

    private const string Key = "b1b4a3d2-55f5-4c2f-9b86-0a7e2f4c1d55";
    private const long MachineId = 9;

    private static Mock<IPickListStorageStockStore> EmptyStorage() => StorageWith();

    private static Mock<IPickListStorageStockStore> StorageWith(
        params (long ProductId, int QuantityInStock)[] products)
    {
        var dictionary = products.ToDictionary(
            product => product.ProductId,
            product => new PickListStorageProduct(
                product.ProductId, $"Product {product.ProductId}", product.QuantityInStock, IsActive: true));

        var store = new Mock<IPickListStorageStockStore>();
        store.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)dictionary);
        return store;
    }

    private static PickListController ControllerWith(GetPickList read, RecordPickListRefill refill) =>
        new(read, refill);

    private static GetPickList NoPickList() =>
        new(new Mock<INayaxLynxClient>(MockBehavior.Strict).Object,
            EmptyStorage().Object,
            FakeNayaxProductLinkStore.EveryIdLinkedToItself());

    [Fact]
    public async Task Get_ReturnsBadRequest_WhenNoMachineIdsAreGiven()
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        var controller = ControllerWith(
            new GetPickList(nayax.Object, EmptyStorage().Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            NoRefill());

        var result = await controller.Get([], CancellationToken.None);

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Get_ReturnsThePickListProjection_ForTheSelectedMachines()
    {
        var nayax = new Mock<INayaxLynxClient>();
        nayax.Setup(x => x.GetMachineProductsFreshAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<NayaxMachineProduct> { new() { NayaxProductID = 100, MDBCode = 12, PAR = 10, MissingStockByMDB = 4 } });
        var storage = new Mock<IPickListStorageStockStore>();
        storage.Setup(x => x.GetStorageProductsAsync(It.IsAny<IReadOnlyCollection<long>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyDictionary<long, PickListStorageProduct>)new Dictionary<long, PickListStorageProduct>
            {
                [100] = new PickListStorageProduct(100, "Coke Zero", 50, IsActive: true),
            });
        var controller = ControllerWith(
            new GetPickList(nayax.Object, storage.Object, FakeNayaxProductLinkStore.EveryIdLinkedToItself()),
            NoRefill());

        var result = await controller.Get([1], CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var body = Assert.IsType<PickListResult>(ok.Value);
        var product = Assert.Single(body.Products);
        Assert.Equal(4, product.TotalQuantityToPick);
        Assert.Equal(12, product.MdbCode);
        Assert.Equal(12, Assert.Single(product.MachineQuantities).MdbCode);
    }

    #region POST api/pick-list/refills (issue #743)

    private static RecordPickListRefill NoRefill() => RefillWith(new FakePickListRefillStore());

    private static RecordPickListRefill RefillWith(
        FakePickListRefillStore store,
        Mock<IPickListStorageStockStore>? storage = null,
        FakeLocalMachineStore? machines = null) =>
        new(
            store,
            (storage ?? StorageWith((100, 10))).Object,
            machines ?? new FakeLocalMachineStore([new LocalMachineRecord(MachineId, "Machine A", IsActive: true)]),
            new FakeClock(RecordedAt));

    private static PickListRefillRequest Request(
        string? key = Key, long? machineId = null, params (long ProductId, int Quantity)[] rows) =>
        new(
            key,
            machineId,
            Notes: null,
            [.. rows.Select(row => new PickListRefillRowRequest(row.ProductId, row.Quantity))]);

    /// <summary>
    /// The success contract: <c>200</c> with the recorded rows and the movement ids, and
    /// <c>replayed</c> false because this request is what recorded them.
    /// </summary>
    [Fact]
    public async Task RecordRefill_ReturnsTheRecordedRefill()
    {
        var controller = ControllerWith(NoPickList(), RefillWith(new FakePickListRefillStore()));

        var result = await controller.RecordRefill(
            Request(machineId: MachineId, rows: [(100, 3)]), CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var body = Assert.IsType<PickListRefillResponse>(ok.Value);
        Assert.Equal(Key, body.IdempotencyKey);
        Assert.False(body.Replayed);
        Assert.Equal(MachineId, body.MachineId);
        Assert.Equal(RecordedAt, body.RecordedAtUtc);
        var row = Assert.Single(body.Rows);
        Assert.Equal(100, row.ProductId);
        Assert.Equal(3, row.QuantityTaken);
    }

    /// <summary>
    /// A retry is the same body with <c>replayed</c> true, so a client that lost the first response
    /// needs no second call to find out what happened.
    /// </summary>
    [Fact]
    public async Task RecordRefill_ReportsAReplayedRefill()
    {
        var controller = ControllerWith(NoPickList(), RefillWith(new FakePickListRefillStore()));
        var request = Request(rows: [(100, 3)]);

        await controller.RecordRefill(request, CancellationToken.None);
        var result = await controller.RecordRefill(request, CancellationToken.None);

        var body = Assert.IsType<PickListRefillResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(body.Replayed);
    }

    /// <summary>
    /// A refusal is a <c>400</c> problem document addressed per row, which is what lets the manual
    /// pick-list screen put each message back on the line the operator typed.
    /// </summary>
    [Fact]
    public async Task RecordRefill_ReportsEveryRefusalAgainstThePartOfTheRequestItIsAbout()
    {
        var controller = ControllerWith(
            NoPickList(),
            RefillWith(new FakePickListRefillStore(), StorageWith((100, 10), (101, 1))));

        var result = await controller.RecordRefill(
            Request(machineId: 404, rows: [(100, 3), (101, 4)]), CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        Assert.Contains("application/problem+json", problem.ContentTypes);

        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(
            PickListRefillPolicy.MachineMustBeActiveMessage,
            Assert.Single(details.Errors["machineId"]));
        Assert.Equal(
            PickListRefillPolicy.NotEnoughStockMessage(1),
            Assert.Single(details.Errors[PickListRefillResponseMapper.RowKey(1)]));
        Assert.DoesNotContain(PickListRefillResponseMapper.RowKey(0), details.Errors.Keys);
    }

    /// <summary>
    /// A row the caller sent as a missing value is refused on its own line, not dereferenced. The
    /// element type says it cannot be null, but a JSON array can carry one anyway, so the row is
    /// carried through as a missing row and refused by the policy - and the valid row beside it is
    /// not written, because one refusal refuses the whole batch.
    ///
    /// <para><c>PickListRefillApiTests</c> proves the same thing through real JSON model binding,
    /// which is where such an element actually comes from; this one holds the controller's own
    /// projection to it.</para>
    /// </summary>
    [Fact]
    public async Task RecordRefill_RefusesAMissingRowRatherThanReadingIt()
    {
        var store = new FakePickListRefillStore();
        var controller = ControllerWith(NoPickList(), RefillWith(store));

        var result = await controller.RecordRefill(
            new PickListRefillRequest(Key, null, null, [new PickListRefillRowRequest(100, 3), null]),
            CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
        var details = Assert.IsType<ValidationProblemDetails>(problem.Value);
        Assert.Equal(
            PickListRefillPolicy.MissingRowMessage,
            Assert.Single(details.Errors[PickListRefillResponseMapper.RowKey(1)]));
        Assert.DoesNotContain(PickListRefillResponseMapper.RowKey(0), details.Errors.Keys);

        // Refused before the write lock is even taken, so the valid row beside it is not written.
        Assert.Equal(0, store.TransactionsOpened);
        Assert.Equal(0, store.ApplyCalls);
        Assert.Empty(store.Committed);
    }

    [Fact]
    public async Task RecordRefill_ReportsAMissingBodyAsARefusalRatherThanThrowing()
    {
        var controller = ControllerWith(NoPickList(), RefillWith(new FakePickListRefillStore()));

        var result = await controller.RecordRefill(null!, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.StatusCode);
    }

    /// <summary>
    /// A key reused for a different refill is a conflict with state, so it propagates as the
    /// <c>DomainConflictException</c> the API's own handler answers with <c>409</c>
    /// (docs/architecture.md § Domain and application error mapping).
    /// </summary>
    [Fact]
    public async Task RecordRefill_LetsAReusedKeyPropagateAsAConflict()
    {
        var controller = ControllerWith(NoPickList(), RefillWith(new FakePickListRefillStore()));

        await controller.RecordRefill(Request(rows: [(100, 3)]), CancellationToken.None);

        var conflict = await Assert.ThrowsAsync<DomainConflictException>(
            () => controller.RecordRefill(Request(rows: [(100, 4)]), CancellationToken.None));

        Assert.Equal(PickListRefillPolicy.IdempotencyKeyReusedMessage, conflict.Message);
    }

    #endregion
}
