using System.Security.Cryptography;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class SecretCipherBindingTests
{
    [Fact]
    public async Task Ciphertext_cannot_be_replayed_for_another_server_asset()
    {
        var provider = new FixedDataEncryptionKeyProvider(
            Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        var cipher = new AesGcmSecretCipher(provider);
        var firstAssetId = Guid.NewGuid();
        var secondAssetId = Guid.NewGuid();

        var envelope = await cipher.EncryptAsync(firstAssetId, "asset-bound", default);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
            () => cipher.DecryptAsync(secondAssetId, envelope, default));
    }

    private sealed class FixedDataEncryptionKeyProvider(byte[] key) : IDataEncryptionKeyProvider
    {
        public Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(1, key));

        public Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct) =>
            Task.FromResult(new DataEncryptionKeyMaterial(keyVersion, key));
    }
}
