using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.UnitTests.Secrets;

public sealed class SecretRevealServiceTests
{
    [Fact]
    public async Task Reveal_without_a_current_session_grant_is_denied_before_plaintext_is_returned()
    {
        await using var fixture = await RevealFixture.CreateAsync(grant: false, permission: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.RevealAsync(fixture.UserId, fixture.AssetId, default));

        var audit = Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal("SecretReveal", audit.Action);
        Assert.Equal("Denied", audit.Result);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task Reveal_rechecks_permission_even_when_a_grant_exists()
    {
        await using var fixture = await RevealFixture.CreateAsync(grant: true, permission: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.RevealAsync(fixture.UserId, fixture.AssetId, default));
    }

    [Fact]
    public async Task Authorized_reveal_returns_plaintext_and_writes_only_redacted_audit_metadata()
    {
        await using var fixture = await RevealFixture.CreateAsync(grant: true, permission: true);

        var result = await fixture.Service.RevealAsync(fixture.UserId, fixture.AssetId, default);

        Assert.Equal("server-password", result.Password);
        var audit = Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal("SecretReveal", audit.Action);
        Assert.Equal(fixture.AssetId.ToString(), audit.ObjectId);
        Assert.Equal("Success", audit.Result);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task Decryption_failure_writes_a_redacted_failure_audit()
    {
        await using var fixture = await RevealFixture.CreateAsync(
            grant: true,
            permission: true,
            decryptFails: true);

        await Assert.ThrowsAsync<CryptographicException>(
            () => fixture.Service.RevealAsync(fixture.UserId, fixture.AssetId, default));

        var audit = Assert.Single(await fixture.Db.AuditLogs.AsNoTracking().ToListAsync());
        Assert.Equal("Failure", audit.Result);
        Assert.Null(audit.Details);
    }

    private sealed class RevealFixture : IAsyncDisposable
    {
        private RevealFixture(
            WebPassDbContext db,
            MemoryCache cache,
            Guid userId,
            Guid assetId,
            SecretRevealService service)
        {
            Db = db;
            Cache = cache;
            UserId = userId;
            AssetId = assetId;
            Service = service;
        }

        public WebPassDbContext Db { get; }
        public MemoryCache Cache { get; }
        public Guid UserId { get; }
        public Guid AssetId { get; }
        public SecretRevealService Service { get; }

        public static async Task<RevealFixture> CreateAsync(
            bool grant,
            bool permission,
            bool decryptFails = false)
        {
            var options = new DbContextOptionsBuilder<WebPassDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options;
            var db = new WebPassDbContext(options);
            var user = new AppUser
            {
                Username = "operator",
                PasswordHash = "unused",
                RowVersion = [1, 2, 3],
            };
            var asset = new ServerAsset
            {
                BusinessIp = "10.0.0.10",
                Location = "DC",
                ComputerName = "server-10",
                SystemName = "ERP",
            };
            var key = new DataEncryptionKey
            {
                KeyVersion = 1,
                WrappedKey = [1],
                CertificateThumbprint = "thumbprint",
                ActivatedAt = DateTimeOffset.UtcNow,
            };
            db.AddRange(user, asset, key);
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = asset.Id,
                Ciphertext = [1],
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
            });
            if (permission)
            {
                db.UserPermissions.Add(new UserPermission
                {
                    UserId = user.Id,
                    PermissionCode = PermissionCode.SecretReveal,
                });
            }
            await db.SaveChangesAsync();

            var cache = new MemoryCache(new MemoryCacheOptions());
            var grants = new InMemoryReauthenticationGrantStore(cache);
            if (grant)
            {
                await grants.StoreAsync(
                    new ReauthenticationGrant(
                        user.Id,
                        "sha256:session-a",
                        user.RowVersion,
                        DateTimeOffset.UtcNow.AddMinutes(5)),
                    default);
            }

            var service = new SecretRevealService(
                db,
                new PermissionAuthorizationHandler(db),
                grants,
                new StubAuthenticationSessionFingerprint(),
                new StubSecretCipher(decryptFails),
                new AuditWriter(db));
            return new RevealFixture(db, cache, user.Id, asset.Id, service);
        }

        public async ValueTask DisposeAsync()
        {
            Cache.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class StubAuthenticationSessionFingerprint
        : IAuthenticationSessionFingerprint
    {
        public string GetCurrent() => "sha256:session-a";
    }

    private sealed class StubSecretCipher(bool decryptFails) : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(Guid secretId, string plaintext, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DecryptAsync(Guid secretId, SecretEnvelope envelope, CancellationToken ct) =>
            decryptFails
                ? Task.FromException<string>(new CryptographicException("Authentication tag mismatch."))
                : Task.FromResult("server-password");
    }
}
