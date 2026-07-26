using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Exporting;

namespace WebPass.Web.Application.Exporting;

public sealed class AssetExportService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    ExportDocumentWriter writer,
    AuditWriter auditWriter)
{
    public async Task<ExportFile> ExportAsync(
        ExportFormat format,
        ServerListQuery query,
        Guid actorId,
        CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(
            actorId,
            PermissionCode.ExportData,
            ct))
        {
            await WriteAuditAsync(actorId, "Denied", null, ct);
            throw new UnauthorizedAccessException(
                "ExportData permission is required.");
        }

        try
        {
            var projected = await AssetExportQuery.Build(db, query)
                .Select(asset => new
                {
                    asset.BusinessIp,
                    asset.Location,
                    asset.AliveStatus,
                    asset.ComputerName,
                    asset.SystemName,
                    asset.OperatingSystemVersion,
                    asset.DatabaseVersion,
                    asset.Notes,
                })
                .ToListAsync(ct);
            var rows = projected
                .Select(asset => new ExportRow(
                    asset.BusinessIp,
                    asset.Location,
                    asset.AliveStatus.ToString(),
                    asset.ComputerName,
                    asset.SystemName,
                    asset.OperatingSystemVersion,
                    asset.DatabaseVersion,
                    asset.Notes))
                .ToArray();
            var file = writer.WriteOrdinary(rows, format);
            await WriteAuditAsync(
                actorId,
                "Success",
                Payload(format, query, rows.Length),
                ct);
            return file;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            await WriteAuditAsync(actorId, "Failure", null, ct);
            throw;
        }
    }

    private Task WriteAuditAsync(
        Guid actorId,
        string result,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken ct) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                actorId,
                "AssetExport",
                "ServerAsset",
                null,
                result,
                null,
                Payload: payload),
            ct);

    private static IReadOnlyDictionary<string, object?> Payload(
        ExportFormat format,
        ServerListQuery query,
        int rowCount) =>
        new Dictionary<string, object?>
        {
            ["format"] = format.ToString(),
            ["rowCount"] = rowCount,
            ["search"] = string.IsNullOrWhiteSpace(query.Search)
                ? null
                : query.Search.Trim(),
            ["status"] = query.Status?.ToString(),
            ["subnetId"] = query.SubnetId,
        };
}
