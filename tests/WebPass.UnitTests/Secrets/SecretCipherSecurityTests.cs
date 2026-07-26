using System.Security.Cryptography;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class SecretCipherSecurityTests
{
    private static readonly Guid SecretId = Guid.Parse("adfc9f96-d9ac-44fd-a33d-c9199a019bba");

    [Fact]
    public async Task Repeated_plaintext_uses_distinct_nonces_and_ciphertext()
    {
        var cipher = NewCipher();

        var first = await cipher.EncryptAsync(SecretId, "same-value", default);
        var second = await cipher.EncryptAsync(SecretId, "same-value", default);

        Assert.False(first.Nonce.AsSpan().SequenceEqual(second.Nonce));
        Assert.False(first.Ciphertext.AsSpan().SequenceEqual(second.Ciphertext));
    }

    [Fact]
    public async Task Tampered_authentication_tag_is_rejected()
    {
        var cipher = NewCipher();
        var envelope = await cipher.EncryptAsync(SecretId, "protected-value", default);
        envelope.AuthenticationTag[0] ^= 0x01;

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => cipher.DecryptAsync(SecretId, envelope, default));
    }

    [Fact]
    public async Task Oversized_plaintext_is_rejected_before_a_key_is_requested()
    {
        var provider = new RejectingDataEncryptionKeyProvider();
        var cipher = new AesGcmSecretCipher(provider);
        var oversized = new string('x', AesGcmSecretCipher.MaximumPlaintextBytes + 1);

        await Assert.ThrowsAsync<ArgumentException>(() => cipher.EncryptAsync(SecretId, oversized, default));
    }

    [Fact]
    public async Task Provider_version_mismatch_is_rejected()
    {
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var cipher = new AesGcmSecretCipher(new FixedDataEncryptionKeyProvider(1, key));
        var envelope = await cipher.EncryptAsync(SecretId, "versioned", default);
        var mismatched = new AesGcmSecretCipher(new FixedDataEncryptionKeyProvider(2, key));

        await Assert.ThrowsAsync<CryptographicException>(
            () => mismatched.DecryptAsync(SecretId, envelope, default));
    }

    private static AesGcmSecretCipher NewCipher() => new(
        new FixedDataEncryptionKeyProvider(
            1,
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray()));

    private sealed class FixedDataEncryptionKeyProvider(int version, byte[] key)
        : IDataEncryptionKeyProvider
    {
        public Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(version, key));

        public Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(version, key));
    }

    private sealed class RejectingDataEncryptionKeyProvider : IDataEncryptionKeyProvider
    {
        public Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The key provider must not be called.");

        public Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct) =>
            throw new InvalidOperationException("The key provider must not be called.");
    }
}
