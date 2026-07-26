namespace WebPass.Web.Application.Secrets;

public interface ISecretCipher
{
    Task<SecretEnvelope> EncryptAsync(Guid secretId, string plaintext, CancellationToken ct);

    Task<string> DecryptAsync(Guid secretId, SecretEnvelope envelope, CancellationToken ct);
}
