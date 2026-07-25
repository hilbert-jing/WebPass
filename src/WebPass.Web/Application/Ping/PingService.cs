using System.Collections.Concurrent;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Networking;
using WebPass.Web.Configuration;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Domain.Enums;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Ping;

public sealed class PingService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    AuditWriter auditWriter,
    IPingTransport transport,
    IOptions<WebPassOptions> options)
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> ConcurrencyGates = new();
    private static readonly ConcurrentDictionary<Guid, Queue<DateTimeOffset>> UserExecutions = new();
    private static int _rateCleanupTick;

    public async Task<PingResult> ExecuteAsync(Guid assetId, Guid actorUserId, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.PingExecute, ct);
        var asset = await db.ServerAssets.Include(x => x.Subnet).SingleOrDefaultAsync(x => x.Id == assetId, ct)
            ?? throw new KeyNotFoundException("Server asset not found.");
        ValidateTarget(asset);
        ConsumeRateAllowance(actorUserId);

        var gate = ConcurrencyGates.GetOrAdd(options.Value.PingMaxConcurrency, static limit => new SemaphoreSlim(limit, limit));
        await gate.WaitAsync(ct);
        PingTransportResult response;
        try
        {
            response = await transport.SendAsync(asset.BusinessIp, options.Value.PingTimeoutMilliseconds, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            response = new PingTransportResult("InternalError", null, "TransportError");
        }
        finally
        {
            gate.Release();
        }

        var result = new PingResult
        {
            ServerAssetId = asset.Id,
            TargetIp = asset.BusinessIp,
            Outcome = NormalizeOutcome(response.Outcome),
            LatencyMilliseconds = response.LatencyMilliseconds,
            ErrorCode = SafeErrorCode(response.ErrorCode),
            ExecutedBy = actorUserId,
        };

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        db.PingResults.Add(result);
        await db.SaveChangesAsync(ct);
        await auditWriter.WriteAsync(new AuditEntry(actorUserId, "PingExecute", "ServerAsset", asset.Id.ToString(), result.Outcome, null,
            Payload: new Dictionary<string, object?>
            {
                ["targetIp"] = asset.BusinessIp,
                ["outcome"] = result.Outcome,
                ["latencyMilliseconds"] = result.LatencyMilliseconds,
                ["errorCode"] = result.ErrorCode,
            }), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return result;
    }

    public async Task MarkAliveAsync(Guid assetId, Guid actorUserId, byte[] rowVersion, CancellationToken ct)
    {
        await EnsureAllowedAsync(actorUserId, PermissionCode.StatusMarkAlive, ct);
        if (rowVersion is null || rowVersion.Length == 0)
            throw new ArgumentException("A row version is required.", nameof(rowVersion));

        var asset = await db.ServerAssets.SingleOrDefaultAsync(x => x.Id == assetId && !x.IsArchived, ct)
            ?? throw new KeyNotFoundException("Server asset not found.");
        db.Entry(asset).Property(x => x.RowVersion).OriginalValue = rowVersion;
        asset.AliveStatus = AliveStatus.Alive;
        asset.UpdatedAt = DateTimeOffset.UtcNow;
        asset.UpdatedBy = actorUserId;

        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("The server was changed by another user. Reload and try again.");
        }

        await auditWriter.WriteAsync(new AuditEntry(actorUserId, "StatusMarkAlive", "ServerAsset", asset.Id.ToString(), "Success", null,
            Payload: new Dictionary<string, object?> { ["aliveStatus"] = asset.AliveStatus.ToString() }), ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    private async Task EnsureAllowedAsync(Guid actorUserId, string permission, CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(actorUserId, permission, ct))
            throw new UnauthorizedAccessException($"{permission} permission is required.");
    }

    private static void ValidateTarget(ServerAsset asset)
    {
        if (asset.IsArchived || !asset.Subnet.IsEnabled ||
            !IPAddress.TryParse(asset.BusinessIp, out var address) ||
            !Ipv4Cidr.Parse(asset.Subnet.Cidr).ContainsUsable(address))
            throw new InvalidOperationException("The Ping target is not a registered address in an enabled subnet.");
    }

    private void ConsumeRateAllowance(Guid actorUserId)
    {
        CleanupExpiredRateWindows(DateTimeOffset.UtcNow);
        var executions = UserExecutions.GetOrAdd(actorUserId, static _ => new Queue<DateTimeOffset>());
        var now = DateTimeOffset.UtcNow;
        lock (executions)
        {
            while (executions.Count > 0 && now - executions.Peek() >= TimeSpan.FromMinutes(1))
                executions.Dequeue();
            if (executions.Count >= options.Value.PingPerUserPerMinute)
                throw new InvalidOperationException("Ping rate limit exceeded.");
            executions.Enqueue(now);
        }
    }

    private static void CleanupExpiredRateWindows(DateTimeOffset now)
    {
        if (Interlocked.Increment(ref _rateCleanupTick) % 64 != 0) return;
        foreach (var pair in UserExecutions)
        {
            lock (pair.Value)
            {
                while (pair.Value.Count > 0 && now - pair.Value.Peek() >= TimeSpan.FromMinutes(1))
                    pair.Value.Dequeue();
                if (pair.Value.Count == 0)
                    UserExecutions.TryRemove(new KeyValuePair<Guid, Queue<DateTimeOffset>>(pair.Key, pair.Value));
            }
        }
    }
    private static string NormalizeOutcome(string outcome) => outcome switch
    {
        "Success" => "Success",
        "Timeout" => "Timeout",
        "Unreachable" => "Unreachable",
        _ => "InternalError",
    };

    private static string? SafeErrorCode(string? errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode)) return null;
        var normalized = errorCode.Trim();
        return normalized.Length <= 128 && normalized.All(char.IsLetterOrDigit) ? normalized : "TransportError";
    }
}
