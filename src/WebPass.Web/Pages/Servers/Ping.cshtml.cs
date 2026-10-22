using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using WebPass.Web.Application.Assets;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;
using WebPass.Web.Infrastructure.Security;
using WebPass.Web.Presentation;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.PingExecute)]
[EnableRateLimiting(SecretRateLimitPolicies.Ping)]
public sealed class PingModel(PingService pingService) : PageModel
{
    [BindProperty]
    public ServerListQuery Query { get; set; } = new();

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
                pageHandler: null,
                routeValues: PingCommandWorkflow.ListRouteValues(Query),
                fragment: $"server-{feedback.AssetId}"),
            ct);
}
