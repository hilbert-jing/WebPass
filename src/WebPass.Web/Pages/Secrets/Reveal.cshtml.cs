using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Secrets;
using WebPass.Web.Infrastructure.Security;

namespace WebPass.Web.Pages.Secrets;

[Authorize(Policy = PermissionCode.SecretReveal)]
[EnableRateLimiting(SecretRateLimitPolicies.Reveal)]
public sealed class RevealModel(SecretRevealService secrets) : PageModel
{
    public IActionResult OnGet() =>
        StatusCode(StatusCodes.Status405MethodNotAllowed);

    public async Task<IActionResult> OnPostAsync(Guid assetId, CancellationToken ct)
    {
        try
        {
            var result = await secrets.RevealAsync(UserId(), assetId, ct);
            Response.Headers.CacheControl = "no-store, no-cache";
            Response.Headers.Pragma = "no-cache";
            Response.Headers.Expires = "0";
            return new JsonResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private Guid UserId() =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
