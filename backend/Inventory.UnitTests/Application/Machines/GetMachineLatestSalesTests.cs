using Inventory.Application.Machines;
using Inventory.Application.Nayax;
using Inventory.Application.SupportSessions;
using Inventory.Domain.Nayax;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Application.Machines;

/// <summary>
/// The machine latest-sales read use case (issue #768), against a mocked
/// <see cref="INayaxLynxClient"/> so no HTTP, EF Core or SQLite is involved.
///
/// <para>Three things are under test, and all three are rules rather than plumbing. <strong>What is
/// published</strong>: an allow-list of the displayed fields, with absent provider values preserved
/// as absent rather than defaulted, and no transaction status - the documented <c>lastSales</c>
/// contract has none. <strong>What order</strong>: newest first, with a sale whose instant the
/// payload did not carry last. <strong>Availability versus failure</strong>: three refusals become
/// an answer about this business, and everything else propagates, so an outage, a cancellation and
/// the support-session refusal are never shown as a machine that has sold nothing.</para>
///
/// <para>The use case is read-only by construction - the Nayax client is its only dependency - so
/// there is no persistence port here to assert nothing was written through.</para>
/// </summary>
public class GetMachineLatestSalesTests
{
    private const long MachineId = 942488501;

    private static readonly DateTimeOffset Earlier = new(2026, 10, 15, 1, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 10, 17, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task A_window_that_answers_is_available_with_no_reason_to_report()
    {
        var result = await Handle(Reported(1, Later, settlementValue: 4.5m));

        Assert.True(result.IsAvailable);
        Assert.Null(result.UnavailableReason);
        Assert.Single(result.Sales);
    }

    /// <summary>
    /// A provider read that succeeds and returns nothing is an <em>available</em> answer with no
    /// sales - a different answer from every unavailable one below, and the whole reason those are
    /// not reported as an empty list.
    /// </summary>
    [Fact]
    public async Task A_window_with_no_sales_is_available_and_empty()
    {
        var result = await Handle();

        Assert.True(result.IsAvailable);
        Assert.Null(result.UnavailableReason);
        Assert.Empty(result.Sales);
    }

    [Fact]
    public async Task Only_the_displayed_fields_of_a_reported_sale_are_published()
    {
        var result = await Handle(new NayaxLastSalesReport
        {
            TransactionID = 2108816976,
            MachineID = MachineId,
            AuthorizationDateTimeGmt = Later,
            ProductName = "Bottled Water",
            PaymentMethod = "Credit Card",
            SettlementValue = 9.5m,
            CurrencyCode = "AUD",

            // Everything the use case must not carry out of the payload. ProductCostPrice is a
            // financial figure the Sales tab never shows, and the status is not part of the
            // documented lastSales contract at all.
            ProductCostPrice = 3.25m,
            TransactionStatusId = 12,
            MachineName = "Pavillion Left",
            NayaxProductId = 4242,
        });

        var sale = Assert.Single(result.Sales);
        Assert.Equal(
            (2108816976L, Later.UtcDateTime, "Bottled Water", "Credit Card", 9.5m, "AUD"),
            (sale.TransactionId, sale.SaleAtUtc, sale.ProductName, sale.PaymentMethod,
                sale.SaleAmount, sale.CurrencyCode));

        // The record's own shape is the allow-list: a cost price, a status, a card number or a
        // payment-service id could only be published by somebody adding it here deliberately.
        // Compared as a set, because reflection does not promise declaration order.
        Assert.Equal(
            ["CurrencyCode", "PaymentMethod", "ProductName", "SaleAmount", "SaleAtUtc", "TransactionId"],
            typeof(MachineLatestSale)
                .GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Absent is absent. A product name, payment method or currency the provider did not report
    /// stays <see langword="null"/> instead of becoming an empty cell that reads as a reported
    /// value, and a payload item carrying no usable GMT instant reports no instant rather than
    /// <see cref="DateTime.MinValue"/>.
    /// </summary>
    [Fact]
    public async Task An_absent_provider_value_is_preserved_as_absent()
    {
        var result = await Handle(new NayaxLastSalesReport
        {
            TransactionID = 7,
            MachineID = MachineId,
            AuthorizationDateTimeGmt = null,
            ProductName = null,
            PaymentMethod = "   ",
            CurrencyCode = null,
            SettlementValue = 0m,
        });

        var sale = Assert.Single(result.Sales);
        Assert.Null(sale.SaleAtUtc);
        Assert.Null(sale.ProductName);
        Assert.Null(sale.PaymentMethod);
        Assert.Null(sale.CurrencyCode);

        // SettlementValue is documented non-nullable, so a reported 0 is a real 0 and is published
        // as one. Nothing here turns an absent value into a zero amount, because the contract has
        // no absent amount to turn.
        Assert.Equal(0m, sale.SaleAmount);
    }

    /// <summary>
    /// The live endpoint renders a product name with trailing whitespace
    /// (<c>"Unknown(0 = 700.00)\n"</c> in the portal's own sample response), so the reported text is
    /// trimmed for display rather than published with its line break.
    /// </summary>
    [Fact]
    public async Task A_reported_product_name_is_published_without_its_surrounding_whitespace()
    {
        var result = await Handle(new NayaxLastSalesReport
        {
            TransactionID = 8,
            MachineID = MachineId,
            AuthorizationDateTimeGmt = Later,
            ProductName = "Unknown(0 = 700.00)\n",
            SettlementValue = 700m,
        });

        Assert.Equal("Unknown(0 = 700.00)", Assert.Single(result.Sales).ProductName);
    }

    [Fact]
    public async Task Sales_are_published_newest_first()
    {
        var result = await Handle(
            Reported(1, Earlier),
            Reported(2, Later),
            Reported(3, Earlier.AddHours(5)));

        Assert.Equal([2L, 3L, 1L], result.Sales.Select(sale => sale.TransactionId));
    }

    /// <summary>
    /// A sale with no usable instant cannot be placed in time, so it goes last rather than to the
    /// top of the list or into the middle of it at some defaulted instant. The transaction id breaks
    /// the remaining ties, so one window always renders in the same order.
    /// </summary>
    [Fact]
    public async Task A_sale_with_no_instant_is_published_last()
    {
        var result = await Handle(
            Reported(10, null),
            Reported(20, Earlier),
            Reported(30, null));

        Assert.Equal([20L, 30L, 10L], result.Sales.Select(sale => sale.TransactionId));
    }

    /// <summary>
    /// The sale instant is the authoritative GMT field, read through the one normalization of a
    /// Nayax timestamp (issue #471) - never the machine-local <c>MachineAuthorizationTime</c>, which
    /// carries no offset and is hours away from it in the portal's own sample.
    /// </summary>
    [Fact]
    public async Task The_sale_instant_is_the_authoritative_gmt_value()
    {
        var result = await Handle(new NayaxLastSalesReport
        {
            TransactionID = 11,
            MachineID = MachineId,
            AuthorizationDateTimeGmt = new DateTimeOffset(2026, 2, 8, 20, 31, 51, TimeSpan.FromHours(11)),
            MachineAuthorizationTime = new DateTime(2026, 2, 8, 11, 31, 51, DateTimeKind.Unspecified),
            SettlementValue = 7m,
        });

        Assert.Equal(new DateTime(2026, 2, 8, 9, 31, 51, DateTimeKind.Utc), Assert.Single(result.Sales).SaleAtUtc);
    }

    /// <summary>
    /// A business with nothing stored has no sales provider at all (issue #759), which is a
    /// supported way to run this application: it is reported as its own state with nothing for an
    /// operator to repair, not as a connection that needs reconnecting and not as a failure.
    /// </summary>
    [Fact]
    public async Task A_business_with_no_stored_connection_has_no_sales_provider()
    {
        var result = await HandleRefusal(new NayaxNotConnectedException(NayaxConnectionStatus.NotConfigured));

        Assert.False(result.IsAvailable);
        Assert.Equal(MachineLatestSalesUnavailableReason.NoSalesProvider, result.UnavailableReason);
        Assert.Empty(result.Sales);
    }

    /// <summary>
    /// Stored credentials the status gate refuses are a different answer: the provider is configured
    /// and currently unusable, so an operator has to reconnect it.
    /// </summary>
    [Fact]
    public async Task A_connection_that_needs_attention_is_reported_as_not_connected()
    {
        var result = await HandleRefusal(new NayaxNotConnectedException(NayaxConnectionStatus.NeedsAttention));

        Assert.False(result.IsAvailable);
        Assert.Equal(MachineLatestSalesUnavailableReason.NayaxNotConnected, result.UnavailableReason);
        Assert.Empty(result.Sales);
    }

    [Fact]
    public async Task A_permission_refusal_is_unavailable_with_its_own_reason()
    {
        var result = await HandleRefusal(new NayaxPermissionNotGrantedException("GetMachineLastSalesAsync"));

        Assert.False(result.IsAvailable);
        Assert.Equal(MachineLatestSalesUnavailableReason.PermissionNotGranted, result.UnavailableReason);
        Assert.Empty(result.Sales);
    }

    /// <summary>
    /// An upstream failure is not an availability answer. A machine whose sales could not be fetched
    /// is not a machine that sold nothing, so the failure propagates to the HTTP boundary's
    /// controlled error instead of being published as an empty window.
    /// </summary>
    [Fact]
    public async Task An_upstream_failure_propagates()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => HandleRefusal(new InvalidOperationException("Nayax returned 500.")));
    }

    /// <summary>
    /// The support-session refusal propagates, so a platform administrator reading a business's data
    /// from outside it still gets the <c>unavailable_in_support_view</c> refusal rather than a
    /// screen implying this business has no provider. The business's Nayax token is the business's
    /// own.
    /// </summary>
    [Fact]
    public async Task The_support_session_refusal_propagates()
    {
        await Assert.ThrowsAsync<SupportSessionNayaxUnavailableException>(
            () => HandleRefusal(new SupportSessionNayaxUnavailableException()));
    }

    [Fact]
    public async Task A_cancellation_propagates()
    {
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => HandleRefusal(new OperationCanceledException()));
    }

    /// <summary>
    /// One adapter call, for the machine the caller asked about, and no other Nayax read: switching
    /// to this tab must not fan out into the fleet or the catalogue.
    /// </summary>
    [Fact]
    public async Task The_read_makes_exactly_one_nayax_call_for_the_requested_machine()
    {
        var nayax = Client(Reported(1, Later));

        await new GetMachineLatestSales(nayax.Object).Handle(MachineId, CancellationToken.None);

        nayax.Verify(
            client => client.GetMachineLastSalesAsync(MachineId, It.IsAny<CancellationToken>()), Times.Once);
        nayax.VerifyNoOtherCalls();
    }

    private static NayaxLastSalesReport Reported(
        long transactionId, DateTimeOffset? at, decimal settlementValue = 1m) =>
        new()
        {
            TransactionID = transactionId,
            MachineID = MachineId,
            AuthorizationDateTimeGmt = at,
            ProductName = "Bottled Water",
            PaymentMethod = "Credit Card",
            SettlementValue = settlementValue,
            CurrencyCode = "AUD",
        };

    private static Mock<INayaxLynxClient> Client(params NayaxLastSalesReport[] reported)
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        nayax.Setup(client => client.GetMachineLastSalesAsync(MachineId, It.IsAny<CancellationToken>()))
            .ReturnsAsync([.. reported]);
        return nayax;
    }

    private static Task<MachineLatestSales> Handle(params NayaxLastSalesReport[] reported) =>
        new GetMachineLatestSales(Client(reported).Object).Handle(MachineId, CancellationToken.None);

    private static Task<MachineLatestSales> HandleRefusal(Exception refusal)
    {
        var nayax = new Mock<INayaxLynxClient>(MockBehavior.Strict);
        nayax.Setup(client => client.GetMachineLastSalesAsync(MachineId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(refusal);

        return new GetMachineLatestSales(nayax.Object).Handle(MachineId, CancellationToken.None);
    }
}
