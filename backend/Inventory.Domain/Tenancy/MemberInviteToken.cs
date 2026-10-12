using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The invite link's secret, and the one representation of it this application keeps (issue #531).
///
/// <para><strong>The token itself is never stored.</strong> An invite row holds
/// <see cref="Hash"/>'s output and nothing else, so a copy of the database - a backup, a restored
/// snapshot, a diagnostics read - yields no usable invite link. The plaintext exists for exactly
/// one response, the creation it was generated in, and is never logged, never audited and never
/// returned again.</para>
///
/// <para><strong>Why a plain SHA-256 and not a password KDF.</strong> A slow, salted KDF defends a
/// secret a person chose, where the whole attack is guessing it. This secret is
/// <see cref="EntropyBytes"/> bytes of cryptographic randomness that no one chose and no one has to
/// remember, so there is nothing to guess: the search space is the point of a 256-bit token, and a
/// salted per-row hash would additionally make the one lookup the redemption of #532 needs - "which
/// invite is this token?" - impossible to answer without reading every row and hashing against each
/// one. The hash is here to make the stored value useless, and for that a single deterministic
/// SHA-256 is the right tool.</para>
///
/// <para>Generating the token is deliberately <em>not</em> here: this layer is the deterministic
/// one, and randomness is not a deterministic rule. <c>Inventory.Application.Members</c>'s invite
/// creation draws the bytes from the platform's cryptographic generator and uses the length
/// constants below.</para>
/// </summary>
public static class MemberInviteToken
{
    /// <summary>
    /// The agreed floor on an invite token's entropy: 128 bits. It is stated separately from
    /// <see cref="EntropyBytes"/> so a test can assert the contract rather than the current choice,
    /// and so lowering the real length below the agreed minimum is a visible change.
    /// </summary>
    public const int MinimumEntropyBytes = 16;

    /// <summary>
    /// What this deployment actually generates: 256 bits, drawn from the platform's cryptographic
    /// random number generator. Comfortably above the floor, and the natural width for the SHA-256
    /// the hash below uses.
    /// </summary>
    public const int EntropyBytes = 32;

    /// <summary>
    /// The length of <see cref="Hash"/>'s output: SHA-256 as 64 lowercase hexadecimal characters.
    /// </summary>
    public const int HashLength = 64;

    /// <summary>
    /// The stored representation of <paramref name="token"/>: its SHA-256 digest as lowercase
    /// hexadecimal. Deterministic, so the redemption of #532 can look an invite up by hashing the
    /// token it was handed, and one-way, so the stored value cannot be turned back into a link.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="token"/> is null, empty or whitespace.</exception>
    public static string Hash(string token)
    {
        // A blank token is a programming error, not a caller's input: the creation path generates
        // the value, and a redemption path that reached here with nothing would otherwise hash the
        // empty string into a perfectly valid-looking lookup key.
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}
