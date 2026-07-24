namespace WebPass.Web.Domain.Entities;

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Username { get; set; }
    public required string PasswordHash { get; set; }
    public bool IsAdministrator { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public bool MustChangePassword { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public ICollection<UserPermission> Permissions { get; } = new List<UserPermission>();
}
