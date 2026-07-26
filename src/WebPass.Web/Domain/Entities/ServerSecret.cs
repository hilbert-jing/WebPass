namespace WebPass.Web.Domain.Entities;

public sealed class ServerSecret
{
    public Guid ServerAssetId { get; set; }

    public ServerAsset ServerAsset { get; set; } = null!;

    public required byte[] Ciphertext { get; set; }

    public required byte[] Nonce { get; set; }

    public required byte[] AuthenticationTag { get; set; }

    public int KeyVersion { get; set; }

    public DataEncryptionKey DataEncryptionKey { get; set; } = null!;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Guid? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}
