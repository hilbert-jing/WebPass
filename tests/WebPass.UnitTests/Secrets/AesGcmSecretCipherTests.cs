using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class AesGcmSecretCipherTests
{
    private static readonly Guid SecretId = Guid.Parse("a50a6f58-0110-4268-9ad4-62a00fbb4193");

    [Fact]
    public async Task Encrypt_then_decrypt_returns_original_value()
    {
        var provider = new FixedDataEncryptionKeyProvider(
            7,
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var cipher = new AesGcmSecretCipher(provider);

        var envelope = await cipher.EncryptAsync(SecretId, "S3cret!数据库", default);

        Assert.Equal(7, envelope.KeyVersion);
        Assert.Equal("S3cret!数据库", await cipher.DecryptAsync(SecretId, envelope, default));
    }

    private sealed class FixedDataEncryptionKeyProvider(int version, byte[] key)
        : IDataEncryptionKeyProvider
    {
        public Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(version, key.ToArray()));

        public Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(keyVersion, key.ToArray()));
    }
}
