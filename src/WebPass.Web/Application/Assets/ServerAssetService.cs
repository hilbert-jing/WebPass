using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Assets;

public sealed class ServerAssetService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter)
{
    public async Task<ServerAsset> CreateAsync(ServerAssetInput input, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.AssetCreate, ct);
        var (address, canonicalIp, number) = ParseAddress(input.BusinessIp);
        var subnet = await FindEnabledContainingSubnetAsync(address, ct);
        await EnsureActiveIpAvailableAsync(canonicalIp, null, ct);

        var asset = new ServerAsset
        {
            SubnetId = subnet.Id,
            BusinessIp = canonicalIp,
            BusinessIpNumber = number,
            Location = Required(input.Location, nameof(input.Location), 256),
            AliveStatus = input.AliveStatus,
            ComputerName = Required(input.ComputerName, nameof(input.ComputerName), 256),
            SystemName = Required(input.SystemName, nameof(input.SystemName), 256),
            OperatingSystemVersion = Optional(input.OperatingSystemVersion),
            DatabaseVersion = Optional(input.DatabaseVersion),
            Notes = Optional(input.Notes),
            CreatedBy = actorUserId,
        };
        // SQL Server populates rowversion. This gives the in-memory provider a usable concurrency token too.
        if (asset.RowVersion.Length == 0) asset.RowVersion = [1];

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        db.ServerAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync("AssetCreate", asset, actorUserId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return asset;
    }

    public async Task<ServerAsset> UpdateAsync(Guid assetId, ServerAssetInput input, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.AssetEdit, ct);
        var asset = await FindActiveAssetAsync(assetId, ct);
        SetOriginalRowVersion(asset, rowVersion);
        var (address, canonicalIp, number) = ParseAddress(input.BusinessIp);
        var subnet = await FindEnabledContainingSubnetAsync(address, ct);
        await EnsureActiveIpAvailableAsync(canonicalIp, assetId, ct);

        asset.SubnetId = subnet.Id;
        asset.BusinessIp = canonicalIp;
        asset.BusinessIpNumber = number;
        asset.Location = Required(input.Location, nameof(input.Location), 256);
        asset.AliveStatus = input.AliveStatus;
        asset.ComputerName = Required(input.ComputerName, nameof(input.ComputerName), 256);
        asset.SystemName = Required(input.SystemName, nameof(input.SystemName), 256);
        asset.OperatingSystemVersion = Optional(input.OperatingSystemVersion);
        asset.DatabaseVersion = Optional(input.DatabaseVersion);
        asset.Notes = Optional(input.Notes);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = actorUserId;

        await SaveAndAuditAsync("AssetEdit", asset, actorUserId, ct);
        return asset;
    }

    public async Task ArchiveAsync(Guid assetId, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.AssetArchive, ct);
        var asset = await FindActiveAssetAsync(assetId, ct);
        SetOriginalRowVersion(asset, rowVersion);
        asset.IsArchived = true;
        asset.ArchivedAt = DateTimeOffset.UtcNow;
        asset.ArchivedBy = actorUserId;
        asset.UpdatedAt = asset.ArchivedAt;
        asset.UpdatedBy = actorUserId;
        await SaveAndAuditAsync("AssetArchive", asset, actorUserId, ct);
    }

    public async Task<ServerListPage> ListAsync(ServerListQuery query, CancellationToken ct)
    {
        ValidateQuery(query);
        if (query.PoolMode) return await ListPoolAsync(query, ct);

        var assets = db.ServerAssets.AsNoTracking().AsQueryable();
        if (!query.IncludeArchived) assets = assets.Where(x => !x.IsArchived);
        if (query.SubnetId is { } subnetId) assets = assets.Where(x => x.SubnetId == subnetId);
        if (query.Status is { } status) assets = assets.Where(x => x.AliveStatus == status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            assets = assets.Where(x => x.BusinessIp.Contains(search) || x.Location.Contains(search) ||
                x.ComputerName.Contains(search) || x.SystemName.Contains(search));
        }

        var total = await assets.LongCountAsync(ct);
        var items = await assets.OrderBy(x => x.BusinessIpNumber).Skip(query.Skip).Take(query.Take)
            .Select(x => ToItem(x)).ToListAsync(ct);
        return new ServerListPage(items, total, false, query.Skip, query.Take);
    }

    private async Task<ServerListPage> ListPoolAsync(ServerListQuery query, CancellationToken ct)
    {
        var subnets = db.Subnets.AsNoTracking().Where(x => x.IsEnabled);
        if (query.SubnetId is { } subnetId) subnets = subnets.Where(x => x.Id == subnetId);
        var selectedSubnets = await subnets.OrderBy(x => x.NetworkAddress).ToListAsync(ct);

        var registeredQuery = db.ServerAssets.AsNoTracking().Where(x => !x.IsArchived && x.Subnet.IsEnabled);
        if (query.SubnetId is { } selectedId) registeredQuery = registeredQuery.Where(x => x.SubnetId == selectedId);
        if (query.Status is { } status) registeredQuery = registeredQuery.Where(x => x.AliveStatus == status);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            registeredQuery = registeredQuery.Where(x => x.BusinessIp.Contains(search) || x.Location.Contains(search) ||
                x.ComputerName.Contains(search) || x.SystemName.Contains(search));
        }
        var registered = await registeredQuery.ToDictionaryAsync(x => x.BusinessIp, ct);

        var allCandidates = new List<ServerListItem>();
        foreach (var subnet in selectedSubnets)
        {
            var cidr = Ipv4Cidr.Parse(subnet.Cidr);
            foreach (var address in cidr.EnumerateUsableAddresses(0, checked((int)cidr.GetUsableAddressCount())))
            {
                var businessIp = address.ToString();
                if (registered.TryGetValue(businessIp, out var asset))
                {
                    allCandidates.Add(ToItem(asset));
                }
                else if ((query.Status is null || query.Status == Domain.Enums.AliveStatus.Unknown) &&
                         (string.IsNullOrWhiteSpace(query.Search) || businessIp.Contains(query.Search.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    allCandidates.Add(new ServerListItem(null, subnet.Id, businessIp, false, false, null, Domain.Enums.AliveStatus.Unknown, null, null, null));
                }
            }
        }

        var ordered = allCandidates.OrderBy(x => ToNumber(IPAddress.Parse(x.BusinessIp))).ToList();
        return new ServerListPage(ordered.Skip(query.Skip).Take(query.Take).ToList(), ordered.Count, true, query.Skip, query.Take);
    }

    private async Task EnsureAllowedAsync(Guid actorUserId, string permission, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(actorUserId, permission, ct))
            throw new UnauthorizedAccessException($"{permission} permission is required.");
    }

    private async Task<ServerAsset> FindActiveAssetAsync(Guid assetId, CancellationToken ct) =>
        await db.ServerAssets.SingleOrDefaultAsync(x => x.Id == assetId && !x.IsArchived, ct)
        ?? throw new KeyNotFoundException("Server asset not found.");

    private async Task<Subnet> FindEnabledContainingSubnetAsync(IPAddress address, CancellationToken ct)
    {
        var candidates = await db.Subnets.Where(x => x.IsEnabled).ToListAsync(ct);
        return candidates.SingleOrDefault(x => Ipv4Cidr.Parse(x.Cidr).ContainsUsable(address))
            ?? throw new InvalidOperationException("The business IP must be a usable address in an enabled subnet.");
    }

    private async Task EnsureActiveIpAvailableAsync(string businessIp, Guid? exceptAssetId, CancellationToken ct)
    {
        if (await db.ServerAssets.AnyAsync(x => !x.IsArchived && x.BusinessIp == businessIp &&
            (exceptAssetId == null || x.Id != exceptAssetId), ct))
            throw new InvalidOperationException("An active server asset already uses this business IP.");
    }

    private void SetOriginalRowVersion(ServerAsset asset, byte[] rowVersion)
    {
        if (rowVersion is null || rowVersion.Length == 0)
            throw new ArgumentException("A row version is required.", nameof(rowVersion));
        db.Entry(asset).Property(x => x.RowVersion).OriginalValue = rowVersion;
    }

    private async Task SaveAndAuditAsync(string action, ServerAsset asset, Guid actorUserId, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ServerAssetConcurrencyException();
        }

        await WriteAuditAsync(action, asset, actorUserId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private Task WriteAuditAsync(string action, ServerAsset asset, Guid actorUserId, CancellationToken ct) =>
        auditWriter.WriteAsync(new AuditEntry(actorUserId, action, "ServerAsset", asset.Id.ToString(), "Success", null,
            Payload: new Dictionary<string, object?>
            {
                ["businessIp"] = asset.BusinessIp,
                ["location"] = asset.Location,
                ["aliveStatus"] = asset.AliveStatus.ToString(),
                ["isArchived"] = asset.IsArchived,
            }), ct);

    private static ServerListItem ToItem(ServerAsset asset) => new(asset.Id, asset.SubnetId, asset.BusinessIp, true, asset.IsArchived,
        asset.Location, asset.AliveStatus, asset.ComputerName, asset.SystemName, asset.RowVersion);

    private static (IPAddress Address, string CanonicalIp, long Number) ParseAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !StringComparer.Ordinal.Equals(value, value.Trim()) ||
            !IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            !StringComparer.Ordinal.Equals(value, address.ToString()))
            throw new ArgumentException("Business IP must be a canonical IPv4 address.", nameof(value));
        return (address, address.ToString(), ToNumber(address));
    }

    private static long ToNumber(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
    }

    private static string Required(string value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A value is required.", name);
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException($"Value exceeds {maxLength} characters.", name);
        return trimmed;
    }

    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateQuery(ServerListQuery query)
    {
        if (query.Skip < 0) throw new ArgumentOutOfRangeException(nameof(query.Skip));
        if (query.Take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query.Take));
    }
}
