using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Secrets;

public sealed record DataKeyRotationResult(
    int PreviousVersion,
    int NewVersion,
    int ReencryptedSecretCount);

public sealed class DataKeyRotationService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    IDataKeyWrapper keyWrapper,
    ISecretCipher secretCipher,
    AuditWriter auditWriter)
{
    public async Task<DataKeyRotationResult> RotateAsync(Guid actorUserId, CancellationToken ct)
    {
        if (!await permissions.IsAdministratorAsync(actorUserId, ct))
            throw new UnauthorizedAccessException("Administrator permission is required.");
        if (!db.Database.IsRelational())
            throw new InvalidOperationException("Data-key rotation requires a relational database transaction.");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        try
        {
            var previous = await db.DataEncryptionKeys
                .SingleOrDefaultAsync(key => key.RetiredAt == null, ct)
                ?? throw new InvalidOperationException("No active data-encryption key exists.");
            var newVersion = (await db.DataEncryptionKeys.MaxAsync(
                key => (int?)key.KeyVersion, ct) ?? 0) + 1;
            var now = DateTimeOffset.UtcNow;
            var keyBytes = RandomNumberGenerator.GetBytes(32);
            byte[] wrappedKey;
            try
            {
                wrappedKey = keyWrapper.WrapKey(keyBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }

            previous.RetiredAt = now;
            var replacement = new DataEncryptionKey
            {
                KeyVersion = newVersion,
                WrappedKey = wrappedKey,
                CertificateThumbprint = keyWrapper.CurrentCertificateThumbprint,
                ActivatedAt = now,
            };
            db.DataEncryptionKeys.Add(replacement);
            await db.SaveChangesAsync(ct);

            var secrets = await db.ServerSecrets.ToListAsync(ct);
            foreach (var secret in secrets)
            {
                var plaintext = await secretCipher.DecryptAsync(secret.ServerAssetId, ToEnvelope(secret), ct);
                var envelope = await secretCipher.EncryptAsync(secret.ServerAssetId, plaintext, ct);
                secret.Ciphertext = envelope.Ciphertext;
                secret.Nonce = envelope.Nonce;
                secret.AuthenticationTag = envelope.AuthenticationTag;
                secret.KeyVersion = envelope.KeyVersion;
                secret.UpdatedAt = now;
                secret.UpdatedBy = actorUserId;
            }

            await db.SaveChangesAsync(ct);
            await auditWriter.WriteAsync(new AuditEntry(
                actorUserId,
                "DataKeyRotate",
                "DataEncryptionKey",
                newVersion.ToString(),
                "Success",
                null,
                Payload: new Dictionary<string, object?>
                {
                    ["previousVersion"] = previous.KeyVersion,
                    ["newVersion"] = newVersion,
                    ["reencryptedRecordCount"] = secrets.Count,
                }), ct);
            await transaction.CommitAsync(ct);
            return new DataKeyRotationResult(previous.KeyVersion, newVersion, secrets.Count);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }

    private static SecretEnvelope ToEnvelope(ServerSecret secret) => new(
        secret.Ciphertext,
        secret.Nonce,
        secret.AuthenticationTag,
        secret.KeyVersion);
}
