using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using WebPass.Web.Application.Secrets;

namespace WebPass.Web.Infrastructure.Secrets;

public sealed class InMemoryReauthenticationGrantStore(
    IMemoryCache cache,
    TimeProvider? clock = null) : IReauthenticationGrantStore
{
    public Task StoreAsync(ReauthenticationGrant grant, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lifetime = grant.ExpiresAt - (clock ?? TimeProvider.System).GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        cache.Set(
            CacheKey(grant.UserId, grant.SessionFingerprint),
            grant with { UserRowVersion = grant.UserRowVersion.ToArray() },
            lifetime);
        return Task.CompletedTask;
    }

    public Task<bool> HasValidGrantAsync(
        Guid userId,
        string sessionFingerprint,
        byte[] userRowVersion,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var valid = cache.TryGetValue<ReauthenticationGrant>(
                CacheKey(userId, sessionFingerprint),
                out var grant)
            && grant is not null
            && grant.ExpiresAt > (clock ?? TimeProvider.System).GetUtcNow()
            && CryptographicOperations.FixedTimeEquals(
                grant.UserRowVersion,
                userRowVersion);
        return Task.FromResult(valid);
    }

    private static string CacheKey(Guid userId, string fingerprint) =>
        $"reauthentication:{userId:N}:{fingerprint}";
}
