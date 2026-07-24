using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Subnets;

public sealed record SubnetInput(string Name, string Cidr, string Location, string? Notes, bool IsEnabled);

public sealed record SubnetPreview(string NetworkAddress, string BroadcastAddress, long UsableAddressCount);

public sealed class SubnetConcurrencyException : InvalidOperationException
{
    public SubnetConcurrencyException() : base("The subnet was changed by another user. Reload and try again.")
    {
    }
}

public sealed class SubnetService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter)
{
    public async Task<SubnetPreview> PreviewAsync(string cidr, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        var parsed = Ipv4Cidr.Parse(cidr);
        return new SubnetPreview(parsed.NetworkAddress.ToString(), parsed.BroadcastAddress.ToString(), parsed.GetUsableAddressCount());
    }

    public async Task<IReadOnlyList<Subnet>> ListAsync(Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        return await db.Subnets.AsNoTracking().OrderBy(x => x.NetworkAddress).ToListAsync(ct);
    }

    public async Task<Subnet> CreateAsync(SubnetInput input, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        var cidr = Ipv4Cidr.Parse(input.Cidr);
        await EnsureDoesNotOverlapAsync(cidr, null, ct);
        var subnet = new Subnet
        {
            Name = Required(input.Name, nameof(input.Name)),
            Cidr = cidr.ToString(),
            NetworkAddress = cidr.NetworkAddress.ToString(),
            PrefixLength = cidr.PrefixLength,
            Location = Required(input.Location, nameof(input.Location)),
            Notes = input.Notes,
            IsEnabled = input.IsEnabled,
        };
        db.Subnets.Add(subnet);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync("SubnetCreate", subnet, actorUserId, ct);
        return subnet;
    }

    public async Task<Subnet> UpdateAsync(Guid subnetId, SubnetInput input, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        var subnet = await FindSubnetAsync(subnetId, ct);
        SetOriginalRowVersion(subnet, rowVersion);
        var cidr = Ipv4Cidr.Parse(input.Cidr);
        await EnsureDoesNotOverlapAsync(cidr, subnetId, ct);
        subnet.Name = Required(input.Name, nameof(input.Name));
        subnet.Cidr = cidr.ToString();
        subnet.NetworkAddress = cidr.NetworkAddress.ToString();
        subnet.PrefixLength = cidr.PrefixLength;
        subnet.Location = Required(input.Location, nameof(input.Location));
        subnet.Notes = input.Notes;
        subnet.IsEnabled = input.IsEnabled;
        await SaveAndAuditAsync("SubnetEdit", subnet, actorUserId, ct);
        return subnet;
    }

    public async Task SetEnabledAsync(Guid subnetId, bool isEnabled, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        var subnet = await FindSubnetAsync(subnetId, ct);
        SetOriginalRowVersion(subnet, rowVersion);
        subnet.IsEnabled = isEnabled;
        await SaveAndAuditAsync(isEnabled ? "SubnetEnable" : "SubnetDisable", subnet, actorUserId, ct);
    }

    public async Task DeleteAsync(Guid subnetId, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, ct);
        var subnet = await FindSubnetAsync(subnetId, ct);
        SetOriginalRowVersion(subnet, rowVersion);
        if (await db.ServerAssets.AnyAsync(x => x.SubnetId == subnetId, ct))
        {
            throw new InvalidOperationException("A subnet with associated assets can only be disabled.");
        }

        db.Subnets.Remove(subnet);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SubnetConcurrencyException();
        }

        await auditWriter.WriteAsync(new AuditEntry(actorUserId, "SubnetDelete", "Subnet", subnetId.ToString(), "Success", null,
            Payload: RedactedPayload(subnet)), ct);
    }

    private async Task EnsureAllowedAsync(Guid actorUserId, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(actorUserId, PermissionCode.SubnetManage, ct))
        {
            throw new UnauthorizedAccessException("SubnetManage permission is required.");
        }
    }

    private async Task EnsureDoesNotOverlapAsync(Ipv4Cidr candidate, Guid? excludedSubnetId, CancellationToken ct)
    {
        var existingCidrs = await db.Subnets.AsNoTracking()
            .Where(x => excludedSubnetId == null || x.Id != excludedSubnetId)
            .Select(x => x.Cidr)
            .ToListAsync(ct);
        if (existingCidrs.Select(Ipv4Cidr.Parse).Any(candidate.Overlaps))
        {
            throw new InvalidOperationException("The subnet overlaps an existing subnet.");
        }
    }

    private async Task<Subnet> FindSubnetAsync(Guid subnetId, CancellationToken ct) =>
        await db.Subnets.SingleOrDefaultAsync(x => x.Id == subnetId, ct) ?? throw new KeyNotFoundException("Subnet not found.");

    private void SetOriginalRowVersion(Subnet subnet, byte[] rowVersion)
    {
        if (rowVersion is null || rowVersion.Length == 0)
        {
            return;
        }

        db.Entry(subnet).Property(x => x.RowVersion).OriginalValue = rowVersion;
    }

    private async Task SaveAndAuditAsync(string action, Subnet subnet, Guid actorUserId, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SubnetConcurrencyException();
        }

        await WriteAuditAsync(action, subnet, actorUserId, ct);
    }

    private Task WriteAuditAsync(string action, Subnet subnet, Guid actorUserId, CancellationToken ct) => auditWriter.WriteAsync(
        new AuditEntry(actorUserId, action, "Subnet", subnet.Id.ToString(), "Success", null, Payload: RedactedPayload(subnet)), ct);

    private static IReadOnlyDictionary<string, object?> RedactedPayload(Subnet subnet) => new Dictionary<string, object?>
    {
        ["name"] = subnet.Name,
        ["cidr"] = subnet.Cidr,
        ["location"] = subnet.Location,
        ["isEnabled"] = subnet.IsEnabled,
    };

    private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A value is required.", name)
        : value.Trim();
}
