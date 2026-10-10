using System.Security.Cryptography;
using System.Text;
using Inventory.Infrastructure.Nayax;
using Xunit;

namespace InventoryApi.Tests.Infrastructure.Nayax;

/// <summary>
/// Issue #518: a business's Nayax access token is stored encrypted, with the id of the key that
/// encrypted it, and every failure mode refuses rather than degrades.
///
/// These tests care about two things a reviewer cannot read off the implementation: that the
/// plaintext token never appears in what is stored or in what is thrown, and that a tampered
/// ciphertext, a retired key and an unconfigured environment all fail closed instead of producing
/// a token that would then be sent to Nayax.
/// </summary>
public class NayaxTokenProtectorTests
{
    private const string Token = "nayax-lynx-access-token-value";
    private const string ActiveKeyId = "2026-10";
    private const string OtherKeyId = "2026-11";

    [Fact]
    public void Protect_then_unprotect_round_trips_the_token_and_records_the_active_key_id()
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)), (OtherKeyId, Key(2)));

        var protectedToken = protector.Protect(Token);

        Assert.Equal(ActiveKeyId, protectedToken.KeyId);
        Assert.Equal(Token, protector.Unprotect(protectedToken));
    }

    [Fact]
    public void The_stored_ciphertext_is_not_the_plaintext_and_differs_on_every_save()
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)));

        var first = protector.Protect(Token);
        var second = protector.Protect(Token);

        // Neither the stored text nor the bytes it decodes to contain the token.
        Assert.DoesNotContain(Token, first.Ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(
            Token,
            Encoding.UTF8.GetString(Convert.FromBase64String(first.Ciphertext)),
            StringComparison.Ordinal);

        // A fresh nonce per encryption: two saves of one token must not be recognisable as equal,
        // or the column itself would tell an observer that two businesses share a credential.
        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.Equal(Token, protector.Unprotect(second));
    }

    [Fact]
    public void A_tampered_ciphertext_fails_closed_without_revealing_the_token()
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)));
        var stored = Convert.FromBase64String(protector.Protect(Token).Ciphertext);
        stored[^1] ^= 0xFF;
        var tampered = new ProtectedNayaxToken(ActiveKeyId, Convert.ToBase64String(stored));

        var exception = Assert.Throws<NayaxTokenProtectionException>(() => protector.Unprotect(tampered));

        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(tampered.Ciphertext, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    [InlineData("YWJj")]
    public void A_malformed_stored_value_fails_closed(string ciphertext)
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)));

        Assert.Throws<NayaxTokenProtectionException>(
            () => protector.Unprotect(new ProtectedNayaxToken(ActiveKeyId, ciphertext)));
    }

    [Fact]
    public void An_unknown_key_id_fails_closed_and_names_the_key_to_restore()
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)));
        var protectedToken = protector.Protect(Token);
        var retiredKeyId = "2025-01";

        var exception = Assert.Throws<NayaxTokenProtectionException>(
            () => protector.Unprotect(new ProtectedNayaxToken(retiredKeyId, protectedToken.Ciphertext)));

        Assert.Contains(retiredKeyId, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// What rotation has to mean in practice: a ciphertext written by the previous key keeps
    /// decrypting while that key is still configured, and stops the moment it is removed. That is
    /// why the key id is stored with the ciphertext instead of assuming one global key.
    /// </summary>
    [Fact]
    public void A_ciphertext_from_the_previous_key_still_decrypts_until_that_key_is_removed()
    {
        var previousKey = Key(1);
        var stored = Protector(OtherKeyId, (OtherKeyId, previousKey)).Protect(Token);

        var afterRotation = Protector(ActiveKeyId, (ActiveKeyId, Key(2)), (OtherKeyId, previousKey));
        Assert.Equal(Token, afterRotation.Unprotect(stored));
        // The active key is the one new ciphertext is produced with, not the one that decrypts.
        Assert.Equal(ActiveKeyId, afterRotation.Protect(Token).KeyId);

        var afterRetiringThePreviousKey = Protector(ActiveKeyId, (ActiveKeyId, Key(2)));
        Assert.Throws<NayaxTokenProtectionException>(() => afterRetiringThePreviousKey.Unprotect(stored));
    }

    [Fact]
    public void A_key_id_is_matched_the_way_the_configuration_providers_compare_their_keys()
    {
        var protector = Protector("Prod-2026", ("Prod-2026", Key(1)));

        var protectedToken = protector.Protect(Token);

        Assert.Equal(Token, protector.Unprotect(new ProtectedNayaxToken("prod-2026", protectedToken.Ciphertext)));
    }

    [Fact]
    public void An_unconfigured_environment_fails_closed_on_both_members()
    {
        var protector = new UnconfiguredNayaxTokenProtector();

        var protectFailure = Assert.Throws<NayaxTokenProtectionException>(() => protector.Protect(Token));
        var unprotectFailure = Assert.Throws<NayaxTokenProtectionException>(
            () => protector.Unprotect(new ProtectedNayaxToken(ActiveKeyId, "AAAA")));

        Assert.Contains(NayaxTokenProtectionOptions.SectionName, protectFailure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, protectFailure.Message, StringComparison.Ordinal);
        Assert.Contains(NayaxTokenProtectionOptions.SectionName, unprotectFailure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_token_is_refused_rather_than_encrypted()
    {
        var protector = Protector(ActiveKeyId, (ActiveKeyId, Key(1)));

        Assert.Throws<ArgumentException>(() => protector.Protect("   "));
        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!));
    }

    /// <summary>
    /// A ciphertext is not a plaintext token, but it is still credential material, so the record's
    /// own <c>ToString</c> must not be the thing that puts it into a log line or a test snapshot.
    /// </summary>
    [Fact]
    public void A_protected_token_redacts_its_ciphertext_when_rendered()
    {
        var protectedToken = new ProtectedNayaxToken(ActiveKeyId, "c2VjcmV0LWNpcGhlcnRleHQ=");

        var rendered = protectedToken.ToString();

        Assert.Contains(ActiveKeyId, rendered, StringComparison.Ordinal);
        Assert.Contains("[redacted]", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("c2VjcmV0", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_active_key_id_fails_at_registration_naming_the_setting()
    {
        var options = new NayaxTokenProtectionOptions();
        options.Keys[ActiveKeyId] = Key(1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => NayaxTokenProtectionConfiguration.ValidateAndDecodeKeys(options));

        Assert.Contains(
            $"{NayaxTokenProtectionOptions.SectionName}:{nameof(NayaxTokenProtectionOptions.ActiveKeyId)}",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void An_active_key_id_with_no_configured_key_fails_at_registration()
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[OtherKeyId] = Key(1);

        var exception = Assert.Throws<InvalidOperationException>(
            () => NayaxTokenProtectionConfiguration.ValidateAndDecodeKeys(options));

        Assert.Contains(nameof(NayaxTokenProtectionOptions.ActiveKeyId), exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-$$$")]
    [InlineData("c2hvcnQta2V5")]
    public void A_malformed_or_wrong_length_key_fails_at_registration_without_echoing_key_material(string encodedKey)
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[ActiveKeyId] = encodedKey;

        var exception = Assert.Throws<InvalidOperationException>(
            () => NayaxTokenProtectionConfiguration.ValidateAndDecodeKeys(options));

        Assert.Contains(ActiveKeyId, exception.Message, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(encodedKey))
        {
            Assert.DoesNotContain(encodedKey, exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_key_longer_than_256_bits_is_refused_rather_than_truncated()
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = ActiveKeyId };
        options.Keys[ActiveKeyId] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        Assert.Throws<InvalidOperationException>(
            () => NayaxTokenProtectionConfiguration.ValidateAndDecodeKeys(options));
    }

    private static AesGcmNayaxTokenProtector Protector(
        string activeKeyId,
        params (string KeyId, string EncodedKey)[] keys)
    {
        var options = new NayaxTokenProtectionOptions { ActiveKeyId = activeKeyId };
        foreach (var (keyId, encodedKey) in keys)
        {
            options.Keys[keyId] = encodedKey;
        }

        return new AesGcmNayaxTokenProtector(options);
    }

    /// <summary>
    /// A deterministic, obviously synthetic 256-bit key. Test key material only: nothing here is a
    /// real key, and a real one never appears in source, a fixture or a log.
    /// </summary>
    private static string Key(byte seed) =>
        Convert.ToBase64String(Enumerable.Repeat(seed, NayaxTokenProtectionConfiguration.KeySizeInBytes).ToArray());
}
