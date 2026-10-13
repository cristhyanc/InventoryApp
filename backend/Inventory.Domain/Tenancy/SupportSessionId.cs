using System.Security.Cryptography;
using System.Text;

namespace Inventory.Domain.Tenancy;

/// <summary>
/// The support session's own identifier, and the one representation of it this application keeps
/// (issue #550, a slice of #512 under #328).
///
/// <para><strong>It is a credential, not a key.</strong> A support session is a person from outside a
/// business reading that business's data, and a request joins the session by presenting this value in
/// a header (issue #551). Anything that can present it can read the business, so it is
/// generated from cryptographic randomness, returned exactly once - in the response to the start that
/// created it - and never reported, logged or audited again.</para>
///
/// <para><strong>The value itself is never stored.</strong> A session row holds
/// <see cref="Hash"/>'s output and nothing else, so a copy of the database - a backup, a restored
/// snapshot, a diagnostics read - yields no usable session id. This is <see cref="MemberInviteToken"/>'s
/// decision for the same reason, and deliberately a separate type rather than a shared one: the two
/// secrets have different lifetimes, different holders and different blast radii, and a single shared
/// constant would make "how long is an invite token?" and "how long is a session id?" one question
/// that nobody could change independently.</para>
///
/// <para><strong>Why a plain SHA-256 and not a password KDF.</strong> A slow, salted KDF defends a
/// secret a person chose, where the whole attack is guessing it. This secret is
/// <see cref="EntropyBytes"/> bytes of randomness that no one chose and no one has to remember, so
/// there is nothing to guess - and a salted per-row hash would make the one lookup a support request
/// needs ("which session is this id?") impossible to answer without reading every row and hashing
/// against each one. The hash is here to make the stored value useless, and a single deterministic
/// SHA-256 is the right tool for that.</para>
///
/// <para>Generating the id is deliberately <em>not</em> here: this layer is the deterministic one,
/// and randomness is not a deterministic rule. <c>Inventory.Application.SupportSessions</c>'s start
/// use case draws the bytes from the platform's cryptographic generator and uses the length constants
/// below.</para>
/// </summary>
public static class SupportSessionId
{
    /// <summary>
    /// The agreed floor on a session id's entropy: 128 bits. It is stated separately from
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
    /// The stored representation of <paramref name="sessionId"/>: its SHA-256 digest as lowercase
    /// hexadecimal. Deterministic, so a request carrying the id can be matched to its session by
    /// hashing what it presented, and one-way, so the stored value cannot be turned back into a
    /// usable id.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// <paramref name="sessionId"/> is null, empty or whitespace.
    /// </exception>
    public static string Hash(string sessionId)
    {
        // A blank id is a programming error, not a caller's input: the start path generates the
        // value, and a lookup path that reached here with nothing would otherwise hash the empty
        // string into a perfectly valid-looking lookup key.
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
    }
}
