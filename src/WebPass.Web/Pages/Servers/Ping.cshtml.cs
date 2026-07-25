using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using WebPass.Web.Application.Authorization;
using WebPass.Web.Application.Ping;

namespace WebPass.Web.Pages.Servers;

[Authorize(Policy = PermissionCode.PingExecute)]
public sealed class PingModel(PingService pingService) : PageModel
{
    public async Task<IActionResult> OnPostAsync(Guid id, CancellationToken ct)
    {
        await pingService.ExecuteAsync(id, Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!), ct);
        return RedirectToPage("/Servers/Index");
    }
}
