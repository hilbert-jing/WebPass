using WebPass.Web.Domain.Enums;

namespace WebPass.Web.Application.Assets;

public sealed record ServerListQuery(
    string? Search = null,
    Guid? SubnetId = null,
    AliveStatus? Status = null,
    bool IncludeArchived = false,
    bool PoolMode = false,
    int Skip = 0,
    int Take = 50);

public sealed record ServerListItem(
    Guid? AssetId,
    Guid SubnetId,
    string BusinessIp,
    bool IsRegistered,
    bool IsArchived,
    string? Location,
    AliveStatus? AliveStatus,
    string? ComputerName,
    string? SystemName,
    string? OperatingSystemVersion,
    string? DatabaseVersion,
    string? Notes,
    byte[]? RowVersion);

public sealed record ServerListPage(
    IReadOnlyList<ServerListItem> Items,
    long TotalCount,
    bool PoolMode,
    int Skip,
    int Take);
