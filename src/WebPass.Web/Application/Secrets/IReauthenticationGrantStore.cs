namespace WebPass.Web.Application.Secrets;

public interface IReauthenticationGrantStore
{
    Task StoreAsync(ReauthenticationGrant grant, CancellationToken ct);

    Task<bool> HasValidGrantAsync(
        Guid userId,
        string sessionFingerprint,
        byte[] userRowVersion,
        CancellationToken ct);
}
