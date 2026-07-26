using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.IntegrationTests.Secrets;

public sealed class AssetSecretWriteTests
{
    [Fact]
    public async Task Creating_asset_with_password_persists_only_ciphertext_and_redacted_audit()
    {
        await using var db = NewDatabase();
        var actor = await SeedAsync(db, PermissionCode.AssetCreate);
        var service = new ServerAssetService(
            db,
            new PermissionAuthorizationHandler(db),
            new AuditWriter(db),
            new StubSecretCipher());
        var input = new ServerAssetInput(
            "10.0.0.10",
            "DC",
            AliveStatus.Unknown,
            "server-10",
            "ERP",
            null,
            null,
            null,
            Password: "plain-server-password");

        var asset = await service.CreateAsync(input, actor.Id, default);

        var secret = await db.ServerSecrets.AsNoTracking().SingleAsync();
        Assert.Equal(asset.Id, secret.ServerAssetId);
        Assert.Equal([7, 8, 9], secret.Ciphertext);
        var persistedText = string.Join(
            "\n",
            await db.AuditLogs.AsNoTracking().Select(x => x.Details ?? string.Empty).ToListAsync());
        Assert.DoesNotContain("plain-server-password", persistedText, StringComparison.Ordinal);
    }

    private static WebPassDbContext NewDatabase() =>
        new(new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static async Task<AppUser> SeedAsync(WebPassDbContext db, string permission)
    {
        var actor = new AppUser { Username = "operator", PasswordHash = "unused" };
        db.Users.Add(actor);
        db.UserPermissions.Add(new UserPermission
        {
            UserId = actor.Id,
            PermissionCode = permission,
        });
        db.Subnets.Add(new Subnet
        {
            Name = "Operations",
            Cidr = "10.0.0.0/24",
            NetworkAddress = "10.0.0.0",
            PrefixLength = 24,
            Location = "DC",
        });
        await db.SaveChangesAsync();
        return actor;
    }

    private sealed class StubSecretCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            Task.FromResult(new SecretEnvelope([7, 8, 9], new byte[12], new byte[16], 1));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
