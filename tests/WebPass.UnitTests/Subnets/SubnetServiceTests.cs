using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Subnets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.UnitTests.Subnets;

public sealed class SubnetServiceTests
{
    [Fact]
    public async Task Create_normalizes_cidr_and_writes_a_redacted_audit_entry()
    {
        await using var db = NewDatabase();
        var actor = await AddSubnetManagerAsync(db);
        var service = NewService(db);

        var subnet = await service.CreateAsync(new SubnetInput("Operations", "10.10.0.25/24", "HQ", "internal", true), actor.Id, default);

        Assert.Equal("10.10.0.0/24", subnet.Cidr);
        Assert.Equal("10.10.0.0", subnet.NetworkAddress);
        var audit = Assert.Single(db.AuditLogs);
        Assert.Equal("SubnetCreate", audit.Action);
        Assert.DoesNotContain("password", audit.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Create_rejects_overlapping_ranges()
    {
        await using var db = NewDatabase();
        var actor = await AddSubnetManagerAsync(db);
        var service = NewService(db);
        await service.CreateAsync(new SubnetInput("Operations", "10.10.0.0/24", "HQ", null, true), actor.Id, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(
            new SubnetInput("Contained", "10.10.0.128/25", "HQ", null, true), actor.Id, default));
    }

    [Fact]
    public async Task Delete_rejects_subnet_with_associated_asset_but_can_disable_it()
    {
        await using var db = NewDatabase();
        var actor = await AddSubnetManagerAsync(db);
        var service = NewService(db);
        var subnet = await service.CreateAsync(new SubnetInput("Operations", "10.10.0.0/24", "HQ", null, true), actor.Id, default);
        db.ServerAssets.Add(new ServerAsset
        {
            SubnetId = subnet.Id,
            BusinessIp = "10.10.0.1",
            BusinessIpNumber = 168427521,
            Location = "HQ",
            ComputerName = "server",
            SystemName = "test",
        });
        await db.SaveChangesAsync();
        subnet.RowVersion = [1];
        await db.SaveChangesAsync();

        await service.SetEnabledAsync(subnet.Id, false, [1], actor.Id, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(subnet.Id, [1], actor.Id, default));
    }

    [Fact]
    public async Task Backend_create_is_denied_to_user_without_subnet_permission()
    {
        await using var db = NewDatabase();
        var user = new AppUser { Username = "operator", PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var service = NewService(db);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(
            new SubnetInput("Operations", "10.10.0.0/24", "HQ", null, true), user.Id, default));
    }

    private static SubnetService NewService(WebPassDbContext db) => new(
        db,
        new PermissionAuthorizationHandler(db),
        new AuditWriter(db));

    private static WebPassDbContext NewDatabase() => new(
        new DbContextOptionsBuilder<WebPassDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static async Task<AppUser> AddSubnetManagerAsync(WebPassDbContext db)
    {
        var user = new AppUser { Username = "administrator", PasswordHash = "hash", IsAdministrator = true };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
}
