using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class DataKeyRotationTests
{
    [Fact]
    public async Task Rotation_reencrypts_secrets_and_retires_the_previous_key()
    {
        await using var db = NewDatabase();
        await db.Database.MigrateAsync();
        try
        {
            var (administrator, asset) = await AddAssetAsync(db);
            var wrapper = new XorDataKeyWrapper();
            var provider = new DatabaseDataEncryptionKeyProvider(db, wrapper);
            var cipher = new AesGcmSecretCipher(provider);
            const string plaintext = "Rotate-me-数据库";
            var original = await cipher.EncryptAsync(asset.Id, plaintext, default);
            db.ServerSecrets.Add(ToSecret(asset.Id, administrator.Id, original));
            await db.SaveChangesAsync();

            var service = new DataKeyRotationService(
                db,
                new PermissionAuthorizationHandler(db),
                wrapper,
                cipher,
                new AuditWriter(db));
            var result = await service.RotateAsync(administrator.Id, default);

            db.ChangeTracker.Clear();
            var keys = await db.DataEncryptionKeys.OrderBy(key => key.KeyVersion).ToListAsync();
            var rotated = await db.ServerSecrets.SingleAsync();
            Assert.Equal(1, result.PreviousVersion);
            Assert.Equal(2, result.NewVersion);
            Assert.Equal(1, result.ReencryptedSecretCount);
            Assert.NotNull(keys[0].RetiredAt);
            Assert.Null(keys[1].RetiredAt);
            Assert.Equal(2, rotated.KeyVersion);
            Assert.NotEqual(original.Ciphertext, rotated.Ciphertext);
            Assert.Equal(plaintext, await cipher.DecryptAsync(rotated.ServerAssetId, ToEnvelope(rotated), default));
            var audit = await db.AuditLogs.SingleAsync(entry => entry.Action == "DataKeyRotate");
            Assert.DoesNotContain(plaintext, audit.Details ?? string.Empty, StringComparison.Ordinal);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static ServerSecret ToSecret(Guid assetId, Guid actorId, SecretEnvelope envelope) => new()
    {
        ServerAssetId = assetId,
        Ciphertext = envelope.Ciphertext,
        Nonce = envelope.Nonce,
        AuthenticationTag = envelope.AuthenticationTag,
        KeyVersion = envelope.KeyVersion,
        UpdatedBy = actorId,
    };

    private static SecretEnvelope ToEnvelope(ServerSecret secret) => new(
        secret.Ciphertext,
        secret.Nonce,
        secret.AuthenticationTag,
        secret.KeyVersion);

    private static WebPassDbContext NewDatabase()
    {
        var databaseName = $"WebPass_Rotation_{Guid.NewGuid():N}";
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
            Cidr = "10.30.0.0/24",
            NetworkAddress = "10.30.0.0",
            PrefixLength = 24,
            Location = "HQ",
        };
        var asset = new ServerAsset
        {
            Subnet = subnet,
            BusinessIp = "10.30.0.10",
            BusinessIpNumber = 169738250,
            Location = "HQ",
            ComputerName = "db-02",
            SystemName = "Database",
            CreatedBy = administrator.Id,
        };
        db.AddRange(administrator, subnet, asset);
        await db.SaveChangesAsync();
        return (administrator, asset);
    }

    private sealed class XorDataKeyWrapper : IDataKeyWrapper
    {
        private const byte Mask = 0xC3;

        public string CurrentCertificateThumbprint { get; } = new('C', 40);

        public byte[] WrapKey(ReadOnlySpan<byte> dataKey) =>
            dataKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();

        public byte[] UnwrapKey(ReadOnlySpan<byte> wrappedKey, string certificateThumbprint) =>
            wrappedKey.ToArray().Select(value => (byte)(value ^ Mask)).ToArray();
    }
}
