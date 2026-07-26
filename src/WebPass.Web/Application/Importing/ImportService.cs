using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;
using WebPass.Web.Infrastructure.Importing;

namespace WebPass.Web.Application.Importing;

public sealed class ImportService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    ISecretCipher cipher,
    InMemoryImportStageStore stages,
    CsvAssetParser csv,
    XlsxAssetParser xlsx,
    AuditWriter auditWriter,
    TimeProvider? clock = null) : IImportService
{
    private const int MaximumRows = 5_000;
    private const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(15);

    public async Task<ImportPreview> PreviewAsync(
        Stream source,
        ImportFileType type,
        Guid actorId,
        CancellationToken ct)
    {
        await EnsureAllowedAsync(actorId, ct);
        await using var buffered = await BufferAsync(source, ct);
        var parser = type switch
        {
            ImportFileType.Csv => (IAssetImportParser)csv,
            ImportFileType.Xlsx => xlsx,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
        var subnets = (await db.Subnets.AsNoTracking().Where(x => x.IsEnabled).ToListAsync(ct))
            .Select(x => (Entity: x, Cidr: Ipv4Cidr.Parse(x.Cidr)))
            .ToArray();
        var existing = await db.ServerAssets.AsNoTracking()
            .Where(x => !x.IsArchived)
            .ToDictionaryAsync(x => x.BusinessIp, StringComparer.Ordinal, ct);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<ImportRowError>();
        var rows = new List<StagedImportRow>();
        var total = 0;

        await foreach (var sourceRow in parser.ParseAsync(buffered, ct))
        {
            total++;
            if (total > MaximumRows)
            {
                errors.Add(new ImportRowError(sourceRow.RowNumber, "File", "The import exceeds 5,000 rows."));
                break;
            }

            if (!TryBuildInput(sourceRow, subnets, out var input, out var error))
            {
                errors.Add(error!);
                continue;
            }
            if (!seen.Add(input!.BusinessIp))
            {
                errors.Add(new ImportRowError(sourceRow.RowNumber, "BusinessIp", "The business IP is duplicated in the import."));
                continue;
            }

            existing.TryGetValue(input.BusinessIp, out var current);
            var assetId = current?.Id ?? Guid.NewGuid();
            SecretEnvelope? envelope = null;
            if (!string.IsNullOrEmpty(sourceRow.Password))
            {
                envelope = await cipher.EncryptAsync(assetId, sourceRow.Password, ct);
            }
            var operation = current is null
                ? ImportOperation.Create
                : envelope is null && SameAsset(current, input)
                    ? ImportOperation.Skip
                    : ImportOperation.Update;
            rows.Add(new StagedImportRow(
                sourceRow.RowNumber,
                operation,
                assetId,
                current?.RowVersion.ToArray(),
                input,
                envelope));
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var preview = new ImportPreview(
            Guid.NewGuid(),
            total,
            rows.Count(x => x.Operation == ImportOperation.Create),
            rows.Count(x => x.Operation == ImportOperation.Update),
            rows.Count(x => x.Operation == ImportOperation.Skip),
            errors,
            errors.Count != 0,
            now.Add(PreviewLifetime));
        stages.Store(new StagedImport(preview, type, actorId, rows));
        return preview;
    }

    public async Task<ImportCommitResult> CommitAsync(
        Guid previewId,
        Guid actorId,
        CancellationToken ct)
    {
        await EnsureAllowedAsync(actorId, ct);
        var stage = stages.Get(previewId);
        if (stage.ActorUserId != actorId)
            throw new UnauthorizedAccessException("The import preview belongs to another user.");
        if (stage.Preview.HasBlockingErrors)
            throw new InvalidOperationException("Blocking import errors must be corrected before commit.");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(ct)
            : null;
        foreach (var row in stage.Rows)
        {
            if (row.Operation == ImportOperation.Skip) continue;
            var subnetId = await FindSubnetIdAsync(row.Input.BusinessIp, ct);
            if (row.Operation == ImportOperation.Create)
            {
                if (await db.ServerAssets.AnyAsync(
                    x => !x.IsArchived && x.BusinessIp == row.Input.BusinessIp,
                    ct))
                    throw new InvalidOperationException("An imported business IP is no longer available.");
                var asset = NewAsset(row.AssetId, subnetId, row.Input, actorId);
                db.ServerAssets.Add(asset);
            }
            else
            {
                var asset = await db.ServerAssets.SingleOrDefaultAsync(
                    x => x.Id == row.AssetId && !x.IsArchived,
                    ct)
                    ?? throw new InvalidOperationException("An imported server was changed or archived.");
                if (row.RowVersion is null || row.RowVersion.Length == 0)
                    throw new InvalidOperationException("An imported server has no concurrency token.");
                db.Entry(asset).Property(x => x.RowVersion).OriginalValue = row.RowVersion;
                ApplyAsset(asset, subnetId, row.Input, actorId);
            }
            if (row.PasswordEnvelope is not null)
                await UpsertSecretAsync(row.AssetId, row.PasswordEnvelope, actorId, ct);
        }

        var job = new ImportJob
        {
            FileType = stage.FileType.ToString(),
            Status = "Committed",
            TotalRows = stage.Preview.TotalRows,
            CreatedCount = stage.Preview.CreateCount,
            UpdatedCount = stage.Preview.UpdateCount,
            SkippedCount = stage.Preview.SkipCount,
            CreatedBy = actorId,
            CommittedAt = (clock ?? TimeProvider.System).GetUtcNow(),
        };
        db.ImportJobs.Add(job);
        await auditWriter.WriteAsync(
            new AuditEntry(
                actorId,
                "ImportCommit",
                "ImportJob",
                job.Id.ToString(),
                "Success",
                null,
                Payload: new Dictionary<string, object?>
                {
                    ["totalRows"] = job.TotalRows,
                    ["createdCount"] = job.CreatedCount,
                    ["updatedCount"] = job.UpdatedCount,
                    ["skippedCount"] = job.SkippedCount,
                }),
            ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        stages.Remove(previewId);
        return new ImportCommitResult(
            job.Id,
            job.CreatedCount,
            job.UpdatedCount,
            job.SkippedCount);
    }

    private async Task EnsureAllowedAsync(Guid actorId, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(actorId, PermissionCode.ImportData, ct))
            throw new UnauthorizedAccessException("ImportData permission is required.");
    }

    private static async Task<MemoryStream> BufferAsync(Stream source, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(chunk, ct);
            if (read == 0) break;
            total += read;
            if (total > MaximumBytes)
            {
                buffer.Dispose();
                throw new InvalidOperationException("The import file exceeds 10 MB.");
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), ct);
        }
        buffer.Position = 0;
        return buffer;
    }

    private static bool TryBuildInput(
        ImportSourceRow row,
        IReadOnlyCollection<(Subnet Entity, Ipv4Cidr Cidr)> subnets,
        out ServerAssetInput? input,
        out ImportRowError? error)
    {
        input = null;
        error = null;
        if (string.IsNullOrWhiteSpace(row.BusinessIp)
            || !StringComparer.Ordinal.Equals(row.BusinessIp, row.BusinessIp.Trim())
            || !IPAddress.TryParse(row.BusinessIp, out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || !StringComparer.Ordinal.Equals(row.BusinessIp, address.ToString()))
        {
            error = new ImportRowError(row.RowNumber, "BusinessIp", "A canonical IPv4 address is required.");
            return false;
        }
        if (!subnets.Any(x => x.Cidr.ContainsUsable(address)))
        {
            error = new ImportRowError(row.RowNumber, "BusinessIp", "The address is outside enabled subnets.");
            return false;
        }
        if (!Enum.TryParse<AliveStatus>(row.AliveStatus, true, out var status)
            || !Enum.IsDefined(status))
        {
            error = new ImportRowError(row.RowNumber, "AliveStatus", "The alive status is invalid.");
            return false;
        }

        try
        {
            input = new ServerAssetInput(
                row.BusinessIp,
                Required(row.Location, 256),
                status,
                Required(row.ComputerName, 256),
                Required(row.SystemName, 256),
                Optional(row.OperatingSystemVersion, 256),
                Optional(row.DatabaseVersion, 256),
                Optional(row.Notes, 4000));
            return true;
        }
        catch (ArgumentException)
        {
            error = new ImportRowError(row.RowNumber, "Row", "A required value is missing or exceeds its limit.");
            return false;
        }
    }

    private async Task<Guid> FindSubnetIdAsync(string businessIp, CancellationToken ct)
    {
        var address = IPAddress.Parse(businessIp);
        var subnets = await db.Subnets.Where(x => x.IsEnabled).ToListAsync(ct);
        return subnets.SingleOrDefault(x => Ipv4Cidr.Parse(x.Cidr).ContainsUsable(address))?.Id
            ?? throw new InvalidOperationException("An imported address is no longer in an enabled subnet.");
    }

    private static ServerAsset NewAsset(
        Guid id,
        Guid subnetId,
        ServerAssetInput input,
        Guid actorId)
    {
        var asset = new ServerAsset
        {
            Id = id,
            SubnetId = subnetId,
            BusinessIp = input.BusinessIp,
            BusinessIpNumber = ToNumber(IPAddress.Parse(input.BusinessIp)),
            Location = input.Location,
            AliveStatus = input.AliveStatus,
            ComputerName = input.ComputerName,
            SystemName = input.SystemName,
            OperatingSystemVersion = input.OperatingSystemVersion,
            DatabaseVersion = input.DatabaseVersion,
            Notes = input.Notes,
            CreatedBy = actorId,
        };
        if (asset.RowVersion.Length == 0) asset.RowVersion = [1];
        return asset;
    }

    private static void ApplyAsset(
        ServerAsset asset,
        Guid subnetId,
        ServerAssetInput input,
        Guid actorId)
    {
        asset.SubnetId = subnetId;
        asset.Location = input.Location;
        asset.AliveStatus = input.AliveStatus;
        asset.ComputerName = input.ComputerName;
        asset.SystemName = input.SystemName;
        asset.OperatingSystemVersion = input.OperatingSystemVersion;
        asset.DatabaseVersion = input.DatabaseVersion;
        asset.Notes = input.Notes;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = actorId;
    }

    private async Task UpsertSecretAsync(
        Guid assetId,
        SecretEnvelope envelope,
        Guid actorId,
        CancellationToken ct)
    {
        var secret = await db.ServerSecrets.SingleOrDefaultAsync(
            x => x.ServerAssetId == assetId,
            ct);
        if (secret is null)
        {
            db.ServerSecrets.Add(new ServerSecret
            {
                ServerAssetId = assetId,
                Ciphertext = envelope.Ciphertext,
                Nonce = envelope.Nonce,
                AuthenticationTag = envelope.AuthenticationTag,
                KeyVersion = envelope.KeyVersion,
                UpdatedBy = actorId,
            });
            return;
        }
        secret.Ciphertext = envelope.Ciphertext;
        secret.Nonce = envelope.Nonce;
        secret.AuthenticationTag = envelope.AuthenticationTag;
        secret.KeyVersion = envelope.KeyVersion;
        secret.UpdatedAt = DateTimeOffset.UtcNow;
        secret.UpdatedBy = actorId;
    }

    private static bool SameAsset(ServerAsset asset, ServerAssetInput input) =>
        asset.Location == input.Location
        && asset.AliveStatus == input.AliveStatus
        && asset.ComputerName == input.ComputerName
        && asset.SystemName == input.SystemName
        && asset.OperatingSystemVersion == input.OperatingSystemVersion
        && asset.DatabaseVersion == input.DatabaseVersion
        && asset.Notes == input.Notes;

    private static string Required(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException();
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException();
        return trimmed;
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > maxLength) throw new ArgumentException();
        return trimmed;
    }

    private static long ToNumber(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return ((long)bytes[0] << 24)
            | ((long)bytes[1] << 16)
            | ((long)bytes[2] << 8)
            | bytes[3];
    }
}
