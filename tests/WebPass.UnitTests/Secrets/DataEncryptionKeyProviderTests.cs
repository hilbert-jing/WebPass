using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class DataEncryptionKeyProviderTests
{
    [Fact]
    public async Task First_active_key_request_persists_only_a_wrapped_key()
    {
        await using var db = NewDatabase();
        var wrapper = new XorDataKeyWrapper();
        var provider = new DatabaseDataEncryptionKeyProvider(db, wrapper);

        using var material = await provider.GetActiveAsync(default);

        var stored = Assert.Single(db.DataEncryptionKeys);
        Assert.Equal(1, material.KeyVersion);
        Assert.Equal(32, material.Key.Length);
        Assert.Equal(wrapper.CurrentCertificateThumbprint, stored.CertificateThumbprint);
        Assert.False(material.Key.Span.SequenceEqual(stored.WrappedKey));
        Assert.Equal(material.Key.ToArray(), wrapper.UnwrapKey(stored.WrappedKey, stored.CertificateThumbprint));
    }

    private static WebPassDbContext NewDatabase() => new(
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class XorDataKeyWrapper : IDataKeyWrapper
    {
        private const byte Mask = 0xA5;

        public string CurrentCertificateThumbprint { get; } = new('A', 40);

        public byte[] WrapKey(ReadOnlySpan<byte> dataKey) => dataKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint) =>
            wrappedKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();
    }
}
