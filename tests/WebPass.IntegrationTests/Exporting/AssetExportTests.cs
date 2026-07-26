using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Exporting;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Exporting;
using Xunit;

namespace WebPass.IntegrationTests.Exporting;

public sealed class AssetExportTests
{
    [Fact]
    public async Task Ordinary_export_requires_ExportData_and_audits_denial()
    {
        await using var db = NewDatabase();
        var deniedUser = AddUser(db, "denied");
        await db.SaveChangesAsync();
        var service = NewService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.ExportAsync(
                ExportFormat.Xlsx,
                new ServerListQuery(),
                deniedUser.Id,
                default));

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("AssetExport", audit.Action);
        Assert.Equal("Denied", audit.Result);
        Assert.Null(audit.Details);
    }

    [Fact]
    public async Task Ordinary_export_filters_active_assets_and_never_exports_secrets()
    {
        await using var db = NewDatabase();
        var exporter = AddUser(db, "exporter");
        db.UserPermissions.Add(new UserPermission
        {
            UserId = exporter.Id,
            PermissionCode = PermissionCode.ExportData,
        });
        var subnet = AddSubnet(db);
        var matching = AddAsset(
            db,
            subnet.Id,
            "10.0.0.10",
            "Primary",
            AliveStatus.Alive,
            "server-10",
            notes: "=2+2");
        AddAsset(
            db,
            subnet.Id,
            "10.0.0.11",
            "Secondary",
            AliveStatus.Fault,
            "server-11");
        AddAsset(
            db,
            subnet.Id,
            "10.0.0.12",
            "Primary",
            AliveStatus.Alive,
            "archived-12",
            archived: true);
        db.ServerSecrets.Add(new ServerSecret
        {
            ServerAssetId = matching.Id,
            Ciphertext = "server-password"u8.ToArray(),
            Nonce = new byte[12],
            AuthenticationTag = new byte[16],
            KeyVersion = 1,
            UpdatedBy = exporter.Id,
        });
        await db.SaveChangesAsync();
        var service = NewService(db);

        var file = await service.ExportAsync(
            ExportFormat.Csv,
            new ServerListQuery(
                Search: "server-10",
                SubnetId: subnet.Id,
                Status: AliveStatus.Alive),
            exporter.Id,
            default);

        var csv = Encoding.UTF8.GetString(file.Content);
        Assert.StartsWith(
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes\r\n",
            csv,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Password", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Ciphertext", csv, StringComparison.Ordinal);
        Assert.Contains("10.0.0.10", csv, StringComparison.Ordinal);
        Assert.Contains("'=2+2", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.11", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("10.0.0.12", csv, StringComparison.Ordinal);

        var audit = await db.AuditLogs.SingleAsync();
        Assert.Equal("Success", audit.Result);
        using var details = JsonDocument.Parse(audit.Details!);
        Assert.Equal(
            ["format", "rowCount", "search", "status", "subnetId"],
            details.RootElement.EnumerateObject()
                .Select(property => property.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(1, details.RootElement.GetProperty("rowCount").GetInt32());
        Assert.DoesNotContain(
            "server-password",
            audit.Details!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Ordinary_export_rejects_archived_and_pool_modes(
        bool includeArchived,
        bool poolMode)
    {
        await using var db = NewDatabase();
        var exporter = AddUser(db, "exporter");
        db.UserPermissions.Add(new UserPermission
        {
            UserId = exporter.Id,
            PermissionCode = PermissionCode.ExportData,
        });
        await db.SaveChangesAsync();
        var service = NewService(db);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExportAsync(
                ExportFormat.Csv,
                new ServerListQuery(
                    IncludeArchived: includeArchived,
                    PoolMode: poolMode),
                exporter.Id,
                default));
    }

    private static AssetExportService NewService(WebPassDbContext db) =>
        new(
            db,
            new PermissionAuthorizationHandler(db),
            new ExportDocumentWriter(),
            new AuditWriter(db));

    private static WebPassDbContext NewDatabase() =>
        new(
            new DbContextOptionsBuilder<WebPassDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options);

    private static AppUser AddUser(WebPassDbContext db, string username)
    {
        var user = new AppUser
        {
            Username = username,
            PasswordHash = "unused",
        };
        db.Users.Add(user);
        return user;
    }

    private static Subnet AddSubnet(WebPassDbContext db)
    {
        var subnet = new Subnet
        {
            Name = "Operations",
            Cidr = "10.0.0.0/24",
            NetworkAddress = "10.0.0.0",
            PrefixLength = 24,
            Location = "DC",
        };
        db.Subnets.Add(subnet);
        return subnet;
    }

    private static ServerAsset AddAsset(
        WebPassDbContext db,
        Guid subnetId,
        string businessIp,
        string location,
        AliveStatus status,
        string computerName,
        string? notes = null,
        bool archived = false)
    {
        var octets = businessIp.Split('.').Select(long.Parse).ToArray();
        var asset = new ServerAsset
        {
            SubnetId = subnetId,
            BusinessIp = businessIp,
            BusinessIpNumber =
                (octets[0] << 24)
                | (octets[1] << 16)
                | (octets[2] << 8)
                | octets[3],
            Location = location,
            AliveStatus = status,
            ComputerName = computerName,
            SystemName = "ERP",
            Notes = notes,
            IsArchived = archived,
            CreatedBy = Guid.NewGuid(),
        };
        db.ServerAssets.Add(asset);
        return asset;
    }
}
