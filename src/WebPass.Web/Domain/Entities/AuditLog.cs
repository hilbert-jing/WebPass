namespace WebPass.Web.Domain.Entities;

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public required string ObjectType { get; set; }
    public string? ObjectId { get; set; }
    public required string Result { get; set; }
    public string? SourceIp { get; set; }
    public string? CorrelationId { get; set; }
    public string? Details { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}
