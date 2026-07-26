using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Assets;

public sealed class ServerAssetService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter,
    ISecretCipher? secretCipher = null)
{
    public async Task<ServerAsset> CreateAsync(ServerAssetInput input, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.AssetCreate, ct);
        var (address, canonicalIp, number) = ParseAddress(input.BusinessIp);
        var subnet = await FindEnabledContainingSubnetAsync(address, ct);
        ValidateAliveStatus(input.AliveStatus);
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
            OperatingSystemVersion = Optional(input.OperatingSystemVersion, nameof(input.OperatingSystemVersion), 256),
            DatabaseVersion = Optional(input.DatabaseVersion, nameof(input.DatabaseVersion), 256),
            Notes = Optional(input.Notes, nameof(input.Notes), 4000),
            CreatedBy = actorUserId,
        };
        // SQL Server populates rowversion. This gives the in-memory provider a usable concurrency token too.
        if (asset.RowVersion.Length == 0) asset.RowVersion = [1];
        var secret = await EncryptSecretAsync(asset.Id, input.Password, actorUserId, ct);

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        db.ServerAssets.Add(asset);
        if (secret is not null) db.ServerSecrets.Add(secret);
        await db.SaveChangesAsync(ct);
        await WriteAuditAsync("AssetCreate", asset, actorUserId, ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return asset;
    }

    public async Task<ServerAsset> UpdateAsync(Guid assetId, ServerAssetInput input, byte[] rowVersion, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.AssetEdit, ct);
        var asset = await FindActiveAssetAsync(assetId, ct);
        ValidateAliveStatus(input.AliveStatus);
        SetOriginalRowVersion(asset, rowVersion);
        var (address, canonicalIp, number) = ParseAddress(input.BusinessIp);
        var subnet = await FindEnabledContainingSubnetAsync(address, ct);
        await EnsureActiveIpAvailableAsync(canonicalIp, assetId, ct);
        var secret = await EncryptSecretAsync(asset.Id, input.Password, actorUserId, ct);

        asset.SubnetId = subnet.Id;
        asset.BusinessIp = canonicalIp;
        asset.BusinessIpNumber = number;
        asset.Location = Required(input.Location, nameof(input.Location), 256);
        asset.AliveStatus = input.AliveStatus;
        asset.ComputerName = Required(input.ComputerName, nameof(input.ComputerName), 256);
        asset.SystemName = Required(input.SystemName, nameof(input.SystemName), 256);
        asset.OperatingSystemVersion = Optional(input.OperatingSystemVersion, nameof(input.OperatingSystemVersion), 256);
        asset.DatabaseVersion = Optional(input.DatabaseVersion, nameof(input.DatabaseVersion), 256);
        asset.Notes = Optional(input.Notes, nameof(input.Notes), 4000);
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = actorUserId;

        if (secret is not null) await UpsertSecretAsync(secret, ct);
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
        var selected = (await subnets.ToListAsync(ct)).Select(x => (Subnet: x, Cidr: Ipv4Cidr.Parse(x.Cidr)))
            .OrderBy(x => ToNumber(x.Cidr.NetworkAddress)).ToList();

        if (query.Status is null && string.IsNullOrWhiteSpace(query.Search))
        {
            var total = selected.Sum(x => x.Cidr.GetUsableAddressCount());
            var items = new List<ServerListItem>(query.Take);
            long skip = query.Skip;
            foreach (var entry in selected)
            {
                var usable = entry.Cidr.GetUsableAddressCount();
                if (skip >= usable) { skip -= usable; continue; }
                var take = (int)Math.Min((long)query.Take - items.Count, usable - skip);
                if (take <= 0) break;
                items.AddRange(await BuildPoolItemsAsync(entry.Subnet, entry.Cidr.EnumerateUsableAddresses(skip, take).ToArray(), query, ct));
                skip = 0;
                if (items.Count >= query.Take) break;
            }
            return new ServerListPage(items, total, true, query.Skip, query.Take);
        }

        return await ListFilteredPoolAsync(query, selected, ct);
    }

    private async Task<ServerListPage> ListFilteredPoolAsync(
        ServerListQuery query,
        IReadOnlyCollection<(Subnet Subnet, Ipv4Cidr Cidr)> selected,
        CancellationToken ct)
    {
        var search = query.Search?.Trim();
        var hasIpPrefix = TryGetIpPrefixRange(search, out _, out _);
        if (TryParseCanonicalIpv4(search, out var exactAddress))
        {
            var entry = selected.SingleOrDefault(x => x.Cidr.ContainsUsable(exactAddress));
            if (entry.Subnet is null) return new ServerListPage([], 0, true, query.Skip, query.Take);
            var exactItems = await BuildPoolItemsAsync(entry.Subnet, [exactAddress], query, ct);
            return new ServerListPage(exactItems.Skip(query.Skip).Take(query.Take).ToList(), exactItems.Count, true, query.Skip, query.Take);
        }

        var canMatchFreeRows = (query.Status is null || query.Status == Domain.Enums.AliveStatus.Unknown) &&
            (search is null || hasIpPrefix);
        if (canMatchFreeRows)
            return await ListGeneratedFilteredPoolAsync(query, selected, search, ct);

        return await ListRegisteredFilteredPoolAsync(query, selected, search, ct);
    }

    private async Task<ServerListPage> ListRegisteredFilteredPoolAsync(
        ServerListQuery query,
        IReadOnlyCollection<(Subnet Subnet, Ipv4Cidr Cidr)> selected,
        string? search,
        CancellationToken ct)
    {
        var assets = PrimaryAssetsQuery(selected, query.IncludeArchived);
        if (query.Status is { } status) assets = assets.Where(x => x.AliveStatus == status);
        if (search is not null)
            assets = assets.Where(x => x.BusinessIp.Contains(search) || x.Location.Contains(search) ||
                x.ComputerName.Contains(search) || x.SystemName.Contains(search));

        var total = await assets.LongCountAsync(ct);
        var items = await assets.OrderBy(x => x.BusinessIpNumber).Skip(query.Skip).Take(query.Take)
            .Select(x => new ServerListItem(x.Id, x.SubnetId, x.BusinessIp, true, x.IsArchived,
                x.Location, x.AliveStatus, x.ComputerName, x.SystemName, x.OperatingSystemVersion,
                x.DatabaseVersion, x.Notes, x.RowVersion))
            .ToListAsync(ct);
        return new ServerListPage(items, total, true, query.Skip, query.Take);
    }

    private async Task<ServerListPage> ListGeneratedFilteredPoolAsync(
        ServerListQuery query,
        IReadOnlyCollection<(Subnet Subnet, Ipv4Cidr Cidr)> selected,
        string? search,
        CancellationToken ct)
    {
        var hasRange = TryGetIpPrefixRange(search, out var rangeStart, out var rangeEnd);
        var primary = PrimaryAssetsQuery(selected, query.IncludeArchived);
        long total = 0;
        var items = new List<ServerListItem>(query.Take);
        foreach (var entry in selected)
        {
            var network = ToNumber(entry.Cidr.NetworkAddress) + 1;
            var broadcast = ToNumber(entry.Cidr.BroadcastAddress) - 1;
            var start = hasRange ? Math.Max(network, rangeStart) : network;
            var end = hasRange ? Math.Min(broadcast, rangeEnd) : broadcast;
            if (start > end) continue;

            var blockedCount = query.Status is { } status
                ? await CountBlockedAsync(primary, entry.Subnet.Id, start, end, status, ct)
                : 0;
            var entryTotal = end - start + 1 - blockedCount;
            if (total + entryTotal <= query.Skip)
            {
                total += entryTotal;
                continue;
            }

            var localSkip = Math.Max(0L, query.Skip - total);
            var take = (int)Math.Min((long)query.Take - items.Count, entryTotal - localSkip);
            if (take > 0)
            {
                var addresses = query.Status is { } requiredStatus
                    ? await GetUnblockedPageAddressesAsync(primary, entry.Subnet.Id, entry.Cidr, network, start, end,
                        localSkip + 1, take, requiredStatus, ct)
                    : entry.Cidr.EnumerateUsableAddresses(start - network + localSkip, take).ToArray();
                items.AddRange(await BuildPoolItemsAsync(entry.Subnet, addresses, query, ct));
            }
            total += entryTotal;
        }
        return new ServerListPage(items, total, true, query.Skip, query.Take);
    }

    private static Task<long> CountBlockedAsync(
        IQueryable<ServerAsset> primary,
        Guid subnetId,
        long start,
        long end,
        Domain.Enums.AliveStatus requiredStatus,
        CancellationToken ct) =>
        primary.Where(x => x.SubnetId == subnetId && x.BusinessIpNumber >= start && x.BusinessIpNumber <= end &&
            x.AliveStatus != requiredStatus).LongCountAsync(ct);

    private async Task<long> FindUnblockedNumberAsync(
        IQueryable<ServerAsset> primary,
        Guid subnetId,
        long start,
        long end,
        long rank,
        Domain.Enums.AliveStatus requiredStatus,
        CancellationToken ct)
    {
        var lower = start;
        var upper = end;
        while (lower < upper)
        {
            var middle = lower + (upper - lower) / 2;
            var blocked = await CountBlockedAsync(primary, subnetId, start, middle, requiredStatus, ct);
            var available = middle - start + 1 - blocked;
            if (available >= rank) upper = middle;
            else lower = middle + 1;
        }
        return lower;
    }

    private async Task<IReadOnlyCollection<IPAddress>> GetUnblockedPageAddressesAsync(
        IQueryable<ServerAsset> primary,
        Guid subnetId,
        Ipv4Cidr cidr,
        long network,
        long start,
        long end,
        long rank,
        int take,
        Domain.Enums.AliveStatus requiredStatus,
        CancellationToken ct)
    {
        const int chunkSize = 2048;
        var cursor = await FindUnblockedNumberAsync(primary, subnetId, start, end, rank, requiredStatus, ct);
        var addresses = new List<IPAddress>(take);
        while (cursor <= end && addresses.Count < take)
        {
            var chunkEnd = Math.Min(end, cursor + chunkSize - 1);
            var blocked = await primary.Where(x => x.SubnetId == subnetId && x.BusinessIpNumber >= cursor &&
                    x.BusinessIpNumber <= chunkEnd && x.AliveStatus != requiredStatus)
                .Select(x => x.BusinessIpNumber).ToListAsync(ct);
            var blockedNumbers = blocked.ToHashSet();
            foreach (var address in cidr.EnumerateUsableAddresses(cursor - network, (int)(chunkEnd - cursor + 1)))
            {
                if (!blockedNumbers.Contains(ToNumber(address))) addresses.Add(address);
                if (addresses.Count == take) break;
            }
            cursor = chunkEnd + 1;
        }
        return addresses;
    }

    private IQueryable<ServerAsset> PrimaryAssetsQuery(
        IReadOnlyCollection<(Subnet Subnet, Ipv4Cidr Cidr)> selected,
        bool includeArchived)
    {
        var subnetIds = selected.Select(x => x.Subnet.Id).ToArray();
        var assets = db.ServerAssets.AsNoTracking().Where(x => subnetIds.Contains(x.SubnetId));
        if (!includeArchived) return assets.Where(x => !x.IsArchived);

        return assets.Where(x => !x.IsArchived ||
            (!db.ServerAssets.Any(other => other.SubnetId == x.SubnetId && other.BusinessIp == x.BusinessIp && !other.IsArchived) &&
             !db.ServerAssets.Any(other => other.SubnetId == x.SubnetId && other.BusinessIp == x.BusinessIp &&
                 other.IsArchived && other.ArchivedAt > x.ArchivedAt)));
    }

    private static bool TryGetIpPrefixRange(string? value, out long start, out long end)
    {
        start = end = 0;
        if (string.IsNullOrWhiteSpace(value) || value.Any(x => !(char.IsDigit(x) || x == '.'))) return false;
        var parts = value.Split('.', StringSplitOptions.None).Where(x => x.Length > 0).ToArray();
        if (parts.Length is < 1 or > 4 || parts.Any(x => !byte.TryParse(x, out _))) return false;
        var bytes = new byte[4];
        for (var index = 0; index < parts.Length; index++) bytes[index] = byte.Parse(parts[index]);
        start = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        for (var index = parts.Length; index < 4; index++) bytes[index] = byte.MaxValue;
        end = ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3];
        return true;
    }

    private static bool TryParseCanonicalIpv4(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value) || !IPAddress.TryParse(value, out var parsed) ||
            parsed.AddressFamily != AddressFamily.InterNetwork || !StringComparer.Ordinal.Equals(value, parsed.ToString()))
            return false;
        address = parsed;
        return true;
    }

    private async Task<IReadOnlyList<ServerListItem>> BuildPoolItemsAsync(Subnet subnet, IReadOnlyCollection<IPAddress> addresses, ServerListQuery query, CancellationToken ct)
    {
        var ips = addresses.Select(x => x.ToString()).ToArray();
        var assets = await db.ServerAssets.AsNoTracking().Where(x => x.SubnetId == subnet.Id && ips.Contains(x.BusinessIp)).ToListAsync(ct);
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var items = new List<ServerListItem>(addresses.Count);
        foreach (var address in addresses)
        {
            var ip = address.ToString();
            var records = assets.Where(x => x.BusinessIp == ip);
            var asset = records.Where(x => !x.IsArchived).OrderByDescending(x => x.UpdatedAt).FirstOrDefault()
                ?? (query.IncludeArchived ? records.Where(x => x.IsArchived).OrderByDescending(x => x.ArchivedAt).FirstOrDefault() : null);
            if (asset is not null)
            {
                if ((query.Status is null || asset.AliveStatus == query.Status) &&
                    (search is null || asset.BusinessIp.Contains(search, StringComparison.OrdinalIgnoreCase) || asset.Location.Contains(search, StringComparison.OrdinalIgnoreCase) || asset.ComputerName.Contains(search, StringComparison.OrdinalIgnoreCase) || asset.SystemName.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    items.Add(ToItem(asset));
                continue;
            }
            if ((query.Status is null || query.Status == Domain.Enums.AliveStatus.Unknown) && (search is null || ip.Contains(search, StringComparison.OrdinalIgnoreCase)))
                items.Add(new ServerListItem(null, subnet.Id, ip, false, false, null, Domain.Enums.AliveStatus.Unknown, null, null, null, null, null, null));
        }
        return items;
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

    private async Task<ServerSecret?> EncryptSecretAsync(
        Guid assetId,
        string? password,
        Guid actorUserId,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password)) return null;
        if (secretCipher is null)
            throw new InvalidOperationException("Secret encryption is unavailable.");

        var envelope = await secretCipher.EncryptAsync(assetId, password, ct);
        return new ServerSecret
        {
            ServerAssetId = assetId,
            Ciphertext = envelope.Ciphertext,
            Nonce = envelope.Nonce,
            AuthenticationTag = envelope.AuthenticationTag,
            KeyVersion = envelope.KeyVersion,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = actorUserId,
        };
    }

    private async Task UpsertSecretAsync(ServerSecret replacement, CancellationToken ct)
    {
        var existing = await db.ServerSecrets.SingleOrDefaultAsync(
            x => x.ServerAssetId == replacement.ServerAssetId,
            ct);
        if (existing is null)
        {
            db.ServerSecrets.Add(replacement);
            return;
        }

        existing.Ciphertext = replacement.Ciphertext;
        existing.Nonce = replacement.Nonce;
        existing.AuthenticationTag = replacement.AuthenticationTag;
        existing.KeyVersion = replacement.KeyVersion;
        existing.UpdatedAt = replacement.UpdatedAt;
        existing.UpdatedBy = replacement.UpdatedBy;
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
        asset.Location, asset.AliveStatus, asset.ComputerName, asset.SystemName, asset.OperatingSystemVersion, asset.DatabaseVersion, asset.Notes, asset.RowVersion);

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

    private static string? Optional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException($"Value exceeds {maxLength} characters.", name);
        return trimmed;
    }

    private static void ValidateAliveStatus(Domain.Enums.AliveStatus value)
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }


    private static void ValidateQuery(ServerListQuery query)
    {
        if (query.Skip < 0) throw new ArgumentOutOfRangeException(nameof(query.Skip));
        if (query.Take is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query.Take));
    }
}
