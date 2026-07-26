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

public sealed class AssetSecretUpdateTests
{
    [Fact]
    public async Task Blank_password_preserves_existing_ciphertext_and_supplied_password_replaces_it()
    {
        await using var db = new WebPassDbContext(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var actor = new AppUser { Username = "editor", PasswordHash = "unused" };
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.0.0.0/24",
            NetworkAddress = "10.0.0.0",
            PrefixLength = 24,
            Location = "DC",
        };
        var asset = new ServerAsset
        {
            SubnetId = subnet.Id,
            BusinessIp = "10.0.0.10",
            BusinessIpNumber = 167772170,
            Location = "DC",
            ComputerName = "server-10",
            SystemName = "ERP",
            RowVersion = [1],
        };
        db.AddRange(actor, subnet, asset);
        db.UserPermissions.Add(new UserPermission
        {
            UserId = actor.Id,
            PermissionCode = PermissionCode.AssetEdit,
        });
        db.ServerSecrets.Add(new ServerSecret
        {
            ServerAssetId = asset.Id,
            Ciphertext = [1, 2, 3],
            Nonce = new byte[12],
            AuthenticationTag = new byte[16],
            KeyVersion = 1,
        });
        await db.SaveChangesAsync();
        var service = new ServerAssetService(
            db,
            new PermissionAuthorizationHandler(db),
            new AuditWriter(db),
            new ReplacementCipher());
        var unchanged = Input(password: null);

        await service.UpdateAsync(asset.Id, unchanged, asset.RowVersion, actor.Id, default);
        Assert.Equal([1, 2, 3], (await db.ServerSecrets.SingleAsync()).Ciphertext);

        await service.UpdateAsync(asset.Id, Input("new-password"), asset.RowVersion, actor.Id, default);
        Assert.Equal([9, 9, 9], (await db.ServerSecrets.SingleAsync()).Ciphertext);
    }

    private static ServerAssetInput Input(string? password) =>
        new(
            "10.0.0.10",
            "DC",
            AliveStatus.Unknown,
            "server-10",
            "ERP",
            null,
            null,
            null,
            password);

    private sealed class ReplacementCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            Task.FromResult(new SecretEnvelope([9, 9, 9], new byte[12], new byte[16], 2));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
