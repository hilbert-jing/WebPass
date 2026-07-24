using Microsoft.AspNetCore.Authorization;

namespace WebPass.Web.Infrastructure.Authorization;

public sealed record PermissionRequirement(string Code) : IAuthorizationRequirement;

public sealed record AdministratorRequirement : IAuthorizationRequirement;
