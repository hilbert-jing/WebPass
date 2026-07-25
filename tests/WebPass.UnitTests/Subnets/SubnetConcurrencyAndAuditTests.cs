using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Subnets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using Xunit;

namespace WebPass.UnitTests.Subnets;

public sealed class SubnetConcurrencyAndAuditTests
{
    [Fact]
    public async Task Create_rejects_an_identical_subnet_range()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        await service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default));
    }

    [Fact]
    public async Task Update_requires_a_row_version()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default);

        await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(subnet.Id, Input("10.0.0.0/24"), [], actor.Id, default));
    }

    [Fact]
    public async Task Set_enabled_requires_a_row_version()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default);

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetEnabledAsync(subnet.Id, false, [], actor.Id, default));
    }

    [Fact]
    public async Task Delete_requires_a_row_version()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default);

        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteAsync(subnet.Id, [], actor.Id, default));
    }

    [Fact]
    public async Task Update_rejects_a_stale_row_version_without_overwriting()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await PersistVersionedSubnetAsync(db);

        await Assert.ThrowsAsync<SubnetConcurrencyException>(() => service.UpdateAsync(subnet.Id, Input("10.0.0.0/24", "Changed"), [9], actor.Id, default));

        Assert.Equal("Operations", (await db.Subnets.AsNoTracking().SingleAsync(x => x.Id == subnet.Id)).Name);
    }

    [Fact]
    public async Task Set_enabled_rejects_a_stale_row_version_without_changing_state()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await PersistVersionedSubnetAsync(db);

        await Assert.ThrowsAsync<SubnetConcurrencyException>(() => service.SetEnabledAsync(subnet.Id, false, [9], actor.Id, default));

        Assert.True((await db.Subnets.AsNoTracking().SingleAsync(x => x.Id == subnet.Id)).IsEnabled);
    }

    [Fact]
    public async Task Delete_rejects_a_stale_row_version_without_removing_the_subnet()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var subnet = await PersistVersionedSubnetAsync(db);

        await Assert.ThrowsAsync<SubnetConcurrencyException>(() => service.DeleteAsync(subnet.Id, [9], actor.Id, default));

        Assert.NotNull(await db.Subnets.AsNoTracking().SingleOrDefaultAsync(x => x.Id == subnet.Id));
    }

    [Fact]
    public async Task Edit_disable_and_delete_each_write_redacted_audit_records()
    {
        await using var db = NewDatabase();
        var (service, actor) = await NewServiceAsync(db);
        var edit = await service.CreateAsync(Input("10.0.0.0/24"), actor.Id, default);
        var disable = await service.CreateAsync(Input("10.0.1.0/24"), actor.Id, default);
        var delete = await service.CreateAsync(Input("10.0.2.0/24"), actor.Id, default);
        edit.RowVersion = [1];
        disable.RowVersion = [2];
        delete.RowVersion = [3];
        await db.SaveChangesAsync();

        await service.UpdateAsync(edit.Id, Input("10.0.0.0/24", "Edited"), [1], actor.Id, default);
        await service.SetEnabledAsync(disable.Id, false, [2], actor.Id, default);
        await service.DeleteAsync(delete.Id, [3], actor.Id, default);

        var actions = await db.AuditLogs.Select(x => x.Action).ToListAsync();
        Assert.Contains("SubnetEdit", actions);
        Assert.Contains("SubnetDisable", actions);
        Assert.Contains("SubnetDelete", actions);
        Assert.All(db.AuditLogs, audit => Assert.DoesNotContain("password", audit.Details ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    private static SubnetInput Input(string cidr, string name = "Operations") => new(name, cidr, "HQ", null, true);

    private static async Task<(SubnetService Service, AppUser Actor)> NewServiceAsync(WebPassDbContext db)
    {
        var actor = new AppUser { Username = Guid.NewGuid().ToString("N"), PasswordHash = "hash", IsAdministrator = true };
        db.Users.Add(actor);
        await db.SaveChangesAsync();
        return (new SubnetService(db, new PermissionAuthorizationHandler(db), new AuditWriter(db)), actor);
    }

    private static async Task<Subnet> PersistVersionedSubnetAsync(WebPassDbContext db)
    {
        var subnet = new Subnet { Name = "Operations", Cidr = "10.0.0.0/24", NetworkAddress = "10.0.0.0", PrefixLength = 24, Location = "HQ", RowVersion = [1] };
        db.Subnets.Add(subnet);
        await db.SaveChangesAsync();
        db.Entry(subnet).State = EntityState.Detached;
        return subnet;
    }

    private static WebPassDbContext NewDatabase() => new(new DbContextOptionsBuilder<WebPassDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
}
