using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Infrastructure.Security;
using WebPass.Web.Presentation;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.PingExecute)]
[EnableRateLimiting(SecretRateLimitPolicies.Ping)]
public sealed class PingModel(PingService pingService) : PageModel
{
    public IActionResult OnGet() => StatusCode(
        StatusCodes.Status405MethodNotAllowed);

    public Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct) =>
        PingCommandWorkflow.ExecuteAsync(
            pingService,
            id,
            Guid.Parse(
                User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            TempData,
            feedback => RedirectToPage(
                "/Servers/Index",
                PingCommandWorkflow.TargetRouteValues(feedback)),
            ct);
}
