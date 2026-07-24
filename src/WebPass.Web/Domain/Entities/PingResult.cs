namespace WebPass.Web.Domain.Entities;

public sealed class PingResult
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ServerAssetId { get; set; }
    public ServerAsset ServerAsset { get; set; } = null!;
    public required string TargetIp { get; set; }
    public required string Outcome { get; set; }
    public long? LatencyMilliseconds { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset ExecutedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid ExecutedBy { get; set; }
}
