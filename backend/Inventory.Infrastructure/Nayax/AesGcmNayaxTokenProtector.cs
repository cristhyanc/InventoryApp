using System.Security.Cryptography;
using System.Text;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Protects a business's Nayax access token with AES-256-GCM (issue #518).
///
/// AES-GCM is authenticated encryption, which is what makes tampering a detectable failure rather
/// than a silent one: a flipped byte in the stored ciphertext fails the tag check and throws,
/// instead of decrypting to a different token that would then be sent to Nayax. A fresh random
/// nonce per encryption means saving the same token twice produces different ciphertext, so the
/// column cannot be used to tell whether two businesses configured the same credential.
///
/// The stored value is <c>base64(nonce || tag || ciphertext)</c> - fixed-size nonce and tag first,
/// so the format is self-describing without a length prefix - and the key id that produced it is
/// stored in its own column, never inside this blob. Nothing in this type logs, throws or returns
/// a plaintext token, a ciphertext or key material.
/// </summary>
public sealed class AesGcmNayaxTokenProtector : INayaxTokenProtector
{
    private const int NonceSizeInBytes = 12;
    private const int TagSizeInBytes = 16;

    private readonly IReadOnlyDictionary<string, byte[]> _keysById;
    private readonly string _activeKeyId;

    /// <param name="options">
    /// The configured keys. Validated here, so an incomplete section fails at startup rather than
    /// on the first save.
    /// </param>
    /// <exception cref="InvalidOperationException">The key configuration is missing or malformed.</exception>
    public AesGcmNayaxTokenProtector(NayaxTokenProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _keysById = NayaxTokenProtectionConfiguration.ValidateAndDecodeKeys(options);
        _activeKeyId = options.ActiveKeyId;
    }

    /// <inheritdoc />
    public ProtectedNayaxToken Protect(string accessToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        var key = _keysById[_activeKeyId];
        var plaintext = Encoding.UTF8.GetBytes(accessToken);
        var stored = new byte[NonceSizeInBytes + TagSizeInBytes + plaintext.Length];
        var nonce = stored.AsSpan(0, NonceSizeInBytes);
        var tag = stored.AsSpan(NonceSizeInBytes, TagSizeInBytes);
        var ciphertext = stored.AsSpan(NonceSizeInBytes + TagSizeInBytes);

        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSizeInBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        // The plaintext copy this method made is cleared; the caller still owns the string it
        // passed in, which only the garbage collector can reclaim.
        CryptographicOperations.ZeroMemory(plaintext);

        return new ProtectedNayaxToken(_activeKeyId, Convert.ToBase64String(stored));
    }

    /// <inheritdoc />
    public string Unprotect(ProtectedNayaxToken protectedToken)
    {
        ArgumentNullException.ThrowIfNull(protectedToken);

        if (!_keysById.TryGetValue(protectedToken.KeyId, out var key))
        {
            // Fail closed, and say which key is missing: the key id is configuration metadata, not
            // a secret, and an operator cannot restore a retired key they are not told about.
            throw new NayaxTokenProtectionException(
                $"The stored Nayax access token was encrypted with key id '{protectedToken.KeyId}', "
                    + $"which is not configured in {NayaxTokenProtectionOptions.SectionName}:"
                    + $"{nameof(NayaxTokenProtectionOptions.Keys)}. The token cannot be decrypted "
                    + "until that key is restored.");
        }

        byte[] stored;

        try
        {
            stored = Convert.FromBase64String(protectedToken.Ciphertext);
        }
        catch (FormatException exception)
        {
            throw new NayaxTokenProtectionException(MalformedMessage, exception);
        }

        if (stored.Length <= NonceSizeInBytes + TagSizeInBytes)
        {
            throw new NayaxTokenProtectionException(MalformedMessage);
        }

        var plaintext = new byte[stored.Length - NonceSizeInBytes - TagSizeInBytes];

        try
        {
            using var aes = new AesGcm(key, TagSizeInBytes);
            aes.Decrypt(
                stored.AsSpan(0, NonceSizeInBytes),
                stored.AsSpan(NonceSizeInBytes + TagSizeInBytes),
                stored.AsSpan(NonceSizeInBytes, TagSizeInBytes),
                plaintext);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            // An authentication-tag mismatch: the ciphertext was altered, or it was produced by a
            // different key than the id claims. Either way the token is unusable, and guessing is
            // worse than refusing.
            throw new NayaxTokenProtectionException(
                "The stored Nayax access token failed its authentication check and was not "
                    + "decrypted. The stored value is corrupt or was tampered with.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string MalformedMessage =>
        "The stored Nayax access token is not a well-formed protected value and was not decrypted.";
}
