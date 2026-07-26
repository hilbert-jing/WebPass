using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Exporting;

namespace WebPass.Web.Application.Exporting;

public sealed class AdministratorPasswordExportService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    IReauthenticationGrantStore grants,
    IAuthenticationSessionFingerprint sessionFingerprint,
    ISecretCipher cipher,
    ExportDocumentWriter writer,
    AuditWriter auditWriter)
{
    public async Task<ExportFile> ExportAsync(
        ServerListQuery query,
        Guid administratorId,
        CancellationToken ct)
    {
        if (!await permissions.IsAdministratorAsync(administratorId, ct))
        {
            await WriteAuditAsync(
                administratorId,
                "Denied",
                null,
                ct);
            throw new UnauthorizedAccessException(
                "Administrator permission is required.");
        }

        var administrator = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(
                user => user.Id == administratorId && user.IsEnabled,
                ct);
        if (administrator is null
            || !await grants.HasValidGrantAsync(
                administratorId,
                sessionFingerprint.GetCurrent(),
                administrator.RowVersion,
                ct))
        {
            await WriteAuditAsync(
                administratorId,
                "Denied",
                null,
                ct);
            throw new UnauthorizedAccessException(
                "Current-password verification is required.");
        }

        try
        {
            var assets = await AssetExportQuery.Build(db, query)
                .Select(asset => new
                {
                    asset.Id,
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
            var assetIds = assets.Select(asset => asset.Id).ToArray();
            var secrets = await db.ServerSecrets
                .AsNoTracking()
                .Where(secret => assetIds.Contains(secret.ServerAssetId))
                .ToDictionaryAsync(
                    secret => secret.ServerAssetId,
                    ct);
            var rows = new List<PasswordExportRow>(assets.Count);
            foreach (var asset in assets)
            {
                string? password = null;
                if (secrets.TryGetValue(asset.Id, out var secret))
                {
                    password = await cipher.DecryptAsync(
                        asset.Id,
                        new SecretEnvelope(
                            secret.Ciphertext,
                            secret.Nonce,
                            secret.AuthenticationTag,
                            secret.KeyVersion),
                        ct);
                }

                rows.Add(new PasswordExportRow(
                    new ExportRow(
                        asset.BusinessIp,
                        asset.Location,
                        asset.AliveStatus.ToString(),
                        asset.ComputerName,
                        asset.SystemName,
                        asset.OperatingSystemVersion,
                        asset.DatabaseVersion,
                        asset.Notes),
                    password));
            }

            var file = writer.WritePasswords(rows);
            await WriteAuditAsync(
                administratorId,
                "Success",
                Payload(query, rows.Count),
                ct);
            return file;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            await WriteAuditAsync(
                administratorId,
                "Failure",
                null,
                ct);
            throw;
        }
    }

    private Task WriteAuditAsync(
        Guid administratorId,
        string result,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken ct) =>
        auditWriter.WriteAsync(
            new AuditEntry(
                administratorId,
                "AdministratorPasswordExport",
                "ServerAsset",
                null,
                result,
                null,
                Payload: payload),
            ct);

    private static IReadOnlyDictionary<string, object?> Payload(
        ServerListQuery query,
        int rowCount) =>
        new Dictionary<string, object?>
        {
            ["format"] = ExportFormat.Xlsx.ToString(),
            ["rowCount"] = rowCount,
            ["search"] = string.IsNullOrWhiteSpace(query.Search)
                ? null
                : query.Search.Trim(),
            ["status"] = query.Status?.ToString(),
            ["subnetId"] = query.SubnetId,
        };
}
