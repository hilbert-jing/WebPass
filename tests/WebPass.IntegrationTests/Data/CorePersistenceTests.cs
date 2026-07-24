using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using Xunit;

namespace WebPass.IntegrationTests.Data;

public sealed class CorePersistenceTests
{
    [Fact]
    public async Task Duplicate_active_business_ip_is_rejected()
    {
        var databaseName = $"WebPass_Task2_{Guid.NewGuid():N}";
        var options = new DbContextOptionsBuilder<WebPassDbContext>()
            .UseSqlServer($"Server=localhost\\SQLEXPRESS;Database={databaseName};Integrated Security=True;TrustServerCertificate=True")
            .Options;

        await using var db = new WebPassDbContext(options);
        await db.Database.MigrateAsync();
        try
        {
            var subnet = new Subnet
            {
                Name = "Test subnet",
                Cidr = "10.0.0.0/24",
                NetworkAddress = "10.0.0.0",
                PrefixLength = 24,
                Location = "Test",
            };
            db.Subnets.Add(subnet);
            await db.SaveChangesAsync();

            db.ServerAssets.AddRange(NewAsset(subnet.Id, "10.0.0.1"), NewAsset(subnet.Id, "10.0.0.1"));

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }

    private static ServerAsset NewAsset(Guid subnetId, string businessIp) => new()
    {
        SubnetId = subnetId,
        BusinessIp = businessIp,
        BusinessIpNumber = 167772161,
        Location = "Test",
        ComputerName = "server",
        SystemName = "WebPass test",
    };
}
