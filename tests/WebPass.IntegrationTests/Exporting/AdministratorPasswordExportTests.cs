using System.Security.Cryptography;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Exporting;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Exporting;
using WebPass.Web.Infrastructure.Secrets;
using Xunit;

namespace WebPass.IntegrationTests.Exporting;

public sealed class AdministratorPasswordExportTests
{
    [Fact]
    public async Task Password_export_requires_administrator_and_current_session_grant()
    {
        await using var fixture = await ExportFixture.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.OrdinaryUser.Id,
                default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.Administrator.Id,
                default));
        await fixture.Grants.StoreAsync(
            fixture.Grant("another-session"),
            default);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.Administrator.Id,
                default));

        await fixture.Grants.StoreAsync(
            fixture.Grant(fixture.Fingerprint.GetCurrent()),
            default);
        var file = await fixture.Service.ExportAsync(
            new ServerListQuery(),
            fixture.Administrator.Id,
            default);

        Assert.EndsWith(".xlsx", file.FileName, StringComparison.Ordinal);
        Assert.Equal(4, await fixture.Db.AuditLogs.CountAsync());
        Assert.Equal(
            3,
            await fixture.Db.AuditLogs.CountAsync(entry => entry.Result == "Denied"));
        Assert.Equal(
            1,
            await fixture.Db.AuditLogs.CountAsync(entry => entry.Result == "Success"));
    }

    [Fact]
    public async Task Password_export_decrypts_present_secret_and_leaves_absent_secret_empty()
    {
        await using var fixture = await ExportFixture.CreateAsync();
        await fixture.Grants.StoreAsync(
            fixture.Grant(fixture.Fingerprint.GetCurrent()),
            default);

        var file = await fixture.Service.ExportAsync(
            new ServerListQuery(Status: AliveStatus.Alive),
            fixture.Administrator.Id,
            default);

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet(1);
        Assert.Equal(9, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal("Password", sheet.Cell(1, 9).GetString());
        var passwordCell = sheet.Cell(2, 9);
        Assert.Equal("=server-password", passwordCell.GetString());
        Assert.True(passwordCell.Style.IncludeQuotePrefix);
        Assert.False(passwordCell.HasFormula);
        Assert.True(sheet.Cell(3, 9).IsEmpty());
        Assert.DoesNotContain(
            "server-password",
            (await fixture.Db.AuditLogs.SingleAsync()).Details ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_export_rejects_stale_or_expired_grant()
    {
        await using var fixture = await ExportFixture.CreateAsync();
        await fixture.Grants.StoreAsync(
            fixture.Grant(
                fixture.Fingerprint.GetCurrent(),
                rowVersion: [9]),
            default);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.Administrator.Id,
                default));

        await fixture.Grants.StoreAsync(
            fixture.Grant(fixture.Fingerprint.GetCurrent()),
            default);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(6);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.Administrator.Id,
                default));
    }

    [Fact]
    public async Task Decryption_failure_returns_no_file_and_audits_without_exception_text()
    {
        await using var fixture = await ExportFixture.CreateAsync(
            new ThrowingSecretCipher());
        await fixture.Grants.StoreAsync(
            fixture.Grant(fixture.Fingerprint.GetCurrent()),
            default);

        await Assert.ThrowsAsync<CryptographicException>(
            () => fixture.Service.ExportAsync(
                new ServerListQuery(),
                fixture.Administrator.Id,
                default));

        var audit = await fixture.Db.AuditLogs.SingleAsync();
        Assert.Equal("Failure", audit.Result);
        Assert.Null(audit.Details);
    }

    private sealed class ExportFixture : IAsyncDisposable
    {
        private readonly MemoryCache _cache;

        private ExportFixture(
            WebPassDbContext db,
            AppUser administrator,
            AppUser ordinaryUser,
            MutableTimeProvider clock,
            FixedFingerprint fingerprint,
            InMemoryReauthenticationGrantStore grants,
            AdministratorPasswordExportService service,
            MemoryCache cache)
        {
            Db = db;
            Administrator = administrator;
            OrdinaryUser = ordinaryUser;
            Clock = clock;
            Fingerprint = fingerprint;
            Grants = grants;
            Service = service;
            _cache = cache;
        }

        public WebPassDbContext Db { get; }
        public AppUser Administrator { get; }
        public AppUser OrdinaryUser { get; }
        public MutableTimeProvider Clock { get; }
        public FixedFingerprint Fingerprint { get; }
        public InMemoryReauthenticationGrantStore Grants { get; }
        public AdministratorPasswordExportService Service { get; }

        public static async Task<ExportFixture> CreateAsync(
            ISecretCipher? cipher = null)
        {
            var db = new WebPassDbContext(
                new DbContextOptionsBuilder<WebPassDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var administrator = new AppUser
            {
                Username = "administrator",
                PasswordHash = "unused",
                IsAdministrator = true,
                RowVersion = [1, 2, 3],
            };
            var ordinaryUser = new AppUser
            {
                Username = "operator",
                PasswordHash = "unused",
            };
            var subnet = new Subnet
            {
                Name = "Operations",
                Cidr = "10.0.0.0/24",
                NetworkAddress = "10.0.0.0",
                PrefixLength = 24,
                Location = "DC",
            };
            db.AddRange(administrator, ordinaryUser, subnet);
            var withSecret = AddAsset(
                db,
                subnet.Id,
                administrator.Id,
                "10.0.0.10",
                167772170);
            AddAsset(
                db,
                subnet.Id,
                administrator.Id,
                "10.0.0.11",
                167772171);
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = withSecret.Id,
                Ciphertext = [1],
                Nonce = new byte[12],
                AuthenticationTag = new byte[16],
                KeyVersion = 1,
                UpdatedBy = administrator.Id,
            });
            await db.SaveChangesAsync();

            var clock = new MutableTimeProvider(
                new DateTimeOffset(2026, 7, 27, 8, 0, 0, TimeSpan.Zero));
            var cache = new MemoryCache(new MemoryCacheOptions());
            var grants = new InMemoryReauthenticationGrantStore(cache, clock);
            var fingerprint = new FixedFingerprint("session-a");
            var service = new AdministratorPasswordExportService(
                db,
                new PermissionAuthorizationHandler(db),
                grants,
                fingerprint,
                cipher ?? new FixedSecretCipher(),
                new ExportDocumentWriter(),
                new AuditWriter(db));
            return new ExportFixture(
                db,
                administrator,
                ordinaryUser,
                clock,
                fingerprint,
                grants,
                service,
                cache);
        }

        public ReauthenticationGrant Grant(
            string fingerprint,
            byte[]? rowVersion = null) =>
            new(
                Administrator.Id,
                fingerprint,
                rowVersion ?? Administrator.RowVersion,
                Clock.UtcNow.AddMinutes(5));

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            _cache.Dispose();
        }

        private static ServerAsset AddAsset(
            WebPassDbContext db,
            Guid subnetId,
            Guid actorId,
            string ip,
            long ipNumber)
        {
            var asset = new ServerAsset
            {
                SubnetId = subnetId,
                BusinessIp = ip,
                BusinessIpNumber = ipNumber,
                Location = "DC",
                AliveStatus = AliveStatus.Alive,
                ComputerName = $"server-{ipNumber}",
                SystemName = "ERP",
                CreatedBy = actorId,
            };
            db.ServerAssets.Add(asset);
            return asset;
        }
    }

    private sealed class FixedFingerprint(string value)
        : IAuthenticationSessionFingerprint
    {
        public string GetCurrent() => value;
    }

    private sealed class FixedSecretCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            Task.FromResult("=server-password");
    }

    private sealed class ThrowingSecretCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new CryptographicException(
                "server-password must not reach audit");
    }

    private sealed class MutableTimeProvider(DateTimeOffset now)
        : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
