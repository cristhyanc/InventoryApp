using System.Buffers;
using System.Security.Cryptography;

namespace Inventory.Infrastructure.Documents.Migration;

/// <summary>
/// What a document's bytes are, independently of where they are stored.
///
/// A migration that compared names, sizes or timestamps would call two different documents the
/// same one; the hash is what lets a rerun tell "already copied" from "something else is sitting
/// at that name".
/// </summary>
/// <param name="ByteLength">Bytes read.</param>
/// <param name="Sha256">Uppercase hex SHA-256 of those bytes.</param>
public sealed record DocumentFingerprint(long ByteLength, string Sha256)
{
    /// <summary>A short, log-safe prefix of the hash, for correlating two fingerprints in a report.</summary>
    public string ShortSha256 => Sha256.Length <= 16 ? Sha256 : Sha256[..16];

    public bool Matches(DocumentFingerprint other) =>
        other is not null &&
        ByteLength == other.ByteLength &&
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
}

/// <summary>Computes a <see cref="DocumentFingerprint"/> by streaming a document's bytes.</summary>
public static class DocumentContentHash
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Reads <paramref name="content"/> to its end, hashing as it goes. Nothing is buffered
    /// whole: a document that outgrew the upload limit, or a future storage that streams, must
    /// not be able to turn verification into an out-of-memory failure.
    /// </summary>
    public static async Task<DocumentFingerprint> ComputeAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        var byteLength = 0L;

        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                byteLength += read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new DocumentFingerprint(byteLength, Convert.ToHexString(hash.GetHashAndReset()));
    }
}
