using System.Globalization;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// One business's encrypted Nayax access token: the ciphertext and the id of the key that produced
/// it (issue #518).
///
/// The key id travels with the ciphertext so the encryption key can be rotated later without
/// re-encrypting every stored token at once, and so a ciphertext whose key is no longer configured
/// is refused rather than decrypted with the wrong key. <see cref="ToString"/> is overridden
/// because a ciphertext is still credential material: useless without the key, but not something
/// to pour into telemetry by interpolating a value.
/// </summary>
/// <param name="KeyId">The id of the key that encrypted <paramref name="Ciphertext"/>.</param>
/// <param name="Ciphertext">The encrypted token, encoded for storage in a text column.</param>
public sealed record ProtectedNayaxToken(string KeyId, string Ciphertext)
{
    /// <inheritdoc />
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"ProtectedNayaxToken {{ KeyId = {KeyId}, Ciphertext = [redacted] }}");
}

/// <summary>
/// A Nayax token could not be protected or unprotected (issue #518).
///
/// It is deliberately the only outcome of a failure: an unusable stored credential must never be
/// reported as absent, blank or usable. The message names the configuration setting or the key id
/// at fault and never contains a token, a ciphertext or key material, so it is safe to log and
/// safe to let an exception handler see.
/// </summary>
public sealed class NayaxTokenProtectionException : Exception
{
    public NayaxTokenProtectionException(string message) : base(message)
    {
    }

    public NayaxTokenProtectionException()
        : this("The Nayax access token could not be protected or unprotected.")
    {
    }

    public NayaxTokenProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The narrow abstraction over encrypting a business's Nayax access token at rest (issue #518).
///
/// It lives in Inventory.Infrastructure, and only Infrastructure depends on it: the Application's
/// <c>INayaxConnectionStore</c> hands the adapter a plaintext token and is told nothing about
/// ciphers, keys, key ids or Key Vault, which is what keeps that configuration out of
/// Inventory.Application and Inventory.Domain.
///
/// Implementations fail closed. Neither member returns a "could not decrypt" value; a corrupt
/// ciphertext, a tampered one, an unknown key id or a missing key configuration throws
/// <see cref="NayaxTokenProtectionException"/>.
/// </summary>
public interface INayaxTokenProtector
{
    /// <summary>Encrypts <paramref name="accessToken"/> with the currently active key.</summary>
    /// <exception cref="NayaxTokenProtectionException">No usable encryption key is configured.</exception>
    ProtectedNayaxToken Protect(string accessToken);

    /// <summary>Decrypts <paramref name="protectedToken"/> with the key it names.</summary>
    /// <exception cref="NayaxTokenProtectionException">
    /// The key id is not configured, or the ciphertext is malformed or has been tampered with.
    /// </exception>
    string Unprotect(ProtectedNayaxToken protectedToken);
}
