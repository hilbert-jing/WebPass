using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Application.Exporting;

public static class AssetExportQuery
{
    public static IQueryable<ServerAsset> Build(
        WebPassDbContext db,
        ServerListQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.IncludeArchived)
        {
            throw new ArgumentException(
                "Archived assets cannot be exported.",
                nameof(query));
        }
        if (query.PoolMode)
        {
            throw new ArgumentException(
                "Address-pool rows cannot be exported.",
                nameof(query));
        }
        if (query.Status is { } status && !Enum.IsDefined(status))
        {
            throw new ArgumentException(
                "Alive status is invalid.",
                nameof(query));
        }

        var assets = db.ServerAssets
            .AsNoTracking()
            .Where(asset => !asset.IsArchived);
        if (query.SubnetId is { } subnetId)
        {
            assets = assets.Where(asset => asset.SubnetId == subnetId);
        }
        if (query.Status is { } aliveStatus)
        {
            assets = assets.Where(asset => asset.AliveStatus == aliveStatus);
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            assets = assets.Where(asset =>
                asset.BusinessIp.Contains(search)
                || asset.Location.Contains(search)
                || asset.ComputerName.Contains(search)
                || asset.SystemName.Contains(search));
        }

        return assets.OrderBy(asset => asset.BusinessIpNumber);
    }
}
