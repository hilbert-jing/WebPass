namespace WebPass.Web.Domain.Entities;

public sealed class DataEncryptionKey
{
    public int KeyVersion { get; set; }

    public required byte[] WrappedKey { get; set; }

    public required string CertificateThumbprint { get; set; }

    public DateTimeOffset ActivatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? RetiredAt { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public ICollection<ServerSecret> ServerSecrets { get; } = new List<ServerSecret>();
}
