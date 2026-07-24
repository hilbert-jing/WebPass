namespace WebPass.Web.Domain.Entities;

public sealed class UserPermission
{
    public Guid UserId { get; set; }
    public required string PermissionCode { get; set; }
    public AppUser User { get; set; } = null!;
}
