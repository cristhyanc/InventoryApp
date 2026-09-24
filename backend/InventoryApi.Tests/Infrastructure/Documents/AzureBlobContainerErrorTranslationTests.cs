using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Inventory.Infrastructure.Documents;
using Moq;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Documents;

/// <summary>
/// How <see cref="AzureBlobContainer"/> translates Azure failures.
///
/// The distinction these tests defend is narrow and easy to lose: Blob Storage reuses HTTP
/// statuses across failures that mean completely different things. A missing container, a
/// revoked role assignment and a genuinely absent document all arrive as 404; a lease conflict
/// and a name collision are both 409. Matching on the status would report a broken deployment as
/// "this business has no document" - every download 404ing while the application looked healthy
/// - so only the exact <see cref="BlobErrorCode"/> values are treated as answers.
///
/// The Blob clients are mocked, so nothing here needs Azure credentials or a network.
/// </summary>
public sealed class AzureBlobContainerErrorTranslationTests
{
    private const string BlobName = "tenants/1/purchases/receipt.jpg";

    #region OpenReadAsync

    [Fact]
    public async Task A_blob_that_does_not_exist_reads_as_nothing()
    {
        var container = ContainerWhoseDownloadThrows(
            Failure(HttpStatusCode.NotFound, BlobErrorCode.BlobNotFound));

        Assert.Null(await container.OpenReadAsync(BlobName, CancellationToken.None));
    }

    /// <summary>
    /// A missing container is a misconfigured or undeployed environment, not an absent document.
    /// Reporting it as "no document" would make every download quietly 404 while the cause went
    /// unnoticed.
    /// </summary>
    [Fact]
    public async Task A_missing_container_propagates_rather_than_reading_as_nothing()
    {
        var container = ContainerWhoseDownloadThrows(
            Failure(HttpStatusCode.NotFound, BlobErrorCode.ContainerNotFound));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.OpenReadAsync(BlobName, CancellationToken.None));

        Assert.Equal(BlobErrorCode.ContainerNotFound.ToString(), failure.ErrorCode);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.NotFound, "AccountIsDisabled")]
    [InlineData((int)HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")]
    [InlineData((int)HttpStatusCode.ServiceUnavailable, "ServerBusy")]
    public async Task A_storage_or_authorization_failure_propagates_rather_than_reading_as_nothing(
        int status, string errorCode)
    {
        var container = ContainerWhoseDownloadThrows(new RequestFailedException(status, "failed", errorCode, null));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.OpenReadAsync(BlobName, CancellationToken.None));

        Assert.Equal(errorCode, failure.ErrorCode);
    }

    /// <summary>
    /// A failure with no error code at all - a malformed or non-storage response - is a fault,
    /// not an answer.
    /// </summary>
    [Fact]
    public async Task A_failure_without_an_error_code_propagates()
    {
        var container = ContainerWhoseDownloadThrows(new RequestFailedException(404, "failed"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.OpenReadAsync(BlobName, CancellationToken.None));
    }

    #endregion

    #region DeleteAsync

    /// <summary>
    /// The wrapper deletes with <c>BlobClient.DeleteAsync</c> and decides the outcome itself.
    /// <c>DeleteIfExistsAsync</c> cannot be used here: Azure.Storage.Blobs 12.29.2 implements it
    /// by catching <see cref="BlobErrorCode.BlobNotFound"/> <em>and</em>
    /// <see cref="BlobErrorCode.ContainerNotFound"/> and returning false for either, so a missing
    /// container never reaches a caller's catch block. Every delete against a misconfigured or
    /// undeployed container would then be reported as an ordinary "there was nothing to delete",
    /// which is exactly the confusion these tests exist to prevent - and which a mocked
    /// <c>DeleteIfExistsAsync</c> would hide, because a mock throws where the real SDK has
    /// already swallowed.
    /// </summary>
    [Fact]
    public async Task Delete_does_not_go_through_the_SDKs_delete_if_exists_helper()
    {
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        blob.Setup(x => x.DeleteAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        // A strict mock fails the call outright if the wrapper reaches for any other member,
        // DeleteIfExistsAsync included.
        Assert.True(await ContainerOver(blob).DeleteAsync(BlobName, CancellationToken.None));

        blob.Verify(
            x => x.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task A_successful_delete_reports_that_a_blob_was_removed()
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.DeleteAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<Response>());

        Assert.True(await ContainerOver(blob).DeleteAsync(BlobName, CancellationToken.None));
    }

    [Fact]
    public async Task Deleting_a_blob_that_does_not_exist_reports_that_there_was_nothing_to_delete()
    {
        var container = ContainerWhoseDeleteThrows(
            Failure(HttpStatusCode.NotFound, BlobErrorCode.BlobNotFound));

        Assert.False(await container.DeleteAsync(BlobName, CancellationToken.None));
    }

    /// <summary>
    /// A delete that quietly did nothing is indistinguishable from one that worked, so only an
    /// absent blob may answer <c>false</c>. A missing container means the delete never happened
    /// against the storage anyone thinks it did.
    /// </summary>
    [Fact]
    public async Task Deleting_from_a_missing_container_propagates_rather_than_reporting_nothing_to_delete()
    {
        var container = ContainerWhoseDeleteThrows(
            Failure(HttpStatusCode.NotFound, BlobErrorCode.ContainerNotFound));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.DeleteAsync(BlobName, CancellationToken.None));

        Assert.Equal(BlobErrorCode.ContainerNotFound.ToString(), failure.ErrorCode);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.PreconditionFailed, "LeaseIdMissing")]
    [InlineData((int)HttpStatusCode.Conflict, "LeaseIdMismatchWithBlobOperation")]
    [InlineData((int)HttpStatusCode.PreconditionFailed, "ConditionNotMet")]
    public async Task A_delete_refused_by_a_lease_or_precondition_propagates(int status, string errorCode)
    {
        var container = ContainerWhoseDeleteThrows(new RequestFailedException(status, "failed", errorCode, null));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.DeleteAsync(BlobName, CancellationToken.None));

        Assert.Equal(errorCode, failure.ErrorCode);
    }

    [Theory]
    [InlineData((int)HttpStatusCode.Forbidden, "AuthorizationPermissionMismatch")]
    [InlineData((int)HttpStatusCode.NotFound, "AccountIsDisabled")]
    [InlineData((int)HttpStatusCode.ServiceUnavailable, "ServerBusy")]
    public async Task A_delete_refused_by_authorization_or_the_account_propagates(int status, string errorCode)
    {
        var container = ContainerWhoseDeleteThrows(new RequestFailedException(status, "failed", errorCode, null));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.DeleteAsync(BlobName, CancellationToken.None));

        Assert.Equal(errorCode, failure.ErrorCode);
    }

    [Fact]
    public async Task A_delete_failure_without_an_error_code_propagates()
    {
        var container = ContainerWhoseDeleteThrows(new RequestFailedException(404, "failed"));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.DeleteAsync(BlobName, CancellationToken.None));
    }

    #endregion

    #region CreateAsync

    /// <summary>
    /// The two codes that mean the destination is already occupied. <c>ConditionNotMet</c>
    /// qualifies only because <c>overwrite: false</c> sets exactly one condition,
    /// <c>If-None-Match: *</c>, so the only condition that can fail is "this blob already
    /// exists".
    /// </summary>
    [Theory]
    [InlineData((int)HttpStatusCode.Conflict, "BlobAlreadyExists")]
    [InlineData((int)HttpStatusCode.PreconditionFailed, "ConditionNotMet")]
    public async Task A_create_that_collides_with_an_existing_blob_reports_the_collision(
        int status, string errorCode)
    {
        var container = ContainerWhoseUploadThrows(new RequestFailedException(status, "failed", errorCode, null));

        Assert.False(await container.CreateAsync(BlobName, new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    /// <summary>
    /// A conflict that is not a name collision - a lease, a container mid-deletion - must reach
    /// an operator. Reported as a collision it would surface to the user as "a document is
    /// already stored under this name", which is both false and unactionable.
    /// </summary>
    [Theory]
    [InlineData("LeaseAlreadyPresent")]
    [InlineData("ContainerBeingDeleted")]
    [InlineData("SnapshotOperationRateExceeded")]
    public async Task An_unrelated_conflict_does_not_become_a_collision(string errorCode)
    {
        var container = ContainerWhoseUploadThrows(
            new RequestFailedException((int)HttpStatusCode.Conflict, "failed", errorCode, null));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.CreateAsync(BlobName, new MemoryStream([1, 2, 3]), CancellationToken.None));

        Assert.Equal(errorCode, failure.ErrorCode);
    }

    [Theory]
    [InlineData("LeaseIdMissing")]
    [InlineData("LeaseIdMismatchWithBlobOperation")]
    public async Task An_unrelated_precondition_failure_does_not_become_a_collision(string errorCode)
    {
        var container = ContainerWhoseUploadThrows(
            new RequestFailedException((int)HttpStatusCode.PreconditionFailed, "failed", errorCode, null));

        var failure = await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.CreateAsync(BlobName, new MemoryStream([1, 2, 3]), CancellationToken.None));

        Assert.Equal(errorCode, failure.ErrorCode);
    }

    [Fact]
    public async Task An_authorization_failure_does_not_become_a_collision()
    {
        var container = ContainerWhoseUploadThrows(new RequestFailedException(
            (int)HttpStatusCode.Forbidden, "failed", "AuthorizationPermissionMismatch", null));

        await Assert.ThrowsAsync<RequestFailedException>(() =>
            container.CreateAsync(BlobName, new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    [Fact]
    public async Task A_successful_create_reports_that_the_blob_was_written()
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(BlobsModelFactory.BlobContentInfo(
                new ETag("\"written\""), DateTimeOffset.UnixEpoch, [], string.Empty, 0L), Mock.Of<Response>()));

        Assert.True(await ContainerOver(blob).CreateAsync(
            BlobName, new MemoryStream([1, 2, 3]), CancellationToken.None));
    }

    #endregion

    #region Helpers

    private static RequestFailedException Failure(HttpStatusCode status, BlobErrorCode errorCode) =>
        new((int)status, $"simulated {errorCode}", errorCode.ToString(), innerException: null);

    private static AzureBlobContainer ContainerWhoseDownloadThrows(Exception failure)
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.DownloadStreamingAsync(
                It.IsAny<BlobDownloadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return ContainerOver(blob);
    }

    private static AzureBlobContainer ContainerWhoseDeleteThrows(Exception failure)
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.DeleteAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return ContainerOver(blob);
    }

    private static AzureBlobContainer ContainerWhoseUploadThrows(Exception failure)
    {
        var blob = new Mock<BlobClient>();
        blob.Setup(x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(failure);
        return ContainerOver(blob);
    }

    private static AzureBlobContainer ContainerOver(Mock<BlobClient> blob)
    {
        var container = new Mock<BlobContainerClient>();
        container.Setup(x => x.GetBlobClient(It.IsAny<string>())).Returns(blob.Object);
        return new AzureBlobContainer(container.Object);
    }

    #endregion
}
