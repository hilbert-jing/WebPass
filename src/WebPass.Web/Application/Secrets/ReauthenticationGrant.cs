namespace WebPass.Web.Application.Secrets;

public sealed record ReauthenticationGrant(
    Guid UserId,
    string SessionFingerprint,
    byte[] UserRowVersion,
    DateTimeOffset ExpiresAt);
