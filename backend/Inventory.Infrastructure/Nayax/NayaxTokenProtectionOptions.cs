using System.Globalization;

namespace Inventory.Infrastructure.Nayax;

/// <summary>
/// The encryption keys a business's stored Nayax access token is protected with (issue #518).
///
/// Every value here is secret key material, so nothing in this section is ever committed: it comes
/// from user-secrets locally and Key Vault/App Service configuration in Azure
/// (<c>NayaxTokenProtection__ActiveKeyId</c>, <c>NayaxTokenProtection__Keys__&lt;key-id&gt;</c>).
/// See README.md § Configuration and secrets.
///
/// <see cref="Keys"/> is a map rather than a single key because rotation has to be possible without
/// re-encrypting every stored token at once: new tokens are encrypted with
/// <see cref="ActiveKeyId"/>, and an older ciphertext keeps naming the key that produced it until
/// something saves it again. Removing a key from this section is therefore what retires it, and it
/// makes every ciphertext still naming it undecryptable on purpose.
/// </summary>
public sealed class NayaxTokenProtectionOptions
{
    public const string SectionName = "NayaxTokenProtection";

    /// <summary>
    /// The id of the key new ciphertext is produced with. Must be one of the <see cref="Keys"/>.
    /// </summary>
    public string ActiveKeyId { get; set; } = string.Empty;

    /// <summary>
    /// Key id to base64-encoded 256-bit AES key. Compared case-insensitively, because the
    /// configuration providers these arrive through treat their keys that way, so two spellings of
    /// one id must not become two keys.
    /// </summary>
    public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this environment has been given any key material at all. Unconfigured is a
    /// legitimate state - an environment that uses no Nayax integration stores no credentials - and
    /// it must not stop the API from starting; it makes storing or reading a per-business token fail
    /// closed instead, which since issue #520 means the Nayax features fail closed and nothing else
    /// does. See <see cref="UnconfiguredNayaxTokenProtector"/>.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ActiveKeyId) || Keys.Count > 0;
}

/// <summary>
/// Validates <see cref="NayaxTokenProtectionOptions"/> and turns it into the key material the
/// protector uses.
///
/// The failure mode is the point, exactly as it is for <see cref="NayaxLynxConfiguration"/>: a
/// half-configured key section - an active key id naming a key that is not there, a value that is
/// not base64, a key of the wrong length - fails at startup with a message naming the setting to
/// fix, rather than surfacing the first time an operator saves a token. No message ever contains
/// key material.
/// </summary>
public static class NayaxTokenProtectionConfiguration
{
    /// <summary>The only supported key length: AES-256, which <see cref="System.Security.Cryptography.AesGcm"/> needs 32 bytes for.</summary>
    public const int KeySizeInBytes = 32;

    /// <summary>
    /// Decodes and checks every configured key.
    /// </summary>
    /// <returns>The decoded keys, keyed by id, case-insensitively.</returns>
    /// <exception cref="InvalidOperationException">A setting is missing, malformed or inconsistent.</exception>
    public static IReadOnlyDictionary<string, byte[]> ValidateAndDecodeKeys(NayaxTokenProtectionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ActiveKeyId))
        {
            throw new InvalidOperationException(Invalid(
                nameof(NayaxTokenProtectionOptions.ActiveKeyId),
                "it is required once any encryption key is configured, and must name one of the "
                    + $"{nameof(NayaxTokenProtectionOptions.Keys)} entries."));
        }

        var decoded = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var (keyId, encodedKey) in options.Keys)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                throw new InvalidOperationException(Invalid(
                    nameof(NayaxTokenProtectionOptions.Keys),
                    "a key id is required: an unnamed key cannot be recorded with the ciphertext it "
                        + "produces, so nothing could decrypt that ciphertext later."));
            }

            if (!TryDecodeKey(encodedKey, out var key))
            {
                throw new InvalidOperationException(Invalid(
                    $"{nameof(NayaxTokenProtectionOptions.Keys)}:{keyId}",
                    $"it must be a base64-encoded {KeySizeInBytes * 8}-bit key "
                        + $"({KeySizeInBytes} bytes)."));
            }

            decoded[keyId] = key;
        }

        if (!decoded.ContainsKey(options.ActiveKeyId))
        {
            throw new InvalidOperationException(Invalid(
                nameof(NayaxTokenProtectionOptions.ActiveKeyId),
                $"no {nameof(NayaxTokenProtectionOptions.Keys)} entry is configured for the active "
                    + "key id, so a token could be encrypted with a key nothing can decrypt with."));
        }

        return decoded;
    }

    private static bool TryDecodeKey(string? encodedKey, out byte[] key)
    {
        key = [];

        if (string.IsNullOrWhiteSpace(encodedKey))
        {
            return false;
        }

        var buffer = new byte[KeySizeInBytes];

        if (!Convert.TryFromBase64String(encodedKey.Trim(), buffer, out var written) || written != KeySizeInBytes)
        {
            return false;
        }

        key = buffer;
        return true;
    }

    private static string Invalid(string setting, string problem) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{NayaxTokenProtectionOptions.SectionName}:{setting} is invalid: {problem}");
}
