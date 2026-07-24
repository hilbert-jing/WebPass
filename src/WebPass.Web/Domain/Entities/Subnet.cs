namespace WebPass.Web.Domain.Entities;

public sealed class Subnet
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Cidr { get; set; }
    public required string NetworkAddress { get; set; }
    public int PrefixLength { get; set; }
    public required string Location { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Notes { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<ServerAsset> ServerAssets { get; } = new List<ServerAsset>();
}
