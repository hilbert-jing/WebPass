using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WebPass.Web.Application.Secrets;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class AesGcmSecretCipher(IDataEncryptionKeyProvider keyProvider) : ISecretCipher
{
    public const int MaximumPlaintextBytes = 4096;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<SecretEnvelope> EncryptAsync(Guid secretId, string plaintext, CancellationToken ct)
    {
        if (secretId == Guid.Empty) throw new ArgumentException("A secret identifier is required.", nameof(secretId));
        ArgumentNullException.ThrowIfNull(plaintext);
        ct.ThrowIfCancellationRequested();

        var plaintextBytes = StrictUtf8.GetBytes(plaintext);
        if (plaintextBytes.Length > MaximumPlaintextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            throw new ArgumentException($"Secret exceeds {MaximumPlaintextBytes} UTF-8 bytes.", nameof(plaintext));
        }

        try
        {
            using var key = await keyProvider.GetActiveAsync(ct);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key.Key.Span, TagSize);
            var associatedData = BuildAssociatedData(secretId, key.KeyVersion);
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
            return new SecretEnvelope(ciphertext, nonce, tag, key.KeyVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
        }
    }

    public async Task<string> DecryptAsync(Guid secretId, SecretEnvelope envelope, CancellationToken ct)
    {
        if (secretId == Guid.Empty) throw new ArgumentException("A secret identifier is required.", nameof(secretId));
        ArgumentNullException.ThrowIfNull(envelope);
        ct.ThrowIfCancellationRequested();
        if (envelope.KeyVersion <= 0) throw new CryptographicException("Secret key version is invalid.");
        if (envelope.Nonce.Length != NonceSize) throw new CryptographicException("Secret nonce is invalid.");
        if (envelope.AuthenticationTag.Length != TagSize) throw new CryptographicException("Secret authentication tag is invalid.");
        if (envelope.Ciphertext.Length > MaximumPlaintextBytes) throw new CryptographicException("Secret ciphertext is too large.");

        using var key = await keyProvider.GetByVersionAsync(envelope.KeyVersion, ct);
        if (key.KeyVersion != envelope.KeyVersion) throw new CryptographicException("Secret key version mismatch.");

        var plaintext = new byte[envelope.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key.Key.Span, TagSize);
            var associatedData = BuildAssociatedData(secretId, envelope.KeyVersion);
            aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.AuthenticationTag, plaintext, associatedData);
            return StrictUtf8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] BuildAssociatedData(Guid secretId, int keyVersion)
    {
        var associatedData = new byte[20];
        secretId.TryWriteBytes(associatedData);
        BinaryPrimitives.WriteInt32BigEndian(associatedData.AsSpan(16), keyVersion);
        return associatedData;
    }
}
