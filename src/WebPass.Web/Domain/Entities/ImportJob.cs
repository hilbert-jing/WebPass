namespace WebPass.Web.Domain.Entities;

public sealed class ImportJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string FileType { get; set; }
    public required string Status { get; set; }
    public int TotalRows { get; set; }
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CommittedAt { get; set; }
    public Guid CreatedBy { get; set; }
}
