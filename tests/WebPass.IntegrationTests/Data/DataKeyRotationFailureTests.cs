using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class DataKeyRotationFailureTests
{
    [Fact]
    public async Task Authentication_failure_rolls_back_the_replacement_key()
    {
        await using var db = NewDatabase();
        await db.Database.MigrateAsync();
        try
        {
            var (administrator, asset) = await AddAssetAsync(db);
            var wrapper = new XorDataKeyWrapper();
            var provider = new DatabaseDataEncryptionKeyProvider(db, wrapper);
            var cipher = new AesGcmSecretCipher(provider);
            var envelope = await cipher.EncryptAsync(asset.Id, "tamper-before-rotation", default);
            envelope.AuthenticationTag[0] ^= 0x01;
            var secret = new ServerSecret
            {
                ServerAssetId = asset.Id,
                Ciphertext = envelope.Ciphertext,
                Nonce = envelope.Nonce,
                AuthenticationTag = envelope.AuthenticationTag,
                KeyVersion = envelope.KeyVersion,
                UpdatedBy = administrator.Id,
            };
            db.ServerSecrets.Add(secret);
            await db.SaveChangesAsync();
            var originalCiphertext = secret.Ciphertext.ToArray();
            var originalTag = secret.AuthenticationTag.ToArray();
            var service = new DataKeyRotationService(
                db,
                new PermissionAuthorizationHandler(db),
                wrapper,
                cipher,
                new AuditWriter(db));

            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(
                () => service.RotateAsync(administrator.Id, default));

            var keys = await db.DataEncryptionKeys.AsNoTracking().ToListAsync();
            var unchanged = await db.ServerSecrets.AsNoTracking().SingleAsync();
            Assert.Single(keys);
            Assert.Equal(1, keys[0].KeyVersion);
            Assert.Null(keys[0].RetiredAt);
            Assert.Equal(1, unchanged.KeyVersion);
            Assert.Equal(originalCiphertext, unchanged.Ciphertext);
            Assert.Equal(originalTag, unchanged.AuthenticationTag);
            Assert.False(await db.AuditLogs.AnyAsync(entry => entry.Action == "DataKeyRotate"));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static WebPassDbContext NewDatabase()
    {
        var databaseName = $"WebPass_RotationFailure_{Guid.NewGuid():N}";
        return new WebPassDbContext(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseSqlServer($"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True")
                .Options);
    }

    private static async Task<(AppUser Administrator, ServerAsset Asset)> AddAssetAsync(WebPassDbContext db)
    {
        var administrator = new AppUser
        {
            Username = "administrator",
            PasswordHash = "not-a-real-hash",
            IsAdministrator = true,
        };
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.50.0.0/24",
            NetworkAddress = "10.50.0.0",
            PrefixLength = 24,
            Location = "HQ",
        };
        var asset = new ServerAsset
        {
            Subnet = subnet,
            BusinessIp = "10.50.0.10",
            BusinessIpNumber = 171049370,
            Location = "HQ",
            ComputerName = "db-04",
            SystemName = "Database",
            CreatedBy = administrator.Id,
        };
        db.AddRange(administrator, subnet, asset);
        await db.SaveChangesAsync();
        return (administrator, asset);
    }

    private sealed class XorDataKeyWrapper : IDataKeyWrapper
    {
        private const byte Mask = 0x39;

        public string CurrentCertificateThumbprint { get; } = new('E', 40);

        public byte[] WrapKey(ReadOnlySpan<byte> dataKey) =>
            dataKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint) =>
            wrappedKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();
    }
}
