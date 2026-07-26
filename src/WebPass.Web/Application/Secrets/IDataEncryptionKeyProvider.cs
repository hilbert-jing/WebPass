namespace WebPass.Web.Application.Secrets;

public interface IDataEncryptionKeyProvider
{
    Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct);

    Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct);
}
