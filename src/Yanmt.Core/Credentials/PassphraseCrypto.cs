using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Yanmt.Core.Credentials;

/// <summary>
/// Passphrase encryption for credential exports: AES-256-GCM with a key from PBKDF2-SHA256 (built-in
/// crypto only). Layout: magic(8) | iterations(4, big endian) | salt(16) | nonce(12) | ciphertext | tag(16).
/// The header is authenticated as associated data, so editing any byte fails the same way a wrong
/// passphrase does ("wrong passphrase or damaged file"); the two can't be told apart by design.
/// </summary>
public static class PassphraseCrypto
{
    public const int DefaultIterations = 600_000;
    public const int MinIterations = 100_000;
    public const int MaxIterations = 5_000_000; // an imported file must not be able to pin a CPU for minutes
    public const int MinPassphraseLength = 12;
    public const int MaxPassphraseLength = 256;
    public const int MaxFileBytes = 4 * 1024 * 1024;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("YANMTCR1");
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 8 + 4 + SaltLength + NonceLength;

    public static string? ValidatePassphrase(string? passphrase) =>
        string.IsNullOrEmpty(passphrase) || passphrase.Length < MinPassphraseLength ? $"The passphrase must be at least {MinPassphraseLength} characters."
        : passphrase.Length > MaxPassphraseLength ? $"The passphrase must be at most {MaxPassphraseLength} characters."
        : null;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext, string passphrase, int iterations = DefaultIterations)
    {
        if (ValidatePassphrase(passphrase) is { } error)
        {
            throw new ArgumentException(error, nameof(passphrase));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, MinIterations);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(iterations, MaxIterations);

        var output = new byte[HeaderLength + plaintext.Length + TagLength];
        Magic.CopyTo(output, 0);
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(8, 4), iterations);
        RandomNumberGenerator.Fill(output.AsSpan(12, SaltLength));
        RandomNumberGenerator.Fill(output.AsSpan(12 + SaltLength, NonceLength));

        var key = DeriveKey(passphrase, output.AsSpan(12, SaltLength), iterations);
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Encrypt(output.AsSpan(12 + SaltLength, NonceLength), plaintext,
                output.AsSpan(HeaderLength, plaintext.Length), output.AsSpan(HeaderLength + plaintext.Length, TagLength),
                output.AsSpan(0, HeaderLength));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        return output;
    }

    /// <summary>Null when the passphrase is wrong or the file was modified (indistinguishable).</summary>
    /// <exception cref="InvalidDataException">Not a YANMT credential export, or its parameters are out of range.</exception>
    public static byte[]? Decrypt(ReadOnlySpan<byte> file, string passphrase)
    {
        if (file.Length < HeaderLength + TagLength || file.Length > MaxFileBytes || !file[..8].SequenceEqual(Magic))
        {
            throw new InvalidDataException("This is not a YANMT credential export.");
        }

        var iterations = BinaryPrimitives.ReadInt32BigEndian(file.Slice(8, 4));
        if (iterations is < MinIterations or > MaxIterations)
        {
            throw new InvalidDataException("This is not a YANMT credential export.");
        }

        var key = DeriveKey(passphrase, file.Slice(12, SaltLength), iterations);
        var plaintext = new byte[file.Length - HeaderLength - TagLength];
        try
        {
            using var gcm = new AesGcm(key, TagLength);
            gcm.Decrypt(file.Slice(12 + SaltLength, NonceLength), file.Slice(HeaderLength, plaintext.Length),
                file.Slice(HeaderLength + plaintext.Length, TagLength), plaintext, file[..HeaderLength]);
            return plaintext;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] DeriveKey(string passphrase, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormKC)), salt, iterations, HashAlgorithmName.SHA256, 32);
}
