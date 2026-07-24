using WebPass.Web.Domain.Enums;

namespace WebPass.Web.Domain.Entities;

public sealed class ServerAsset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubnetId { get; set; }
    public Subnet Subnet { get; set; } = null!;
    public required string BusinessIp { get; set; }
    public long BusinessIpNumber { get; set; }
    public required string Location { get; set; }
    public AliveStatus AliveStatus { get; set; } = AliveStatus.Unknown;
    public required string ComputerName { get; set; }
    public required string SystemName { get; set; }
    public string? OperatingSystemVersion { get; set; }
    public string? DatabaseVersion { get; set; }
    public string? Notes { get; set; }
    public bool IsArchived { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public Guid? ArchivedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<PingResult> PingResults { get; } = new List<PingResult>();
}
