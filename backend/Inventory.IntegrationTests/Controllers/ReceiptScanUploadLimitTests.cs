using System.Reflection;
using InventoryApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Pins the upload cap on <c>POST /api/purchases/scan</c> (issue #1163), the one action on this
/// controller that reads a file it never stores.
///
/// <para>Two attributes are required, because each is enforced somewhere else. <c>RequestSizeLimit</c>
/// caps the raw body through the server's max-request-body-size feature; <c>RequestFormLimits</c> caps
/// what the multipart reader itself consumes, in process. The application configures neither Kestrel's
/// limits nor <c>FormOptions</c> globally, so without the second one the action would read a multipart
/// body up to <c>FormOptions</c>' 128 MB default however small the first one is - which is why a cap
/// declared in only one of the two, raised, or removed is a regression rather than a refactor, and why
/// SonarCloud's S5693 reads the declared limit as the mitigation.</para>
/// </summary>
public class ReceiptScanUploadLimitTests
{
    private const long ExpectedLimitBytes = 8_000_000;

    private static MethodInfo Scan =>
        typeof(PurchasesController).GetMethod(nameof(PurchasesController.Scan))!;

    [Fact]
    public void The_scan_body_is_capped_at_the_scan_upload_limit()
    {
        var requestSizeLimit = Scan.GetCustomAttributesData()
            .Single(attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));

        Assert.Equal(ExpectedLimitBytes, Convert.ToInt64(requestSizeLimit.ConstructorArguments[0].Value));
    }

    [Fact]
    public void The_scan_multipart_reader_is_capped_at_the_same_limit()
    {
        var formLimits = Scan.GetCustomAttribute<RequestFormLimitsAttribute>();

        Assert.NotNull(formLimits);
        Assert.Equal(ExpectedLimitBytes, formLimits.MultipartBodyLengthLimit);
    }

    /// <summary>
    /// A disabled limit would leave the endpoint unbounded while both attributes above still read as
    /// present, so the absence of that escape hatch is asserted on the action and the controller.
    /// </summary>
    [Fact]
    public void Neither_the_action_nor_the_controller_disables_the_request_size_limit()
    {
        Assert.Null(Scan.GetCustomAttribute<DisableRequestSizeLimitAttribute>());
        Assert.Null(typeof(PurchasesController).GetCustomAttribute<DisableRequestSizeLimitAttribute>());
    }
}
