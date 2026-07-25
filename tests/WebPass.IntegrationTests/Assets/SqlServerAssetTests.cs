using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.IntegrationTests.Assets;

public sealed class SqlServerAssetTests
{
    [Fact]
    public async Task Sql_filtered_active_ip_unique_index_allows_reuse_after_archive_and_persists_audit()
    {
        await using var db = await NewSqlDatabaseAsync();
        try
        {
            var (service, actor) = await NewServiceAsync(db);
            await AddSubnetAsync(db);
            var first = await service.CreateAsync(Input("10.0.0.9", "First"), actor.Id, default);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Input("10.0.0.9", "Duplicate"), actor.Id, default));
            await service.ArchiveAsync(first.Id, first.RowVersion, actor.Id, default);
            var replacement = await service.CreateAsync(Input("10.0.0.9", "Replacement"), actor.Id, default);

            Assert.NotEqual(first.Id, replacement.Id);
            Assert.Equal(3, await db.AuditLogs.CountAsync());
            Assert.True(await db.ServerAssets.AnyAsync(x => x.Id == first.Id && x.IsArchived));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Sql_generated_rowversion_rejects_stale_update_without_overwrite()
    {
        await using var db = await NewSqlDatabaseAsync();
        try
        {
            var (service, actor) = await NewServiceAsync(db);
            await AddSubnetAsync(db);
            var asset = await service.CreateAsync(Input("10.0.0.9", "Original"), actor.Id, default);
            var stale = asset.RowVersion.ToArray();
            Assert.NotEmpty(stale);

            db.ChangeTracker.Clear();
            await service.UpdateAsync(asset.Id, Input("10.0.0.9", "Current"), stale, actor.Id, default);
            await Assert.ThrowsAsync<ServerAssetConcurrencyException>(() =>
                service.UpdateAsync(asset.Id, Input("10.0.0.9", "Stale"), stale, actor.Id, default));

            db.ChangeTracker.Clear();
            Assert.Equal("Current", (await db.ServerAssets.SingleAsync(x => x.Id == asset.Id)).Location);
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Sql_direct_context_enforces_filtered_unique_index_and_allows_reuse_after_archive()
    {
        await using var db = await NewSqlDatabaseAsync();
        try
        {
            var (service, actor) = await NewServiceAsync(db);
            await AddSubnetAsync(db);
            var first = await service.CreateAsync(Input("10.0.0.9", "First"), actor.Id, default);
            var subnet = await db.Subnets.SingleAsync();

            db.ServerAssets.Add(DirectAsset(subnet.Id, "10.0.0.9", "Duplicate"));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

            db.ChangeTracker.Clear();
            var archived = await db.ServerAssets.SingleAsync(x => x.Id == first.Id);
            archived.IsArchived = true;
            archived.ArchivedAt = DateTimeOffset.UtcNow;
            archived.ArchivedBy = actor.Id;
            await db.SaveChangesAsync();

            db.ServerAssets.Add(DirectAsset(subnet.Id, "10.0.0.9", "Replacement"));
            await db.SaveChangesAsync();
            Assert.Equal(2, await db.ServerAssets.CountAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Sql_audit_failure_rolls_back_the_asset_command_transaction()
    {
        await using var db = await NewSqlDatabaseAsync();
        try
        {
            var (service, actor) = await NewServiceAsync(db);
            await AddSubnetAsync(db);
            await db.Database.ExecuteSqlRawAsync(
                "CREATE TRIGGER [TR_AuditLogs_Reject] ON [AuditLogs] INSTEAD OF INSERT AS BEGIN THROW 50000, 'Audit rejected', 1; END");

            await Assert.ThrowsAsync<DbUpdateException>(() => service.CreateAsync(Input("10.0.0.9", "Will Roll Back"), actor.Id, default));

            db.ChangeTracker.Clear();
            Assert.Empty(await db.ServerAssets.ToListAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static async Task<WebPassDbContext> NewSqlDatabaseAsync()
    {
        var name = "WebPassTask4_" + Guid.NewGuid().ToString("N");
        var connection = $"Server=localhost\\SQLEXPRESS;Database={name};Integrated Security=True;TrustServerCertificate=True";
        var db = new WebPassDbContext(new DbContextOptionsBuilder<WebPassDbContext>().UseSqlServer(connection).Options);
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    private static async Task<(ServerAssetService Service, AppUser Actor)> NewServiceAsync(WebPassDbContext db)
    {
        var actor = new AppUser { Username = Guid.NewGuid().ToString("N"), PasswordHash = "hash", IsAdministrator = true };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        return (new ServerAssetService(db, new PermissionAuthorizationHandler(db), new AuditWriter(db)), actor);
    }

    private static async Task AddSubnetAsync(WebPassDbContext db)
    {
        db.Subnets.Add(new Subnet { Name = "Operations", Cidr = "10.0.0.0/24", NetworkAddress = "10.0.0.0", PrefixLength = 24, Location = "HQ" });
        await db.SaveChangesAsync();
    }

    private static ServerAssetInput Input(string ip, string location) =>
        new(ip, location, WebPass.Web.Domain.Enums.AliveStatus.Unknown, "server", "System", null, null, null);
    private static ServerAsset DirectAsset(Guid subnetId, string ip, string location) => new()
    {
        SubnetId = subnetId,
        BusinessIp = ip,
        BusinessIpNumber = 167772169,
        Location = location,
        ComputerName = "server",
        SystemName = "System",
    };
}
