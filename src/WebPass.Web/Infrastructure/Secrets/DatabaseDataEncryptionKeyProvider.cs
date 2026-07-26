using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Data;
using WebPass.Web.Domain.Entities;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class DatabaseDataEncryptionKeyProvider(
    WebPassDbContext db,
    IDataKeyWrapper keyWrapper) : IDataEncryptionKeyProvider
{
    public async Task<DataEncryptionKeyMaterial> GetActiveAsync(CancellationToken ct)
    {
        var stored = await db.DataEncryptionKeys.AsNoTracking()
            .SingleOrDefaultAsync(key => key.RetiredAt == null, ct);
        if (stored is not null) return Unwrap(stored);

        var keyBytes = RandomNumberGenerator.GetBytes(32);
        try
        {
            var nextVersion = (await db.DataEncryptionKeys.MaxAsync(
                key => (int?)key.KeyVersion, ct) ?? 0) + 1;
            stored = new DataEncryptionKey
            {
                KeyVersion = nextVersion,
                WrappedKey = keyWrapper.WrapKey(keyBytes),
                CertificateThumbprint = keyWrapper.CurrentCertificateThumbprint,
            };
            if (stored.RowVersion.Length == 0) stored.RowVersion = [1];
            var entry = db.DataEncryptionKeys.Add(stored);
            try
            {
                await db.SaveChangesAsync(ct);
                return new DataEncryptionKeyMaterial(stored.KeyVersion, keyBytes);
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is SqlException { Number: 2601 or 2627 })
            {
                entry.State = EntityState.Detached;
                var winner = await db.DataEncryptionKeys.AsNoTracking()
                    .SingleOrDefaultAsync(key => key.RetiredAt == null, ct);
                if (winner is null)
                    throw new InvalidOperationException(
                        "The active data-encryption key could not be resolved after concurrent initialization.",
                        exception);
                return Unwrap(winner);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    public async Task<DataEncryptionKeyMaterial> GetByVersionAsync(int keyVersion, CancellationToken ct)
    {
        if (keyVersion <= 0) throw new ArgumentOutOfRangeException(nameof(keyVersion));
        var stored = await db.DataEncryptionKeys.AsNoTracking()
            .SingleOrDefaultAsync(key => key.KeyVersion == keyVersion, ct)
            ?? throw new CryptographicException("The requested data-encryption key version does not exist.");
        return Unwrap(stored);
    }

    private DataEncryptionKeyMaterial Unwrap(DataEncryptionKey stored)
    {
        var keyBytes = keyWrapper.UnwrapKey(stored.WrappedKey, stored.CertificateThumbprint);
        try
        {
            return new DataEncryptionKeyMaterial(stored.KeyVersion, keyBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyBytes);
        }
    }
}
