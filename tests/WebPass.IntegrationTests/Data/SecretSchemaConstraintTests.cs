using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class SecretSchemaConstraintTests
{
    [Fact]
    public async Task Database_rejects_a_second_active_data_key()
    {
        await using var db = NewDatabase();
        await db.Database.MigrateAsync();
        try
        {
            db.DataEncryptionKeys.Add(NewKey(1));
            await db.SaveChangesAsync();
            db.DataEncryptionKeys.Add(NewKey(2));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Database_allows_only_one_secret_per_server_asset()
    {
        await using var db = NewDatabase();
        await db.Database.MigrateAsync();
        try
        {
            var asset = await AddAssetAsync(db);
            db.DataEncryptionKeys.Add(NewKey(1));
            db.ServerSecrets.Add(NewSecret(asset.Id, 1, 0x10));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            db.ServerSecrets.Add(NewSecret(asset.Id, 1, 0x20));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static DataEncryptionKey NewKey(int version) => new()
    {
        KeyVersion = version,
        WrappedKey = Enumerable.Repeat((byte)version, 256).ToArray(),
        CertificateThumbprint = new((char)('A' + version), 40),
    };

    private static ServerSecret NewSecret(Guid assetId, int version, byte value) => new()
    {
        ServerAssetId = assetId,
        Ciphertext = [value],
        Nonce = Enumerable.Repeat(value, 12).ToArray(),
        AuthenticationTag = Enumerable.Repeat(value, 16).ToArray(),
        KeyVersion = version,
    };

    private static WebPassDbContext NewDatabase()
    {
        var databaseName = $"WebPass_SecretSchema_{Guid.NewGuid():N}";
        return new WebPassDbContext(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseSqlServer($"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True")
                .Options);
    }

    private static async Task<ServerAsset> AddAssetAsync(WebPassDbContext db)
    {
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.40.0.0/24",
            NetworkAddress = "10.40.0.0",
            PrefixLength = 24,
            Location = "HQ",
        };
        var asset = new ServerAsset
        {
            Subnet = subnet,
            BusinessIp = "10.40.0.10",
            BusinessIpNumber = 170393610,
            Location = "HQ",
            ComputerName = "db-03",
            SystemName = "Database",
        };
        db.AddRange(subnet, asset);
        await db.SaveChangesAsync();
        return asset;
    }
}
