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

public sealed class ImportAtomicityTests
{
    [Fact]
    public async Task Commit_conflict_leaves_no_partial_asset_job_or_audit()
    {
        await using var db = new WebPassDbContext(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);
        var actor = new AppUser { Username = "importer", PasswordHash = "unused" };
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.0.0.0/24",
            NetworkAddress = "10.0.0.0",
            PrefixLength = 24,
            Location = "DC",
        };
        db.AddRange(actor, subnet);
        db.UserPermissions.Add(new UserPermission
        {
            UserId = actor.Id,
            PermissionCode = PermissionCode.ImportData,
        });
        await db.SaveChangesAsync();
        using var stages = new InMemoryImportStageStore();
        var service = new ImportService(
            db,
            new PermissionAuthorizationHandler(db),
            new StubCipher(),
            stages,
            new CsvAssetParser(),
            new XlsxAssetParser(),
            new AuditWriter(db));
        const string csv =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n" +
            "10.0.0.10,DC,Unknown,one,ERP,,,,\r\n" +
            "10.0.0.11,DC,Unknown,two,ERP,,,,\r\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var preview = await service.PreviewAsync(source, ImportFileType.Csv, actor.Id, default);
        db.ServerAssets.Add(new ServerAsset
        {
            SubnetId = subnet.Id,
            BusinessIp = "10.0.0.11",
            BusinessIpNumber = 167772171,
            Location = "DC",
            ComputerName = "conflict",
            SystemName = "Other",
        });
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CommitAsync(preview.Id, actor.Id, default));

        Assert.False(await db.ServerAssets.AsNoTracking().AnyAsync(x => x.BusinessIp == "10.0.0.10"));
        Assert.Empty(await db.ImportJobs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AuditLogs.AsNoTracking().ToListAsync());
    }

    private sealed class StubCipher : ISecretCipher
    {
        public Task<SecretEnvelope> EncryptAsync(
            Guid secretId,
            string plaintext,
            CancellationToken ct) =>
            Task.FromResult(new SecretEnvelope([1], new byte[12], new byte[16], 1));

        public Task<string> DecryptAsync(
            Guid secretId,
            SecretEnvelope envelope,
            CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
