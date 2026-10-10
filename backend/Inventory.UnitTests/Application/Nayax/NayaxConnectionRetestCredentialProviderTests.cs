using Inventory.Application.Nayax;
using Inventory.Domain.Nayax;
using Xunit;

namespace InventoryApi.Tests.Application.Nayax;

/// <summary>
/// The gate-free re-test read of issue #520, which exists so issue #506's Owner-only re-test can
/// recover a connection the gate refuses.
///
/// Two properties matter here: it ignores the status (otherwise
/// <see cref="NayaxConnectionStatus.NeedsAttention"/> would be unrecoverable, since nothing could
/// ever test the stored token again), and it returns the revision it read (otherwise the result of
/// that test could not be applied back to the exact credentials it tested). That it is
/// *unreachable* from an ordinary consumer is enforced separately, against the compiled assemblies,
/// by <c>NayaxRetestCredentialBoundaryTests</c>.
/// </summary>
public class NayaxConnectionRetestCredentialProviderTests
{
    private const string OperatorId = "2002736764";
    private const string Token = "fake-token-not-a-real-credential";

    [Theory]
    [InlineData(NayaxConnectionStatus.NotConfigured)]
    [InlineData(NayaxConnectionStatus.PendingPermissions)]
    [InlineData(NayaxConnectionStatus.Ready)]
    [InlineData(NayaxConnectionStatus.NeedsAttention)]
    public async Task The_token_is_decrypted_whatever_the_status_is(NayaxConnectionStatus status)
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, status, credentialRevision: 3);
        var provider = new NayaxConnectionRetestCredentialProvider(store);

        var credential = await provider.GetForRetestAsync(CancellationToken.None);

        Assert.NotNull(credential);
        Assert.Equal(OperatorId, credential!.OperatorId);
        Assert.Equal(Token, credential.AccessToken);
        Assert.Equal(3, credential.CredentialRevision);
    }

    [Fact]
    public async Task A_business_with_no_stored_connection_has_nothing_to_re_test()
    {
        var provider = new NayaxConnectionRetestCredentialProvider(new FakeNayaxConnectionStore());

        Assert.Null(await provider.GetForRetestAsync(CancellationToken.None));
    }

    /// <summary>
    /// Ignoring the status must not mean ignoring a failure: a ciphertext that cannot be decrypted
    /// still fails closed rather than reading as "nothing stored", which an operator would try to
    /// fix by reconnecting rather than by restoring the key.
    /// </summary>
    [Fact]
    public async Task An_undecryptable_credential_still_fails_closed()
    {
        var store = new FakeNayaxConnectionStore(OperatorId, Token, NayaxConnectionStatus.NeedsAttention)
        {
            CredentialReadFailure = new InvalidOperationException("ciphertext cannot be decrypted"),
        };
        var provider = new NayaxConnectionRetestCredentialProvider(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetForRetestAsync(CancellationToken.None));
    }
}
