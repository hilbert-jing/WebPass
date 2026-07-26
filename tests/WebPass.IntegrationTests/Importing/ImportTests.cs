using System.Text;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Importing;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Importing;
using Xunit;

namespace WebPass.IntegrationTests.Importing;

public sealed class ImportTests
{
    [Fact]
    public async Task Duplicate_business_ip_is_a_blocking_preview_error()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var source = Csv(
            Row("10.0.0.10", "one") +
            Row("10.0.0.10", "two"));

        var preview = await fixture.Service.PreviewAsync(
            source,
            ImportFileType.Csv,
            fixture.ActorId,
            default);

        Assert.True(preview.HasBlockingErrors);
        Assert.Contains(preview.Errors, error =>
            error.Field == "BusinessIp" && error.RowNumber == 3);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Service.CommitAsync(preview.Id, fixture.ActorId, default));
    }

    [Fact]
    public async Task Valid_preview_commits_asset_encrypted_password_job_and_redacted_summary()
    {
        await using var fixture = await ImportFixture.CreateAsync();
        await using var source = Csv(Row("10.0.0.10", "server-password"));

        var preview = await fixture.Service.PreviewAsync(
            source,
            ImportFileType.Csv,
            fixture.ActorId,
            default);
        var result = await fixture.Service.CommitAsync(
            preview.Id,
            fixture.ActorId,
            default);

        Assert.False(preview.HasBlockingErrors);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal([4, 5, 6], (await fixture.Db.ServerSecrets.AsNoTracking().SingleAsync()).Ciphertext);
        Assert.Equal("Committed", (await fixture.Db.ImportJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("Csv", (await fixture.Db.ImportJobs.AsNoTracking().SingleAsync()).FileType);
        var auditText = string.Join(
            "\n",
            await fixture.Db.AuditLogs.AsNoTracking().Select(x => x.Details ?? "").ToListAsync());
        Assert.DoesNotContain("server-password", auditText, StringComparison.Ordinal);
    }

    private static MemoryStream Csv(string rows)
    {
        const string header =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n";
        return new MemoryStream(Encoding.UTF8.GetBytes(header + rows));
    }

    private static string Row(string ip, string password) =>
        $"{ip},DC,Unknown,server,ERP,,,,{password}\r\n";

    private sealed class ImportFixture : IAsyncDisposable
    {
        private ImportFixture(
            WebPassDbContext db,
            Guid actorId,
            ImportService service,
            InMemoryImportStageStore stages)
        {
            Db = db;
            ActorId = actorId;
            Service = service;
            Stages = stages;
        }

        public WebPassDbContext Db { get; }
        public Guid ActorId { get; }
        public ImportService Service { get; }
        public InMemoryImportStageStore Stages { get; }

        public static async Task<ImportFixture> CreateAsync()
        {
            var db = new WebPassDbContext(
                new DbContextOptionsBuilder<WebPassDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options);
            var actor = new AppUser { Username = "importer", PasswordHash = "unused" };
            db.Users.Add(actor);
            db.UserPermissions.Add(new UserPermission
            {
                UserId = actor.Id,
                PermissionCode = PermissionCode.ImportData,
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
            var cipher = new StubSecretCipher();
            var stages = new InMemoryImportStageStore();
            var service = new ImportService(
                db,
                new PermissionAuthorizationHandler(db),
                cipher,
                stages,
                new CsvAssetParser(),
                new XlsxAssetParser(),
                new AuditWriter(db));
            return new ImportFixture(db, actor.Id, service, stages);
        }

        public async ValueTask DisposeAsync()
        {
            Stages.Dispose();
            await Db.DisposeAsync();
        }
    }

    private sealed class StubSecretCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            Task.FromResult(new SecretEnvelope([4, 5, 6], new byte[12], new byte[16], 1));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
