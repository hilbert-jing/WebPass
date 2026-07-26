using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Data;
using WebPass.Web.Infrastructure.Auditing;
using WebPass.Web.Infrastructure.Authorization;

namespace WebPass.Web.Application.Secrets;

public sealed record RevealResult(string Password);

public sealed class SecretRevealService(
    WebPassDbContext db,
    PermissionAuthorizationHandler permissions,
    IReauthenticationGrantStore grants,
    IAuthenticationSessionFingerprint sessionFingerprint,
    ISecretCipher cipher,
    AuditWriter auditWriter)
{
    public async Task<RevealResult> RevealAsync(
        Guid userId,
        Guid assetId,
        CancellationToken ct)
    {
        if (!await permissions.IsAllowedAsync(userId, PermissionCode.SecretReveal, ct))
        {
            await WriteAuditAsync(userId, assetId, "Denied", ct);
            throw new UnauthorizedAccessException("Secret reveal is not authorized.");
        }

        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null
            || !await grants.HasValidGrantAsync(
                userId,
                sessionFingerprint.GetCurrent(),
                user.RowVersion,
                ct))
        {
            await WriteAuditAsync(userId, assetId, "Denied", ct);
            throw new UnauthorizedAccessException("Current-password verification is required.");
        }

        var secret = await db.ServerSecrets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ServerAssetId == assetId, ct);
        if (secret is null)
        {
            await WriteAuditAsync(userId, assetId, "NotFound", ct);
            throw new KeyNotFoundException("The server password was not found.");
        }

        var envelope = new SecretEnvelope(
            secret.Ciphertext,
            secret.Nonce,
            secret.AuthenticationTag,
            secret.KeyVersion);
        try
        {
            var password = await cipher.DecryptAsync(assetId, envelope, ct);
            await WriteAuditAsync(userId, assetId, "Success", ct);
            return new RevealResult(password);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteAuditAsync(userId, assetId, "Failure", ct);
            throw;
        }
    }

    private Task WriteAuditAsync(
        Guid userId,
        Guid assetId,
        string result,
        CancellationToken ct) =>
        auditWriter.WriteAsync(
            new AuditEntry(userId, "SecretReveal", "ServerAsset", assetId.ToString(), result, null),
            ct);
}
