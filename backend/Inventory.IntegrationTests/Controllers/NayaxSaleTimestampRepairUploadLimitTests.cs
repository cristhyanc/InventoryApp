using System.Reflection;
using InventoryApi.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InventoryApi.Tests.Controllers;

/// <summary>
/// Pins the upload cap on the sale timestamp repair preview (issue #472), the one action in this
/// maintenance operation that accepts a file. The cap matters twice over: the evidence export is an
/// operator-supplied file read at an authenticated maintenance endpoint that then does relational
/// work, and SonarCloud's S5693 reads the declared limit as the mitigation, so a limit that is
/// raised, removed or enforced in only one place is a regression rather than a refactor.
///
/// Two attributes are required, because they are enforced in different places. RequestSizeLimit
/// caps the raw body through the server's max-request-body-size feature; RequestFormLimits caps what
/// the multipart reader consumes in process. The application configures neither Kestrel's limits nor
/// FormOptions globally, so without the second one the action would read a multipart body up to
/// FormOptions' 128 MB default however small the first one is.
/// </summary>
public class NayaxSaleTimestampRepairUploadLimitTests
{
    private const long ExpectedLimitBytes = 8_000_000;

    private static MethodInfo Preview =>
        typeof(NayaxSaleTimestampRepairsController)
            .GetMethod(nameof(NayaxSaleTimestampRepairsController.Preview))!;

    [Fact]
    public void The_preview_body_is_capped_at_the_evidence_export_limit()
    {
        var requestSizeLimit = Preview.GetCustomAttributesData()
            .Single(attribute => attribute.AttributeType == typeof(RequestSizeLimitAttribute));

        Assert.Equal(ExpectedLimitBytes, Convert.ToInt64(requestSizeLimit.ConstructorArguments[0].Value));
    }

    [Fact]
    public void The_preview_multipart_reader_is_capped_at_the_same_limit()
    {
        var formLimits = Preview.GetCustomAttribute<RequestFormLimitsAttribute>();

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
        Assert.Null(Preview.GetCustomAttribute<DisableRequestSizeLimitAttribute>());
        Assert.Null(typeof(NayaxSaleTimestampRepairsController)
            .GetCustomAttribute<DisableRequestSizeLimitAttribute>());
    }
}
