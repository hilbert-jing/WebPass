using System.Net;
using System.Text.Json;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Infrastructure.Auditing;

public sealed record AuditEntry(
    Guid? ActorUserId,
    string Action,
    string ObjectType,
    string? ObjectId,
    string Result,
    IPAddress? SourceIp,
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Payload = null);

public sealed class AuditWriter(WebPassDbContext db, IHttpContextAccessor? httpContextAccessor = null)
{
    private static readonly string[] SensitiveNames = ["password", "secret", "ciphertext", "token", "cookie", "authorization", "key"];

    public async Task WriteAsync(AuditEntry entry, CancellationToken ct)
    {
        var context = httpContextAccessor?.HttpContext;
        var sourceIp = entry.SourceIp?.ToString() ?? context?.Connection.RemoteIpAddress?.ToString();
        var correlationId = entry.CorrelationId ?? context?.TraceIdentifier;
        var details = entry.Payload is null ? null : SerializeAndValidate(entry.Payload);

        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = entry.ActorUserId,
            Action = entry.Action,
            ObjectType = entry.ObjectType,
            ObjectId = entry.ObjectId,
            Result = entry.Result,
            SourceIp = sourceIp,
            CorrelationId = correlationId,
            Details = details,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string SerializeAndValidate(IReadOnlyDictionary<string, object?> payload)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        if (ContainsSensitiveProperty(document.RootElement))
        {
            throw new ArgumentException("Audit payload contains a sensitive property name.", nameof(payload));
        }

        return document.RootElement.GetRawText();
    }

    private static bool ContainsSensitiveProperty(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            IsSensitiveName(property.Name) || ContainsSensitiveProperty(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsSensitiveProperty),
        _ => false,
    };

    private static bool IsSensitiveName(string name) =>
        SensitiveNames.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase));
}
