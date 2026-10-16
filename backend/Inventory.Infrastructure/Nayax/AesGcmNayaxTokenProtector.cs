using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Inventory.Domain.Tenancy;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// Protects a business's Nayax access token with AES-256-GCM (issue #518), bound to the business it
/// belongs to (issue #681).
///
/// AES-GCM is authenticated encryption, which is what makes tampering a detectable failure rather
/// than a silent one: a flipped byte in the stored ciphertext fails the tag check and throws,
/// instead of decrypting to a different token that would then be sent to Nayax. A fresh random
/// nonce per encryption means saving the same token twice produces different ciphertext, so the
/// column cannot be used to tell whether two businesses configured the same credential.
///
/// <para><strong>The owning business is authenticated with the ciphertext</strong> (issue #681).
/// AES-GCM's associated data is the business id, so the tag covers it: a ciphertext encrypted for
/// business 4 fails its authentication check when it is presented as business 9's, whatever the key.
/// Until this binding existed, encryption on its own answered "is this a genuine ciphertext?" and
/// said nothing about <em>whose</em>, so a value copied between rows - by a hand-written
/// <c>UPDATE</c>, a restored backup, a mistaken repair - decrypted perfectly and the other business
/// then authenticated to Nayax with it. The business id is not a secret and does not need to be: the
/// property wanted is that it cannot be changed without invalidating the tag, which is exactly what
/// associated data gives.</para>
///
/// <para><strong>Two stored formats, and the format is self-describing.</strong> A value this type
/// writes is <c>v2:base64(nonce || tag || ciphertext)</c> - the fixed-size nonce and tag first, so
/// the body needs no length prefix, behind the version marker that says the business id is part of
/// the authentication. A value written before issue #681 is bare base64 with no marker, is decrypted
/// with no associated data, and so still decrypts: existing rows keep working across the deployment
/// without a migration or a re-encryption step. The marker is unambiguous because base64 cannot
/// contain <c>:</c>, so no legacy value can be mistaken for a bound one or the reverse.</para>
///
/// <para>A legacy row is therefore <em>not</em> business-bound, and re-encrypting one is the
/// human-run <c>migrate-nayax-connection</c> path or an Owner saving their connection again - never
/// an automatic rewrite, which AGENTS.md § Database and migrations forbids at startup and which this
/// type could not do anyway: it holds no database.</para>
///
/// The key id that produced a value is stored in its own column, never inside this blob. Nothing in
/// this type logs, throws or returns a plaintext token, a ciphertext or key material.
/// </summary>
public sealed class AesGcmNayaxTokenProtector : INayaxTokenProtector
{
    private const int NonceSizeInBytes = 12;
    private const int TagSizeInBytes = 16;

    /// <summary>
    /// The marker every business-bound value carries. It is a prefix on the stored text rather than
    /// a byte inside the blob because a leading byte would be indistinguishable from a nonce byte
    /// that happened to hold the same value - base64 has no <c>:</c>, so this cannot be confused
    /// with a legacy value.
    /// </summary>
    internal const string BusinessBoundPrefix = "v2:";

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
    public ProtectedNayaxToken Protect(string accessToken, BusinessId businessId)
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
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedDataFor(businessId));

        // The plaintext copy this method made is cleared; the caller still owns the string it
        // passed in, which only the garbage collector can reclaim.
        CryptographicOperations.ZeroMemory(plaintext);

        return new ProtectedNayaxToken(
            _activeKeyId,
            BusinessBoundPrefix + Convert.ToBase64String(stored));
    }

    /// <inheritdoc />
    public string Unprotect(ProtectedNayaxToken protectedToken, BusinessId businessId)
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

        // The marker decides whether the business id is part of the authentication, and it is read
        // from the stored value rather than inferred from anything else: a value written before
        // issue #681 carries none and is decrypted exactly as it was written, with no associated
        // data, which is what lets existing rows keep working without a re-encryption step.
        var businessBound = protectedToken.Ciphertext.StartsWith(BusinessBoundPrefix, StringComparison.Ordinal);
        var encoded = businessBound
            ? protectedToken.Ciphertext[BusinessBoundPrefix.Length..]
            : protectedToken.Ciphertext;

        byte[] stored;

        try
        {
            stored = Convert.FromBase64String(encoded);
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
                plaintext,
                businessBound ? AssociatedDataFor(businessId) : default);

            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException exception)
        {
            // An authentication-tag mismatch: the ciphertext was altered, it was produced by a
            // different key than the id claims, or - since issue #681 - it was encrypted for a
            // different business than the row it is stored on. Either way the token is unusable, and
            // guessing is worse than refusing. The message names no business id, because this
            // exception is logged and a refusal is not a place to publish which business a copied
            // ciphertext came from.
            throw new NayaxTokenProtectionException(
                "The stored Nayax access token failed its authentication check and was not "
                    + "decrypted. The stored value is corrupt, was tampered with, or was encrypted "
                    + "for a different business.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The authenticated, unencrypted context of one ciphertext: a fixed label, the format version
    /// and the owning business's key. The label and version are in it so a value could never be
    /// replayed into some other use of the same key, and the number is formatted with the invariant
    /// culture so the bytes do not depend on the host's locale.
    /// </summary>
    private static byte[] AssociatedDataFor(BusinessId businessId) =>
        Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"nayax-access-token|v2|business={businessId.Value}"));

    private static string MalformedMessage =>
        "The stored Nayax access token is not a well-formed protected value and was not decrypted.";
}
