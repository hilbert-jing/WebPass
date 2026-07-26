using System.Text;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class SecretPersistenceTests
{
    [Fact]
    public async Task Migration_persists_only_an_encrypted_server_secret()
    {
        await using var db = NewDatabase();
        await db.Database.MigrateAsync();
        try
        {
            var asset = await AddAssetAsync(db);
            var provider = new DatabaseDataEncryptionKeyProvider(db, new XorDataKeyWrapper());
            var cipher = new AesGcmSecretCipher(provider);
            const string plaintext = "Database-P@ssword-数据库";

            var envelope = await cipher.EncryptAsync(asset.Id, plaintext, default);
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = asset.Id,
                Ciphertext = envelope.Ciphertext,
                Nonce = envelope.Nonce,
                AuthenticationTag = envelope.AuthenticationTag,
                KeyVersion = envelope.KeyVersion,
                UpdatedBy = asset.CreatedBy,
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var stored = await db.ServerSecrets.AsNoTracking().SingleAsync();
            Assert.False(stored.Ciphertext.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(plaintext)));
            var restored = await cipher.DecryptAsync(stored.ServerAssetId, new SecretEnvelope(
                stored.Ciphertext,
                stored.Nonce,
                stored.AuthenticationTag,
                stored.KeyVersion), default);
            Assert.Equal(plaintext, restored);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static WebPassDbContext NewDatabase()
    {
        var databaseName = $"WebPass_Secrets_{Guid.NewGuid():N}";
        return new WebPassDbContext(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseSqlServer($"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True")
                .Options);
    }

    private static async Task<ServerAsset> AddAssetAsync(WebPassDbContext db)
    {
        var actor = new AppUser
        {
            Username = "administrator",
            PasswordHash = "not-a-real-hash",
            IsAdministrator = true,
        };
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.20.0.0/24",
            NetworkAddress = "10.20.0.0",
            PrefixLength = 24,
            Location = "HQ",
        };
        var asset = new ServerAsset
        {
            Subnet = subnet,
            BusinessIp = "10.20.0.10",
            BusinessIpNumber = 169082890,
            Location = "HQ",
            ComputerName = "db-01",
            SystemName = "Database",
            CreatedBy = actor.Id,
        };
        db.AddRange(actor, subnet, asset);
        await db.SaveChangesAsync();
        return asset;
    }

    private sealed class XorDataKeyWrapper : IDataKeyWrapper
    {
        private const byte Mask = 0x5A;

        public string CurrentCertificateThumbprint { get; } = new('B', 40);

        public byte[] WrapKey(ReadOnlySpan<byte> dataKey) =>
            dataKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint) =>
            wrappedKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();
    }
}
